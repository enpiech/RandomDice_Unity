using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Dice.Skills
{
    public class Growth : MonoBehaviour
    {
        private Dice dice;
        private float time;

        private void Start()
        {
            dice = gameObject.GetComponent<Dice>();
        }

        private void Update()
        {
            time += Time.deltaTime;
            if (time >= dice.diceStruct.s0)
            {
                // 성장 대기 시간이 끝나면
                var diceManagerScript = GameObject.Find("DiceManager").GetComponent<DiceManager>();

                // 현재 주사위의 정보 저장
                var diceIndex = Array.IndexOf(diceManagerScript.diceFieldArray, gameObject);
                var diceName = dice.diceStruct.name;
                var diceLevel = dice.diceStruct.level;
                var diceS1 = dice.diceStruct.s1;

                // 기존 주사위 제거
                dice.destroyDice();

                switch (diceName)
                {
                    case "도박 성장": // 도박 성장 주사위: 같은 자리에 랜덤 주사위 생성
                        diceManagerScript.createDice(diceIndex, Random.Range(1, 8), -1);
                        break;
                    case "고장난 성장": // 고장난 성장 주사위: 같은 자리에 +1 또는 -1 주사위 생성
                        var randomNum = Random.Range(0, 100);
                        // 성장 실패
                        if (randomNum < diceS1)
                        {
                            if (diceLevel > 1) // 2눈금 이상 -> 1눈금 감소
                            {
                                diceManagerScript.createDice(diceIndex, diceLevel - 1, -1);
                            }
                            // 성장 성공
                            else if (diceLevel < 7) // 7눈금 미만일 경우 성장
                            {
                                diceManagerScript.createDice(diceIndex, diceLevel + 1, -1);
                            }
                        }
                        break;
                    case "성장": // 성장 주사위: 같은 자리에 +1 주사위 생성
                        if (diceLevel < 7)
                        {
                            diceManagerScript.createDice(diceIndex, diceLevel + 1, -1);
                        }
                        break;
                }
            }
        }
    }
}