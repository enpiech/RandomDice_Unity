using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Plugins.AssetUsageDetector.Editor
{
    // Custom class to hold search results
    [Serializable]
    public class SearchResult : IEnumerable<SearchResultGroup>, ISerializationCallbackReceiver
    {
        private SceneSetup[] initialSceneSetup;

        // Each TreeView in the drawn search results must use unique ids for their TreeViewItems. Otherwise, strange things happen: https://forum.unity.com/threads/multiple-editor-treeviews-selection-issue.601471/
        internal int nextTreeViewId = 1;

        private List<SearchResultGroup> result;

        private AssetUsageDetector searchHandler;
        private List<SerializableResultGroup> serializedGroups;

        private List<SerializableNode> serializedNodes;
        private Object[] serializedUsedObjects;

        public SearchResult(bool success, List<SearchResultGroup> result, HashSet<Object> usedObjects, SceneSetup[] initialSceneSetup,
            AssetUsageDetector searchHandler, AssetUsageDetector.Parameters searchParameters)
        {
            if (result == null)
            {
                result = new List<SearchResultGroup>(0);
            }

            SearchCompletedSuccessfully = success;
            this.result = result;
            UsedObjects = usedObjects ?? new HashSet<Object>();
            this.initialSceneSetup = initialSceneSetup;
            this.searchHandler = searchHandler;
            SearchParameters = searchParameters;
        }

        public int NumberOfGroups => result.Count;

        public SearchResultGroup this[int index] => result[index];

        public HashSet<Object> UsedObjects { get; private set; }

        public bool SearchCompletedSuccessfully { get; }

        public bool InitialSceneSetupConfigured => initialSceneSetup != null && initialSceneSetup.Length > 0;

        public bool HasPendingLazySceneSearchResults
        {
            get { return result.Find(group => group.PendingSearch) != null; }
        }

        public AssetUsageDetector.Parameters SearchParameters { get; }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)result).GetEnumerator();
        }

        IEnumerator<SearchResultGroup> IEnumerable<SearchResultGroup>.GetEnumerator()
        {
            return ((IEnumerable<SearchResultGroup>)result).GetEnumerator();
        }

        // Assembly reloading; serialize nodes in a way that Unity can serialize
        // Credit: https://docs.unity3d.com/Manual/script-Serialization-Custom.html
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (result == null)
            {
                return;
            }

            if (serializedGroups == null)
            {
                serializedGroups = new List<SerializableResultGroup>(result.Count);
            }
            else
            {
                serializedGroups.Clear();
            }

            if (serializedNodes == null)
            {
                serializedNodes = new List<SerializableNode>(result.Count * 16);
            }
            else
            {
                serializedNodes.Clear();
            }

            var nodeToIndex = new Dictionary<ReferenceNode, int>(result.Count * 16);
            for (var i = 0; i < result.Count; i++)
            {
                serializedGroups.Add(result[i].Serialize(nodeToIndex, serializedNodes));
            }

            if (serializedUsedObjects == null || serializedUsedObjects.Length != UsedObjects.Count)
            {
                serializedUsedObjects = new Object[UsedObjects.Count];
            }

            UsedObjects.CopyTo(serializedUsedObjects);
        }

        // Assembly reloaded; deserialize nodes to construct the original graph
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (serializedGroups == null || serializedNodes == null || serializedUsedObjects == null)
            {
                return;
            }

            if (result == null)
            {
                result = new List<SearchResultGroup>(serializedGroups.Count);
            }
            else
            {
                result.Clear();
            }

            var allNodes = new List<ReferenceNode>(serializedNodes.Count);
            for (var i = 0; i < serializedNodes.Count; i++)
            {
                allNodes.Add(new ReferenceNode());
            }

            for (var i = 0; i < serializedNodes.Count; i++)
            {
                allNodes[i].Deserialize(serializedNodes[i], allNodes);
            }

            for (var i = 0; i < serializedGroups.Count; i++)
            {
                result.Add(new SearchResultGroup(serializedGroups[i].title, serializedGroups[i].type, serializedGroups[i].isExpanded,
                    serializedGroups[i].pendingSearch));
                result[i].Deserialize(serializedGroups[i], allNodes);
            }

            if (UsedObjects == null)
            {
                UsedObjects = new HashSet<Object>(serializedUsedObjects);
            }
            else
            {
                UsedObjects.UnionWith(serializedUsedObjects);
            }

            serializedNodes.Clear();
            serializedGroups.Clear();
            serializedUsedObjects = null;
        }

        public void RemoveSearchResultGroup(SearchResultGroup searchResultGroup)
        {
            result.Remove(searchResultGroup);
        }

        public void RefreshSearchResultGroup(SearchResultGroup searchResultGroup, bool noAssetDatabaseChanges)
        {
            if (searchResultGroup == null)
            {
                Debug.LogError("SearchResultGroup is null!");
                return;
            }

            var searchResultGroupIndex = result.IndexOf(searchResultGroup);
            if (searchResultGroupIndex < 0)
            {
                Debug.LogError("SearchResultGroup is not a part of SearchResult!");
                return;
            }

            if (searchResultGroup.Type == SearchResultGroup.GroupType.Scene && EditorApplication.isPlaying &&
                !SceneManager.GetSceneByPath(searchResultGroup.ScenePath).isLoaded)
            {
                Debug.LogError("Can't search unloaded scene while in Play Mode!");
                return;
            }

            if (searchHandler == null)
            {
                searchHandler = new AssetUsageDetector();
            }

            var searchInScenes = SearchParameters.searchInScenes;
            var searchInScenesSubset = SearchParameters.searchInScenesSubset;
            var searchInAssetsFolder = SearchParameters.searchInAssetsFolder;
            var searchInAssetsSubset = SearchParameters.searchInAssetsSubset;
            var searchInProjectSettings = SearchParameters.searchInProjectSettings;
            var lazySceneSearch = SearchParameters.lazySceneSearch;
            var calculateUnusedObjects = SearchParameters.calculateUnusedObjects;
            var _noAssetDatabaseChanges = SearchParameters.noAssetDatabaseChanges;

            try
            {
                if (searchResultGroup.Type == SearchResultGroup.GroupType.Assets)
                {
                    SearchParameters.searchInScenes = SceneSearchMode.None;
                    SearchParameters.searchInScenesSubset = null;
                    SearchParameters.searchInProjectSettings = false;
                }
                else if (searchResultGroup.Type == SearchResultGroup.GroupType.ProjectSettings)
                {
                    SearchParameters.searchInScenes = SceneSearchMode.None;
                    SearchParameters.searchInScenesSubset = null;
                    SearchParameters.searchInAssetsFolder = false;
                    SearchParameters.searchInAssetsSubset = null;
                    SearchParameters.searchInProjectSettings = true;
                }
                else if (searchResultGroup.Type == SearchResultGroup.GroupType.Scene)
                {
                    SearchParameters.searchInScenes = SceneSearchMode.None;
                    SearchParameters.searchInScenesSubset = new Object[1]
                        { AssetDatabase.LoadAssetAtPath<SceneAsset>(searchResultGroup.ScenePath) };
                    SearchParameters.searchInAssetsFolder = false;
                    SearchParameters.searchInAssetsSubset = null;
                    SearchParameters.searchInProjectSettings = false;
                }
                else if (searchResultGroup.Type == SearchResultGroup.GroupType.DontDestroyOnLoad)
                {
                    SearchParameters.searchInScenes = (SceneSearchMode)1024; // A unique value to search only the DontDestroyOnLoad scene
                    SearchParameters.searchInScenesSubset = null;
                    SearchParameters.searchInAssetsFolder = false;
                    SearchParameters.searchInAssetsSubset = null;
                    SearchParameters.searchInProjectSettings = false;
                }
                else
                {
                    Debug.LogError("Can't refresh group: " + searchResultGroup.Type);
                    return;
                }

                SearchParameters.lazySceneSearch = false;
                SearchParameters.calculateUnusedObjects =
                    result.Find(group => group.Type == SearchResultGroup.GroupType.UnusedObjects) != null;
                SearchParameters.noAssetDatabaseChanges = noAssetDatabaseChanges;

                // Make sure the AssetDatabase is up-to-date
                AssetDatabase.SaveAssets();

                var searchResult = searchHandler.Run(SearchParameters);
                if (!searchResult.SearchCompletedSuccessfully)
                {
                    EditorUtility.DisplayDialog("Error", "Couldn't refresh, check console for more info.", "OK");
                    return;
                }

                if (searchResult.result != null)
                {
                    var newSearchResultGroup = searchResult.result.Find(group => group.Title == searchResultGroup.Title);
                    if (newSearchResultGroup != null)
                    {
                        result[searchResultGroupIndex] = newSearchResultGroup;
                    }
                    else
                    {
                        searchResultGroup.Clear();
                    }

                    UsedObjects.UnionWith(searchResult.UsedObjects);

                    var unusedObjectsSearchResultGroup = result.Find(group => group.Type == SearchResultGroup.GroupType.UnusedObjects);
                    if (unusedObjectsSearchResultGroup != null)
                    {
                        var newUnusedObjectsSearchResultGroup =
                            searchResult.result.Find(group => group.Type == SearchResultGroup.GroupType.UnusedObjects);
                        if (newUnusedObjectsSearchResultGroup == null)
                        {
                            // UnusedObjects search result group doesn't exist in 2 cases:
                            // - When there are no search results found (NumberOfGroups == 0)
                            // - When all searched objects are referenced (NumberOfGroups > 0)
                            if (searchResult.result.Count > 0)
                            {
                                unusedObjectsSearchResultGroup.Clear();
                            }
                        }
                        else
                        {
                            // NOTE: We can process UnusedObjects graphs iteratively (instead of recursively) because for the time being, these graphs have a maximum depth of 1
                            var unusedObjectsGraphChanged = false;
                            var newUnusedObjectsSet = new HashSet<Object>();
                            for (var i = newUnusedObjectsSearchResultGroup.NumberOfReferences - 1; i >= 0; i--)
                            {
                                var node = newUnusedObjectsSearchResultGroup[i];
                                newUnusedObjectsSet.Add(node.UnityObject);

                                for (var j = node.NumberOfOutgoingLinks - 1; j >= 0; j--)
                                {
                                    newUnusedObjectsSet.Add(node[j].targetNode.UnityObject);
                                }
                            }

                            for (var i = unusedObjectsSearchResultGroup.NumberOfReferences - 1; i >= 0; i--)
                            {
                                var node = unusedObjectsSearchResultGroup[i];
                                var parentNodeRemoved = false;
                                var obj = node.UnityObject;
                                if (!obj || !newUnusedObjectsSet.Contains(obj))
                                {
                                    unusedObjectsSearchResultGroup.RemoveReference(i);
                                    unusedObjectsGraphChanged = parentNodeRemoved = true;
                                }

                                bool hasUnusedSubObjects = false, hasUsedSubObjects = false;
                                var hadSubObjects = node.NumberOfOutgoingLinks > 0;
                                for (var j = 0; j < node.NumberOfOutgoingLinks; j++)
                                {
                                    if (node[j].targetNode.usedState ==
                                        ReferenceNode.UsedState
                                            .Used) // User has explicitly displayed this used child object/sub-asset in the TreeView
                                    {
                                        continue;
                                    }

                                    var _obj = node[j].targetNode.UnityObject;
                                    if (newUnusedObjectsSet.Contains(_obj))
                                    {
                                        hasUnusedSubObjects = true;

                                        if (parentNodeRemoved)
                                        {
                                            unusedObjectsSearchResultGroup.AddReference(node[j].targetNode);
                                        }
                                    }
                                    else if (!parentNodeRemoved)
                                    {
                                        node.RemoveLink(j--);
                                        unusedObjectsGraphChanged = hasUsedSubObjects = true;
                                    }
                                }

                                if (!parentNodeRemoved)
                                {
                                    // When all sub-assets of a main asset are used, consider the main asset as used, as well
                                    if (!hasUnusedSubObjects && hadSubObjects && AssetDatabase.IsMainAsset(obj))
                                    {
                                        unusedObjectsSearchResultGroup.RemoveReference(i);
                                        unusedObjectsGraphChanged = true;
                                    }

                                    if (hasUsedSubObjects && node.usedState == ReferenceNode.UsedState.Unused)
                                    {
                                        node.usedState = ReferenceNode.UsedState.MixedCollapsed;
                                    }
                                }
                            }

                            if (unusedObjectsGraphChanged && unusedObjectsSearchResultGroup.treeView != null)
                            {
                                unusedObjectsSearchResultGroup.treeView.SetSelection(new int[0],
                                    TreeViewSelectionOptions.FireSelectionChanged);
                                unusedObjectsSearchResultGroup.treeViewState.preSearchExpandedIds = null;
                                unusedObjectsSearchResultGroup.treeView.Reload();
                                unusedObjectsSearchResultGroup.treeView.ExpandAll();
                            }
                        }

                        if (unusedObjectsSearchResultGroup.NumberOfReferences == 0)
                        {
                            result.Remove(unusedObjectsSearchResultGroup);
                        }
                    }
                }
            }
            finally
            {
                SearchParameters.searchInScenes = searchInScenes;
                SearchParameters.searchInScenesSubset = searchInScenesSubset;
                SearchParameters.searchInAssetsFolder = searchInAssetsFolder;
                SearchParameters.searchInAssetsSubset = searchInAssetsSubset;
                SearchParameters.searchInProjectSettings = searchInProjectSettings;
                SearchParameters.lazySceneSearch = lazySceneSearch;
                SearchParameters.calculateUnusedObjects = calculateUnusedObjects;
                SearchParameters.noAssetDatabaseChanges = _noAssetDatabaseChanges;
            }
        }

        public float DrawOnGUI(EditorWindow window, float scrollPosition, bool noAssetDatabaseChanges)
        {
            for (var i = 0; i < result.Count; i++)
            {
                scrollPosition = result[i].DrawOnGUI(this, window, scrollPosition, noAssetDatabaseChanges);

                if (i < result.Count - 1)
                {
                    GUILayout.Space(10f);
                }
            }

            return scrollPosition;
        }

        public int IndexOf(SearchResultGroup searchResultGroup)
        {
            return result.IndexOf(searchResultGroup);
        }

        public void CollapseAllSearchResultGroups()
        {
            for (var i = 0; i < result.Count; i++)
            {
                result[i].Collapse();
            }
        }

        public void CancelDelayedTreeViewTooltip()
        {
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i].treeView != null)
                {
                    result[i].treeView.CancelDelayedTooltip();
                }
            }
        }

        // Returns if RestoreInitialSceneSetup will have any effect on the current scene setup
        public bool IsSceneSetupDifferentThanCurrentSetup()
        {
            if (initialSceneSetup == null)
            {
                return false;
            }

            var sceneFinalSetup = EditorSceneManager.GetSceneManagerSetup();
            if (initialSceneSetup.Length != sceneFinalSetup.Length)
            {
                return true;
            }

            for (var i = 0; i < sceneFinalSetup.Length; i++)
            {
                var sceneIsOneOfInitials = false;
                for (var j = 0; j < initialSceneSetup.Length; j++)
                {
                    if (sceneFinalSetup[i].path == initialSceneSetup[j].path)
                    {
                        if (sceneFinalSetup[i].isLoaded != initialSceneSetup[j].isLoaded)
                        {
                            return true;
                        }

                        sceneIsOneOfInitials = true;
                        break;
                    }
                }

                if (!sceneIsOneOfInitials)
                {
                    return true;
                }
            }

            return false;
        }

        // Close the scenes that were not part of the initial scene setup
        // Returns true if initial scene setup is restored successfully
        public bool RestoreInitialSceneSetup()
        {
            if (initialSceneSetup == null || initialSceneSetup.Length == 0)
            {
                return true;
            }

            if (EditorApplication.isPlaying)
            {
                return false;
            }

            if (!IsSceneSetupDifferentThanCurrentSetup())
            {
                return true;
            }

            var sb = Utilities.stringBuilder;
            sb.Length = 0;

            sb.AppendLine("Restore initial scene setup?");
            for (var i = 0; i < initialSceneSetup.Length; i++)
            {
                sb.AppendLine().Append("- ").Append(initialSceneSetup[i].path);
            }

            switch (EditorUtility.DisplayDialogComplex("Asset Usage Detector", sb.ToString(), "Yes", "Cancel", "Leave it as is"))
            {
                case 1: return false;
                case 2: return true;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return false;
            }

            for (var i = 0; i < initialSceneSetup.Length; i++)
            {
                var scene = SceneManager.GetSceneByPath(initialSceneSetup[i].path);
                if (!scene.isLoaded)
                {
                    scene = EditorSceneManager.OpenScene(initialSceneSetup[i].path,
                        initialSceneSetup[i].isLoaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading);
                }

                if (initialSceneSetup[i].isActive)
                {
                    SceneManager.SetActiveScene(scene);
                }
            }

            var sceneFinalSetup = EditorSceneManager.GetSceneManagerSetup();
            for (var i = 0; i < sceneFinalSetup.Length; i++)
            {
                var sceneIsOneOfInitials = false;
                for (var j = 0; j < initialSceneSetup.Length; j++)
                {
                    if (sceneFinalSetup[i].path == initialSceneSetup[j].path)
                    {
                        sceneIsOneOfInitials = true;
                        break;
                    }
                }

                if (!sceneIsOneOfInitials)
                {
                    EditorSceneManager.CloseScene(SceneManager.GetSceneByPath(sceneFinalSetup[i].path), true);
                }
            }

            for (var i = 0; i < initialSceneSetup.Length; i++)
            {
                if (!initialSceneSetup[i].isLoaded)
                {
                    EditorSceneManager.CloseScene(SceneManager.GetSceneByPath(initialSceneSetup[i].path), false);
                }
            }

            initialSceneSetup = null;
            return true;
        }

        [Serializable]
        internal class SerializableResultGroup
        {
            public string title;
            public SearchResultGroup.GroupType type;
            public bool isExpanded;
            public bool pendingSearch;
            public SearchResultTreeViewState treeViewState;

            public List<int> initialSerializedNodes;
        }

        [Serializable]
        internal class SerializableNode
        {
            public string label;
            public int instanceId;
            public bool isUnityObject, isMainReference;
            public ReferenceNode.UsedState usedState;

            public List<int> links;
            public List<SerializableLinkDescriptions> linkDescriptions;
            public List<bool> linkWeakStates;

            [Serializable]
            public class SerializableLinkDescriptions
            {
                public List<string> value;
            }
        }

        internal class SortedEntry : IComparable<SortedEntry>
        {
            public readonly string assetPath, subAssetName;
            public readonly object entry;
            public readonly bool isMainAsset;
            public readonly Transform transform;

            public SortedEntry(ReferenceNode node) : this(node.nodeObject as Object)
            {
                entry = node;
            }

            public SortedEntry(ReferenceNode.Link link) : this(link.targetNode.nodeObject as Object)
            {
                entry = link;
            }

            private SortedEntry(Object obj)
            {
                if (obj)
                {
                    assetPath = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        assetPath = null;

                        if (obj is Component)
                        {
                            transform = ((Component)obj).transform;
                        }
                        else if (obj is GameObject)
                        {
                            transform = ((GameObject)obj).transform;
                        }
                    }
                    else
                    {
                        isMainAsset = AssetDatabase.IsMainAsset(obj is Component ? ((Component)obj).gameObject : obj);
                        if (!isMainAsset)
                        {
                            subAssetName = obj.name;
                        }
                    }
                }
            }

            // Sorting order:
            // 1) Scene objects come first and are sorted by their absolute sibling indices in Hierarchy
            // 2) Assets come later and are sorted by their asset paths (for assets sharing the same path, main assets come first and sub-assets are then sorted by their names)
            // 3) Regular C# objects come last
            int IComparable<SortedEntry>.CompareTo(SortedEntry other)
            {
                if (this == other)
                {
                    return 0;
                }
                if (assetPath == null)
                {
                    if (!transform)
                    {
                        return 1;
                    }
                    if (other.assetPath == null)
                    {
                        return other.transform ? Utilities.CompareHierarchySiblingIndices(transform, other.transform) : -1;
                    }
                    return -1;
                }
                if (other.assetPath == null)
                {
                    return other.transform ? 1 : -1;
                }
                var assetPathComparison = EditorUtility.NaturalCompare(assetPath, other.assetPath);
                if (assetPathComparison != 0)
                {
                    return assetPathComparison;
                }
                if (isMainAsset)
                {
                    return -1;
                }
                if (other.isMainAsset)
                {
                    return 1;
                }
                return subAssetName.CompareTo(other.subAssetName);
            }
        }
    }

    // Custom class to hold the results for a single scene or Assets folder
    public class SearchResultGroup : IEnumerable<ReferenceNode>
    {
        public enum GroupType
        {
            Assets = 0,
            Scene = 1,
            DontDestroyOnLoad = 2,
            ProjectSettings = 3,
            UnusedObjects = 4
        }

        private readonly List<ReferenceNode> references = new();
        private Rect lastTreeViewRect;

        internal SearchResultTreeView treeView;
        private SearchField treeViewSearchField;
        internal SearchResultTreeViewState treeViewState;

        public SearchResultGroup(string title, GroupType type, bool isExpanded = true, bool pendingSearch = false)
        {
            Title = title.StartsWith("<b>") ? title : string.Concat("<b>", title, "</b>");
            ScenePath = type != GroupType.Scene ? null : title.StartsWith("<b>") ? title.Substring(3, title.Length - 7) : title;
            Type = type;
            IsExpanded = isExpanded;
            PendingSearch = pendingSearch;
        }

        public string Title { get; }

        public GroupType Type { get; }

        public string ScenePath { get; }

        public bool IsExpanded { get; private set; }

        public bool PendingSearch { get; private set; }

        public int NumberOfReferences => references.Count;

        public ReferenceNode this[int index] => references[index];

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)references).GetEnumerator();
        }

        IEnumerator<ReferenceNode> IEnumerable<ReferenceNode>.GetEnumerator()
        {
            return ((IEnumerable<ReferenceNode>)references).GetEnumerator();
        }

        public void AddReference(ReferenceNode node)
        {
            references.Add(node);
        }

        public void RemoveReference(int index)
        {
            references.RemoveAt(index);
        }

        // Removes all nodes
        public void Clear()
        {
            PendingSearch = false;

            references.Clear();

            treeView = null;
            treeViewState = null;
            treeViewSearchField = null;
        }

        public void Collapse()
        {
            IsExpanded = false;
        }

        // Initializes commonly used variables of the nodes
        public void InitializeNodes(HashSet<Object> objectsToSearchSet)
        {
            var _references = new List<ReferenceNode>(references);
            references.Clear();

            // Reverse the links of the search results graph so that the root ReferenceNodes are the searched objects
            var reverseGraphNodes = new Dictionary<ReferenceNode, ReferenceNode>(references.Count * 16);
            for (var i = 0; i < _references.Count; i++)
            {
                _references[i].CreateReverseGraphRecursively(this, references, reverseGraphNodes, objectsToSearchSet);
            }

            // Remove weak links if they aren't ultimately connected to a non-weak link
            var visitedNodes = new HashSet<ReferenceNode>();
            for (var i = references.Count - 1; i >= 0; i--)
            {
                references[i].RemoveRedundantLinksRecursively(visitedNodes);
            }

            // When a GameObject is a root node, then any components of that GameObject that are also root nodes should omit their links to the
            // GameObject's node because otherwise, search results are filled with redundant 'GameObject->Its Component' references
            var rootGameObjectNodes = new HashSet<ReferenceNode>();
            for (var i = references.Count - 1; i >= 0; i--)
            {
                if (references[i].nodeObject as GameObject)
                {
                    rootGameObjectNodes.Add(references[i]);
                }
            }

            for (var i = references.Count - 1; i >= 0; i--)
            {
                var node = references[i];
                var component = node.nodeObject as Component;
                if (component)
                {
                    for (var j = node.NumberOfOutgoingLinks - 1; j >= 0; j--)
                    {
                        if (ReferenceEquals(node[j].targetNode.nodeObject, component.gameObject) &&
                            rootGameObjectNodes.Contains(node[j].targetNode))
                        {
                            node.RemoveLink(j);
                            break;
                        }
                    }
                }
            }

            // Remove root nodes that don't have any outgoing links
            for (var i = references.Count - 1; i >= 0; i--)
            {
                if (references[i].NumberOfOutgoingLinks == 0)
                {
                    references.RemoveAt(i);
                }
            }

            // Sort root nodes
            if (references.Count > 1)
            {
                var sortedEntries = new SearchResult.SortedEntry[references.Count];
                for (var i = references.Count - 1; i >= 0; i--)
                {
                    sortedEntries[i] = new SearchResult.SortedEntry(references[i]);
                }

                Array.Sort(sortedEntries);

                for (var i = 0; i < sortedEntries.Length; i++)
                {
                    references[i] = (ReferenceNode)sortedEntries[i].entry;
                }
            }

            for (var i = references.Count - 1; i >= 0; i--)
            {
                references[i].SortLinks(); // Sort immediate links of the root nodes
                references[i].InitializeRecursively();
            }
        }

        // Draw the results found for this container
        public float DrawOnGUI(SearchResult searchResult, EditorWindow window, float scrollPosition, bool noAssetDatabaseChanges)
        {
            var ev = Event.current;
            var c = GUI.backgroundColor;

            var headerHeight = EditorGUIUtility.singleLineHeight * 2f;
            var refreshButtonWidth = 100f;

            GUI.backgroundColor = AssetUsageDetectorSettings.SearchResultGroupHeaderColor;

            var headerRect = EditorGUILayout.GetControlRect(false, headerHeight);
            var width = headerRect.width;
            headerRect.width = headerHeight;
            if (GUI.Button(headerRect, IsExpanded ? "v" : ">"))
            {
                IsExpanded = !IsExpanded;
                if (ev.alt && treeView != null)
                {
                    if (!IsExpanded)
                    {
                        treeView.CollapseAll();
                    }
                    else
                    {
                        treeView.ExpandAll();
                    }
                }

                window.Repaint();
                GUIUtility.ExitGUI();
            }

            headerRect.x += headerHeight;
            headerRect.width = width - (searchResult != null ? refreshButtonWidth + headerHeight : headerHeight);

            if (GUI.Button(headerRect, Title, Utilities.BoxGUIStyle))
            {
                if (ev.button != 1)
                {
                    if (Type == GroupType.Scene)
                    {
                        // If the container (scene, usually) is left clicked, highlight it on Project view
                        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
                        if (sceneAsset)
                        {
                            if (AssetUsageDetectorSettings.PingClickedObjects)
                            {
                                EditorGUIUtility.PingObject(sceneAsset);
                            }
                            if (AssetUsageDetectorSettings.SelectClickedObjects)
                            {
                                Selection.activeObject = sceneAsset;
                            }
                        }
                    }
                }
                else
                {
                    var contextMenu = new GenericMenu();

                    if (searchResult != null)
                    {
                        contextMenu.AddItem(new GUIContent("Hide"), false, () => searchResult.RemoveSearchResultGroup(this));
                    }

                    if (references.Count > 0 && treeView != null)
                    {
                        if (contextMenu.GetItemCount() > 0)
                        {
                            contextMenu.AddSeparator("");
                        }

                        if (Type != GroupType.UnusedObjects)
                        {
                            contextMenu.AddItem(new GUIContent("Expand Direct References Only"), false, () =>
                            {
                                treeView.ExpandDirectReferences();
                                IsExpanded = true;
                            });

                            contextMenu.AddItem(new GUIContent("Expand Main References Only"), false, () =>
                            {
                                treeView.ExpandMainReferences();
                                IsExpanded = true;
                            });
                        }

                        if (!string.IsNullOrEmpty(treeViewState.searchTerm) &&
                            treeViewState.searchMode == SearchResultTreeView.SearchMode.ReferencesOnly)
                        {
                            contextMenu.AddItem(new GUIContent("Expand Matching Search Results Only"), false, () =>
                            {
                                treeView.ExpandMatchingSearchResults();
                                IsExpanded = true;
                            });
                        }

                        contextMenu.AddItem(new GUIContent("Expand All"), false, () =>
                        {
                            treeView.ExpandAll();
                            IsExpanded = true;
                        });

                        contextMenu.AddItem(new GUIContent("Collapse All"), false, () =>
                        {
                            treeView.CollapseAll();
                            IsExpanded = true;
                        });

                        if (searchResult != null && searchResult.NumberOfGroups > 1 && !string.IsNullOrEmpty(treeViewState.searchTerm))
                        {
                            if (contextMenu.GetItemCount() > 0)
                            {
                                contextMenu.AddSeparator("");
                            }

                            contextMenu.AddItem(new GUIContent("Apply Search to All Results"), false, () =>
                            {
                                for (var i = 0; i < searchResult.NumberOfGroups; i++)
                                {
                                    if (searchResult[i].treeView == null)
                                    {
                                        continue;
                                    }

                                    var previousSearchTerm = searchResult[i].treeViewState.searchTerm ?? "";
                                    var previousSearchMode = searchResult[i].treeViewState.searchMode;

                                    searchResult[i].treeViewState.searchTerm = treeViewState.searchTerm ?? "";
                                    searchResult[i].treeViewState.searchMode = treeViewState.searchMode;

                                    if (treeViewState.searchTerm != previousSearchTerm || treeViewState.searchMode != previousSearchMode)
                                    {
                                        searchResult[i].treeView.RefreshSearch(previousSearchTerm);
                                    }
                                }
                            });
                        }
                    }

                    if (Type == GroupType.Scene && !EditorApplication.isPlaying && EditorSceneManager.loadedSceneCount > 1)
                    {
                        // Show context menu when SearchResultGroup's header is right clicked
                        var scene = SceneManager.GetSceneByPath(ScenePath);
                        if (scene.isLoaded)
                        {
                            if (contextMenu.GetItemCount() > 0)
                            {
                                contextMenu.AddSeparator("");
                            }

                            contextMenu.AddItem(new GUIContent("Close Scene"), false, () =>
                            {
                                if (!scene.isDirty || EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new Scene[1] { scene }))
                                {
                                    EditorSceneManager.CloseScene(scene, true);
                                }
                            });
                        }
                    }

                    contextMenu.ShowAsContext();
                }
            }

            if (searchResult != null)
            {
                var guiEnabled = GUI.enabled;
                GUI.enabled = Type != GroupType.UnusedObjects;

                headerRect.x += width - (refreshButtonWidth + headerHeight);
                headerRect.width = refreshButtonWidth;
                if (GUI.Button(headerRect, "Refresh"))
                {
                    searchResult.RefreshSearchResultGroup(this, noAssetDatabaseChanges);
                    GUIUtility.ExitGUI();
                }

                GUI.enabled = guiEnabled;
            }

            GUI.backgroundColor = c;

            if (IsExpanded)
            {
                if (PendingSearch)
                {
                    GUILayout.Box("Lazy Search: this scene potentially has some references, hit Refresh to find them", Utilities.BoxGUIStyle);
                }
                else if (references.Count == 0)
                {
                    GUILayout.Box(Type == GroupType.UnusedObjects ? "No unused objects left..." : "No references found...",
                        Utilities.BoxGUIStyle);
                }
                else
                {
                    if (Type == GroupType.UnusedObjects)
                    {
                        if (searchResult != null && searchResult.HasPendingLazySceneSearchResults)
                        {
                            EditorGUILayout.HelpBox(
                                "Some scene(s) aren't searched yet (lazy scene search). Refreshing those scene(s) will automatically update this list.",
                                MessageType.Warning);
                        }

                        if (searchResult != null && searchResult.SearchParameters.dontSearchInSourceAssets &&
                            searchResult.SearchParameters.objectsToSearch.Length > 1)
                        {
                            EditorGUILayout.HelpBox(
                                "'Don't search \"SEARCHED OBJECTS\" themselves for references' is enabled, some of these objects might be used by \"SEARCHED OBJECTS\".",
                                MessageType.Warning);
                        }

                        EditorGUILayout.HelpBox(
                            "Although no references to these objects are found, they might still be used somewhere (e.g. via Resources.Load). If you intend to delete these objects, consider creating a backup of your project first.",
                            MessageType.Info);
                    }

                    if (treeView == null)
                    {
                        var isFirstInitialization = treeViewState == null;
                        if (isFirstInitialization)
                        {
                            treeViewState = new SearchResultTreeViewState();
                        }

                        // This isn't inside isFirstInitialization because SearchResultTreeViewState might have been initialized by
                        // Unity's serialization system after a domain reload
                        var shouldUpdateInitialTreeViewNodeId = treeViewState.initialNodeId == 0 && searchResult != null;
                        if (shouldUpdateInitialTreeViewNodeId)
                        {
                            treeViewState.initialNodeId = searchResult.nextTreeViewId;
                        }

                        treeView = new SearchResultTreeView(treeViewState, references,
                            Type == GroupType.UnusedObjects
                                ? SearchResultTreeView.TreeType.UnusedObjects
                                : SearchResultTreeView.TreeType.Normal, searchResult != null ? searchResult.UsedObjects : null,
                            searchResult != null && searchResult.SearchParameters.hideDuplicateRows,
                            searchResult != null && searchResult.SearchParameters.hideReduntantPrefabVariantLinks, true);

                        if (isFirstInitialization)
                        {
                            if (Type != GroupType.UnusedObjects)
                            {
                                treeView.ExpandMainReferences();
                            }
                            else
                            {
                                treeView.ExpandAll();
                            }
                        }

                        if (shouldUpdateInitialTreeViewNodeId)
                        {
                            searchResult.nextTreeViewId = treeViewState.finalNodeId;
                        }
                    }

                    if (treeViewSearchField == null)
                    {
                        treeViewSearchField = new SearchField { autoSetFocusOnFindCommand = false };
                        treeViewSearchField.downOrUpArrowKeyPressed +=
                            () => treeView
                                .SetFocusAndEnsureSelectedItem(); // Not assigning SetFocusAndEnsureSelectedItem directly in case treeView's value changes
                    }

                    var searchFieldRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                    var previousSearchTerm = treeViewState.searchTerm ?? "";
                    var previousSearchMode = treeViewState.searchMode;
                    treeViewState.searchTerm =
                        treeViewSearchField.OnToolbarGUI(
                            new Rect(searchFieldRect.x, searchFieldRect.y, searchFieldRect.width - 100f, searchFieldRect.height),
                            treeViewState.searchTerm) ?? "";
                    treeViewState.searchMode = (SearchResultTreeView.SearchMode)EditorGUI.EnumPopup(
                        new Rect(searchFieldRect.xMax - 100f, searchFieldRect.y, 100f, searchFieldRect.height), treeViewState.searchMode);
                    if (treeViewState.searchTerm != previousSearchTerm || treeViewState.searchMode != previousSearchMode)
                    {
                        treeView.RefreshSearch(previousSearchTerm);
                    }

                    var pressedKeyboardNavigationKey = KeyCode.None;
                    var treeViewKeyboardNavigation = false;
                    if (ev.type == EventType.KeyDown)
                    {
                        pressedKeyboardNavigationKey = ev.keyCode;
                        switch (pressedKeyboardNavigationKey)
                        {
                            case KeyCode.UpArrow:
                            case KeyCode.DownArrow:
                            case KeyCode.LeftArrow:
                            case KeyCode.RightArrow:
                            case KeyCode.PageUp:
                            case KeyCode.PageDown:
                            case KeyCode.Home:
                            case KeyCode.End:
                            case KeyCode.F:
                                treeViewKeyboardNavigation = true;
                                break;
                        }

                        SearchResultTooltip.Hide();
                    }
                    else if ((ev.type == EventType.ValidateCommand || ev.type == EventType.ExecuteCommand) && ev.commandName == "Find" &&
                             treeView.HasFocus())
                    {
                        if (ev.type == EventType.ExecuteCommand)
                        {
                            treeViewSearchField.SetFocus();

                            // Framed rect padding: Top = 2, Bottom = 2 + the first element in the TreeView
                            scrollPosition = FrameRectInScrollView(scrollPosition,
                                new Vector2(searchFieldRect.y - 2f, searchFieldRect.yMax + EditorGUIUtility.singleLineHeight + 2f),
                                window.position.height);
                            window.Repaint();

                            SearchResultTooltip.Hide();
                        }

                        ev.Use();
                    }
                    else if (ev.type == EventType.ScrollWheel)
                    {
                        SearchResultTooltip.Hide();
                    }

                    bool isFirstRowSelected = false, isLastRowSelected = false, isSelectedRowExpanded = false, canExpandSelectedRow = false;
                    if (treeViewKeyboardNavigation && treeView.HasFocus() && treeView.HasSelection())
                    {
                        treeView.GetRowStateWithId(treeViewState.lastClickedID, out isFirstRowSelected, out isLastRowSelected,
                            out isSelectedRowExpanded, out canExpandSelectedRow);
                    }

                    var treeViewRect = EditorGUILayout.GetControlRect(false, treeView.totalHeight);
                    if (ev.type == EventType.Repaint)
                    {
                        lastTreeViewRect = treeViewRect;

#if !UNITY_2018_2_OR_NEWER
						// TreeView calls RowGUI for all rows instead of only the visible rows on early Unity versions which leads to performance issues. Do manual row culling on those versions
						// Credit: https://github.com/Unity-Technologies/UnityCsReference/blob/a048de916b23331bf6dfe92c4a6c205989b83b4f/Editor/Mono/GUI/TreeView/TreeViewGUI.cs#L273-L276
						float topPixel = scrollPosition - treeViewRect.y;
						float heightInPixels = window.position.height;
						treeView.visibleRowTop = (int) Mathf.Floor( topPixel / treeView.rowHeight );
						treeView.visibleRowBottom = treeView.visibleRowTop + (int) Mathf.Ceil( heightInPixels / treeView.rowHeight );
#endif
                    }

                    treeView.OnGUI(treeViewRect);

                    if (treeViewKeyboardNavigation && treeView.HasFocus() && treeView.HasSelection())
                    {
                        Rect targetTreeViewRowRect;
                        if (treeView.GetRowRectWithId(treeViewState.lastClickedID, out targetTreeViewRowRect))
                        {
                            // Allow keyboard navigation between different SearchResultGroups' TreeViews
                            var targetTreeViewRect = lastTreeViewRect;
                            if (!ev.control && !ev.command && !ev.shift)
                            {
                                if (isFirstRowSelected && (pressedKeyboardNavigationKey == KeyCode.UpArrow ||
                                                           pressedKeyboardNavigationKey == KeyCode.PageUp ||
                                                           pressedKeyboardNavigationKey == KeyCode.Home ||
                                                           pressedKeyboardNavigationKey == KeyCode.LeftArrow && !isSelectedRowExpanded))
                                {
                                    var searchResultGroupIndex = searchResult.IndexOf(this);
                                    for (var i = searchResultGroupIndex - 1; i >= 0; i--)
                                    {
                                        if (!searchResult[i].PendingSearch && searchResult[i].IsExpanded &&
                                            searchResult[i].references.Count > 0)
                                        {
                                            searchResult[i].treeView.SetFocus();

                                            targetTreeViewRect = searchResult[i].lastTreeViewRect;
                                            targetTreeViewRowRect = searchResult[i].treeView.SelectLastRowAndReturnRect();

                                            break;
                                        }
                                    }
                                }
                                else if (isLastRowSelected && (pressedKeyboardNavigationKey == KeyCode.DownArrow ||
                                                               pressedKeyboardNavigationKey == KeyCode.PageDown ||
                                                               pressedKeyboardNavigationKey == KeyCode.End ||
                                                               pressedKeyboardNavigationKey == KeyCode.RightArrow && !canExpandSelectedRow))
                                {
                                    var searchResultGroupIndex = searchResult.IndexOf(this);
                                    for (var i = searchResultGroupIndex + 1; i < searchResult.NumberOfGroups; i++)
                                    {
                                        if (!searchResult[i].PendingSearch && searchResult[i].IsExpanded &&
                                            searchResult[i].references.Count > 0)
                                        {
                                            searchResult[i].treeView.SetFocus();

                                            targetTreeViewRect = searchResult[i].lastTreeViewRect;
                                            targetTreeViewRowRect = searchResult[i].treeView.SelectFirstRowAndReturnRect();

                                            break;
                                        }
                                    }
                                }
                            }

                            // When key event isn't automatically used by the focused TreeView (happens when its search results are empty), if we navigate to
                            // a new TreeView, key event will be consumed by that TreeView and hence, keyboard navigation will occur twice
                            if (ev.type != EventType.Used)
                            {
                                ev.Use();
                            }

                            var scrollTop = targetTreeViewRect.y + targetTreeViewRowRect.y;
                            var scrollBottom = targetTreeViewRect.y + targetTreeViewRowRect.yMax;

                            scrollPosition = FrameRectInScrollView(scrollPosition, new Vector2(scrollTop, scrollBottom),
                                window.position.height);
                            window.Repaint();
                        }
                    }
                }
            }

            return scrollPosition;
        }

        // Frame selection (it isn't handled automatically when using an external scroll view)
        // Credit: https://github.com/Unity-Technologies/UnityCsReference/blob/d0fe81a19ce788fd1d94f826cf797aafc37db8ea/Editor/Mono/GUI/TreeView/TreeViewController.cs#L1329-L1351
        private float FrameRectInScrollView(float scrollPosition, Vector2 rectBounds, float windowHeight)
        {
            return Mathf.Clamp(scrollPosition, rectBounds.y - windowHeight, rectBounds.x);
        }

        // Serialize this result group
        internal SearchResult.SerializableResultGroup Serialize(Dictionary<ReferenceNode, int> nodeToIndex,
            List<SearchResult.SerializableNode> serializedNodes)
        {
            var serializedResultGroup = new SearchResult.SerializableResultGroup
            {
                title = Title,
                type = Type,
                isExpanded = IsExpanded,
                pendingSearch = PendingSearch,
                treeViewState = treeViewState
            };

            if (references != null)
            {
                serializedResultGroup.initialSerializedNodes = new List<int>(references.Count);

                for (var i = 0; i < references.Count; i++)
                {
                    serializedResultGroup.initialSerializedNodes.Add(references[i].SerializeRecursively(nodeToIndex, serializedNodes));
                }
            }

            return serializedResultGroup;
        }

        // Deserialize this result group from the serialized data
        internal void Deserialize(SearchResult.SerializableResultGroup serializedResultGroup, List<ReferenceNode> allNodes)
        {
            treeViewState = serializedResultGroup.treeViewState;

            if (serializedResultGroup.initialSerializedNodes != null)
            {
                for (var i = 0; i < serializedResultGroup.initialSerializedNodes.Count; i++)
                {
                    references.Add(allNodes[serializedResultGroup.initialSerializedNodes[i]]);
                }
            }
        }
    }

    // Custom class to hold an object in the path to a reference as a node
    public class ReferenceNode
    {
        // Unique identifier is used while serializing the node
        private static int uid_last;

        private readonly List<Link> links = new(2);
        private readonly int uid;
        private int? instanceId; // instanceId of the nodeObject if it is a Unity object, null otherwise

        internal object nodeObject;

        internal UsedState usedState;

        public ReferenceNode()
        {
            uid = uid_last++;
            usedState = UsedState.Used;
        }

        public string Label { get; private set; }

        public bool
            IsMainReference
        {
            get;
            private set;
        } // True: if belongs to a scene search result group, then it's an object in that scene. If belongs to the assets search result group, then it's an asset

        public Object UnityObject => instanceId.HasValue ? EditorUtility.InstanceIDToObject(instanceId.Value) : null;

        public int NumberOfOutgoingLinks => links.Count;

        public Link this[int index] => links[index];

        // Add a one-way connection to another node
        public void AddLinkTo(ReferenceNode nextNode, string description = null, bool isWeakLink = false)
        {
            if (nextNode != null && nextNode != this)
            {
                if (!string.IsNullOrEmpty(description))
                {
                    description = "[" + description + "]";
                }

                // Avoid duplicate links
                for (var i = 0; i < links.Count; i++)
                {
                    if (links[i].targetNode == nextNode)
                    {
                        if (!string.IsNullOrEmpty(description) && !links[i].descriptions.Contains(description))
                        {
                            links[i].descriptions.Add(description);
                        }

                        links[i].isWeakLink &= isWeakLink;
                        return;
                    }
                }

                links.Add(new Link(nextNode, description, isWeakLink));
            }
        }

        public void RemoveLink(int index)
        {
            links.RemoveAt(index);
        }

        public bool RemoveLink(ReferenceNode nextNode)
        {
            for (var i = links.Count - 1; i >= 0; i--)
            {
                if (links[i].targetNode == nextNode)
                {
                    links.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void SortLinks()
        {
            if (links.Count > 1)
            {
                var sortedEntries = new SearchResult.SortedEntry[links.Count];
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    sortedEntries[i] = new SearchResult.SortedEntry(links[i]);
                }

                Array.Sort(sortedEntries);

                for (var i = 0; i < sortedEntries.Length; i++)
                {
                    links[i] = (Link)sortedEntries[i].entry;
                }
            }
        }

        internal bool HasLinkToObjectWithDescriptions(int instanceId, List<string> descriptions)
        {
            for (var i = links.Count - 1; i >= 0; i--)
            {
                var link = links[i];
                if (link.targetNode.instanceId == instanceId)
                {
                    var _descriptions = link.descriptions;
                    if (_descriptions.Count != descriptions.Count)
                    {
                        return false;
                    }

                    for (var j = _descriptions.Count - 1; j >= 0; j--)
                    {
                        if (_descriptions[j] != descriptions[j])
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        public void CopyReferencesTo(ReferenceNode other)
        {
            other.links.Clear();
            other.links.AddRange(links);
        }

        // Clear this node so that it can be reused later
        public void Clear()
        {
            nodeObject = null;
            links.Clear();
        }

        public void InitializeRecursively()
        {
            if (Label != null) // Already initialized
            {
                return;
            }

            var unityObject = nodeObject as Object;
            if (unityObject != null)
            {
                instanceId = unityObject.GetInstanceID();
                Label = unityObject.name + " (" + unityObject.GetType().Name + ")";
            }
            else if (nodeObject != null)
            {
                instanceId = null;
                Label = nodeObject.GetType() + " object";
            }
            else
            {
                instanceId = null;
                Label = "<<destroyed>>";
            }

            nodeObject = null; // Don't hold Object reference, allow Unity to GC used memory

            for (var i = 0; i < links.Count; i++)
            {
                links[i].targetNode.InitializeRecursively();
            }
        }

        public ReferenceNode CreateReverseGraphRecursively(SearchResultGroup searchResultGroup, List<ReferenceNode> reverseGraphRoots,
            Dictionary<ReferenceNode, ReferenceNode> reverseGraphNodes, HashSet<Object> objectsToSearchSet)
        {
            ReferenceNode result;
            if (!reverseGraphNodes.TryGetValue(this, out result))
            {
                reverseGraphNodes[this] = result = new ReferenceNode { nodeObject = nodeObject };

                var obj = nodeObject as Object;
                if (obj && objectsToSearchSet.Contains(obj))
                {
                    reverseGraphRoots.Add(result);
                }
                //else // When 'else' is uncommented, 'Don't search "Find referenced of" themselves for references" option simply does nothing. I am not entirely sure if commenting it out will have any side effects, so fingers crossed?
                {
                    for (var i = 0; i < links.Count; i++)
                    {
                        var linkedNode = links[i].targetNode.CreateReverseGraphRecursively(searchResultGroup, reverseGraphRoots,
                            reverseGraphNodes, objectsToSearchSet);
                        linkedNode.links.Add(new Link(result, links[i].descriptions, links[i].isWeakLink));
                    }
                }

                if (obj)
                {
                    if (obj is Component)
                    {
                        obj = ((Component)obj).gameObject;
                    }

                    switch (searchResultGroup.Type)
                    {
                        case SearchResultGroup.GroupType.Assets:
                            result.IsMainReference = obj.IsAsset() && (obj is GameObject ||
                                                                       (obj.hideFlags & (HideFlags.HideInInspector |
                                                                                         HideFlags.HideInHierarchy)) == HideFlags.None);
                            break;
                        case SearchResultGroup.GroupType.ProjectSettings:
                            result.IsMainReference = obj.IsAsset() && AssetDatabase.GetAssetPath(obj).StartsWith("ProjectSettings/");
                            break;
                        case SearchResultGroup.GroupType.Scene:
                        case SearchResultGroup.GroupType.DontDestroyOnLoad:
                        {
                            if (obj is GameObject)
                            {
                                var scene = ((GameObject)obj).scene;
                                if (scene.IsValid())
                                {
                                    result.IsMainReference = searchResultGroup.Type == SearchResultGroup.GroupType.Scene
                                        ? scene.path == searchResultGroup.ScenePath
                                        : scene.name == "DontDestroyOnLoad";
                                }
                            }

                            break;
                        }
                    }
                }
            }

            return result;
        }

        public void RemoveRedundantLinksRecursively(HashSet<ReferenceNode> visitedNodes)
        {
            if (!visitedNodes.Add(this))
            {
                return;
            }

            List<ReferenceNode> stack = null;
            for (var i = links.Count - 1; i >= 0; i--)
            {
                if (!links[i].isWeakLink)
                {
                    continue;
                }

                if (links[i].targetNode.links.Count == 0)
                {
                    links.RemoveAt(i);
                }
                else
                {
                    if (stack == null)
                    {
                        stack = new List<ReferenceNode>(2);
                    }
                    else
                    {
                        stack.Clear();
                    }

                    if (!links[i].targetNode.CheckForNonWeakLinksRecursively(stack))
                    {
                        links.RemoveAt(i);
                    }
                }
            }

            for (var i = links.Count - 1; i >= 0; i--)
            {
                links[i].targetNode.RemoveRedundantLinksRecursively(visitedNodes);
            }
        }

        private bool CheckForNonWeakLinksRecursively(List<ReferenceNode> stack)
        {
            if (stack.Contains(this) || links.Count == 0)
            {
                return false;
            }

            for (var i = links.Count - 1; i >= 0; i--)
            {
                if (!links[i].isWeakLink)
                {
                    return true;
                }
            }

            stack.Add(this);

            for (var i = links.Count - 1; i >= 0; i--)
            {
                if (links[i].targetNode.CheckForNonWeakLinksRecursively(stack))
                {
                    return true;
                }
            }

            stack.RemoveAt(stack.Count - 1);

            return false;
        }

        // Serialize this node and its connected nodes recursively
        internal int SerializeRecursively(Dictionary<ReferenceNode, int> nodeToIndex, List<SearchResult.SerializableNode> serializedNodes)
        {
            int index;
            if (nodeToIndex.TryGetValue(this, out index))
            {
                return index;
            }

            var serializedNode = new SearchResult.SerializableNode
            {
                label = Label,
                isMainReference = IsMainReference,
                instanceId = instanceId ?? 0,
                isUnityObject = instanceId.HasValue,
                usedState = usedState
            };

            index = serializedNodes.Count;
            nodeToIndex[this] = index;
            serializedNodes.Add(serializedNode);

            if (links.Count > 0)
            {
                serializedNode.links = new List<int>(links.Count);
                serializedNode.linkDescriptions = new List<SearchResult.SerializableNode.SerializableLinkDescriptions>(links.Count);
                serializedNode.linkWeakStates = new List<bool>(links.Count);

                for (var i = 0; i < links.Count; i++)
                {
                    serializedNode.links.Add(links[i].targetNode.SerializeRecursively(nodeToIndex, serializedNodes));
                    serializedNode.linkDescriptions.Add(new SearchResult.SerializableNode.SerializableLinkDescriptions
                        { value = links[i].descriptions });
                    serializedNode.linkWeakStates.Add(links[i].isWeakLink);
                }
            }

            return index;
        }

        // Deserialize this node and its links from the serialized data
        internal void Deserialize(SearchResult.SerializableNode serializedNode, List<ReferenceNode> allNodes)
        {
            if (serializedNode.isUnityObject)
            {
                instanceId = serializedNode.instanceId;
            }
            else
            {
                instanceId = null;
            }

            Label = serializedNode.label;
            IsMainReference = serializedNode.isMainReference;
            usedState = serializedNode.usedState;

            if (serializedNode.links != null)
            {
                for (var i = 0; i < serializedNode.links.Count; i++)
                {
                    links.Add(new Link(allNodes[serializedNode.links[i]], serializedNode.linkDescriptions[i].value,
                        serializedNode.linkWeakStates[i]));
                }
            }
        }

        public override int GetHashCode()
        {
            return uid;
        }

        internal enum UsedState
        {
            Unused,
            MixedCollapsed,
            MixedExpanded,
            Used
        }

        public class Link
        {
            public readonly List<string> descriptions;
            public readonly ReferenceNode targetNode;
            public bool isWeakLink; // Weak links can be omitted from search results if this ReferenceNode isn't referenced by any other node

            public Link(ReferenceNode targetNode, string description, bool isWeakLink)
            {
                this.targetNode = targetNode;
                descriptions = string.IsNullOrEmpty(description) ? new List<string>() : new List<string>(1) { description };
                this.isWeakLink = isWeakLink;
            }

            public Link(ReferenceNode targetNode, List<string> descriptions, bool isWeakLink)
            {
                this.targetNode = targetNode;
                this.descriptions = descriptions;
                this.isWeakLink = isWeakLink;
            }
        }
    }
}