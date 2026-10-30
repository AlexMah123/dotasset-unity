using UnityEngine;

namespace DotAsset.Smoke
{
    // Smoke test only. OnValidate clamps a negative amount to 0, the case Edit runs must catch: Unity keeps a value
    // other than the one the spec wrote, and the run fails the target instead of saving it.
    public sealed class SmokeClamped : ScriptableObject
    {
        public int amount;

        void OnValidate()
        {
            if (amount < 0)
            {
                amount = 0;
            }
        }
    }
}
