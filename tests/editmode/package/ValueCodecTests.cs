using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // ValueCodec on an in-memory ScriptableObject: what a write accepts and refuses, and that an export reads back
    // as a spec that changes nothing.
    public sealed class ValueCodecTests
    {
        CodecFixture fixture;
        SerializedObject so;

        [SetUp]
        public void SetUp()
        {
            fixture = ScriptableObject.CreateInstance<CodecFixture>();
            so = new SerializedObject(fixture);
        }

        [TearDown]
        public void TearDown()
        {
            so.Dispose();
            Object.DestroyImmediate(fixture);
        }

        bool Write(string path, JToken value, out List<string> errors)
        {
            var wc = new WriteContext();
            bool ok = ValueCodec.Write(so.FindProperty(path), value, wc);
            so.ApplyModifiedPropertiesWithoutUndo();
            errors = wc.Errors;
            return ok;
        }

        void Write(string path, JToken value)
        {
            List<string> errors;
            Assert.IsTrue(Write(path, value, out errors), path + ": " + string.Join(", ", errors.ToArray()));
        }

        void Refused(string path, JToken value)
        {
            List<string> errors;
            Assert.IsFalse(Write(path, value, out errors), path + " took " + value);
            Assert.AreEqual(1, errors.Count);
            StringAssert.StartsWith(path, errors[0]);
        }

        JToken Read(string path, ReadOptions opt = null)
        {
            so.Update();
            return ValueCodec.Read(so.FindProperty(path), opt ?? new ReadOptions());
        }

        [Test]
        public void WritesEachKind()
        {
            Write("count", 42);
            Write("big", JToken.Parse("9007199254740993"));
            Write("flag", "1");
            Write("ratio", 0.25);
            Write("label", "hi");
            Write("kind", "Third");
            Write("offset", new JArray(1, 2, 3));
            Write("tint", "#FF000080");
            Write("turn", new JArray(0, 90, 0));
            Write("numbers", new JArray(4, 5, 6));
            Write("pair", new JObject { ["b"] = "x" });
            Assert.AreEqual(42, fixture.count);
            Assert.AreEqual(9007199254740993L, fixture.big);
            Assert.IsTrue(fixture.flag);
            Assert.AreEqual(0.25f, fixture.ratio);
            Assert.AreEqual("hi", fixture.label);
            Assert.AreEqual(CodecKind.Third, fixture.kind);
            Assert.AreEqual(new Vector3(1, 2, 3), fixture.offset);
            Assert.AreEqual(1f, fixture.tint.r);
            Assert.AreEqual(128f / 255f, fixture.tint.a, 1e-6f);
            Assert.AreEqual(90f, fixture.turn.eulerAngles.y, 1e-3f);
            CollectionAssert.AreEqual(new[] { 4, 5, 6 }, fixture.numbers);
            Assert.AreEqual("x", fixture.pair.b);
        }

        [Test]
        public void PatchesOnlyTheFieldsGiven()
        {
            Write("pair", new JObject { ["a"] = 3, ["b"] = "x" });
            Write("pair", new JObject { ["b"] = "y" });
            Assert.AreEqual(3, fixture.pair.a);
            Assert.AreEqual("y", fixture.pair.b);
        }

        [Test]
        public void RefusesWrongValues()
        {
            Refused("count", "abc");
            Refused("count", 1.5);
            Refused("count", JToken.Parse("3000000000"));
            Refused("small", 256);
            Refused("small", -1);
            Refused("flag", "maybe");
            Refused("ratio", "abc");
            Refused("label", new JArray());
            Refused("kind", "Fourth");
            Refused("offset", new JArray(1, 2));
            Refused("tint", "red");
            Assert.AreEqual(0, fixture.count);
            Assert.AreEqual(0, fixture.small);
            Assert.AreEqual(CodecKind.First, fixture.kind);
        }

        [Test]
        public void RefusesAStringCutOnExport()
        {
            fixture.label = new string('x', 50);
            JToken cut = Read("label", new ReadOptions { MaxString = 10 });
            StringAssert.Contains("truncated", (string)cut);
            Refused("label", cut);
            Assert.AreEqual(50, fixture.label.Length);
        }

        [Test]
        public void ReadsEnumsByNameAndCutsLongArrays()
        {
            fixture.kind = CodecKind.Second;
            fixture.numbers = new List<int> { 1, 2, 3 };
            Assert.AreEqual("Second", (string)Read("kind"));
            var cut = (JArray)Read("numbers", new ReadOptions { MaxArray = 2 });
            Assert.AreEqual(3, cut.Count);
            Assert.AreEqual(3, (int)cut[2]["$truncated"]);
        }

        [Test]
        public void AnExportReadsBackWithoutChange()
        {
            fixture.count = -7;
            fixture.small = 200;
            fixture.big = long.MinValue;
            fixture.flag = true;
            fixture.ratio = 0.1f;
            fixture.label = "a \"quoted\" label";
            fixture.kind = CodecKind.Second;
            fixture.offset = new Vector3(0.3f, -1e-7f, 12345.678f);
            fixture.tint = new Color(0.2f, 0.4f, 0.6f, 0.8f);
            fixture.turn = Quaternion.Euler(10, 20, 30);
            fixture.numbers = new List<int> { 1, 2, 3 };
            fixture.pair = new CodecPair { a = 5, b = "z" };
            fixture.curve = new AnimationCurve(new Keyframe(0, 0, 0.5f, 1), new Keyframe(1, 2));
            string[] fields = { "count", "small", "big", "flag", "ratio", "label", "kind", "offset", "tint", "turn", "numbers", "pair", "curve" };
            so.Update();
            var exported = new Dictionary<string, JToken>();
            foreach (string f in fields)
            {
                exported[f] = Read(f, ReadOptions.Unbounded);
            }

            var copy = ScriptableObject.CreateInstance<CodecFixture>();
            var copySo = new SerializedObject(copy);
            try
            {
                foreach (string f in fields)
                {
                    var wc = new WriteContext();
                    Assert.IsTrue(ValueCodec.Write(copySo.FindProperty(f), exported[f], wc), f + ": " + string.Join(", ", wc.Errors.ToArray()));
                }
                copySo.ApplyModifiedPropertiesWithoutUndo();
                copySo.Update();
                foreach (string f in fields)
                {
                    JToken again = ValueCodec.Read(copySo.FindProperty(f), ReadOptions.Unbounded);
                    Assert.IsTrue(JToken.DeepEquals(exported[f], again), f + ": " + exported[f] + " became " + again);
                }
                Assert.AreEqual(fixture.ratio, copy.ratio);
                Assert.AreEqual(fixture.offset, copy.offset);
                Assert.AreEqual(fixture.big, copy.big);
                Assert.AreEqual(fixture.curve.keys[0].outTangent, copy.curve.keys[0].outTangent);
            }
            finally
            {
                copySo.Dispose();
                Object.DestroyImmediate(copy);
            }
        }
    }
}
