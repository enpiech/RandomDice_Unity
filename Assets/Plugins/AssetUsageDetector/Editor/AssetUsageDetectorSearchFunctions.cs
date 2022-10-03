using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Compilation;
using UnityEditor.U2D;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using AnimatorController = UnityEditor.Animations.AnimatorController;
using BlendTree = UnityEditor.Animations.BlendTree;
using Object = UnityEngine.Object;
#if UNITY_2017_1_OR_NEWER
using UnityEngine.U2D;
using UnityEngine.Playables;
#endif

#if UNITY_2018_2_OR_NEWER
#endif
#if UNITY_2017_3_OR_NEWER
#endif
#if UNITY_2017_2_OR_NEWER
#endif

namespace Plugins.AssetUsageDetector.Editor
{
    public partial class AssetUsageDetector
    {
        // An optimization to fetch an animation clip's curve bindings only once
        private readonly Dictionary<AnimationClip, EditorCurveBinding[]> animationClipUniqueBindings = new(256);

#if UNITY_2017_3_OR_NEWER
        // Path(s) of the Assembly Definition Files in objectsToSearchSet (Value: files themselves)
        private readonly Dictionary<string, Object> assemblyDefinitionFilesToSearch = new(8);
#endif

        // All MonoScripts in objectsToSearchSet
        private readonly List<MonoScript> monoScriptsToSearch = new();
        private readonly List<Type> monoScriptsToSearchTypes = new();

        // Path(s) of .cginc, .cg, .hlsl and .glslinc assets in assetsToSearchSet
        private readonly HashSet<string> shaderIncludesToSearchSet = new();

        // An optimization to fetch & filter fields and properties of a class only once
        private readonly Dictionary<Type, VariableGetterHolder[]> typeToVariables = new(4096);

        private readonly List<VariableGetterHolder> validVariables = new(32);

        // Dictionary to associate special file extensions with their search functions
        private Dictionary<string, Func<Object, ReferenceNode>> extensionToSearchFunction;
        private FieldInfoGetter fieldInfoGetter;

        private BindingFlags fieldModifiers, propertyModifiers;
        private BindingFlags prevFieldModifiers, prevPropertyModifiers;
        private bool prevSearchSerializableVariablesOnly;
        private bool searchMonoBehavioursForScript;

        private bool searchPrefabConnections;

        private bool searchSerializableVariablesOnly;
#if UNITY_2018_1_OR_NEWER
        private bool searchShaderGraphsForSubGraphs;
#endif
        private bool searchTextureReferences;

        // Dictionary to quickly find the function to search a specific type with
        private Dictionary<Type, Func<Object, ReferenceNode>> typeToSearchFunction;

        private void InitializeSearchFunctionsData(Parameters searchParameters)
        {
            if (typeToSearchFunction == null)
            {
                typeToSearchFunction = new Dictionary<Type, Func<Object, ReferenceNode>>
                {
                    { typeof(GameObject), SearchGameObject },
                    { typeof(Material), SearchMaterial },
                    { typeof(Shader), SearchShader },
                    { typeof(MonoScript), SearchMonoScript },
                    { typeof(RuntimeAnimatorController), SearchAnimatorController },
                    { typeof(AnimatorOverrideController), SearchAnimatorController },
                    { typeof(AnimatorController), SearchAnimatorController },
                    { typeof(AnimatorStateMachine), SearchAnimatorStateMachine },
                    { typeof(AnimatorState), SearchAnimatorState },
                    { typeof(AnimatorStateTransition), SearchAnimatorStateTransition },
                    { typeof(BlendTree), SearchBlendTree },
                    { typeof(AnimationClip), SearchAnimationClip },
                    { typeof(TerrainData), SearchTerrainData },
#if UNITY_2017_1_OR_NEWER
                    { typeof(SpriteAtlas), SearchSpriteAtlas },
#endif
                };
            }

            if (extensionToSearchFunction == null)
            {
                extensionToSearchFunction = new Dictionary<string, Func<Object, ReferenceNode>>
                {
                    { "compute", SearchShaderSecondaryAsset },
                    { "cginc", SearchShaderSecondaryAsset },
                    { "cg", SearchShaderSecondaryAsset },
                    { "glslinc", SearchShaderSecondaryAsset },
                    { "hlsl", SearchShaderSecondaryAsset },
#if UNITY_2017_3_OR_NEWER
                    { "asmdef", SearchAssemblyDefinitionFile },
#endif
#if UNITY_2019_2_OR_NEWER
                    { "asmref", SearchAssemblyDefinitionFile },
#endif
#if UNITY_2018_1_OR_NEWER
                    { "shadergraph", SearchShaderGraph },
                    { "shadersubgraph", SearchShaderGraph },
#endif
                };
            }

            fieldModifiers = searchParameters.fieldModifiers | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            propertyModifiers = searchParameters.propertyModifiers | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            searchSerializableVariablesOnly = !searchParameters.searchNonSerializableVariables;

            if (prevFieldModifiers != fieldModifiers || prevPropertyModifiers != propertyModifiers ||
                prevSearchSerializableVariablesOnly != searchSerializableVariablesOnly)
            {
                typeToVariables.Clear();
            }

            prevFieldModifiers = fieldModifiers;
            prevPropertyModifiers = propertyModifiers;
            prevSearchSerializableVariablesOnly = searchSerializableVariablesOnly;

            searchPrefabConnections = false;
            searchMonoBehavioursForScript = false;
            searchTextureReferences = false;
#if UNITY_2018_1_OR_NEWER
            searchShaderGraphsForSubGraphs = false;
#endif

            foreach (var obj in objectsToSearchSet)
            {
                if (obj is Texture || obj is Sprite)
                {
                    searchTextureReferences = true;
                }
                else if (obj is MonoScript)
                {
                    searchMonoBehavioursForScript = true;

                    var monoScriptType = ((MonoScript)obj).GetClass();
                    if (monoScriptType != null && !monoScriptType.IsSealed)
                    {
                        monoScriptsToSearch.Add((MonoScript)obj);
                        monoScriptsToSearchTypes.Add(monoScriptType);
                    }
                }
                else if (obj is GameObject)
                {
                    searchPrefabConnections = true;
                }
#if UNITY_2017_3_OR_NEWER
                else if (obj is AssemblyDefinitionAsset)
                {
                    assemblyDefinitionFilesToSearch[AssetDatabase.GetAssetPath(obj)] = obj;
                }
#endif
            }

            // We need to search for class/interface inheritance references manually because AssetDatabase.GetDependencies doesn't take that into account
            if (monoScriptsToSearch.Count > 0)
            {
                alwaysSearchedExtensionsSet.Add("cs");
                alwaysSearchedExtensionsSet.Add("dll");
            }

            foreach (var path in assetsToSearchPathsSet)
            {
                var extension = Utilities.GetFileExtension(path);
                if (extension == "hlsl" || extension == "cginc" || extension == "cg" || extension == "glslinc")
                {
                    shaderIncludesToSearchSet.Add(path);
                }
#if UNITY_2018_1_OR_NEWER
                else if (extension == "shadersubgraph")
                {
                    searchShaderGraphsForSubGraphs = true;
                }
#endif
            }

            // AssetDatabase.GetDependencies doesn't take #include lines in shader source codes into consideration. If we are searching for references
            // of a potential #include target (shaderIncludesToSearchSet), we must search all shader assets and check their #include lines manually
            if (shaderIncludesToSearchSet.Count > 0)
            {
                alwaysSearchedExtensionsSet.Add("shader");
                alwaysSearchedExtensionsSet.Add("compute");
                alwaysSearchedExtensionsSet.Add("cginc");
                alwaysSearchedExtensionsSet.Add("cg");
                alwaysSearchedExtensionsSet.Add("glslinc");
                alwaysSearchedExtensionsSet.Add("hlsl");
            }

#if UNITY_2017_3_OR_NEWER
            // AssetDatabase.GetDependencies doesn't return references from Assembly Definition Files to their Assembly Definition References,
            // so if we are searching for an Assembly Definition File's usages, we must search all Assembly Definition Files' references manually.
            if (assemblyDefinitionFilesToSearch.Count > 0)
            {
                alwaysSearchedExtensionsSet.Add("asmdef");
#if UNITY_2019_2_OR_NEWER
                alwaysSearchedExtensionsSet.Add("asmref");
#endif
            }
#endif

#if UNITY_2018_1_OR_NEWER
            // AssetDatabase.GetDependencies doesn't work with Shader Graph assets. We must search all Shader Graph assets in the following cases:
            // searchTextureReferences: to find Texture references used in various nodes and properties
            // searchShaderGraphsForSubGraphs: to find Shader Sub-graph references in other Shader Graph assets
            // shaderIncludesToSearchSet: to find .cginc, .cg, .glslinc and .hlsl references used in Custom Function nodes
            if (searchTextureReferences || searchShaderGraphsForSubGraphs || shaderIncludesToSearchSet.Count > 0)
            {
                alwaysSearchedExtensionsSet.Add("shadergraph");
                alwaysSearchedExtensionsSet.Add("shadersubgraph");
            }
#endif

#if UNITY_2019_3_OR_NEWER
            var fieldInfoGetterMethod = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ScriptAttributeUtility")
                .GetMethod("GetFieldInfoAndStaticTypeFromProperty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
#else
			MethodInfo fieldInfoGetterMethod =
 typeof( Editor ).Assembly.GetType( "UnityEditor.ScriptAttributeUtility" ).GetMethod( "GetFieldInfoFromProperty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static );
#endif
            fieldInfoGetter = (FieldInfoGetter)Delegate.CreateDelegate(typeof(FieldInfoGetter), fieldInfoGetterMethod);
        }

        private ReferenceNode SearchGameObject(Object unityObject)
        {
            var go = (GameObject)unityObject;
            var referenceNode = PopReferenceNode(go);

            // Check if this GameObject's prefab is one of the selected assets
            if (searchPrefabConnections)
            {
#if UNITY_2018_3_OR_NEWER
                Object prefab = go;
                while (prefab = PrefabUtility.GetCorrespondingObjectFromSource(prefab))
#else
				Object prefab = PrefabUtility.GetPrefabParent( go );
				if( prefab )
#endif
                {
                    if (objectsToSearchSet.Contains(prefab) && assetsToSearchRootPrefabs.ContainsFast(prefab as GameObject))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(prefab), "Prefab object");

                        if (searchParameters.searchRefactoring != null)
                        {
                            searchParameters.searchRefactoring(new PrefabMatch(go, prefab));
                        }
                    }
                }
            }

            // Search through all the components of the object
            var components = go.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                referenceNode.AddLinkTo(SearchObject(components[i]), isWeakLink: true);
            }

