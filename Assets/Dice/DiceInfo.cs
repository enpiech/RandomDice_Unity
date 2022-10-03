using System.Collections.Generic;
using UnityEngine;

namespace Dice
{
    public class DiceInfo : MonoBehaviour
    {
        // 깊은 복사를 위한 구조체 사용(클래스는 얕은 복사: 필드 내 같은 종류의 주사위는 무조건 같은 눈금이 적용됨)
        public struct DiceStruct
        {
            public int id; // 주사위 ID
            public string name; // 주사위 이름
            public string rarity; // 희귀도
            public int spriteID; // 스프라이트 ID
            public float attackDamage; // 기본 공격력
            public float attackSpeed; // 공격 속도(초)
            public string target; // 타겟
            public float s0; // 특수 능력치 0
            public float s1; // 특수 능력치 1
            public float s2; // 특수 능력치 2
            public Color32 color; // 투사체 색상
            public int level; // 레벨(눈금) - 초기에는 1눈금

            public DiceStruct(string diceDataText)
            {
                // 구분자('/')를 기준으로 문자열을 토큰으로 분해하여 임시 리스트에 추가
                var tokens = diceDataText.Split('/');
                var tempList = new List<string>();
                foreach (var token in tokens)
                {
                    tempList.Add(token);
                }

                // 리스트에 저장된 각 토큰의 값을 순서대로 구조체 멤버 변수에 대입
                id = int.Parse(tempList[0]);
                name = tempList[1];
                rarity = tempList[2];
                spriteID = int.Parse(tempList[3]);
                attackDamage = float.Parse(tempList[4]);
                attackSpeed = float.Parse(tempList[5]);
                target = tempList[6];
                s0 = float.Parse(tempList[7]);
                s1 = float.Parse(tempList[8]);
                s2 = float.Parse(tempList[9]);
                color = new Color32(byte.Parse(tempList[10]), byte.Parse(tempList[11]), byte.Parse(tempList[12]), 255);
                level = 1;
            }
        }
    }
}