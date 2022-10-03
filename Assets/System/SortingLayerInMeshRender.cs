using UnityEngine;

namespace System
{
    public class SortingLayerInMeshRender : MonoBehaviour
    {
        public string sortingLayerName;
        public int sortingOrder;

        private void Start()
        {
            var mesh = GetComponent<MeshRenderer>();
            mesh.sortingLayerName = sortingLayerName;
            mesh.sortingOrder = sortingOrder;
        }
    }
}