            return referenceNode;
        }

        private ReferenceNode SearchComponent(Object unityObject)
        {
            var component = (Component)unityObject;

            // Ignore Transform component (no object field to search for)
            if (component is Transform)
            {
                return null;
            }

            var referenceNode = PopReferenceNode(component);

            if (searchMonoBehavioursForScript && component is MonoBehaviour)
            {
                // If a searched asset is script, check if this component is an instance of it
                // Although SearchVariablesWithSerializedObject can detect these references with SerializedObject, it isn't possible when reflection is used in Play mode
                var script = MonoScript.FromMonoBehaviour((MonoBehaviour)component);
                if (objectsToSearchSet.Contains(script))
                {
                    referenceNode.AddLinkTo(GetReferenceNode(script));

                    if (searchParameters.searchRefactoring != null)
                    {
                        searchParameters.searchRefactoring(new BehaviourUsageMatch(component.gameObject, script, component));
                    }
                }
            }

            if (component is Animation)
            {
                // Search animation clips for references
                if (searchParameters.searchRefactoring == null)
                {
                    foreach (AnimationState anim in (Animation)component)
                    {
                        referenceNode.AddLinkTo(SearchObject(anim.clip));
                    }
                }
                else
                {
                    var clips = AnimationUtility.GetAnimationClips(component.gameObject);
                    var modifiedClips = false;
                    for (var i = 0; i < clips.Length; i++)
                    {
                        referenceNode.AddLinkTo(SearchObject(clips[i]));

                        if (objectsToSearchSet.Contains(clips[i]))
                        {
                            searchParameters.searchRefactoring(new AnimationSystemMatch(component, clips[i], newValue =>
                            {
                                clips[i] = (AnimationClip)newValue;
                                modifiedClips = true;
                            }));
                        }
                    }

                    if (modifiedClips)
                    {
                        AnimationUtility.SetAnimationClips((Animation)component, clips);
                    }
                }

                // Search the objects that are animated by this Animation component for references
                SearchAnimatedObjects(referenceNode);
            }
            else if (component is Animator)
            {
                // Search animation clips for references (via AnimatorController)
                var animatorController = ((Animator)component).runtimeAnimatorController;
                referenceNode.AddLinkTo(SearchObject(animatorController));

                if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(animatorController))
                {
                    searchParameters.searchRefactoring(new AnimationSystemMatch(component, animatorController,
                        newValue => ((Animator)component).runtimeAnimatorController = (RuntimeAnimatorController)newValue));
                }

                // Search the objects that are animated by this Animator component for references
                SearchAnimatedObjects(referenceNode);
            }
#if UNITY_2017_2_OR_NEWER
            else if (component is Tilemap)
            {
                // Search the tiles for references
                var tiles = new TileBase[((Tilemap)component).GetUsedTilesCount()];
                ((Tilemap)component).GetUsedTilesNonAlloc(tiles);

                if (tiles != null)
                {
                    for (var i = 0; i < tiles.Length; i++)
                    {
                        referenceNode.AddLinkTo(SearchObject(tiles[i]), "Tile");

                        if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(tiles[i]))
                        {
                            searchParameters.searchRefactoring(new OtherSearchMatch(component, tiles[i],
                                newValue => ((Tilemap)component).SwapTile(tiles[i], (TileBase)newValue)));
                        }
                    }
                }
            }
#endif
#if UNITY_2017_1_OR_NEWER
            else if (component is PlayableDirector)
            {
                // Search the PlayableAsset's scene bindings for references
                var playableAsset = ((PlayableDirector)component).playableAsset;
                if (playableAsset != null && !playableAsset.Equals(null))
                {
                    foreach (var binding in playableAsset.outputs)
                    {
                        var bindingValue = ((PlayableDirector)component).GetGenericBinding(binding.sourceObject);
                        referenceNode.AddLinkTo(SearchObject(bindingValue), "Binding: " + binding.streamName);

                        if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(bindingValue))
                        {
                            searchParameters.searchRefactoring(new AnimationSystemMatch(component, bindingValue,
                                newValue => ((PlayableDirector)component).SetGenericBinding(binding.sourceObject, newValue)));
                        }
                    }
                }
            }
#endif
            else if (component is ParticleSystemRenderer)
            {
                // Search ParticleSystemRenderer's custom meshes for references (at runtime, they can't be searched with reflection, unfortunately)
                if (isInPlayMode && !AssetDatabase.Contains(component))
                {
                    var meshes = new Mesh[((ParticleSystemRenderer)component).meshCount];
                    var meshCount = ((ParticleSystemRenderer)component).GetMeshes(meshes);
                    var modifiedMeshes = false;
                    for (var i = 0; i < meshCount; i++)
                    {
                        referenceNode.AddLinkTo(SearchObject(meshes[i]), "Renderer Module: Mesh");

                        if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(meshes[i]))
                        {
                            searchParameters.searchRefactoring(new OtherSearchMatch(component, meshes[i], newValue =>
                            {
                                meshes[i] = (Mesh)newValue;
                                modifiedMeshes = true;
                            }));
                        }
                    }

                    if (modifiedMeshes)
                    {
                        ((ParticleSystemRenderer)component).SetMeshes(meshes, meshCount);
                    }
                }
            }
            else if (component is ParticleSystem)
            {
                // At runtime, some ParticleSystem properties can't be searched with reflection, search them manually here
                if (isInPlayMode && !AssetDatabase.Contains(component))
                {
                    var particleSystem = (ParticleSystem)component;

                    try
                    {
                        var collisionModule = particleSystem.collision;
#if UNITY_2020_2_OR_NEWER
                        for (int i = 0, j = collisionModule.planeCount; i < j; i++)
#else
						for( int i = 0, j = collisionModule.maxPlaneCount; i < j; i++ )
#endif
                        {
                            var plane = collisionModule.GetPlane(i);
                            referenceNode.AddLinkTo(SearchObject(plane), "Collision Module: Plane");

                            if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(plane))
                            {
                                searchParameters.searchRefactoring(new OtherSearchMatch(collisionModule, plane, component,
                                    newValue => collisionModule.SetPlane(i, (Transform)newValue)));
                            }
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        var triggerModule = particleSystem.trigger;
#if UNITY_2020_2_OR_NEWER
                        for (int i = 0, j = triggerModule.colliderCount; i < j; i++)
#else
						for( int i = 0, j = triggerModule.maxColliderCount; i < j; i++ )
#endif
                        {
                            var collider = triggerModule.GetCollider(i);
                            referenceNode.AddLinkTo(SearchObject(collider), "Trigger Module: Collider");

                            if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(collider))
                            {
                                searchParameters.searchRefactoring(new OtherSearchMatch(triggerModule, collider, component,
                                    newValue => triggerModule.SetCollider(i, (Component)newValue)));
                            }
                        }
                    }
                    catch
                    {
                    }

#if UNITY_2017_1_OR_NEWER
                    try
                    {
                        var textureSheetAnimationModule = particleSystem.textureSheetAnimation;
                        for (int i = 0, j = textureSheetAnimationModule.spriteCount; i < j; i++)
                        {
                            var sprite = textureSheetAnimationModule.GetSprite(i);
                            referenceNode.AddLinkTo(SearchObject(sprite), "Texture Sheet Animation Module: Sprite");

                            if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(sprite))
                            {
                                searchParameters.searchRefactoring(new OtherSearchMatch(textureSheetAnimationModule, sprite, component,
                                    newValue => textureSheetAnimationModule.SetSprite(i, (Sprite)newValue)));
                            }
                        }
                    }
                    catch
                    {
                    }
