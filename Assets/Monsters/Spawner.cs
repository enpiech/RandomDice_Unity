using UnityEngine;
using Wave;

namespace Monsters
{
    public class Spawner : MonoBehaviour
    {
        public GameObject monsterManager;
        public GameObject waveObject;
        private readonly float[] spawnTimer = new float[3]; // 몬스터 스폰 타이머(common, speed, big)

        private int spawnHp; // 몬스터 스폰될 때 체력
        private WaveObject waveObjectScript;

        private void Start()
        {
            waveObjectScript = waveObject.GetComponent<WaveObject>();
            spawnHp = 100;
            spawnTimer[0] = 0.0f;
            spawnTimer[1] = -5.0f;
            spawnTimer[2] = -10.0f;
        }

        private void Update()
        {
            // 스폰 체력 설정
            if (waveObjectScript.Timer > 60)
            {
                spawnHp = 100 * waveObjectScript.Wave;
            }
            else
            {
                spawnHp = Mathf.RoundToInt(100 * waveObjectScript.Wave * (6 - waveObjectScript.Timer / 10));
            }


            // 타이머를 deltaTime만큼 증가
            for (var i = 0; i < spawnTimer.Length; i++)
            {
                spawnTimer[i] += Time.deltaTime;
            }

            // 주기적으로 몬스터 생성
            if (spawnTimer[0] >= 2.0f)
            {
                // big
                if (spawnTimer[2] >= 10.0f)
                {
                    monsterManager.GetComponent<MonsterManager>().AddMonster(2, spawnHp * 5);
                    spawnTimer[2] = 0.0f;
                }
                // speed
                else if (spawnTimer[1] >= 10.0f)
                {
                    monsterManager.GetComponent<MonsterManager>().AddMonster(1, spawnHp / 2);
                    spawnTimer[1] = 0.0f;
                }
                // common
                else
                {
                    monsterManager.GetComponent<MonsterManager>().AddMonster(0, spawnHp);
                }
                spawnTimer[0] = 0.0f;
            }
        }
    }
}