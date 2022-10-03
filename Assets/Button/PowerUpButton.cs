using Dice;
using UnityEngine;

namespace Button
{
    public class PowerUpButton : MonoBehaviour
    {
        public int deckIndex;
        public GameObject diceManager;
        public int diceId;
        private DiceInfo.DiceStruct[] deckArray; // 주사위 덱 정보

        private void Start()
        {
            deckArray = diceManager.GetComponent<DiceManager>().deckArray;
        }

        private void Update()
        {
            diceId = deckArray[deckIndex].id;
            diceManager.GetComponent<DiceManager>().loadDiceSprite(gameObject, deckArray[deckIndex].rarity, deckArray[deckIndex].spriteID);
        }
    }
}