#endif

#if UNITY_5_5_OR_NEWER
                    try
                    {
                        var subEmittersModule = particleSystem.subEmitters;
                        for (int i = 0, j = subEmittersModule.subEmittersCount; i < j; i++)
                        {
                            var subEmitterSystem = subEmittersModule.GetSubEmitterSystem(i);
                            referenceNode.AddLinkTo(SearchObject(subEmitterSystem), "Sub Emitters Module: ParticleSystem");

                            if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(subEmitterSystem))
                            {
                                searchParameters.searchRefactoring(new OtherSearchMatch(subEmittersModule, subEmitterSystem, component,
                                    newValue => subEmittersModule.SetSubEmitterSystem(i, (ParticleSystem)newValue)));
                            }
                        }
                    }
                    catch
                    {
                    }
#endif
                }
            }

            SearchVariablesWithSerializedObject(referenceNode);
            return referenceNode;
        }

        private ReferenceNode SearchMaterial(Object unityObject)
        {
            const string TEXTURE_PROPERTY_PREFIX = "m_SavedProperties.m_TexEnvs[";

            var material = (Material)unityObject;
            var referenceNode = PopReferenceNode(material);

            // We used to search only the shader and the Texture properties in this function but it has changed for 2 major reasons:
            // 1) Materials can store more than these references now. For example, HDRP materials can have references to other HDRP materials
            // 2) It wasn't possible to search Texture properties that were no longer used by the shader
            // Thus, we are searching every property of the material using SerializedObject
            SearchVariablesWithSerializedObject(referenceNode);

            // Post-process the found results and convert links that start with TEXTURE_PROPERTY_PREFIX to their readable names
            SerializedObject materialSO = null;
            for (var i = referenceNode.NumberOfOutgoingLinks - 1; i >= 0; i--)
            {
                var linkDescriptions = referenceNode[i].descriptions;
                for (var j = linkDescriptions.Count - 1; j >= 0; j--)
                {
                    var texturePropertyPrefixIndex = linkDescriptions[j].IndexOf(TEXTURE_PROPERTY_PREFIX);
                    if (texturePropertyPrefixIndex >= 0)
                    {
                        texturePropertyPrefixIndex += TEXTURE_PROPERTY_PREFIX.Length;
                        var texturePropertyEndIndex = linkDescriptions[j].IndexOf(']', texturePropertyPrefixIndex);
                        if (texturePropertyEndIndex > texturePropertyPrefixIndex)
                        {
                            int texturePropertyIndex;
                            if (int.TryParse(
                                    linkDescriptions[j].Substring(texturePropertyPrefixIndex,
                                        texturePropertyEndIndex - texturePropertyPrefixIndex), out texturePropertyIndex))
                            {
                                if (materialSO == null)
                                {
                                    materialSO = new SerializedObject(material);
                                }

                                var propertyName = materialSO
                                    .FindProperty("m_SavedProperties.m_TexEnvs.Array.data[" + texturePropertyIndex + "].first").stringValue;
                                if (material.HasProperty(propertyName))
                                {
                                    linkDescriptions[j] = "[Property: " + propertyName + "]";
                                }
                                else if (searchParameters.searchUnusedMaterialProperties)
                                {
                                    // Move unused references to the end of the list so that used references come first
                                    linkDescriptions.Add("[Property (UNUSED): " + propertyName + "]");
                                    linkDescriptions.RemoveAt(j);
                                }
                                else
                                {
                                    linkDescriptions.RemoveAt(j);
                                }
                            }
                        }
                    }
                }

                if (linkDescriptions.Count == 0) // All shader properties were unused and we weren't searching for unused material properties
                {
                    referenceNode.RemoveLink(i);
                }
            }

            // At runtime, Textures assigned to clone materials can't be searched with reflection, search them manually here
            if (searchTextureReferences && isInPlayMode && !AssetDatabase.Contains(material))
            {
                var shader = material.shader;
                var shaderPropertyCount = ShaderUtil.GetPropertyCount(shader);
                for (var i = 0; i < shaderPropertyCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        var propertyName = ShaderUtil.GetPropertyName(shader, i);
                        var assignedTexture = material.GetTexture(propertyName);
                        if (objectsToSearchSet.Contains(assignedTexture))
                        {
                            referenceNode.AddLinkTo(GetReferenceNode(assignedTexture), "Shader property: " + propertyName);

                            if (searchParameters.searchRefactoring != null)
                            {
                                searchParameters.searchRefactoring(new OtherSearchMatch(material, assignedTexture,
                                    newValue => material.SetTexture(propertyName, (Texture)newValue)));
                            }
                        }
                    }
                }
            }

            return referenceNode;
        }

        // Searches default Texture values assigned to shader properties, as well as #include references in shader source code
        private ReferenceNode SearchShader(Object unityObject)
        {
            var shader = (Shader)unityObject;
            var referenceNode = PopReferenceNode(shader);

            if (searchTextureReferences)
            {
                var shaderImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(unityObject)) as ShaderImporter;
                if (shaderImporter != null)
                {
                    var shaderPropertyCount = ShaderUtil.GetPropertyCount(shader);
                    for (var i = 0; i < shaderPropertyCount; i++)
                    {
                        if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            var propertyName = ShaderUtil.GetPropertyName(shader, i);
                            var defaultTexture = shaderImporter.GetDefaultTexture(propertyName);
#if UNITY_2018_1_OR_NEWER
                            if (!defaultTexture)
                            {
                                defaultTexture = shaderImporter.GetNonModifiableTexture(propertyName);
                            }
#endif

                            if (objectsToSearchSet.Contains(defaultTexture))
                            {
                                referenceNode.AddLinkTo(GetReferenceNode(defaultTexture), "Default Texture: " + propertyName);

                                if (searchParameters.searchRefactoring != null)
                                {
                                    searchParameters.searchRefactoring(new AssetImporterDefaultValueMatch(shaderImporter, defaultTexture,
                                        propertyName, null));
                                }
                            }
                        }
                    }
                }
            }

            // Search shader source code for #include references
            if (shaderIncludesToSearchSet.Count > 0)
            {
                SearchShaderSourceCodeForCGIncludes(referenceNode);
            }

            return referenceNode;
        }

        // Searches .compute, .cginc, .cg, .hlsl and .glslinc assets for #include references
        private ReferenceNode SearchShaderSecondaryAsset(Object unityObject)
        {
            if (shaderIncludesToSearchSet.Count == 0)
            {
                return null;
            }

            var referenceNode = PopReferenceNode(unityObject);
            SearchShaderSourceCodeForCGIncludes(referenceNode);
            return referenceNode;
        }

        // Searches class/interface inheritances and default UnityEngine.Object values assigned to script variables
        private ReferenceNode SearchMonoScript(Object unityObject)
        {
            var script = (MonoScript)unityObject;
            var scriptType = script.GetClass();
            if (scriptType == null || !scriptType.IsSubclassOf(typeof(MonoBehaviour)) && !scriptType.IsSubclassOf(typeof(ScriptableObject)))
            {
                return null;
            }

            var referenceNode = PopReferenceNode(script);

            // Check for class/interface inheritance references
            for (var i = monoScriptsToSearch.Count - 1; i >= 0; i--)
            {
                if (monoScriptsToSearchTypes[i] != scriptType && monoScriptsToSearchTypes[i].IsAssignableFrom(scriptType))
                {
                    referenceNode.AddLinkTo(GetReferenceNode(monoScriptsToSearch[i]),
                        monoScriptsToSearchTypes[i].IsInterface ? "Implements interface" : "Extends class");
                }
            }

            var scriptImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(unityObject)) as MonoImporter;
            if (scriptImporter != null)
            {
                var variables = GetFilteredVariablesForType(scriptType);
                for (var i = 0; i < variables.Length; i++)
                {
                    if (variables[i].isSerializable && !variables[i].IsProperty)
                    {
                        var defaultValue = scriptImporter.GetDefaultReference(variables[i].Name);
                        if (objectsToSearchSet.Contains(defaultValue))
                        {
                            referenceNode.AddLinkTo(GetReferenceNode(defaultValue), "Default variable value: " + variables[i].Name);

                            if (searchParameters.searchRefactoring != null)
                            {
                                searchParameters.searchRefactoring(new AssetImporterDefaultValueMatch(scriptImporter, defaultValue,
                                    variables[i].Name, variables));
                            }
                        }
                    }
                }
            }

            return referenceNode;
        }

        private ReferenceNode SearchAnimatorController(Object unityObject)
        {
            var controller = (RuntimeAnimatorController)unityObject;
            var referenceNode = PopReferenceNode(controller);

            if (controller is AnimatorController)
            {
                var layers = ((AnimatorController)controller).layers;
                for (var i = 0; i < layers.Length; i++)
                {
                    if (objectsToSearchSet.Contains(layers[i].avatarMask))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(layers[i].avatarMask), layers[i].name + " Mask");

                        if (searchParameters.searchRefactoring != null)
                        {
                            searchParameters.searchRefactoring(new AnimationSystemMatch(layers[i], layers[i].avatarMask, controller,
                                newValue => layers[i].avatarMask = (AvatarMask)newValue));
                        }
                    }

                    referenceNode.AddLinkTo(SearchObject(layers[i].stateMachine));
                }
            }
            else
            {
                if (controller is AnimatorOverrideController)
                {
                    var parentController = ((AnimatorOverrideController)controller).runtimeAnimatorController;
                    if (objectsToSearchSet.Contains(parentController))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(parentController));

                        if (searchParameters.searchRefactoring != null)
                        {
                            searchParameters.searchRefactoring(new AnimationSystemMatch(controller, parentController,
                                newValue => ((AnimatorOverrideController)controller).runtimeAnimatorController =
                                    (RuntimeAnimatorController)newValue));
                        }
                    }

                    if (searchParameters.searchRefactoring != null)
                    {
                        var overrideClips =
                            new List<KeyValuePair<AnimationClip, AnimationClip>>(((AnimatorOverrideController)controller).overridesCount);
                        ((AnimatorOverrideController)controller).GetOverrides(overrideClips);
                        var modifiedOverrideClips = false;
                        for (var i = overrideClips.Count - 1; i >= 0; i--)
                        {
                            if (objectsToSearchSet.Contains(overrideClips[i].Value))
                            {
                                searchParameters.searchRefactoring(new AnimationSystemMatch(controller, overrideClips[i].Value, newValue =>
                                {
                                    overrideClips[i] =
                                        new KeyValuePair<AnimationClip, AnimationClip>(overrideClips[i].Key, (AnimationClip)newValue);
                                    modifiedOverrideClips = true;
                                }));
                            }
                        }

                        if (modifiedOverrideClips)
                        {
                            ((AnimatorOverrideController)controller).ApplyOverrides(overrideClips);
                        }
                    }
                }

                var animClips = controller.animationClips;
                for (var i = 0; i < animClips.Length; i++)
                {
                    referenceNode.AddLinkTo(SearchObject(animClips[i]));
                }
            }

            return referenceNode;
        }

        private ReferenceNode SearchAnimatorStateMachine(Object unityObject)
        {
            var animatorStateMachine = (AnimatorStateMachine)unityObject;
            var referenceNode = PopReferenceNode(animatorStateMachine);

            var stateMachines = animatorStateMachine.stateMachines;
            for (var i = 0; i < stateMachines.Length; i++)
            {
                referenceNode.AddLinkTo(SearchObject(stateMachines[i].stateMachine), "Child State Machine");
            }

            var states = animatorStateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                referenceNode.AddLinkTo(SearchObject(states[i].state));
            }

            if (searchMonoBehavioursForScript)
            {
                var behaviours = animatorStateMachine.behaviours;
                for (var i = 0; i < behaviours.Length; i++)
                {
                    var script = MonoScript.FromScriptableObject(behaviours[i]);
                    if (objectsToSearchSet.Contains(script))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(script));

                        if (searchParameters.searchRefactoring != null)
                        {
                            searchParameters.searchRefactoring(new BehaviourUsageMatch(animatorStateMachine, script, behaviours[i]));
                        }
                    }
                }
            }

            return referenceNode;
        }

        private ReferenceNode SearchAnimatorState(Object unityObject)
        {
            var animatorState = (AnimatorState)unityObject;
            var referenceNode = PopReferenceNode(animatorState);

            referenceNode.AddLinkTo(SearchObject(animatorState.motion), "Motion");

            if (searchParameters.searchRefactoring != null && animatorState.motion as AnimationClip &&
                objectsToSearchSet.Contains(animatorState.motion))
            {
                searchParameters.searchRefactoring(new AnimationSystemMatch(animatorState, animatorState.motion,
                    newValue => animatorState.motion = (Motion)newValue));
            }

            if (searchMonoBehavioursForScript)
            {
                var behaviours = animatorState.behaviours;
                for (var i = 0; i < behaviours.Length; i++)
                {
                    var script = MonoScript.FromScriptableObject(behaviours[i]);
                    if (objectsToSearchSet.Contains(script))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(script));

                        if (searchParameters.searchRefactoring != null)
                        {
                            searchParameters.searchRefactoring(new BehaviourUsageMatch(animatorState, script, behaviours[i]));
                        }
                    }
                }
            }

            return referenceNode;
        }

        private ReferenceNode SearchAnimatorStateTransition(Object unityObject)
        {
            // Don't search AnimatorStateTransition objects, it will just return duplicate results of SearchAnimatorStateMachine
            return PopReferenceNode(unityObject);
        }

        private ReferenceNode SearchBlendTree(Object unityObject)
        {
            var blendTree = (BlendTree)unityObject;
            var referenceNode = PopReferenceNode(blendTree);

            var children = blendTree.children;
            for (var i = 0; i < children.Length; i++)
            {
                referenceNode.AddLinkTo(SearchObject(children[i].motion), "Motion");

                if (searchParameters.searchRefactoring != null && children[i].motion as AnimationClip &&
                    objectsToSearchSet.Contains(children[i].motion))
                {
                    searchParameters.searchRefactoring(new AnimationSystemMatch(blendTree, children[i].motion,
                        newValue => children[i].motion = (Motion)newValue));
                }
            }

            return referenceNode;
        }

        private ReferenceNode SearchAnimationClip(Object unityObject)
        {
            var clip = (AnimationClip)unityObject;
            var referenceNode = PopReferenceNode(clip);

            // Get all curves from animation clip
            var objectCurves = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (var i = 0; i < objectCurves.Length; i++)
            {
                // Search through all the keyframes in this curve
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, objectCurves[i]);
                var modifiedKeyframes = false;
                for (var j = 0; j < keyframes.Length; j++)
                {
                    referenceNode.AddLinkTo(SearchObject(keyframes[j].value), "Keyframe: " + keyframes[j].time);

                    if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(keyframes[j].value))
                    {
                        searchParameters.searchRefactoring(new AnimationSystemMatch(clip, keyframes[j].value, newValue =>
                        {
                            keyframes[j].value = newValue;
                            modifiedKeyframes = true;
                        }));
                    }
                }

                if (modifiedKeyframes)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, objectCurves[i], keyframes);
                }
            }

            // Get all events from animation clip
            var events = AnimationUtility.GetAnimationEvents(clip);
            var modifiedEvents = false;
            for (var i = 0; i < events.Length; i++)
            {
                referenceNode.AddLinkTo(SearchObject(events[i].objectReferenceParameter), "AnimationEvent: " + events[i].time);

                if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(events[i].objectReferenceParameter))
                {
                    searchParameters.searchRefactoring(new AnimationSystemMatch(clip, events[i].objectReferenceParameter, newValue =>
                    {
                        events[i].objectReferenceParameter = newValue;
                        modifiedEvents = true;
                    }));
                }
            }

            if (modifiedEvents)
            {
                AnimationUtility.SetAnimationEvents(clip, events);
            }

            return referenceNode;
        }

        // TerrainData's properties like tree/detail/layer definitions aren't exposed to SerializedObject so use reflection instead
        private ReferenceNode SearchTerrainData(Object unityObject)
        {
            var referenceNode = PopReferenceNode(unityObject);
            SearchVariablesWithReflection(referenceNode);
            return referenceNode;
        }

