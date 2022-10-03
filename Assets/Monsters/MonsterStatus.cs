using UnityEngine;

namespace Monsters
{
    public sealed class MonsterStatus : MonoBehaviour
    {
        public bool _isPoison;
        public float _poisonDamage;

        public bool _isFreeze;
        public float _freezeEffect;

        [SerializeField]
        private Monster _monster;

        [SerializeField]
        private float _poisonTickInterval = 1f;

        private float _time;

        private void Update()
        {
            _time += Time.deltaTime;

            if (_time >= _poisonTickInterval)
            {
                if (_isPoison)
                {
                    TickPoison();
                }
                _time = 0.0f;
            }

            if (_isFreeze)
            {
                ApplyFreeze();
            }
        }

        private void TickPoison()
        {
            _monster.MonsterStruct.hp -= (int)_poisonDamage;
        }

        private void ApplyFreeze()
        {
            _monster.MonsterStruct.moveSpeed = 1 - _freezeEffect * 0.01f;
        }
    }
}