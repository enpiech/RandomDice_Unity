using BattleGround;
using MyBox;
using UnityEngine;

namespace Monsters
{
    public sealed class Monster : MonoBehaviour
    {
        [Header("Configs")]
        [SerializeField]
        [ConstantsSelection(typeof(Vector3))]
        private Vector3 _direction = Vector3.zero;

        public GameObject _hpTextPrefab;

        [SerializeField]
        private SpriteRenderer _spriteRenderer;

        private BattleGroundManager _battleGroundManager = default!;

        private bool _isInit;
        private MonsterManager _monsterManager;
        private Player.Player _player;

        public MonsterInfo.MonsterStruct MonsterStruct;

        private void Awake()
        {
            _isInit = false;
        }

        private void Update()
        {
            if (!_isInit)
            {
                return;
            }

            _monsterManager.LoadMonsterSprite(this, MonsterStruct.id);
            UpdateHpText();
            if (MonsterStruct.hp <= 0)
            {
                Die();
                return;
            }
            Move();
        }

        public void Init(BattleGroundManager battleGroundManager, MonsterManager monsterManager, Player.Player player)
        {
            _battleGroundManager = battleGroundManager;
            _monsterManager = monsterManager;
            _player = player;

            _hpTextPrefab = Instantiate(_hpTextPrefab);
            _direction = _battleGroundManager.StartDirection;
        }

        private void UpdateHpText()
        {
            _hpTextPrefab.GetComponent<TextMesh>().text = MonsterStruct.hp.ToString();
            _hpTextPrefab.gameObject.transform.position = transform.position;
        }

        private void Move()
        {
            if (transform.position.y >= _battleGroundManager.FirstCornerY)
            {
                _direction = transform.position.x >= _battleGroundManager.SecondCornerX
                    ? _battleGroundManager.SecondTurnDirection
                    : _battleGroundManager.FirstTurnDirection;
            }
            transform.position += _direction * (MonsterStruct.moveSpeed * Time.deltaTime);
        }

        private void Die()
        {
            MonsterStruct.hp = 0;
            Destroy(gameObject);
            Destroy(_hpTextPrefab);
            _monsterManager.RemoveMonster(gameObject);
            _player.sp += 10; // SP 획득
            if (MonsterStruct.id >= 2)
            {
                _player.sp += 90; // big 및 보스 몬스터 처치 시 추가 SP 획득
            }
        }

        public void SetSprite(Sprite sprite)
        {
            _spriteRenderer.sprite = sprite;
        }
    }
}