using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;
#if UNITY_2021_2_OR_NEWER
using PrefabStageUtility = UnityEditor.SceneManagement.PrefabStageUtility;

#elif UNITY_2018_3_OR_NEWER
using PrefabStage = UnityEditor.Experimental.SceneManagement.PrefabStage;
using PrefabStageUtility = UnityEditor.Experimental.SceneManagement.PrefabStageUtility;
#endif

namespace Plugins.AssetUsageDetector.Editor
{
    [Serializable]
    public class SearchResultTreeViewState : TreeViewState
    {
        // - initialNodeId is serialized because we want to preserve the expanded states of the TreeViewItems after domain reload and
        //   it's only possible if TreeView is reconstructed with the same ids
        // - finalNodeId is serialized because if the same id used for multiple TreeViewItems across multiple TreeViews, strange issues occur.
        //   Thus, each new TreeView will set its initialNodeId to the previous TreeView's finalNodeId
        // - Each TreeViewItem's id is different even if two TreeViewItems point to the exact same ReferenceNode. That's because TreeView
        //   doesn't work well when some TreeViewItems share the same id (e.g. while navigating the tree with arrow keys)
        public int initialNodeId, finalNodeId;

        // Not using the built-in searchString and hasSearch properties of TreeView because:
        // - This search algorithm is a bit more complicated than usual, we don't flatten the tree during the search
        // - If code is recompiled while searchString wasn't empty, the tree isn't rebuilt and remains empty (at least on Unity 5.6)
        public string searchTerm;
        public SearchResultTreeView.SearchMode searchMode = SearchResultTreeView.SearchMode.All;
        public bool selectionChangedDuringSearch;

        public List<int> preSearchExpandedIds;
    }

    public class SearchResultTreeView : TreeView
    {
        public enum TreeType
        {
            Normal,
            UnusedObjects,
            IsolatedView
        }

        public enum SearchMode
        {
            SearchedObjectsOnly,
            ReferencesOnly,
            All
        }

        private class ReferenceNodeData
        {
            public readonly TreeViewItem item;
            public readonly int linkIndex;
            public readonly ReferenceNode node;
            public readonly ReferenceNodeData parent;
            public bool isDuplicate;
            public bool isLastLink;

            private string m_tooltipText;
            public bool shouldExpandAfterSearch;

            public ReferenceNodeData(TreeViewItem item, ReferenceNode node, ReferenceNodeData parent, int linkIndex)
            {
                this.item = item;
                this.node = node;
                this.parent = parent;
                this.linkIndex = linkIndex;
            }

            public string tooltipText
            {
                get
                {
                    if (m_tooltipText != null)
                    {
                        return m_tooltipText;
                    }

                    return GetTooltipText(Utilities.stringBuilder);
                }
            }

            private string GetTooltipText(StringBuilder sb)
            {
                sb.Length = 0;
                sb.Append("- ").Append(node.Label);

                if (parent != null)
                {
                    sb.Append("\n");

                    if (parent.node[linkIndex].descriptions.Count > 0)
                    {
                        var linkDescriptions = parent.node[linkIndex].descriptions;
                        for (var i = 0; i < linkDescriptions.Count; i++)
                        {
                            sb.Append("  <color=#")
                                .Append(ColorUtility.ToHtmlStringRGBA(AssetUsageDetectorSettings.TooltipDescriptionTextColor)).Append(">")
                                .Append(linkDescriptions[i]).Append("</color>\n");
                        }
                    }

                    if (parent.m_tooltipText != null)
                    {
                        sb.Append(parent.m_tooltipText);
                    }
                    else // Cache parents' tooltips along the way because they'll likely be reused frequently. We need to use new StringBuilder instances for them
                    {
                        sb.Append(parent.GetTooltipText(new StringBuilder(256)));
                    }
                }

                m_tooltipText = sb.ToString();
                return m_tooltipText;
            }

            public void ResetTooltip()
            {
                m_tooltipText = null;
            }
        }

        private const float SEARCHED_OBJECTS_BORDER_THICKNESS = 1f;
#if UNITY_2019_3_OR_NEWER
        private const float
            TREE_VIEW_LINES_THICKNESS =
                1.5f; // There are inexplicable spaces between the vertical and horizontal lines if we don't change thickness by 0.5f on 2019.3+
#else
		private const float TREE_VIEW_LINES_THICKNESS = 2f;
#endif
        private const float HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS = TREE_VIEW_LINES_THICKNESS * 2f;

        private new readonly SearchResultTreeViewState state;

        private readonly List<ReferenceNode> references;
        private readonly List<ReferenceNodeData> idToNodeDataLookup = new(128);

        private readonly HashSet<ReferenceNode> selectedReferenceNodes = new();
        private readonly HashSet<int> selectedReferenceNodesHierarchyIds = new();
        private readonly HashSet<int> selectedReferenceNodesHierarchyIndirectIds = new();

        private readonly HashSet<Object> usedObjectsSet;

        private readonly TreeType treeType;
        private readonly bool hideDuplicateRows;
        private readonly bool hideReduntantPrefabVariantLinks;

        private bool isSearching;

#if !UNITY_2018_2_OR_NEWER
		public int visibleRowTop = 0, visibleRowBottom = int.MaxValue;
#endif

        private readonly CompareInfo textComparer = new CultureInfo("en-US").CompareInfo;
        private readonly CompareOptions textCompareOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        private readonly GUIContent sharedGUIContent = new();
        private GUIStyle foldoutLabelStyle;
        private Texture2D whiteGradientTexture;
        private string highlightedSearchTextColor;

        private ReferenceNodeData prevHoveredData, hoveredData;
        private Rect hoveredDataRect;

        private bool isTreeViewEmpty;
        private bool isLMBDown;

        private double customTooltipShowTime;

        public new float rowHeight
        {
            get { return base.rowHeight; }
            set
            {
                base.rowHeight = value;
#if !UNITY_2019_3_OR_NEWER
				customFoldoutYOffset = ( value - EditorGUIUtility.singleLineHeight ) * 0.5f;
#endif
            }
        }

        // Avoid using these properties of TreeView by mistake
        [Obsolete]
        private new string searchString { get; }

        [Obsolete]
        private new bool hasSearch { get; }

