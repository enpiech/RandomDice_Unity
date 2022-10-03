using Monsters;
using UnityEngine;

namespace Dice.Skills
{
    public class Fire : MonoBehaviour
    {
        public float damage;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other.tag == "Monster")
            {
                other.GetComponent<Monster>().MonsterStruct.hp -= (int)damage;
            }
            Destroy(gameObject);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            Destroy(gameObject);
        }
    }
}