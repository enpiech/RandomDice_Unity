using System.Collections.Generic;
using BattleGround;
using UnityEngine;

namespace Monsters
{
    public sealed class MonsterManager : MonoBehaviour
    {
        private const int MONSTER_SIZE = 12;
        public static readonly string[] MONSTER_DATA_TEXT = new string[MONSTER_SIZE];

        [Header("References")]
        [SerializeField]
        private BattleGroundManager _battleGroundManager;

        [SerializeField]
        private Player.Player _player;

        [SerializeField]
        private Monster _monsterPrefab;

        [SerializeField]
        private Spawner _monsterSpawner;

        [SerializeField]
        private MonsterDatabaseConnector _monsterDataBaseConnector;

        public List<GameObject> _monsterList = new();

        [SerializeField]
        private MonsterSprites _monsterSprites;

        private readonly MonsterInfo.MonsterStruct[] _monsterInfoArray = new MonsterInfo.MonsterStruct[MONSTER_SIZE];

        private void Start()
        {
            for (var i = 0; i < MONSTER_SIZE; i++)
            {
                _monsterDataBaseConnector.getMonsterInfoFromDatabase(i);
            }
        }

        private void Update()
        {
            for (var i = 0; i < MONSTER_SIZE; i++)
            {
                if (MONSTER_DATA_TEXT[i] != null)
                {
                    _monsterInfoArray[i] = new MonsterInfo.MonsterStruct(MONSTER_DATA_TEXT[i]);
                }
            }
        }

        public void AddMonster(int monsterID, int monsterHp)
        {
            var newMonster = Instantiate(_monsterPrefab, _monsterSpawner.transform.position, Quaternion.identity);

            newMonster.Init(_battleGroundManager, this, _player);
            newMonster.MonsterStruct = _monsterInfoArray[monsterID];
            newMonster.MonsterStruct.hp = monsterHp;

            var monsterId = newMonster.MonsterStruct.id;

            if (monsterId == 1)
            {
                _monsterList.Insert(0, newMonster.gameObject);
            }
            else
            {
                _monsterList.Add(newMonster.gameObject);
            }

            ChangeMonsterSize(monsterId, newMonster.gameObject);
        }

        private static void ChangeMonsterSize(int monsterId, GameObject newMonster)
        {
            newMonster.transform.localScale = monsterId switch
            {
                1 => new Vector3(0.35f, 0.35f, 1),
                2 => new Vector3(0.5f, 0.5f, 1),
                _ => newMonster.gameObject.transform.localScale
            };
        }

        public void RemoveMonster(GameObject monster)
        {
            _monsterList.Remove(monster);
        }

        public void LoadMonsterSprite(Monster monster, int spriteNum)
        {
            monster.SetSprite(_monsterSprites.Sprites[spriteNum]);
        }
    }
}