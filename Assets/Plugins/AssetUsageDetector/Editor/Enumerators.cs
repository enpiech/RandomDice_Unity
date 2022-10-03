using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Plugins.AssetUsageDetector.Editor
{
    public class EmptyEnumerator<T> : IEnumerable<T>, IEnumerator<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            return this;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        }

        public T Current => default;

        object IEnumerator.Current => Current;

        public void Dispose()
        {
        }

        public void Reset()
        {
        }

        public bool MoveNext()
        {
            return false;
        }
    }

    public class ObjectToSearchEnumerator : IEnumerable<Object>
    {
        private readonly List<ObjectToSearch> source;

        public ObjectToSearchEnumerator(List<ObjectToSearch> source)
        {
            this.source = source;
        }

        public IEnumerator<Object> GetEnumerator()
        {
            return new Enumerator(source);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public Object[] ToArray()
        {
            var count = 0;
            foreach (var obj in this)
            {
                count++;
            }

            var result = new Object[count];
            var index = 0;
            foreach (var obj in this)
            {
                result[index++] = obj;
            }

            return result;
        }

        public class Enumerator : IEnumerator<Object>
        {
            private int index;

            private List<ObjectToSearch> source;
            private int subAssetIndex;

            public Enumerator(List<ObjectToSearch> source)
            {
                this.source = source;
                Reset();
            }

            public Object Current
            {
                get
                {
                    if (subAssetIndex < 0)
                    {
                        return source[index].obj;
                    }

                    return source[index].subAssets[subAssetIndex].subAsset;
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                source = null;
            }

            public bool MoveNext()
            {
                if (subAssetIndex < -1)
                {
                    subAssetIndex = -1;

                    if (++index >= source.Count)
                    {
                        return false;
                    }

                    // Skip folder assets in the enumeration, AssetUsageDetector expands encountered folders automatically
                    // and we don't want that to happen as source[index].subAssets already contains the folder's contents
                    if (!source[index].obj.IsFolder())
                    {
                        return true;
                    }
                }

                var subAssets = source[index].subAssets;
                if (subAssets != null)
                {
                    while (++subAssetIndex < subAssets.Count && !subAssets[subAssetIndex].shouldSearch)
                    {
                    }

                    if (subAssetIndex < subAssets.Count)
                    {
                        return true;
                    }
                }

                subAssetIndex = -2;
                return MoveNext();
            }

            public void Reset()
            {
                index = -1;
                subAssetIndex = -2;
            }
        }
    }
}