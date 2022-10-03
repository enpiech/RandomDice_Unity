using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Plugins.AssetUsageDetector.Editor
{
    public partial class AssetUsageDetector
    {
        // An optimization to fetch the dependencies of an asset only once (key is the path of the asset)
        private Dictionary<string, CacheEntry> assetDependencyCache;
        private CacheEntry lastRefreshedCacheEntry;

        private string CachePath => Application.dataPath + "/../Library/AssetUsageDetector.cache"; // Path of the cache file

        public void SaveCache()
        {
            if (assetDependencyCache == null)
            {
                return;
            }

            try
            {
                using (var stream = new FileStream(CachePath, FileMode.Create))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(assetDependencyCache.Count);

                    foreach (var keyValuePair in assetDependencyCache)
                    {
                        var cacheEntry = keyValuePair.Value;
                        var dependencies = cacheEntry.dependencies;
                        var fileSizes = cacheEntry.fileSizes;

                        writer.Write(keyValuePair.Key);
                        writer.Write(cacheEntry.hash);
                        writer.Write(dependencies.Length);

                        for (var i = 0; i < dependencies.Length; i++)
                        {
                            writer.Write(dependencies[i]);
                            writer.Write(fileSizes[i]);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void LoadCache()
        {
            if (File.Exists(CachePath))
            {
                using (var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    try
                    {
                        var cacheSize = reader.ReadInt32();
                        assetDependencyCache = new Dictionary<string, CacheEntry>(cacheSize);

                        for (var i = 0; i < cacheSize; i++)
                        {
                            var assetPath = reader.ReadString();
                            var hash = reader.ReadString();

                            var dependenciesLength = reader.ReadInt32();
                            var dependencies = new string[dependenciesLength];
                            var fileSizes = new long[dependenciesLength];
                            for (var j = 0; j < dependenciesLength; j++)
                            {
                                dependencies[j] = reader.ReadString();
                                fileSizes[j] = reader.ReadInt64();
                            }

                            assetDependencyCache[assetPath] = new CacheEntry(hash, dependencies, fileSizes);
                        }
                    }
                    catch (Exception e)
                    {
                        assetDependencyCache = null;
                        Debug.LogWarning("Couldn't load cache (probably cache format has changed in an update), will regenerate cache.\n" + e);
                    }
                }
            }

            // Generate cache for all assets for the first time
            if (assetDependencyCache == null)
            {
                assetDependencyCache = new Dictionary<string, CacheEntry>(1024 * 8);

                var allAssets = AssetDatabase.GetAllAssetPaths();
                if (allAssets.Length > 0)
                {
                    var startTime = EditorApplication.timeSinceStartup;

                    try
                    {
                        for (var i = 0; i < allAssets.Length; i++)
                        {
                            if (i % 30 == 0 && EditorUtility.DisplayCancelableProgressBar("Please wait...",
                                    "Generating cache for the first time (optional)", (float)i / allAssets.Length))
                            {
                                EditorUtility.ClearProgressBar();
                                Debug.LogWarning(
                                    "Initial cache generation cancelled, cache will be generated on the fly as more and more assets are searched.");
                                break;
                            }

                            assetDependencyCache[allAssets[i]] = new CacheEntry(allAssets[i]);
                        }

                        EditorUtility.ClearProgressBar();

                        Debug.Log("Cache generated in " + (EditorApplication.timeSinceStartup - startTime).ToString("F2") + " seconds");
                        Debug.Log("You can always reset the cache by deleting " + Path.GetFullPath(CachePath));

                        SaveCache();
                    }
                    catch (Exception e)
                    {
                        EditorUtility.ClearProgressBar();
                        Debug.LogException(e);
                    }
                }
            }
        }

		#region Helper Classes

        private class CacheEntry
        {
            public enum Result
            {
                Unknown = 0,
                No = 1,
                Yes = 2
            }

            public string[] dependencies;
            public long[] fileSizes;

            public string hash;
            public Result searchResult;

            public bool verified;

            public CacheEntry(string path)
            {
                Verify(path);
            }

            public CacheEntry(string hash, string[] dependencies, long[] fileSizes)
            {
                this.hash = hash;
                this.dependencies = dependencies;
                this.fileSizes = fileSizes;
            }

            public void Verify(string path)
            {
                var hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                if (this.hash != hash)
                {
                    this.hash = hash;
                    Refresh(path);
                }

                verified = true;
            }

            public void Refresh(string path)
            {
                dependencies = AssetDatabase.GetDependencies(path, false);
                if (fileSizes == null || fileSizes.Length != dependencies.Length)
                {
                    fileSizes = new long[dependencies.Length];
                }

                var length = dependencies.Length;
                for (var i = 0; i < length; i++)
                {
                    if (!string.IsNullOrEmpty(dependencies[i]))
                    {
                        var assetFile = new FileInfo(dependencies[i]);
                        fileSizes[i] = assetFile.Exists ? assetFile.Length : 0L;
                    }
                    else
                    {
                        // This dependency is empty which causes issues when passed to FileInfo constructor
                        // Find a non-empty dependency and move it to this index
                        for (var j = length - 1; j > i; j--, length--)
                        {
                            if (!string.IsNullOrEmpty(dependencies[j]))
                            {
                                dependencies[i--] = dependencies[j];
                                break;
                            }
                        }

                        length--;
                    }
                }

                if (length != fileSizes.Length)
                {
                    Array.Resize(ref dependencies, length);
                    Array.Resize(ref fileSizes, length);
                }
            }
        }

		#endregion
    }
}