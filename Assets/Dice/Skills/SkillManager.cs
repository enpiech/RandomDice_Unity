using System.Collections.Generic;
using UnityEngine;

namespace Dice.Skills
{
    public class SkillManager : MonoBehaviour
    {
        [SerializeField]
        public List<GameObject> skillList = new();
    }
}