#if UNITY_2017_3_OR_NEWER
        // Find references from an Assembly Definition File to its Assembly Definition References
        private ReferenceNode SearchAssemblyDefinitionFile(Object unityObject)
        {
            if (assemblyDefinitionFilesToSearch.Count == 0)
            {
                return null;
            }

            var assemblyDefinitionFile = JsonUtility.FromJson<AssemblyDefinitionReferences>(((TextAsset)unityObject).text);
            var referenceNode = PopReferenceNode(unityObject);

            if (!string.IsNullOrEmpty(assemblyDefinitionFile.reference))
            {
                if (assemblyDefinitionFile.references == null)
                {
                    assemblyDefinitionFile.references = new List<string>(1) { assemblyDefinitionFile.reference };
                }
                else
                {
                    assemblyDefinitionFile.references.Add(assemblyDefinitionFile.reference);
                }
            }

            if (assemblyDefinitionFile.references != null)
            {
                for (var i = 0; i < assemblyDefinitionFile.references.Count; i++)
                {
#if UNITY_2019_1_OR_NEWER
                    var assemblyPath =
                        CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyReference(assemblyDefinitionFile.references[i]);
#else
					string assemblyPath =
 CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName( assemblyDefinitionFile.references[i] );
#endif
                    if (!string.IsNullOrEmpty(assemblyPath))
                    {
                        Object searchedAssemblyDefinitionFile;
                        if (assemblyDefinitionFilesToSearch.TryGetValue(assemblyPath, out searchedAssemblyDefinitionFile))
                        {
                            referenceNode.AddLinkTo(GetReferenceNode(searchedAssemblyDefinitionFile), "Referenced Assembly");
                        }
                    }
                }
            }

            return referenceNode;
        }
