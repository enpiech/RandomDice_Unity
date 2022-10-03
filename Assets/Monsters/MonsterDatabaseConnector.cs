using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Monsters
{
    public class MonsterDatabaseConnector : MonoBehaviour
    {
        private readonly string selectURL = "http://152.70.94.65/random_dice/select_monster.php"; // PHP 스크립트

        // 데이터베이스에서 몬스터 정보 가져오기(SELECT)
        public void getMonsterInfoFromDatabase(int monsterId)
        {
            StartCoroutine(selectMonsterInfo(monsterId));
        }

        // 데이터베이스에서 몬스터 정보 가져오기(SELECT)
        private IEnumerator selectMonsterInfo(int monsterId)
        {
            // 데이터 POST 전송
            var form = new WWWForm();
            form.AddField("monster_id_field", monsterId.ToString());
            var webRequest = UnityWebRequest.Post(selectURL, form);
            yield return webRequest.SendWebRequest();

            // 메시지 출력
            if (webRequest.error != null)
            {
                Debug.Log(webRequest.error);
            }
            else
            {
                var dataText = webRequest.downloadHandler.text;
                MonsterManager.MONSTER_DATA_TEXT[monsterId] = dataText; // MonsterManager로 텍스트 전달
            }
        }
    }
}