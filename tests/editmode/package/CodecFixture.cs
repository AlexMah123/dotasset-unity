using System;
using System.Collections.Generic;
using UnityEngine;

namespace DotAsset.Tests
{
    public enum CodecKind
    {
        First,
        Second,
        Third,
    }

    [Serializable]
    public struct CodecPair
    {
        public int a;
        public string b;
    }

    // One field of each kind ValueCodecTests writes and reads, created in memory only.
    public sealed class CodecFixture : ScriptableObject
    {
        public int count;
        public byte small;
        public long big;
        public bool flag;
        public float ratio;
        public string label;
        public CodecKind kind;
        public Vector3 offset;
        public Color tint;
        public Quaternion turn;
        public List<int> numbers = new List<int>();
        public CodecPair pair;
        public AnimationCurve curve = new AnimationCurve();
    }
}
