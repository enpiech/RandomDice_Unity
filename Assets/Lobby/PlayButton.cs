using Dice;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Lobby
{
    public class PlayButton : MonoBehaviour
    {
        public Text[] diceIdText = new Text[5];

        public void play()
        {
            for (var i = 0; i < 5; i++)
            {
                DiceManager.deckIdArray[i] = int.Parse(diceIdText[i].text);
            }

            SceneManager.LoadScene("PlayScene");
        }
    }
}