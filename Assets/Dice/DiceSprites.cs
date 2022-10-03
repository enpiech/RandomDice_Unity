using System.Collections.Generic;
using UnityEngine;

namespace Dice
{
    public class DiceSprites : MonoBehaviour
    {
        [SerializeField]
        public List<Sprite> Common = new();

        [SerializeField]
        public List<Sprite> Rare = new();

        [SerializeField]
        public List<Sprite> Unique = new();

        [SerializeField]
        public List<Sprite> Legendary = new();
    }
}