        public SearchResultTreeView(SearchResultTreeViewState state, List<ReferenceNode> references, TreeType treeType,
            HashSet<Object> usedObjectsSet, bool hideDuplicateRows, bool hideReduntantPrefabVariantLinks,
            bool usesExternalScrollView) : base(state)
        {
            this.state = state;
            this.references = references;
            this.treeType = treeType;
            this.hideDuplicateRows = hideDuplicateRows;
            this.hideReduntantPrefabVariantLinks = hideReduntantPrefabVariantLinks;

            highlightedSearchTextColor = "<color=#" + ColorUtility.ToHtmlStringRGBA(AssetUsageDetectorSettings.SearchMatchingTextColor) + ">";

            rowHeight = EditorGUIUtility.singleLineHeight + AssetUsageDetectorSettings.ExtraRowHeight;

            if (treeType == TreeType.UnusedObjects)
            {
                showBorder = true;
                this.usedObjectsSet = usedObjectsSet;
            }

            if (treeType != TreeType.IsolatedView)
            {
                // Draw only the visible rows. This requires setting useScrollView to false because we are using an external scroll view: https://docs.unity3d.com/ScriptReference/IMGUI.Controls.TreeView-useScrollView.html
#if UNITY_2018_2_OR_NEWER
                useScrollView = false;
#else
				// In my tests, SetUseScrollView seems to have no effect unfortunately but let's keep this line in case it fixes some other issues with the external scroll view
				object treeViewController =
 typeof( TreeView ).GetField( "m_TreeView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance ).GetValue( this );
				treeViewController.GetType().GetMethod( "SetUseScrollView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance ).Invoke( treeViewController, new object[1] { false } );
#endif
            }

            isSearching = !string.IsNullOrEmpty(state.searchTerm);

            Reload();

            if (HasSelection())
            {
                RefreshSelectedNodes(GetSelection());
            }
        }

        public void RefreshSearch(string prevSearchTerm)
        {
            var wasSearchTermEmpty = string.IsNullOrEmpty(prevSearchTerm);
            var isSearchTermEmpty = string.IsNullOrEmpty(state.searchTerm);

            isSearching = !isSearchTermEmpty;

            if (!wasSearchTermEmpty || !isSearchTermEmpty)
            {
                Reload();

                if (!isSearchTermEmpty)
                {
                    if (wasSearchTermEmpty)
                    {
                        state.preSearchExpandedIds = new List<int>(GetExpanded() ?? new int[0]);
                        state.selectionChangedDuringSearch = false;
                    }

                    ExpandMatchingSearchResults();
                }
                else if (!wasSearchTermEmpty && state.preSearchExpandedIds != null && state.preSearchExpandedIds.Count > 0)
                {
                    var expandedIds = state.preSearchExpandedIds;
                    var expandedIdsSet = new HashSet<int>(expandedIds);
                    if (state.selectionChangedDuringSearch)
                    {
                        var selection = GetSelection();
                        for (var i = 0; i < selection.Count; i++)
                        {
                            for (var item = GetDataFromId(selection[i]).item; item != null; item = item.parent)
                            {
                                if (expandedIdsSet.Add(item.id))
                                {
                                    expandedIds.Add(item.id);
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }

                    SetExpanded(state.preSearchExpandedIds);
                    expandedIds.Clear();
                }

                if (HasSelection())
                {
                    RefreshSelectedNodes(GetSelection());
                }
            }
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = state.initialNodeId, depth = -1, displayName = "Root" };
            var id = state.initialNodeId + 1;

            idToNodeDataLookup.Clear();

            var stack = new List<ReferenceNode>(8);
            HashSet<ReferenceNode> processedNodes = null;
            if (hideDuplicateRows)
            {
                processedNodes = new HashSet<ReferenceNode>();
                for (var i = references.Count - 1; i >= 0; i--)
                {
                    // Don't mark root nodes as duplicates unless we're in ReferencesOnly search mode (in which case, it's just technically unfeasible to know which root nodes will be displayed in advance)
                    if (!isSearching || state.searchMode != SearchMode.ReferencesOnly &&
                        textComparer.IndexOf(references[i].Label, state.searchTerm, textCompareOptions) >= 0)
                    {
                        processedNodes.Add(references[i]);
                    }
                }
            }

            for (var i = 0; i < references.Count; i++)
            {
                GenerateRowsRecursive(root, references[i], null, i, 0, null, stack, processedNodes, ref id);
            }

            isTreeViewEmpty = !root.hasChildren;
            if (isTreeViewEmpty) // May happen if all items are hidden inside HideItems function or there are no matching search results. If we don't create a dummy child, Unity throws an exception
            {
                root.AddChild(new TreeViewItem(state.initialNodeId +
                                               1)); // If we don't give it a valid id, some functions throw exceptions when there are no matching search results
            }
            else
            {
                GetDataFromId(root.children[root.children.Count - 1].id).isLastLink = true;
            }

            state.finalNodeId = id + 1;

            return root;
        }

        private bool GenerateRowsRecursive(TreeViewItem parent, ReferenceNode referenceNode, ReferenceNodeData parentData, int siblingIndex,
            int depth, bool? itemForcedVisibility, List<ReferenceNode> stack, HashSet<ReferenceNode> processedNodes, ref int id)
        {
#if UNITY_2018_3_OR_NEWER
            // If the same reference is found in both a prefab variant and its base prefab, omit the prefab variant to reduce the number of
            // redundant links (changing the value in base prefab will automatically change the value in its variants, after all)
            if (hideReduntantPrefabVariantLinks && depth > 0)
            {
                var unityObject = referenceNode.UnityObject;
                if (unityObject && (unityObject is Component || unityObject is GameObject) && unityObject.IsAsset())
                {
                    var linkDescriptions = parentData.node[siblingIndex].descriptions;
                    if (linkDescriptions.Count > 0)
                    {
                        var prefabObject = PrefabUtility.GetCorrespondingObjectFromSource(unityObject);
                        if (prefabObject && parentData.node.HasLinkToObjectWithDescriptions(prefabObject.GetHashCode(), linkDescriptions))
                        {
                            return false;
                        }
                    }
                }
            }
#endif

            var item = new TreeViewItem(id++, depth, "");
            var data = new ReferenceNodeData(item, referenceNode, parentData, siblingIndex);

            bool shouldShowItem;
            if (itemForcedVisibility.HasValue)
            {
                shouldShowItem = itemForcedVisibility.Value;
            }
            else
            {
                if (!isSearching)
                {
                    shouldShowItem = true;
                }
                else if (state.searchMode == SearchMode.All || depth == 0 == (state.searchMode == SearchMode.SearchedObjectsOnly))
                {
                    shouldShowItem = textComparer.IndexOf(referenceNode.Label, state.searchTerm, textCompareOptions) >= 0;
                    if (!shouldShowItem && depth > 0)
                    {
                        var descriptions = parentData.node[siblingIndex].descriptions;
                        for (var i = descriptions.Count - 1; i >= 0; i--)
                        {
                            if (textComparer.IndexOf(descriptions[i], state.searchTerm, textCompareOptions) >= 0)
                            {
                                shouldShowItem = true;
                                break;
                            }
                        }
                    }

                    data.shouldExpandAfterSearch = shouldShowItem;

                    if (state.searchMode == SearchMode.SearchedObjectsOnly || state.searchMode == SearchMode.All && shouldShowItem)
                    {
                        itemForcedVisibility = shouldShowItem;
                    }
                }
                else
                {
                    shouldShowItem = false;
                }
            }

            idToNodeDataLookup.Add(data);

            // Disallow recursion (stack) because it would crash Unity
            if (referenceNode.NumberOfOutgoingLinks > 0 && !stack.ContainsFast(referenceNode))
            {
                // Add children only if hideDuplicateRows is false (processedNodes == null) or this node hasn't been seen before
                if (processedNodes != null && !processedNodes.Add(referenceNode) &&
                    depth > 0) // "depth > 0": Root nodes are either added to processedNodes prior to generating rows (so that they're never marked as duplicate), or they just shouldn't be trimmed
                {
                    data.isDuplicate = true;
                }
                else
                {
                    stack.Add(referenceNode);

                    // Generate child items even if they will be forced invisible so that each visible row's id is deterministic and doesn't change when some rows become invisible
                    for (var i = 0; i < referenceNode.NumberOfOutgoingLinks; i++)
                    {
                        shouldShowItem |= GenerateRowsRecursive(item, referenceNode[i].targetNode, data, i, depth + 1, itemForcedVisibility,
                            stack, processedNodes, ref id);
                    }

                    stack.RemoveAt(stack.Count - 1);
                }
            }

            if (shouldShowItem)
            {
                if (item.hasChildren)
                {
                    GetDataFromId(item.children[item.children.Count - 1].id).isLastLink = true;
                }

                parent.AddChild(item);
                return true;
            }

            return false;
        }

        private ReferenceNodeData GetDataFromId(int id)
        {
            return idToNodeDataLookup[id - state.initialNodeId - 1];
        }

        public override void OnGUI(Rect rect)
        {
            // Disallow clicking on "No matching results" text when in search mode
            var guiEnabled = GUI.enabled;
            if (isTreeViewEmpty)
            {
                GUI.enabled = false;
            }

            // Mouse and special keyboard events are already in Used state in CommandEventHandling, so we need to process them here
            var ev = Event.current;
            if (ev.type == EventType.MouseDown)
            {
                if (ev.button == 0)
                {
                    isLMBDown = true;
                }
            }
            else if (ev.type == EventType.MouseUp)
            {
                if (ev.button == 0)
                {
                    isLMBDown = false;
                }
            }
            else if (ev.type == EventType.MouseMove)
            {
                hoveredData = null;
            }
            else if (ev.type == EventType.KeyDown)
            {
                if ((ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && HasSelection() && HasFocus())
                {
                    DoubleClickedItem(state.lastClickedID);
                    ev.Use();
                }
            }

            base.OnGUI(rect);

            if (prevHoveredData != hoveredData)
            {
                if (AssetUsageDetectorSettings.CustomTooltipDelay > 0f)
                {
                    EditorApplication.update -= ShowTooltipDelayed;
                }

                prevHoveredData = hoveredData;
                if (hoveredData != null)
                {
                    if (AssetUsageDetectorSettings.CustomTooltipDelay <= 0f)
                    {
                        SearchResultTooltip.Show(hoveredDataRect, hoveredData.tooltipText);
                    }
                    else
                    {
                        customTooltipShowTime = EditorApplication.timeSinceStartup + AssetUsageDetectorSettings.CustomTooltipDelay;
                        EditorApplication.update += ShowTooltipDelayed;
                    }
                }
                else
                {
                    SearchResultTooltip.Hide();
                }

                Repaint();
            }

            GUI.enabled = guiEnabled;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
#if !UNITY_2018_2_OR_NEWER
			// Do manual row culling on early Unity versions
			if( args.row < visibleRowTop || args.row > visibleRowBottom )
				return;
#endif

            if (isTreeViewEmpty)
            {
                EditorGUI.LabelField(args.rowRect, "No matching results...");
                return;
            }

            var ev = Event.current;
            var data = GetDataFromId(args.item.id);
            var rect = args.rowRect;

            if (string.IsNullOrEmpty(args.item.displayName))
            {
                var unityObject = data.node.UnityObject;
                if (unityObject)
                {
                    args.item.icon = AssetPreview.GetMiniThumbnail(unityObject);
                }

                var sb = Utilities.stringBuilder;
                sb.Length = 0;

                if (data.isDuplicate)
                {
                    sb.Append("<b>[D]</b> ");
                }

                if (data.parent == null)
                {
                    if (treeType != TreeType.UnusedObjects)
                    {
                        sb.Append("<b>");
                    }
                    else if (data.node.usedState == ReferenceNode.UsedState.MixedCollapsed)
                    {
                        sb.Append("<b>[!]</b> ");
                    }
                    else if (data.node.usedState == ReferenceNode.UsedState.MixedExpanded)
                    {
                        sb.Append("<b><i>[!]</i></b> ");
                    }

                    if (!isSearching || state.searchMode == SearchMode.ReferencesOnly)
                    {
                        sb.Append(data.node.Label);
                    }
                    else
                    {
                        HighlightSearchTermInString(sb, data.node.Label);
                    }

                    if (treeType != TreeType.UnusedObjects)
                    {
                        sb.Append("</b>");
                    }
                }
                else
                {
                    var linkDescriptions = data.parent.node[data.linkIndex].descriptions;
                    if (linkDescriptions.Count > 0)
                    {
                        if (!isSearching || state.searchMode == SearchMode.SearchedObjectsOnly)
                        {
                            sb.Append(data.node.Label).Append(" <b>").Append(linkDescriptions[0]);
                        }
                        else
                        {
                            HighlightSearchTermInString(sb, data.node.Label);
                            sb.Append(" <b>");
                            HighlightSearchTermInString(sb, linkDescriptions[0]);
                        }

                        if (linkDescriptions.Count > 1)
                        {
                            var shouldHighlightRemainingLinkDescriptions = false;
                            if (isSearching && state.searchMode != SearchMode.SearchedObjectsOnly)
                            {
                                for (var i = linkDescriptions.Count - 1; i > 0; i--)
                                {
                                    if (textComparer.IndexOf(linkDescriptions[i], state.searchTerm, textCompareOptions) >= 0)
                                    {
                                        shouldHighlightRemainingLinkDescriptions = true;
                                        sb.Append(highlightedSearchTextColor);

                                        break;
                                    }
                                }
                            }

                            sb.Append(" <i>and ").Append(linkDescriptions.Count - 1).Append(" more</i>");

                            if (shouldHighlightRemainingLinkDescriptions)
                            {
                                sb.Append("</color>");
                            }
                        }

                        sb.Append("</b>");
                    }
                    else if (isSearching && state.searchMode != SearchMode.SearchedObjectsOnly)
                    {
                        HighlightSearchTermInString(sb, data.node.Label);
                    }
                    else
                    {
                        sb.Append(data.node.Label);
                    }
                }

                args.item.displayName = sb.ToString();
            }

            sharedGUIContent.text = args.item.displayName;
            sharedGUIContent.tooltip = AssetUsageDetectorSettings.ShowUnityTooltip ? data.tooltipText : null;
            sharedGUIContent.image = args.item.icon;

            if (ev.type == EventType.Repaint)
            {
                if (treeType != TreeType.UnusedObjects)
                {
                    if (args.item.depth == 0)
                    {
                        var guiColor = GUI.color;

                        // Draw background
                        if (!args.selected)
                        {
                            GUI.color = guiColor *
                                        (AssetUsageDetectorSettings.ApplySelectedRowParentsTintToRootRows &&
                                         selectedReferenceNodesHierarchyIds.Contains(args.item.id)
                                            ? AssetUsageDetectorSettings.SelectedRowParentsTint
                                            : AssetUsageDetectorSettings.RootRowsBackgroundColor);
                            GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                        }

                        // Draw border: https://github.com/Unity-Technologies/UnityCsReference/blob/33cbfe062d795667c39e16777230e790fcd4b28b/Editor/Mono/GUI/InternalEditorGUI.cs#L262-L275
                        if (AssetUsageDetectorSettings.RootRowsBorderColor.a > 0f)
                        {
                            GUI.color = guiColor * AssetUsageDetectorSettings.RootRowsBorderColor;
                            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, SEARCHED_OBJECTS_BORDER_THICKNESS),
                                EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);

                            // Draw bottom border only if there isn't another searched object immediately below this one (otherwise, this bottom border and the following top border are drawn at the same space, resulting in darker shade for that edge)
                            if (data.isLastLink || args.item.hasChildren && IsExpanded(args.item.id))
                            {
                                GUI.DrawTexture(
                                    new Rect(rect.x, rect.yMax - SEARCHED_OBJECTS_BORDER_THICKNESS, rect.width,
                                        SEARCHED_OBJECTS_BORDER_THICKNESS), EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                                GUI.DrawTexture(
                                    new Rect(rect.x, rect.y + 1, SEARCHED_OBJECTS_BORDER_THICKNESS,
                                        rect.height - 2f * SEARCHED_OBJECTS_BORDER_THICKNESS), EditorGUIUtility.whiteTexture,
                                    ScaleMode.StretchToFill, true, 0f);
                                GUI.DrawTexture(
                                    new Rect(rect.xMax - SEARCHED_OBJECTS_BORDER_THICKNESS, rect.y + 1, SEARCHED_OBJECTS_BORDER_THICKNESS,
                                        rect.height - 2f * SEARCHED_OBJECTS_BORDER_THICKNESS), EditorGUIUtility.whiteTexture,
                                    ScaleMode.StretchToFill, true, 0f);
                            }
                            else
                            {
                                GUI.DrawTexture(new Rect(rect.x, rect.y + 1, SEARCHED_OBJECTS_BORDER_THICKNESS, rect.height),
                                    EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                                GUI.DrawTexture(
                                    new Rect(rect.xMax - SEARCHED_OBJECTS_BORDER_THICKNESS, rect.y + 1, SEARCHED_OBJECTS_BORDER_THICKNESS,
                                        rect.height), EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                            }
                        }

                        GUI.color = guiColor;
                    }
                    else
                    {
                        if (!args.selected)
                        {
                            if (selectedReferenceNodesHierarchyIds.Contains(args.item.id))
                            {
                                EditorGUI.DrawRect(rect, AssetUsageDetectorSettings.SelectedRowParentsTint);
                            }

                            if (data.node.IsMainReference)
                            {
                                EditorGUI.DrawRect(new Rect(rect.x, rect.y, GetContentIndent(args.item) - 1f, rect.height),
                                    AssetUsageDetectorSettings.MainReferencesBackgroundColor);
                            }
                        }
                    }
                }
                else
                {
                    if (!args.selected && data.node.usedState == ReferenceNode.UsedState.Used)
                    {
                        EditorGUI.DrawRect(new Rect(rect.x, rect.y, GetContentIndent(args.item) - 1f, rect.height),
                            AssetUsageDetectorSettings.MainReferencesBackgroundColor);
                    }
                }

                if (!isLMBDown && treeType != TreeType.UnusedObjects && !args.selected && selectedReferenceNodes.Contains(data.node))
                {
                    if (!whiteGradientTexture)
                    {
                        whiteGradientTexture = new Texture2D(2, 1, TextureFormat.RGBA32, false)
                        {
                            hideFlags = HideFlags.HideAndDontSave,
                            alphaIsTransparency = true,
                            filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Clamp
                        };

                        whiteGradientTexture.SetPixels32(new Color32[2] { Color.white, new(255, 255, 255, 0) });
                        whiteGradientTexture.Apply(false, true);
                    }

                    var guiColor = GUI.color;
                    GUI.color = guiColor * AssetUsageDetectorSettings.SelectedRowOccurrencesColor;
                    GUI.DrawTexture(new Rect(GetContentIndent(args.item), rect.y, 125f, rect.height), whiteGradientTexture,
                        ScaleMode.StretchToFill, true, 0f);
                    GUI.color = guiColor;
                }

                if (hoveredData == data)
                {
                    EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.25f));
                }

                if (AssetUsageDetectorSettings.ShowTreeLines && args.item.depth > 0)
                {
                    // I was using EditorGUI.DrawRect here but looking at its source code, it's more performant to call GUI.DrawTexture directly: https://github.com/Unity-Technologies/UnityCsReference/blob/e740821767d2290238ea7954457333f06e952bad/Editor/Mono/GUI/InternalEditorGUI.cs#L246-L255
                    var guiColor = GUI.color;
                    bool shouldHighlightTreeLine;

                    var verticalLineRect =
                        new Rect(rect.x + GetContentIndent(args.item.parent) - (foldoutWidth + TREE_VIEW_LINES_THICKNESS) * 0.5f - 2f, rect.y,
                            TREE_VIEW_LINES_THICKNESS, rect.height);
                    var horizontalLineRect = new Rect(verticalLineRect.x,
                        verticalLineRect.y + (verticalLineRect.height - TREE_VIEW_LINES_THICKNESS) * 0.5f,
                        foldoutWidth + TREE_VIEW_LINES_THICKNESS - 4f, TREE_VIEW_LINES_THICKNESS);

                    for (var parentData = data.parent; parentData.parent != null; parentData = parentData.parent)
                    {
                        if (!parentData.isLastLink)
                        {
                            shouldHighlightTreeLine = selectedReferenceNodesHierarchyIndirectIds.Contains(parentData.item.id);
                            var _verticalLineRect = new Rect(verticalLineRect.x - depthIndentWidth * (args.item.depth - parentData.item.depth),
                                verticalLineRect.y, verticalLineRect.width, verticalLineRect.height);
                            if (shouldHighlightTreeLine)
                            {
                                _verticalLineRect.x -= (HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS - TREE_VIEW_LINES_THICKNESS) * 0.5f;
                                _verticalLineRect.width = HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS;
                            }

                            GUI.color = guiColor * (shouldHighlightTreeLine
                                ? AssetUsageDetectorSettings.HighlightedTreeLinesColor
                                : AssetUsageDetectorSettings.TreeLinesColor);
                            GUI.DrawTexture(_verticalLineRect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                        }
                    }

                    var isInSelectedReferenceNodesHierarchy = selectedReferenceNodesHierarchyIds.Contains(args.item.id);
                    if (isInSelectedReferenceNodesHierarchy)
                    {
                        horizontalLineRect.y -= (HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS - TREE_VIEW_LINES_THICKNESS) * 0.5f;
                        horizontalLineRect.height = HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS;
                    }

                    GUI.color = guiColor * (isInSelectedReferenceNodesHierarchy
                        ? AssetUsageDetectorSettings.HighlightedTreeLinesColor
                        : AssetUsageDetectorSettings.TreeLinesColor);
                    GUI.DrawTexture(horizontalLineRect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);

                    if (data.isLastLink)
                    {
                        verticalLineRect.height = (verticalLineRect.height + TREE_VIEW_LINES_THICKNESS) * 0.5f;
                    }

                    GUI.color = guiColor * AssetUsageDetectorSettings.TreeLinesColor;
                    GUI.DrawTexture(verticalLineRect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);

                    var isInSelectedReferenceNodesIndirectHierarchy = selectedReferenceNodesHierarchyIndirectIds.Contains(args.item.id);
                    if (isInSelectedReferenceNodesHierarchy || isInSelectedReferenceNodesIndirectHierarchy)
                    {
                        GUI.color = guiColor * AssetUsageDetectorSettings.HighlightedTreeLinesColor;

                        if (isInSelectedReferenceNodesHierarchy && !isInSelectedReferenceNodesIndirectHierarchy)
                        {
                            if (!data.isLastLink)
                            {
                                verticalLineRect.height = (verticalLineRect.height + HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS) * 0.5f;
                            }
                            else
                            {
                                verticalLineRect.height += (HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS - TREE_VIEW_LINES_THICKNESS) * 0.5f;
                            }
                        }

                        verticalLineRect.x -= (HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS - TREE_VIEW_LINES_THICKNESS) * 0.5f;
                        verticalLineRect.width = HIGHLIGHTED_TREE_VIEW_LINES_THICKNESS;

                        GUI.DrawTexture(verticalLineRect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f);
                    }

                    GUI.color = guiColor;
                }

                rect.xMin += GetContentIndent(args.item);
                rect.y += AssetUsageDetectorSettings.ExtraRowHeight * 0.5f;
#if !UNITY_2019_3_OR_NEWER
				rect.y -= 2f;
#endif
                rect.height += 4f; // Incrementing height fixes cropped icon issue on Unity 2019.2 or earlier

                if (foldoutLabelStyle == null)
                {
                    foldoutLabelStyle = new GUIStyle(DefaultStyles.foldoutLabel) { richText = true };
                }

                foldoutLabelStyle.Draw(rect, sharedGUIContent, false, false, args.selected && args.focused, args.selected);

                // The only way to support Unity's tooltips seems to be by drawing an invisible GUI.Label over our own label
                if (sharedGUIContent.tooltip != null)
                {
                    sharedGUIContent.text = "";
                    sharedGUIContent.image = null;
                    GUI.Label(rect, sharedGUIContent, foldoutLabelStyle);
                }
            }
            else if (ev.type == EventType.MouseDown)
            {
                if (ev.button == 2 && rect.Contains(ev.mousePosition))
                {
                    HideItems(new int[1] { args.item.id });
                    GUIUtility.ExitGUI();
                }
            }
            else if (ev.type == EventType.MouseMove)
            {
                if (hoveredData != data && AssetUsageDetectorSettings.ShowCustomTooltip && rect.Contains(ev.mousePosition))
                {
                    hoveredData = data;
                    hoveredDataRect = new Rect(GUIUtility.GUIToScreenPoint(rect.position), new Vector2(EditorGUIUtility.currentViewWidth, 0f));
                }
            }
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            if (isTreeViewEmpty)
            {
                return;
            }

            RefreshSelectedNodes(selectedIds);

            if (selectedIds.Count == 0)
            {
                return;
            }

            if (isSearching)
            {
                state.selectionChangedDuringSearch = true;
            }

            Object selection, pingTarget = null;
            var selectedUnityObjects = new List<Object>(selectedIds.Count);
            for (var i = 0; i < selectedIds.Count; i++)
            {
                var obj = GetDataFromId(selectedIds[i]).node.UnityObject;
                if (obj)
                {
                    obj.GetObjectsToSelectAndPing(out selection, out pingTarget);
                    if (selection && !selectedUnityObjects.Contains(selection))
                    {
                        selectedUnityObjects.Add(selection);
                    }
                }
            }

            if (selectedUnityObjects.Count > 0)
            {
                if (AssetUsageDetectorSettings.PingClickedObjects && pingTarget)
                {
                    EditorGUIUtility.PingObject(pingTarget);
                }
                if (AssetUsageDetectorSettings.SelectClickedObjects ||
                    AssetUsageDetectorSettings.SelectDoubleClickedObjects && selectedUnityObjects.Count > 1)
                {
                    Selection.objects = selectedUnityObjects.ToArray();
                }
            }
        }

        protected override void DoubleClickedItem(int id)
        {
            if (isTreeViewEmpty)
            {
                return;
            }

            isLMBDown = false;

            var clickedObject = GetDataFromId(id).node.UnityObject;
#if UNITY_2018_3_OR_NEWER
            if (clickedObject && clickedObject.IsAsset())
            {
                GameObject clickedPrefabRoot = null;
                if (clickedObject is Component)
                {
                    clickedPrefabRoot = ((Component)clickedObject).transform.root.gameObject;
                }
                else if (clickedObject is GameObject)
                {
                    clickedPrefabRoot = ((GameObject)clickedObject).transform.root.gameObject;
                }

                if (clickedPrefabRoot)
                {
                    var prefabAssetType = PrefabUtility.GetPrefabAssetType(clickedPrefabRoot);
                    if (prefabAssetType == PrefabAssetType.Regular || prefabAssetType == PrefabAssetType.Variant)
                    {
                        // Try to open the prefab stage of this prefab
                        var assetPath = AssetDatabase.GetAssetPath(clickedPrefabRoot);
                        var openPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
#if UNITY_2020_1_OR_NEWER
                        if (openPrefabStage == null || !openPrefabStage.stageHandle.IsValid() || assetPath != openPrefabStage.assetPath)
#else
						if( openPrefabStage == null || !openPrefabStage.stageHandle.IsValid() || assetPath != openPrefabStage.prefabAssetPath )
#endif
                        {
                            AssetDatabase.OpenAsset(clickedPrefabRoot);
                        }
                    }
                }
            }
#endif

            // Ping the clicked GameObject in the open prefab stage
            Object selection, pingTarget;
            clickedObject.GetObjectsToSelectAndPing(out selection, out pingTarget);

            if (AssetUsageDetectorSettings.PingClickedObjects && pingTarget)
            {
                EditorGUIUtility.PingObject(pingTarget);
            }
            if (AssetUsageDetectorSettings.SelectDoubleClickedObjects)
            {
                Selection.activeObject = selection;
            }
        }

        protected override void ContextClickedItem(int id)
        {
            ContextClicked();
        }

        protected override void ContextClicked()
        {
            if (!isTreeViewEmpty && HasSelection() && HasFocus())
            {
                var selection = SortItemIDsInRowOrder(GetSelection());

                bool hasAnyDuplicateRows = false, hasAnyRowWithOutgoingLinks = false, hasAnyUnusedMixedCollapsedNode = false;
                for (var i = 0; i < selection.Count; i++)
                {
                    var data = GetDataFromId(selection[i]);
                    if (!hasAnyDuplicateRows && data.isDuplicate)
                    {
                        hasAnyDuplicateRows = true;
                    }
                    if (!hasAnyRowWithOutgoingLinks && data.node.NumberOfOutgoingLinks > 0)
                    {
                        hasAnyRowWithOutgoingLinks = true;
                    }
                    if (!hasAnyUnusedMixedCollapsedNode && data.node.usedState == ReferenceNode.UsedState.MixedCollapsed)
                    {
                        hasAnyUnusedMixedCollapsedNode = true;
                    }
                }

                var contextMenu = new GenericMenu();

                if (treeType != TreeType.IsolatedView)
                {
                    contextMenu.AddItem(new GUIContent("Hide"), false, () => HideItems(selection));
                }

                if (treeType == TreeType.UnusedObjects)
                {
                    if (hasAnyUnusedMixedCollapsedNode)
                    {
                        if (contextMenu.GetItemCount() > 0)
                        {
                            contextMenu.AddSeparator("");
                        }

                        contextMenu.AddItem(new GUIContent("Show Used Children"), false, ShowChildrenOfSelectedUnusedObjects);
                    }
                }
                else
                {
                    if (contextMenu.GetItemCount() > 0)
                    {
                        contextMenu.AddSeparator("");
                    }

                    if (hasAnyDuplicateRows)
                    {
                        contextMenu.AddItem(new GUIContent("Select First Occurrence"), false, SelectFirstOccurrencesOfDuplicateSelection);
                    }

                    contextMenu.AddItem(new GUIContent("Expand All Occurrences"), false, ExpandAllSelectionOccurrences);
                }

                if (hasAnyRowWithOutgoingLinks)
                {
                    if (contextMenu.GetItemCount() > 0)
                    {
                        contextMenu.AddSeparator("");
                    }

                    contextMenu.AddItem(new GUIContent("Show Children In New Window"), false, ShowChildrenOfSelectionInNewWindow);
                }

                contextMenu.ShowAsContext();

                if (Event.current != null && Event.current.type == EventType.ContextClick)
                {
                    Event.current.Use(); // It's safer to eat the event and if we don't, the context menu is sometimes displayed with a delay
                }
            }
        }

        protected override void CommandEventHandling()
        {
            if (!isTreeViewEmpty &&
                HasFocus()) // There may be multiple SearchResultTreeViews. Execute the event only for the currently focused one
            {
                var ev = Event.current;
                if (ev.type == EventType.ValidateCommand || ev.type == EventType.ExecuteCommand)
                {
                    if (ev.commandName == "Delete" || ev.commandName == "SoftDelete")
                    {
                        if (ev.type == EventType.ExecuteCommand)
                        {
                            HideItems(GetSelection());
                        }

                        ev.Use();
                        return;
                    }
                }
            }

            base.CommandEventHandling();
        }

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return true;
        }

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            var draggedItemIds = args.draggedItemIDs;
            if (draggedItemIds.Count == 0)
            {
                return;
            }

            var draggedUnityObjects = new List<Object>(draggedItemIds.Count);
            for (var i = 0; i < draggedItemIds.Count; i++)
            {
                var obj = GetDataFromId(draggedItemIds[i]).node.UnityObject;
                if (obj)
                {
                    draggedUnityObjects.Add(obj);
                }
            }

            if (draggedUnityObjects.Count > 0)
            {
                DragAndDrop.objectReferences = draggedUnityObjects.ToArray();
                DragAndDrop.StartDrag(draggedUnityObjects.Count > 1 ? "<Multiple>" : draggedUnityObjects[0].name);
            }
        }

        public void ExpandDirectReferences()
        {
            var expandedIds = new List<int>(rootItem.children.Count);
            for (var i = 0; i < rootItem.children.Count; i++)
            {
                expandedIds.Add(rootItem.children[i].id);
            }

            SetExpanded(expandedIds);
        }

        public void ExpandMainReferences()
        {
            var expandedIds = new List<int>(references.Count * 12);
            for (var i = 0; i < rootItem.children.Count; i++)
            {
                GetMainReferenceIdsRecursive(rootItem.children[i], expandedIds);
            }

            SetExpanded(expandedIds);
        }

        public void ExpandMatchingSearchResults()
        {
            if (state.searchMode != SearchMode.ReferencesOnly)
            {
                return;
            }

            var expandedIds = new List<int>(references.Count * 12);
            for (var i = 0; i < rootItem.children.Count; i++)
            {
                GetMatchingSearchResultIdsRecursive(rootItem.children[i], expandedIds);
            }

            SetExpanded(expandedIds);
        }

        private void ExpandAllSelectionOccurrences()
        {
            var selection = GetSelection();
            if (selection.Count == 0)
            {
                return;
            }

            var selectedNodes = new HashSet<ReferenceNode>();
            for (var i = selection.Count - 1; i >= 0; i--)
            {
                selectedNodes.Add(GetDataFromId(selection[i]).node);
            }

            var expandedIds = new List<int>(GetExpanded());
            for (var i = 0; i < rootItem.children.Count; i++)
            {
                GetReferenceNodeOccurrenceIdsRecursive(rootItem.children[i], selectedNodes, expandedIds);
            }

            SetExpanded(expandedIds);
        }

        private bool GetMainReferenceIdsRecursive(TreeViewItem item, List<int> ids)
        {
            if (item.depth > 0 && GetDataFromId(item.id).node.IsMainReference)
            {
                return true;
            }

            var shouldExpand = false;
            if (item.hasChildren)
            {
                for (var i = 0; i < item.children.Count; i++)
                {
                    shouldExpand |= GetMainReferenceIdsRecursive(item.children[i], ids);
                }
            }

            if (shouldExpand)
            {
                ids.Add(item.id);
            }

            return shouldExpand;
        }

        private bool GetMatchingSearchResultIdsRecursive(TreeViewItem item, List<int> ids)
        {
            var shouldExpand = false;
            if (item.hasChildren)
            {
                for (var i = 0; i < item.children.Count; i++)
                {
                    shouldExpand |= GetMatchingSearchResultIdsRecursive(item.children[i], ids);
                }
            }

            if (shouldExpand)
            {
                ids.Add(item.id);
            }
            else
            {
                shouldExpand = GetDataFromId(item.id).shouldExpandAfterSearch;
            }

            return shouldExpand;
        }

        private bool GetReferenceNodeOccurrenceIdsRecursive(TreeViewItem item, HashSet<ReferenceNode> referenceNodes, List<int> ids)
        {
            var shouldExpand = false;
            if (item.hasChildren)
            {
                for (var i = 0; i < item.children.Count; i++)
                {
                    shouldExpand |= GetReferenceNodeOccurrenceIdsRecursive(item.children[i], referenceNodes, ids);
                }
            }

            if (shouldExpand)
            {
                if (!ids.Contains(item.id))
                {
                    ids.Add(item.id);
                }

                return true;
            }
            return referenceNodes.Contains(GetDataFromId(item.id).node);
        }

        private void HideItems(IList<int> ids)
        {
            if (ids.Count > 0)
            {
                var hiddenNodes = new List<ReferenceNode>(ids.Count);
                var hiddenLinks = new List<ReferenceNode.Link>(ids.Count);
                var newExpandedItemIDs = new List<int>(32);
                var newSelectedItemIDs = new List<int>(16);

                for (var i = 0; i < ids.Count; i++)
                {
                    var data = GetDataFromId(ids[i]);
                    if (data.item.depth > 0)
                    {
                        hiddenLinks.Add(data.parent.node[data.linkIndex]);
                    }
                    else
                    {
                        hiddenNodes.Add(data.node);
                    }
                }

                var id = state.initialNodeId + 1;
                for (var i = 0; i < rootItem.children.Count; i++)
                {
                    CalculateNewItemIdsAfterHideRecursive(rootItem.children[i], hiddenNodes, hiddenLinks, newExpandedItemIDs,
                        newSelectedItemIDs, ref id);
                }

                for (var i = 0; i < ids.Count; i++)
                {
                    var data = GetDataFromId(ids[i]);
                    if (data.item.depth > 0)
                    {
                        // Can't remove by index here because if multiple sibling nodes are removed at once, the latter sibling nodes' linkIndex
                        // will be different than their actual sibling indices until this TreeView is refreshed
                        data.parent.node.RemoveLink(data.node);
                    }
                    else
                    {
                        references.Remove(data.node);
                    }
                }

                SetSelection(newSelectedItemIDs);
                SetExpanded(newExpandedItemIDs);
                Reload();
            }
        }

        private void CalculateNewItemIdsAfterHideRecursive(TreeViewItem item, List<ReferenceNode> hiddenNodes,
            List<ReferenceNode.Link> hiddenLinks, List<int> newExpandedItemIDs, List<int> newSelectedItemIDs, ref int id)
        {
            var data = GetDataFromId(item.id);
            if (hiddenNodes.Contains(data.node) || data.parent != null && hiddenLinks.Contains(data.parent.node[data.linkIndex]))
            {
                return;
            }

            if (IsExpanded(item.id))
            {
                newExpandedItemIDs.Add(id);
            }
            if (IsSelected(item.id))
            {
                newSelectedItemIDs.Add(id);
            }

            id++;

            if (item.hasChildren)
            {
                for (var i = 0; i < item.children.Count; i++)
                {
                    CalculateNewItemIdsAfterHideRecursive(item.children[i], hiddenNodes, hiddenLinks, newExpandedItemIDs, newSelectedItemIDs,
                        ref id);
                }
            }
        }

        private void SelectFirstOccurrencesOfDuplicateSelection()
        {
            var selection = GetSelection();
            if (selection.Count == 0)
            {
                return;
            }

            var selectedNodes = new HashSet<ReferenceNode>();
            for (var i = selection.Count - 1; i >= 0; i--)
            {
                var data = GetDataFromId(selection[i]);
                if (data.isDuplicate)
                {
                    selectedNodes.Add(data.node);
                }
            }

            var newSelection = new List<int>(selection.Count);
            for (var i = 0; i < rootItem.children.Count; i++)
            {
                FindFirstOccurrencesOfSelectionRecursive(rootItem.children[i], selectedNodes, newSelection);
            }

            if (newSelection.Count > 0)
            {
                SetSelection(newSelection, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);

                if (treeType != TreeType.IsolatedView)
                {
                    EditorWindow.focusedWindow.SendEvent(new Event
                        { type = EventType.KeyDown, keyCode = KeyCode.F }); // To actually frame the row when external scroll view is used
                }
            }
        }

        private void FindFirstOccurrencesOfSelectionRecursive(TreeViewItem item, HashSet<ReferenceNode> selectedNodes, List<int> result)
        {
            var data = GetDataFromId(item.id);
            if (!data.isDuplicate && selectedNodes.Remove(data.node))
            {
                result.Add(item.id);
            }

            if (item.hasChildren)
            {
                for (var i = 0; i < item.children.Count; i++)
                {
                    FindFirstOccurrencesOfSelectionRecursive(item.children[i], selectedNodes, result);
                }
            }
        }

        private void ShowChildrenOfSelectedUnusedObjects()
        {
            var selection = GetSelection();
            if (selection.Count == 0)
            {
                return;
            }

            for (var i = selection.Count - 1; i >= 0; i--)
            {
                var data = GetDataFromId(selection[i]);
                if (data.node.usedState != ReferenceNode.UsedState.MixedCollapsed)
                {
                    continue;
                }

                data.node.usedState = ReferenceNode.UsedState.MixedExpanded;

                var unityObject = data.node.UnityObject;
                if (!unityObject)
                {
                    continue;
                }

                var assetPath = AssetDatabase.GetAssetPath(unityObject);
                if (string.IsNullOrEmpty(assetPath))
                {
                    foreach (var obj in usedObjectsSet)
                    {
                        if (obj && obj is GameObject && obj != unityObject &&
                            ((GameObject)obj).transform.IsChildOf(((GameObject)unityObject).transform))
                        {
                            var childNode = new ReferenceNode { nodeObject = obj };
                            childNode.InitializeRecursively();
                            data.node.AddLinkTo(childNode, "USED");
                        }
                    }
                }
                else
                {
                    foreach (var obj in usedObjectsSet)
                    {
                        if (obj && AssetDatabase.GetAssetPath(obj) == assetPath)
                        {
                            var childNode = new ReferenceNode { nodeObject = obj };
                            childNode.InitializeRecursively();
                            data.node.AddLinkTo(childNode, "USED");
                        }
                    }
                }
            }

            Reload();
        }

        private void ShowChildrenOfSelectionInNewWindow()
        {
            var selection = SortItemIDsInRowOrder(GetSelection());
            if (selection.Count == 0)
            {
                return;
            }

            var selectedNodes = new List<ReferenceNode>(selection.Count);
            for (var i = 0; i < selection.Count; i++)
            {
                var data = GetDataFromId(selection[i]);
                if (data.node.NumberOfOutgoingLinks > 0 && !selectedNodes.Contains(data.node))
                {
                    selectedNodes.Add(data.node);
                }
            }

            if (selectedNodes.Count > 0)
            {
                var isolatedTreeView = new SearchResultTreeView(new SearchResultTreeViewState(), selectedNodes, TreeType.IsolatedView, null,
                    hideDuplicateRows, hideReduntantPrefabVariantLinks, false);
                isolatedTreeView.ExpandMainReferences();

                SearchResultTreeViewIsolatedView.Show(
                    new Vector2(EditorWindow.focusedWindow.position.width, isolatedTreeView.totalHeight + 1f), isolatedTreeView,
                    new GUIContent(selectedNodes[0].Label +
                                   (selectedNodes.Count <= 1 ? "" : " (and " + (selectedNodes.Count - 1) + " more)")));
            }
        }

        private void RefreshSelectedNodes(IList<int> selectedIds)
        {
            selectedReferenceNodes.Clear();
            selectedReferenceNodesHierarchyIds.Clear();
            selectedReferenceNodesHierarchyIndirectIds.Clear();

            for (var i = 0; i < selectedIds.Count; i++)
            {
                var data = GetDataFromId(selectedIds[i]);

                selectedReferenceNodes.Add(data.node);
                selectedReferenceNodesHierarchyIds.Add(selectedIds[i]);

                if (data.item.parent == null)
                {
                    continue;
                }

                var linkItem = data.item;
                for (var parentItem = linkItem.parent; parentItem.depth >= 0; parentItem = parentItem.parent)
                {
                    selectedReferenceNodesHierarchyIds.Add(parentItem.id);

                    var parentItemChildren = parentItem.children;
                    for (var j = 0; parentItemChildren[j] != linkItem; j++)
                    {
                        selectedReferenceNodesHierarchyIndirectIds.Add(parentItemChildren[j].id);
                    }

                    linkItem = parentItem;
                }
            }
        }

        private void ShowTooltipDelayed()
        {
            if (EditorApplication.timeSinceStartup >= customTooltipShowTime)
            {
                EditorApplication.update -= ShowTooltipDelayed;

                if (GetRows().Contains(hoveredData
                        .item)) // Make sure that the hovered item is still a part of the tree (e.g. it might have been removed with middle mouse button)
                {
                    SearchResultTooltip.Show(hoveredDataRect, hoveredData.tooltipText);
                }
            }
        }

        public void CancelDelayedTooltip()
        {
            EditorApplication.update -= ShowTooltipDelayed;
        }

        public void GetRowStateWithId(int id, out bool isFirstRow, out bool isLastRow, out bool isExpanded, out bool canExpand)
        {
            if (isTreeViewEmpty)
            {
                isFirstRow = isLastRow = true;
                isExpanded = canExpand = false;

                return;
            }

            var rows = GetRows();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].id == id)
                {
                    isFirstRow = i <= 0;
                    isLastRow = i >= rows.Count - 1;
                    isExpanded = rows[i].hasChildren && IsExpanded(id);
                    canExpand = rows[i].hasChildren && !IsExpanded(id);

                    return;
                }
            }

            isFirstRow = isLastRow = isExpanded = canExpand = false;
        }

