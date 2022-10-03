using System;
using Dice.Skills;
using Monsters;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Dice.Bullet
{
    public class Bullet : MonoBehaviour
    {
        public GameObject damageTextPrefab;
        public int targetIndex;
        public DiceInfo.DiceStruct diceStruct;
        private MonsterManager monsterManager;
        private SkillManager skillManager;

        private void Start()
        {
            monsterManager = GameObject.Find("MonsterManager").GetComponent<MonsterManager>();
            skillManager = GameObject.Find("SkillManager").GetComponent<SkillManager>();
            GetComponent<SpriteRenderer>().color = diceStruct.color;
        }

        private void Update()
        {
            // 필드에 몬스터가 없으면 투사체 소멸
            if (monsterManager._monsterList.Count == 0)
            {
                Destroy(gameObject);
            }

            // 투사체 발사
            try
            {
                var targetMonster = monsterManager._monsterList[targetIndex];
                var targetPosition = targetMonster.transform.position;
                transform.position = Vector3.MoveTowards(transform.position, targetPosition, 10 * Time.deltaTime);
            }
            catch (ArgumentOutOfRangeException e)
            {
                Destroy(gameObject);
            }
        }


        // 몬스터와 투사체 충돌 시(트리거)
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other.tag == "Monster")
            {
                var monster = other.GetComponent<Monster>();

                // 도박 주사위 랜덤 데미지
                if (diceStruct.name == "도박")
                {
                    diceStruct.attackDamage = Random.Range(diceStruct.attackDamage, diceStruct.attackDamage * 25 + 1);
                }

                monster.MonsterStruct.hp -= (int)diceStruct.attackDamage;
                var damageText = Instantiate(damageTextPrefab, monster.transform.position, Quaternion.identity);
                damageText.GetComponent<TextMesh>().text = ((int)diceStruct.attackDamage).ToString();

                Destroy(gameObject); // 총알 삭제

                // 스킬 발동
                switch (diceStruct.name)
                {
                    case "불":
                        var fire = Instantiate(skillManager.skillList[0]);
                        fire.transform.position = transform.position;
                        fire.GetComponent<Fire>().damage = diceStruct.s0;
                        break;
                    case "전기":
                        GameObject electricMonster;
                        for (var i = 0; i < 3; i++)
                        {
                            try
                            {
                                electricMonster = monsterManager._monsterList[i].gameObject;
                                var electric = Instantiate(skillManager.skillList[1]);
                                electric.transform.position = electricMonster.transform.position;
                                electric.GetComponent<Fire>().damage = diceStruct.s0;
                            }
                            catch (ArgumentOutOfRangeException e)
                            {
                                break;
                            }
                        }
                        break;
                    case "독":
                        monster.GetComponent<MonsterStatus>()._poisonDamage = diceStruct.s0;
                        monster.GetComponent<MonsterStatus>()._isPoison = true;
                        break;
                    case "얼음":
                        monster.GetComponent<MonsterStatus>()._freezeEffect = diceStruct.s0;
                        monster.GetComponent<MonsterStatus>()._isFreeze = true;
                        break;
                }
            }
        }

        // 투사체가 일정 범위 밖으로 벗어나면 true 리턴
        private bool isOutOfRange(Vector3 pos)
        {
            if (pos.x > 3 || pos.y < -3 || pos.y > 2 || pos.y < -2)
            {
                return true;
            }
            return false;
        }
    }
}