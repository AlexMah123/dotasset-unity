using UnityEngine;

namespace DotAsset.Tests
{
    // Stands in for a TimelineAsset: a ScriptableObject saved as .playable, its recorded clips stored inside it.
    public class RunTestTimeline : ScriptableObject
    {
        public int tracks = 1;
    }
}