        public bool GetRowRectWithId(int id, out Rect rect)
        {
            var rows = GetRows();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].id == id)
                {
                    rect = GetRowRect(i);
                    return true;
                }
            }

            rect = new Rect();
            return false;
        }

        public Rect SelectFirstRowAndReturnRect()
        {
            SetSelection(new int[1] { GetRows()[0].id }, TreeViewSelectionOptions.FireSelectionChanged);
            return GetRowRect(0);
        }

        public Rect SelectLastRowAndReturnRect()
        {
            var rows = GetRows();
            SetSelection(new int[1] { rows[rows.Count - 1].id }, TreeViewSelectionOptions.FireSelectionChanged);
            return GetRowRect(rows.Count - 1);
        }

        private void HighlightSearchTermInString(StringBuilder sb, string str)
        {
            int prevSearchOccurrenceIndex = 0, searchOccurrenceIndex = 0;
            while ((searchOccurrenceIndex = textComparer.IndexOf(str, state.searchTerm, searchOccurrenceIndex, textCompareOptions)) >= 0)
            {
                sb.Append(str, prevSearchOccurrenceIndex, searchOccurrenceIndex - prevSearchOccurrenceIndex);
                sb.Append(highlightedSearchTextColor).Append("<b>");
                sb.Append(str, searchOccurrenceIndex, state.searchTerm.Length);
                sb.Append("</b>").Append("</color>");

                searchOccurrenceIndex += state.searchTerm.Length;
                prevSearchOccurrenceIndex = searchOccurrenceIndex;
            }

            if (prevSearchOccurrenceIndex < str.Length)
            {
                sb.Append(str, prevSearchOccurrenceIndex, str.Length - prevSearchOccurrenceIndex);
            }
        }

        public void OnSettingsChanged(bool resetHighlightedSearchTextColor, bool resetTooltipDescriptionsTextColor)
        {
            hoveredData = null;

            if (!resetHighlightedSearchTextColor && !resetTooltipDescriptionsTextColor)
            {
                return;
            }

            if (resetHighlightedSearchTextColor)
            {
                highlightedSearchTextColor =
                    "<color=#" + ColorUtility.ToHtmlStringRGBA(AssetUsageDetectorSettings.SearchMatchingTextColor) + ">";
            }

            for (var i = idToNodeDataLookup.Count - 1; i >= 0; i--)
            {
                if (isSearching && resetHighlightedSearchTextColor)
                {
                    idToNodeDataLookup[i].item.displayName = "";
                }
                if (resetTooltipDescriptionsTextColor)
                {
                    idToNodeDataLookup[i].ResetTooltip();
                }
            }
        }
    }

    public class SearchResultTreeViewIsolatedView : EditorWindow
    {
        private bool shouldRepositionSelf = true;
        private SearchResultTreeView treeView;

        private void OnEnable()
        {
            wantsMouseMove = wantsMouseEnterLeaveWindow = true;
        }

        private void OnGUI()
        {
            if (treeView == null) // After domain reload
            {
                Close();
            }
            else
            {
                treeView.OnGUI(GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f));

                if (shouldRepositionSelf)
                {
                    var preferredHeight = GUILayoutUtility.GetLastRect().height;
                    if (preferredHeight > 10f)
                    {
                        var size = position.size;
                        position = Utilities.GetScreenFittedRect(
                            new Rect(GUIUtility.GUIToScreenPoint(Event.current.mousePosition) + new Vector2(size.x * -0.5f, 10f), size));

                        shouldRepositionSelf = false;
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        public static void Show(Vector2 preferredSize, SearchResultTreeView treeView, GUIContent title)
        {
            var window = CreateInstance<SearchResultTreeViewIsolatedView>();
            window.treeView = treeView;
            window.titleContent = title;
            window.ShowAuxWindow();

            window.minSize = new Vector2(150f, Mathf.Min(preferredSize.y, EditorGUIUtility.singleLineHeight * 2f));
            window.position = new Rect(new Vector2(-9999f, -9999f), preferredSize);
            window.Repaint();
        }
    }
}