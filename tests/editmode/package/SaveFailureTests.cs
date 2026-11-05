using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace DotAsset.Tests
{
    // N12: a save Unity could not write (another program holds the file) is a failed target, not a success with a
    // warning, and the change does not stay behind in memory.
    public sealed class SaveFailureTests : RunFixture
    {
        [Test]
        public void ASaveUnityCouldNotWriteFailsTheTarget()
        {
            // Unity logs the failed write as an error.
            LogAssert.ignoreFailingMessages = true;
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'RenderQueue': 2450}]}");
            int queue = AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue;
            byte[] before = File.ReadAllBytes(Absolute(Red));
            RunResult r;
            using (new FileStream(Absolute(Red), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("EditMaterial", spec + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, Red, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(Red)), "the file changed after all");
            Assert.AreEqual(queue, AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, "the edit stayed in memory");
        }
    }
}