#endif

#if UNITY_2018_1_OR_NEWER
        // Searches Shader Graph assets for references
        private ReferenceNode SearchShaderGraph(Object unityObject)
        {
            if (!searchTextureReferences && !searchShaderGraphsForSubGraphs && shaderIncludesToSearchSet.Count == 0)
            {
                return null;
            }

            var referenceNode = PopReferenceNode(unityObject);

            // Shader Graph assets are JSON files, they must be crawled manually to find references
            var graphJson = File.ReadAllText(AssetDatabase.GetAssetPath(unityObject));
            if (graphJson.IndexOf("\"m_ObjectId\"", 0, Mathf.Min(200, graphJson.Length)) >= 0)
            {
                // New Shader Graph serialization format is used: https://github.com/Unity-Technologies/Graphics/pull/222
                // Iterate over all these occurrences:   "guid\": \"GUID_VALUE\" (\" is used instead of " because it is a nested JSON)
                IterateOverValuesInString(graphJson, new[] { "\"guid\\\"" }, '"', guid =>
                {
                    if (guid.Length > 1)
                    {
                        if (guid[guid.Length - 1] == '\\')
                        {
                            guid = guid.Substring(0, guid.Length - 1);
                        }

                        var referencePath = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(referencePath) && assetsToSearchPathsSet.Contains(referencePath))
                        {
                            var reference = AssetDatabase.LoadMainAssetAtPath(referencePath);
                            if (objectsToSearchSet.Contains(reference))
                            {
                                referenceNode.AddLinkTo(GetReferenceNode(reference), "Used in graph");
                            }
                        }
                    }
                });

                if (shaderIncludesToSearchSet.Count > 0)
                {
                    // Iterate over all these occurrences:   "m_FunctionSource": "GUID_VALUE" (this one is not nested JSON)
                    IterateOverValuesInString(graphJson, new[] { "\"m_FunctionSource\"" }, '"', guid =>
                    {
                        var referencePath = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(referencePath) && assetsToSearchPathsSet.Contains(referencePath))
                        {
                            var reference = AssetDatabase.LoadMainAssetAtPath(referencePath);
                            if (objectsToSearchSet.Contains(reference))
                            {
                                referenceNode.AddLinkTo(GetReferenceNode(reference), "Used in node: Custom Function");
                            }
                        }
                    });
                }
            }
            else
            {
                // Old Shader Graph serialization format is used. Although we could use the same search method as the new serialization format (which
                // is potentially faster), this alternative search method yields more information about references
                var shaderGraph = JsonUtility.FromJson<ShaderGraphReferences>(graphJson);

                if (shaderGraph.m_SerializedProperties != null)
                {
                    for (var i = shaderGraph.m_SerializedProperties.Count - 1; i >= 0; i--)
                    {
                        var propertyJSON = shaderGraph.m_SerializedProperties[i].JSONnodeData;
                        if (string.IsNullOrEmpty(propertyJSON))
                        {
                            continue;
                        }

                        var propertyData = JsonUtility.FromJson<ShaderGraphReferences.PropertyData>(propertyJSON);
                        if (propertyData.m_Value == null)
                        {
                            continue;
                        }

                        var texturePath = propertyData.m_Value.GetTexturePath();
                        if (string.IsNullOrEmpty(texturePath) || !assetsToSearchPathsSet.Contains(texturePath))
                        {
                            continue;
                        }

                        var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                        if (objectsToSearchSet.Contains(texture))
                        {
                            referenceNode.AddLinkTo(GetReferenceNode(texture), "Default Texture: " + propertyData.GetName());
                        }
                    }
                }

                if (shaderGraph.m_SerializableNodes != null)
                {
                    for (var i = shaderGraph.m_SerializableNodes.Count - 1; i >= 0; i--)
                    {
                        var nodeJSON = shaderGraph.m_SerializableNodes[i].JSONnodeData;
                        if (string.IsNullOrEmpty(nodeJSON))
                        {
                            continue;
                        }

                        var nodeData = JsonUtility.FromJson<ShaderGraphReferences.NodeData>(nodeJSON);
                        if (!string.IsNullOrEmpty(nodeData.m_FunctionSource))
                        {
                            var customFunctionPath = AssetDatabase.GUIDToAssetPath(nodeData.m_FunctionSource);
                            if (!string.IsNullOrEmpty(customFunctionPath) && assetsToSearchPathsSet.Contains(customFunctionPath))
                            {
                                var customFunction = AssetDatabase.LoadMainAssetAtPath(customFunctionPath);
                                if (objectsToSearchSet.Contains(customFunction))
                                {
                                    referenceNode.AddLinkTo(GetReferenceNode(customFunction), "Used in node: " + nodeData.m_Name);
                                }
                            }
                        }

                        if (searchShaderGraphsForSubGraphs)
                        {
                            var subGraphPath = nodeData.GetSubGraphPath();
                            if (!string.IsNullOrEmpty(subGraphPath) && assetsToSearchPathsSet.Contains(subGraphPath))
                            {
                                var subGraph = AssetDatabase.LoadMainAssetAtPath(subGraphPath);
                                if (objectsToSearchSet.Contains(subGraph))
                                {
                                    referenceNode.AddLinkTo(GetReferenceNode(subGraph), "Used as Sub-graph");
                                }
                            }
                        }

                        if (nodeData.m_SerializableSlots == null)
                        {
                            continue;
                        }

                        for (var j = nodeData.m_SerializableSlots.Count - 1; j >= 0; j--)
                        {
                            var nodeSlotJSON = nodeData.m_SerializableSlots[j].JSONnodeData;
                            if (string.IsNullOrEmpty(nodeSlotJSON))
                            {
                                continue;
                            }

                            var texturePath = JsonUtility.FromJson<ShaderGraphReferences.NodeSlotData>(nodeSlotJSON).GetTexturePath();
                            if (string.IsNullOrEmpty(texturePath) || !assetsToSearchPathsSet.Contains(texturePath))
                            {
                                continue;
                            }

                            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                            if (objectsToSearchSet.Contains(texture))
                            {
                                referenceNode.AddLinkTo(GetReferenceNode(texture), "Used in node: " + nodeData.m_Name);
                            }
                        }
                    }
                }
            }

            return referenceNode;
        }
