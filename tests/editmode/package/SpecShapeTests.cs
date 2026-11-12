using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // R7-1: a key that takes an object or an array, given another shape, fails and names the key. It read as "not
    // given" before, so the run saved without it and reported success. A run that checks its spec up front refuses
    // it (exit 2), otherwise the target fails (exit 1). Either way nothing is saved.
    public sealed class SpecShapeTests : RunFixture
    {
        static string Lines(RunResult r)
        {
            return "exit " + r.ExitCode + ", failed [" + string.Join(", ", r.FailedTargets.ToArray()) + "]\n" + string.Join("\n", r.Lines.ToArray());
        }

        // expected is "<key> <object|array>", the message is "<key>: <value> is not an <shape>".
        static void Fails(RunResult r, string expected, string what)
        {
            string[] parts = expected.Split(' ');
            Assert.AreNotEqual(0, r.ExitCode, what + "\n" + Lines(r));
            StringAssert.Contains(parts[0] + ": ", Lines(r), what);
            StringAssert.Contains(" is not an " + parts[1], Lines(r), what);
        }

        [Test]
        public void CreateAssetMakesNothingFromAWrongShape()
        {
            string[][] cases =
            {
                new[] { "Made.mat", "'Kind': 'Material', 'Properties': ['_Color', '#FF0000']", "Properties object" },
                new[] { "Object.prefab", "'Kind': 'Prefab', 'Components': {'Type': 'BoxCollider'}", "Components array" },
                new[] { "Name.prefab", "'Kind': 'Prefab', 'Components': 'BoxCollider'", "Components array" },
                new[] { "Entry.prefab", "'Kind': 'Prefab', 'Components': [{'Type': 'SphereCollider', 'Properties': ['m_Radius', 3]}]", "Properties object" },
                new[] { "Made.asset", "'Kind': 'ScriptableObject', 'Type': 'DotAsset.Tests.CodecFixture', 'Properties': ['count', 5]", "Properties object" },
                new[] { "Ops.asset", "'Kind': 'ScriptableObject', 'Type': 'DotAsset.Tests.CodecFixture', 'Properties': {'count': 5}, 'ArrayOps': {'Property': 'numbers', 'Append': 7}", "ArrayOps array" },
            };
            foreach (string[] c in cases)
            {
                string made = Folder + "/" + c[0];
                RunResult r = Run("CreateAsset", Spec(@"{'Assets': [{'Path': '" + made + "', " + c[1] + "}]}"));
                Fails(r, c[2], c[1]);
                Assert.IsFalse(File.Exists(Absolute(made)), c[1] + ": written anyway");
            }
        }

        [Test]
        public void EditPrefabSavesNothingFromAWrongShape()
        {
            string[][] cases =
            {
                new[] { "{'Op': 'AddComponent', 'Path': 'Crate', 'Type': 'SphereCollider', 'Properties': 'm_Radius'}", "Properties object" },
                new[] { "{'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid', 'Components': 'BoxCollider'}", "Components array" },
                new[] { "{'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid', 'Components': [{'Type': 'SphereCollider', 'Properties': ['m_Radius', 3]}]}", "Properties object" },
                new[] { "{'Op': 'Set', 'Path': 'Crate/Mesh', 'Component': 'MeshRenderer', 'Properties': {'m_Enabled': false}, 'ArrayOps': {'Property': 'm_Materials', 'Append': '" + Red + "'}}", "ArrayOps array" },
                new[] { "{'Op': 'Set', 'Path': 'Crate/Mesh', 'Component': 'MeshRenderer', 'Properties': ['m_Enabled', false], 'ArrayOps': [{'Property': 'm_Materials', 'Append': '" + Red + "'}]}", "Properties object" },
            };
            string before = Text(Crate);
            foreach (string[] c in cases)
            {
                RunResult r = Run("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Crate + "', 'Ops': [" + c[0] + "]}]}") + " -apply");
                Fails(r, c[1], c[0]);
                Assert.AreEqual(before, Text(Crate), c[0]);
            }
        }

        [Test]
        public void EditAssetSavesNothingFromAWrongShape()
        {
            string codec = Folder + "/Codec.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<CodecFixture>(), codec);
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(codec));
            string[][] cases =
            {
                new[] { "'Properties': ['count', 5], 'ArrayOps': [{'Property': 'numbers', 'Append': 7}]", "Properties object" },
                new[] { "'Properties': {'count': 5}, 'ArrayOps': {'Property': 'numbers', 'Append': 7}", "ArrayOps array" },
            };
            string before = Text(codec);
            foreach (string[] c in cases)
            {
                RunResult r = Run("EditAsset", Spec(@"{'Targets': [{'AssetPath': '" + codec + "', " + c[0] + "}]}") + " -apply");
                Fails(r, c[1], c[0]);
                Assert.AreEqual(before, Text(codec), c[0]);
            }
        }

        [Test]
        public void EditImporterAndEditMaterialSaveNothingFromAWrongShape()
        {
            string png = Folder + "/Rock.png";
            WritePng(png);
            string meta = Text(png + ".meta");
            foreach (string block in new[] { "'Properties': ['m_IsReadable', 1]", "'Texture': {'MaxSize': 512}, 'Properties': ['m_IsReadable', 1]" })
            {
                RunResult r = Run("EditImporter", Spec(@"{'Targets': [{'AssetPath': '" + png + "', " + block + "}]}") + " -apply");
                Fails(r, "Properties object", block);
                Assert.AreEqual(meta, Text(png + ".meta"), block);
            }
            string before = Text(Red);
            RunResult m = Run("EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'EnableInstancing': true, 'Properties': ['_Color']}]}") + " -apply");
            Fails(m, "Properties object", "EditMaterial");
            Assert.AreEqual(before, Text(Red));
            Assert.IsFalse(AssetDatabase.LoadAssetAtPath<Material>(Red).enableInstancing, "the failed material kept instancing in memory");
        }

        [Test]
        public void EditAnimatorControllerSavesNothingFromAWrongShape()
        {
            string[][] cases =
            {
                new[] { "{'Op': 'SetState', 'State': 'Closed', 'Properties': ['m_Speed', 3]}", "Properties object" },
                new[] { "{'Op': 'AddTransition', 'From': 'Opened', 'To': 'Closed', 'Conditions': {'Parameter': 'Open', 'Mode': 'IfNot'}}", "Conditions array" },
                new[] { "{'Op': 'AddState', 'Path': 'Waiting', 'Position': '1,0'}", "Position 2" },
                new[] { "{'Op': 'AddStateMachine', 'Path': 'Combat', 'Position': {'x': 1, 'y': 0}}", "Position 2" },
                new[] { "{'Op': 'AddState', 'Path': 'Move', 'Motion': {'BlendTree': 'Move', 'Children': [{'Motion': null, 'Position': '1,0'}]}}", "Position 2" },
            };
            string before = Text(Door);
            foreach (string[] c in cases)
            {
                RunResult r = Run("EditAnimatorController", Spec(@"{'Targets': [{'AssetPath': '" + Door + "', 'Ops': [" + c[0] + "]}]}") + " -apply");
                if (c[1].StartsWith("Position"))
                {
                    Assert.AreNotEqual(0, r.ExitCode, c[0] + "\n" + Lines(r));
                    StringAssert.Contains("Position: ", Lines(r), c[0]);
                    StringAssert.Contains(" is not 2 numbers", Lines(r), c[0]);
                }
                else
                {
                    Fails(r, c[1], c[0]);
                }
                Assert.AreEqual(before, Text(Door), c[0]);
            }
        }
    }
}
