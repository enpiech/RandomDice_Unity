using MyBox;
using UnityEngine;

namespace BattleGround
{
    public sealed class BattleGroundManager : MonoBehaviour
    {
        [SerializeField]
        [ConstantsSelection(typeof(Vector3))]
        private Vector3 _startDirection = Vector3.up;

        [SerializeField]
        [ConstantsSelection(typeof(Vector3))]
        private Vector3 _firstTurnDirection = Vector3.right;

        [SerializeField]
        [ConstantsSelection(typeof(Vector3))]
        private Vector3 _secondTurnDirection = Vector3.down;

        [Header("References")]
        [SerializeField]
        private Transform _firstCorner = default!;

        [SerializeField]
        private Transform _secondCorner = default!;

        public Vector3 StartDirection => _startDirection;

        public Vector3 FirstTurnDirection => _firstTurnDirection;

        public Vector3 SecondTurnDirection => _secondTurnDirection;

        public Transform FirstCorner => _firstCorner;

        public float FirstCornerY => _firstCorner.position.y;

        public Transform SecondCorner => _secondCorner;

        public float SecondCornerX => _secondCorner.position.x;
    }
}