#endif

        // Find references from an Animation/Animator component to the objects that it animates
        private void SearchAnimatedObjects(ReferenceNode referenceNode)
        {
            var root = ((Component)referenceNode.nodeObject).gameObject;
            var clips = AnimationUtility.GetAnimationClips(root);
            for (var i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                if (!clip)
                {
                    continue;
                }

                var isClipUnique = true;
                for (var j = i - 1; j >= 0; j--)
                {
                    if (clips[j] == clip)
                    {
                        isClipUnique = false;
                        break;
                    }
                }

                if (!isClipUnique)
                {
                    continue;
                }

                EditorCurveBinding[] uniqueBindings;
                if (!animationClipUniqueBindings.TryGetValue(clip, out uniqueBindings))
                {
                    // Calculate all the "unique" paths that the animation clip's curves have
                    // Both float curves (GetCurveBindings) and object reference curves (GetObjectReferenceCurveBindings) are checked
                    var _uniqueBindings = new List<EditorCurveBinding>(2);
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    for (var j = 0; j < bindings.Length; j++)
                    {
                        var bindingPath = bindings[j].path;
                        if (string.IsNullOrEmpty(bindingPath)) // Ignore the root animated object
                        {
                            continue;
                        }

                        var isBindingUnique = true;
                        for (var k = _uniqueBindings.Count - 1; k >= 0; k--)
                        {
                            if (bindingPath == _uniqueBindings[k].path)
                            {
                                isBindingUnique = false;
                                break;
                            }
                        }

                        if (isBindingUnique)
                        {
                            _uniqueBindings.Add(bindings[j]);
                        }
                    }

                    bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    for (var j = 0; j < bindings.Length; j++)
                    {
                        var bindingPath = bindings[j].path;
                        if (string.IsNullOrEmpty(bindingPath)) // Ignore the root animated object
                        {
                            continue;
                        }

                        var isBindingUnique = true;
                        for (var k = _uniqueBindings.Count - 1; k >= 0; k--)
                        {
                            if (bindingPath == _uniqueBindings[k].path)
                            {
                                isBindingUnique = false;
                                break;
                            }
                        }

                        if (isBindingUnique)
                        {
                            _uniqueBindings.Add(bindings[j]);
                        }
                    }

                    uniqueBindings = _uniqueBindings.ToArray();
                    animationClipUniqueBindings[clip] = uniqueBindings;
                }

                var clipName = clip.name;
                for (var j = 0; j < uniqueBindings.Length; j++)
                {
                    referenceNode.AddLinkTo(SearchObject(AnimationUtility.GetAnimatedObject(root, uniqueBindings[j])),
                        "Animated via clip: " + clipName);
                }
            }
        }

        // Search #include references in shader source code
        private void SearchShaderSourceCodeForCGIncludes(ReferenceNode referenceNode)
        {
            var shaderPath = AssetDatabase.GetAssetPath((Object)referenceNode.nodeObject);

            // Iterate over all these occurrences:    #include "INCLUDE_REFERENCE"   or   #include_with_pragmas "INCLUDE_REFERENCE"
            IterateOverValuesInString(File.ReadAllText(shaderPath), new[] { "#include ", "#include_with_pragmas " }, '"', include =>
            {
                var isIncludePotentialReference = shaderIncludesToSearchSet.Contains(include);
                if (!isIncludePotentialReference)
                {
                    // Get absolute path of the #include
                    include = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath), include));

                    var trimStartLength = Directory.GetCurrentDirectory().Length + 1; // Convert absolute path to a Project-relative path
                    if (include.Length > trimStartLength)
                    {
                        include = include.Substring(trimStartLength).Replace('\\', '/');
                        isIncludePotentialReference = shaderIncludesToSearchSet.Contains(include);
                    }
                }

                if (isIncludePotentialReference)
                {
                    var cgShader = AssetDatabase.LoadMainAssetAtPath(include);
                    if (objectsToSearchSet.Contains(cgShader))
                    {
                        referenceNode.AddLinkTo(GetReferenceNode(cgShader), "Used with #include");
                    }
                }
            });
        }

        // Search through variables of an object with SerializedObject
        private void SearchVariablesWithSerializedObject(ReferenceNode referenceNode)
        {
            if (!isInPlayMode || referenceNode.nodeObject.IsAsset())
            {
                var so = new SerializedObject((Object)referenceNode.nodeObject);
                var iterator = so.GetIterator();
                var iteratorVisible = so.GetIterator();
                if (iterator.Next(true))
                {
                    var iteratingVisible = iteratorVisible.NextVisible(true);
                    bool enterChildren;
                    do
                    {
                        // Iterate over NextVisible properties AND the properties that have corresponding FieldInfos (internal Unity
                        // properties don't have FieldInfos so we are skipping them, which is good because search results found in
                        // those properties aren't interesting and mostly confusing)
                        var isVisible = iteratingVisible && SerializedProperty.EqualContents(iterator, iteratorVisible);
                        if (isVisible)
                        {
                            iteratingVisible = iteratorVisible.NextVisible(iteratorVisible.propertyType == SerializedPropertyType.Generic);
                        }
                        else
                        {
                            Type propFieldType;
                            isVisible = iterator.type == "Array" || fieldInfoGetter(iterator, out propFieldType) != null;
                        }

                        if (!isVisible)
                        {
                            enterChildren = false;
                            continue;
                        }

                        Object propertyValue;
                        ReferenceNode searchResult;
                        switch (iterator.propertyType)
                        {
                            case SerializedPropertyType.ObjectReference:
                                propertyValue = iterator.objectReferenceValue;
                                searchResult = SearchObject(PreferablyGameObject(propertyValue));
                                enterChildren = false;
                                break;
                            case SerializedPropertyType.ExposedReference:
                                propertyValue = iterator.exposedReferenceValue;
                                searchResult = SearchObject(PreferablyGameObject(propertyValue));
                                enterChildren = false;
                                break;
#if UNITY_2019_3_OR_NEWER
                            case SerializedPropertyType.ManagedReference:
                                propertyValue = GetRawSerializedPropertyValue(iterator) as Object;
                                searchResult = SearchObject(PreferablyGameObject(propertyValue));
                                enterChildren = false;
                                break;
#endif
                            case SerializedPropertyType.Generic:
                                propertyValue = null;
                                searchResult = null;
                                enterChildren = true;
                                break;
                            default:
                                propertyValue = null;
                                searchResult = null;
                                enterChildren = false;
                                break;
                        }

                        if (searchResult != null && searchResult != referenceNode)
                        {
                            var propertyPath = iterator.propertyPath;

                            // m_RD.texture is a redundant reference that shows up when searching sprites
                            if (!propertyPath.EndsWithFast("m_RD.texture"))
                            {
                                referenceNode.AddLinkTo(searchResult,
                                    "Variable: " + propertyPath.Replace(".Array.data[",
                                        "[")); // "arrayVariable.Array.data[0]" becomes "arrayVariable[0]"

                                if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(propertyValue))
                                {
                                    searchParameters.searchRefactoring(new SerializedPropertyMatch((Object)referenceNode.nodeObject,
                                        propertyValue, iterator));
                                }
                            }
                        }
                    } while (iterator.Next(enterChildren));

                    return;
                }
            }

            // Use reflection algorithm as fallback
            SearchVariablesWithReflection(referenceNode);
        }

        // Search through variables of an object with reflection
        private void SearchVariablesWithReflection(ReferenceNode referenceNode)
        {
            // Get filtered variables for this object
            var variables = GetFilteredVariablesForType(referenceNode.nodeObject.GetType());
            for (var i = 0; i < variables.Length; i++)
            {
                // When possible, don't search non-serializable variables
                if (searchSerializableVariablesOnly && !variables[i].isSerializable)
                {
                    continue;
                }

                try
                {
                    var variableValue = variables[i].Get(referenceNode.nodeObject);
                    if (variableValue == null || variableValue.Equals(null))
                    {
                        continue;
                    }

                    // Values stored inside ICollection objects are searched using IEnumerable,
                    // no need to have duplicate search entries
                    if (!(variableValue is ICollection))
                    {
                        var searchResult = SearchObject(PreferablyGameObject(variableValue));
                        if (searchResult != null && searchResult != referenceNode)
                        {
                            referenceNode.AddLinkTo(searchResult, (variables[i].IsProperty ? "Property: " : "Variable: ") + variables[i].Name);

                            if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(variableValue as Object))
                            {
                                searchParameters.searchRefactoring(new ReflectionMatch(referenceNode.nodeObject, (Object)variableValue,
                                    variables[i].variable));
                            }
                        }
                    }

                    if (variableValue is IEnumerable && !(variableValue is Transform))
                    {
                        // If the field is IEnumerable (possibly an array or collection), search through members of it
                        // Note that Transform IEnumerable (children of the transform) is not iterated
                        var index = 0;
                        List<Object> foundReferences = null;
                        foreach (var element in (IEnumerable)variableValue)
                        {
                            var searchResult = SearchObject(PreferablyGameObject(element));
                            if (searchResult != null && searchResult != referenceNode)
                            {
                                referenceNode.AddLinkTo(searchResult,
                                    string.Concat(variables[i].IsProperty ? "Property: " : "Variable: ", variables[i].Name, "[", index + "]"));

                                if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(element as Object))
                                {
                                    if (foundReferences == null)
                                    {
                                        foundReferences = new List<Object>(2) { (Object)element };
                                    }
                                    else if (!foundReferences.Contains((Object)element))
                                    {
                                        foundReferences.Add((Object)element);
                                    }
                                }
                            }

                            index++;
                        }

                        if (foundReferences != null)
                        {
                            for (var j = foundReferences.Count - 1; j >= 0; j--)
                            {
                                searchParameters.searchRefactoring(new ReflectionMatch(referenceNode.nodeObject, foundReferences[j],
                                    variableValue));
                            }
                        }
                    }
                }
                catch (UnassignedReferenceException)
                {
                }
                catch (MissingReferenceException)
                {
                }
                catch (MissingComponentException)
                {
                }
                catch (NotImplementedException)
                {
                }
                catch (Exception e)
                {
                    // Unknown exceptions usually occur when variableValue is an IEnumerable and its enumerator throws an unhandled exception in MoveNext or Current
                    var sb = Utilities.stringBuilder;
                    sb.Length = 0;
                    sb.EnsureCapacity(callStack.Count * 50 + 1000);

                    sb.Append("Skipped searching ").Append(referenceNode.nodeObject.GetType().FullName).Append(".").Append(variables[i].Name)
                        .AppendLine(" because it threw exception:").Append(e).AppendLine();

                    var latestUnityObjectInCallStack = AppendCallStackToStringBuilder(sb);
                    Debug.LogWarning(sb.ToString(), latestUnityObjectInCallStack);
                }
            }
        }

        // Get filtered variables for a type
        private VariableGetterHolder[] GetFilteredVariablesForType(Type type)
        {
            VariableGetterHolder[] result;
            if (typeToVariables.TryGetValue(type, out result))
            {
                return result;
            }

            // This is the first time this type of object is seen, filter and cache its variables
            // Variable filtering process:
            // 1- skip Obsolete variables
            // 2- skip primitive types, enums and strings
            // 3- skip common Unity types that can't hold any references (e.g. Vector3, Rect, Color, Quaternion)
            // 
            // P.S. IsIgnoredUnityType() extension function handles steps 2) and 3)

            validVariables.Clear();

            // Filter the fields
            if (fieldModifiers != (BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var currType = type;
                while (currType != typeof(object))
                {
                    var fields = currType.GetFields(fieldModifiers);
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];

                        // Skip obsolete fields
                        if (Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                        {
                            continue;
                        }

                        // Skip primitive types
                        if (field.FieldType.IsIgnoredUnityType())
                        {
                            continue;
                        }

                        // Additional filtering for fields:
                        // 1- Ignore "m_RectTransform", "m_CanvasRenderer" and "m_Canvas" fields of Graphic components
                        var fieldName = field.Name;
                        if (typeof(Graphic).IsAssignableFrom(currType) &&
                            (fieldName == "m_RectTransform" || fieldName == "m_CanvasRenderer" || fieldName == "m_Canvas"))
                        {
                            continue;
                        }

                        var getter = field.CreateGetter(type);
                        if (getter != null)
                        {
                            validVariables.Add(new VariableGetterHolder(field, getter,
                                searchSerializableVariablesOnly ? field.IsSerializable() : true));
                        }
                    }

                    currType = currType.BaseType;
                }
            }

            if (propertyModifiers != (BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var currType = type;
                while (currType != typeof(object))
                {
                    var properties = currType.GetProperties(propertyModifiers);
                    for (var i = 0; i < properties.Length; i++)
                    {
                        var property = properties[i];

                        // Skip obsolete properties
                        if (Attribute.IsDefined(property, typeof(ObsoleteAttribute)))
                        {
                            continue;
                        }

                        // Skip primitive types
                        if (property.PropertyType.IsIgnoredUnityType())
                        {
                            continue;
                        }

                        // Skip properties without a getter function
                        var propertyGetter = property.GetGetMethod(true);
                        if (propertyGetter == null)
                        {
                            continue;
                        }

                        // Skip indexer properties
                        if (property.GetIndexParameters().Length > 0)
                        {
                            continue;
                        }

                        // No need to check properties with 'override' keyword
                        if (propertyGetter.GetBaseDefinition().DeclaringType != propertyGetter.DeclaringType)
                        {
                            continue;
                        }

                        var propertyName = property.Name;

                        // Ignore "gameObject", "transform", "rectTransform" and "attachedRigidbody" properties of components to get more useful results
                        if (typeof(Component).IsAssignableFrom(currType) && (propertyName == "gameObject" ||
                                                                             propertyName == "transform" ||
                                                                             propertyName == "attachedRigidbody" ||
                                                                             propertyName == "rectTransform"))
                        {
                            continue;
                        }
                        // Ignore "canvasRenderer" and "canvas" properties of Graphic components to get more useful results
                        if (typeof(Graphic).IsAssignableFrom(currType) &&
                            (propertyName == "canvasRenderer" || propertyName == "canvas"))
                        {
                            continue;
                        }
                        // Prevent accessing properties of Unity that instantiate an existing resource (causing memory leak)
                        if (typeof(MeshFilter).IsAssignableFrom(currType) && propertyName == "mesh")
                        {
                            continue;
                        }
                        // Same as above
                        if ((propertyName == "material" || propertyName == "materials") &&
                            (typeof(Renderer).IsAssignableFrom(currType) || typeof(Collider).IsAssignableFrom(currType) ||
#if !UNITY_2019_3_OR_NEWER
#pragma warning disable 0618
							typeof( GUIText ).IsAssignableFrom( currType ) ||
#pragma warning restore 0618
#endif
                             typeof(Collider2D).IsAssignableFrom(currType)))
                        {
                            continue;
                        }
                        // Ignore certain Material properties that are already searched via SearchMaterial function (also, if a material doesn't have a _Color or _BaseColor
                        // property and its "color" property is called, it logs an error to the console, so this rule helps avoid that scenario, as well)
                        if ((propertyName == "color" || propertyName == "mainTexture") && typeof(Material).IsAssignableFrom(currType))
                        {
                            continue;
                        }
                        // Ignore "parameters" property of Animator since it doesn't contain any useful data and logs a warning to the console when Animator is inactive
                        if (typeof(Animator).IsAssignableFrom(currType) && propertyName == "parameters")
                        {
                            continue;
                        }
                        // Ignore "spriteAnimator" property of TMP_Text component because this property adds a TMP_SpriteAnimator component to the object if it doesn't exist
                        if (propertyName == "spriteAnimator" && currType.Name == "TMP_Text")
                        {
                            continue;
                        }
                        // Ignore "meshFilter" property of TextMeshPro and TMP_SubMesh components because this property adds a MeshFilter component to the object if it doesn't exist
                        if (propertyName == "meshFilter" && (currType.Name == "TextMeshPro" || currType.Name == "TMP_SubMesh"))
                        {
                            continue;
                        }
                        // Ignore "users" property of TerrainData because it returns the Terrains in the scene that use that TerrainData. This causes issues with callStack because TerrainData
                        // is already in callStack when Terrains are searched via "users" property of it and hence, Terrain->TerrainData references for that TerrainData can't be found in scenes
                        // (this is how callStack works, it prevents searching an object if it's already in callStack to avoid infinite recursion)
                        if (propertyName == "users" && typeof(TerrainData).IsAssignableFrom(currType))
                        {
                            continue;
                        }
                        var getter = property.CreateGetter();
                        if (getter != null)
                        {
                            validVariables.Add(new VariableGetterHolder(property, getter,
                                searchSerializableVariablesOnly ? property.IsSerializable() : true));
                        }
                    }

                    currType = currType.BaseType;
                }
            }

            result = validVariables.ToArray();

            // Cache the filtered fields
            typeToVariables.Add(type, result);

            return result;
        }

        // Credit: http://answers.unity.com/answers/425602/view.html
        // Returns the raw System.Object value of a SerializedProperty
        private object GetRawSerializedPropertyValue(SerializedProperty property)
        {
            object result = property.serializedObject.targetObject;
            var path = property.propertyPath.Replace(".Array.data[", "[").Split('.');
            for (var i = 0; i < path.Length; i++)
            {
                var pathElement = path[i];

                var arrayStartIndex = pathElement.IndexOf('[');
                if (arrayStartIndex < 0)
                {
                    result = GetFieldValue(result, pathElement);
                }
                else
                {
                    var variableName = pathElement.Substring(0, arrayStartIndex);

                    var arrayEndIndex = pathElement.IndexOf(']', arrayStartIndex + 1);
                    var arrayElementIndex = int.Parse(pathElement.Substring(arrayStartIndex + 1, arrayEndIndex - arrayStartIndex - 1));
                    result = GetFieldValue(result, variableName, arrayElementIndex);
                }
            }

            return result;
        }

        // Credit: http://answers.unity.com/answers/425602/view.html
        private object GetFieldValue(object source, string fieldName)
        {
            if (source == null)
            {
                return null;
            }

            FieldInfo fieldInfo = null;
            var type = source.GetType();
            while (fieldInfo == null && type != typeof(object))
            {
                fieldInfo = type.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }

            if (fieldInfo != null)
            {
                return fieldInfo.GetValue(source);
            }

            PropertyInfo propertyInfo = null;
            type = source.GetType();
            while (propertyInfo == null && type != typeof(object))
            {
                propertyInfo = type.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly |
                    BindingFlags.IgnoreCase);
                type = type.BaseType;
            }

            if (propertyInfo != null)
            {
                return propertyInfo.GetValue(source, null);
            }

            if (fieldName.Length > 2 && fieldName.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
            {
                return GetFieldValue(source, fieldName.Substring(2));
            }

            return null;
        }

        // Credit: http://answers.unity.com/answers/425602/view.html
        private object GetFieldValue(object source, string fieldName, int arrayIndex)
        {
            var enumerable = GetFieldValue(source, fieldName) as IEnumerable;
            if (enumerable == null)
            {
                return null;
            }

            if (enumerable is IList)
            {
                return ((IList)enumerable)[arrayIndex];
            }

            var enumerator = enumerable.GetEnumerator();
            for (var i = 0; i <= arrayIndex; i++)
            {
                enumerator.MoveNext();
            }

            return enumerator.Current;
        }

        // Iterates over all occurrences of specific key-value pairs in string
        // Example1: #include "VALUE"  valuePrefix=#include, valueWrapperChar="
        // Example2: "guid": "VALUE"  valuePrefix="guid", valueWrapperChar="
        private void IterateOverValuesInString(string str, string[] valuePrefixes, char valueWrapperChar, Action<string> valueAction)
        {
            for (var i = 0; i < valuePrefixes.Length; i++)
            {
                var valuePrefix = valuePrefixes[i];
                int valueStartIndex, valueEndIndex = 0;
                while (true)
                {
                    valueStartIndex = str.IndexOf(valuePrefix, valueEndIndex);
                    if (valueStartIndex < 0)
                    {
                        break;
                    }

                    valueStartIndex = str.IndexOf(valueWrapperChar, valueStartIndex + valuePrefix.Length);
                    if (valueStartIndex < 0)
                    {
                        break;
                    }

                    valueStartIndex++;
                    valueEndIndex = str.IndexOf(valueWrapperChar, valueStartIndex);
                    if (valueEndIndex < 0)
                    {
                        break;
                    }

                    if (valueEndIndex > valueStartIndex)
                    {
                        valueAction(str.Substring(valueStartIndex, valueEndIndex - valueStartIndex));
                    }
                }
            }
        }

        // If obj is Component, switches to its GameObject
        private object PreferablyGameObject(object obj)
        {
            var component = obj as Component;
            return component != null && !component.Equals(null) ? component.gameObject : obj;
        }

        // Unity's internal function that returns a SerializedProperty's corresponding FieldInfo
        private delegate FieldInfo FieldInfoGetter(SerializedProperty p, out Type t);

		#region Helper Classes

#if UNITY_2017_3_OR_NEWER
#pragma warning disable 0649 // The fields' values are assigned via JsonUtility
        [Serializable]
        private struct AssemblyDefinitionReferences
        {
            public string reference; // Used by AssemblyDefinitionReferenceAssets
            public List<string> references; // Used by AssemblyDefinitionAssets
        }
#pragma warning restore 0649
#endif

#if UNITY_2018_1_OR_NEWER
#pragma warning disable 0649 // The fields' values are assigned via JsonUtility
        [Serializable]
        private struct ShaderGraphReferences // Used by old Shader Graph serialization format
        {
            public List<JSONHolder> m_SerializedProperties;
            public List<JSONHolder> m_SerializableNodes;

            // String can be in one of the following formats:
            // "guid":"GUID_VALUE"
            // "guid": "GUID_VALUE"
            // "guid" : "GUID_VALUE"
            private static string ExtractGUIDFromString(string str)
            {
                if (!string.IsNullOrEmpty(str))
                {
                    var guidStartIndex = str.IndexOf("\"guid\"");
                    if (guidStartIndex >= 0)
                    {
                        guidStartIndex += 6;
                        guidStartIndex = str.IndexOf('"', guidStartIndex);
                        if (guidStartIndex > 0)
                        {
                            guidStartIndex++;

                            var guidEndIndex = str.IndexOf('"', guidStartIndex);
                            if (guidEndIndex > 0)
                            {
                                return str.Substring(guidStartIndex, guidEndIndex - guidStartIndex);
                            }
                        }
                    }
                }

                return null;
            }

            [Serializable]
            public struct JSONHolder
            {
                public string JSONnodeData;
            }

            [Serializable]
            public class TextureHolder
            {
                public string m_SerializedTexture;
                public string m_SerializedCubemap;
                public string m_Guid;

                public string GetTexturePath()
                {
                    var guid = ExtractGUIDFromString(!string.IsNullOrEmpty(m_SerializedTexture) ? m_SerializedTexture : m_SerializedCubemap);
                    if (string.IsNullOrEmpty(guid))
                    {
                        guid = m_Guid;
                    }

                    return string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                }
            }

            [Serializable]
            public struct PropertyData
            {
                public string m_Name;
                public string m_DefaultReferenceName;
                public string m_OverrideReferenceName;
                public TextureHolder m_Value;

                public string GetName()
                {
                    if (!string.IsNullOrEmpty(m_OverrideReferenceName))
                    {
                        return m_OverrideReferenceName;
                    }
                    if (!string.IsNullOrEmpty(m_DefaultReferenceName))
                    {
                        return m_DefaultReferenceName;
                    }
                    if (!string.IsNullOrEmpty(m_Name))
                    {
                        return m_Name;
                    }

                    return "Property";
                }
            }

            [Serializable]
            public struct NodeData
            {
                public string m_Name;
                public string m_FunctionSource; // Custom Function node's Source field
                public string m_SerializedSubGraph; // Sub-graph node
                public List<JSONHolder> m_SerializableSlots;

                public string GetSubGraphPath()
                {
                    var guid = ExtractGUIDFromString(m_SerializedSubGraph);
                    return string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                }
            }

            [Serializable]
            public struct NodeSlotData
            {
                public TextureHolder m_Texture;
                public TextureHolder m_TextureArray;
                public TextureHolder m_Cubemap;

                public string GetTexturePath()
                {
                    if (m_Texture != null)
                    {
                        return m_Texture.GetTexturePath();
                    }
                    if (m_Cubemap != null)
                    {
                        return m_Cubemap.GetTexturePath();
                    }
                    if (m_TextureArray != null)
                    {
                        return m_TextureArray.GetTexturePath();
                    }

                    return null;
                }
            }
        }
#pragma warning restore 0649
#endif

		#endregion

#if UNITY_2017_1_OR_NEWER
        private ReferenceNode SearchSpriteAtlas(Object unityObject)
        {
            var spriteAtlas = (SpriteAtlas)unityObject;
            var referenceNode = PopReferenceNode(spriteAtlas);

            var spriteAtlasSO = new SerializedObject(spriteAtlas);
            if (spriteAtlas.isVariant)
            {
                var masterAtlasProperty = spriteAtlasSO.FindProperty("m_MasterAtlas");
                var masterAtlas = masterAtlasProperty.objectReferenceValue;
                if (objectsToSearchSet.Contains(masterAtlas))
                {
                    referenceNode.AddLinkTo(SearchObject(masterAtlas), "Master Atlas");

                    if (searchParameters.searchRefactoring != null)
                    {
                        searchParameters.searchRefactoring(new SerializedPropertyMatch(spriteAtlas, masterAtlas, masterAtlasProperty));
                    }
                }
            }

            var packables = spriteAtlasSO.FindProperty("m_EditorData.packables");
            if (packables != null)
            {
                for (int i = 0, length = packables.arraySize; i < length; i++)
                {
                    var packedSpriteProperty = packables.GetArrayElementAtIndex(i);
                    var packedSprite = packedSpriteProperty.objectReferenceValue;
                    SearchSpriteAtlas(referenceNode, packedSprite);

                    if (searchParameters.searchRefactoring != null && objectsToSearchSet.Contains(packedSprite))
                    {
                        searchParameters.searchRefactoring(new SerializedPropertyMatch(spriteAtlas, packedSprite, packedSpriteProperty));
                    }
                }
            }
#if UNITY_2018_2_OR_NEWER
            else
            {
                var _packables = spriteAtlas.GetPackables();
                if (_packables != null)
                {
                    for (var i = 0; i < _packables.Length; i++)
                    {
                        SearchSpriteAtlas(referenceNode, _packables[i]);
                    }
                }
            }
#endif

            return referenceNode;
        }

        private void SearchSpriteAtlas(ReferenceNode referenceNode, Object packedAsset)
        {
            if (packedAsset == null || packedAsset.Equals(null))
            {
                return;
            }

            referenceNode.AddLinkTo(SearchObject(packedAsset), "Packed Texture");

            if (packedAsset is Texture)
            {
                // Search the Texture's sprites if the Texture asset isn't included in the "SEARCHED OBJECTS" list (i.e. user has
                // added only a Sprite sub-asset of the Texture to the list, not the Texture asset itself). Otherwise, references to
                // both the Texture and its sprites will be found which can be considered as duplicate references
                if (AssetDatabase.IsMainAsset(packedAsset) && !assetsToSearchSet.Contains(packedAsset))
                {
                    var textureSubAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(AssetDatabase.GetAssetPath(packedAsset));
                    for (var i = 0; i < textureSubAssets.Length; i++)
                    {
                        if (textureSubAssets[i] is Sprite)
                        {
                            referenceNode.AddLinkTo(SearchObject(textureSubAssets[i]), "Packed Texture");
                        }
                    }
                }
            }
            else if (packedAsset.IsFolder())
            {
                // Search all Sprites in the folder
                var texturesInFolder = AssetDatabase.FindAssets("t:Texture2D", new[] { AssetDatabase.GetAssetPath(packedAsset) });
                if (texturesInFolder != null)
                {
                    for (var i = 0; i < texturesInFolder.Length; i++)
                    {
                        var texturePath = AssetDatabase.GUIDToAssetPath(texturesInFolder[i]);
                        var textureImporter = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                        if (textureImporter != null && textureImporter.textureType == TextureImporterType.Sprite)
                        {
                            // Search the Texture and its sprites
                            SearchSpriteAtlas(referenceNode, AssetDatabase.LoadMainAssetAtPath(texturePath));
                        }
                    }
                }
            }
        }
#endif
    }
}