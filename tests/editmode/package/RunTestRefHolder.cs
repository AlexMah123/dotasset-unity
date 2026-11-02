using System;
using UnityEngine;

namespace DotAsset.Tests
{
    // [SerializeReference] data for RenameSerializeReferenceType and the R5 audit rule.
    public class RunTestRefHolder : ScriptableObject
    {
        [SerializeReference] public RunTestRefBase item;
    }

    [Serializable]
    public abstract class RunTestRefBase
    {
        public int x;
    }

    [Serializable]
    public class RunTestRefItem : RunTestRefBase
    {
    }
}
