using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // An UndoRun that fails at a restore stays open, so the same undo goes through once the problem is gone.
    public sealed class UndoRetryTests
    {
        const string Folder = "Assets/DotAssetUndoTest";
        const string Mat = Folder + "/Undo.mat";
        string spec;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetUndoTest");
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), Mat);
            spec = Path.Combine(Path.GetTempPath(), "DotAssetUndo" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(spec, "{\"Targets\": [{\"AssetPath\": \"" + Mat + "\", \"RenderQueue\": 2480}]}");
        }

        [TearDown]
        public void TearDown()
        {
            File.Delete(spec);
            AssetDatabase.DeleteAsset(Folder);
        }

        static int Queue()
        {
            return AssetDatabase.LoadAssetAtPath<Material>(Mat).renderQueue;
        }

        [Test]
        public void FailedUndoCanBeRetried()
        {
            int before = Queue();
            RunResult edit = RunDispatcher.Run("EditMaterial", new string[0], "-spec=\"" + spec + "\" -apply", false);
            Assert.AreEqual(0, edit.ExitCode, string.Join("\n", edit.Lines.ToArray()));
            string journal = edit.Outputs.Select(o => o.Replace('\\', '/')).FirstOrDefault(o => o.EndsWith("/journal.json"));
            Assert.IsNotNull(journal, "EditMaterial -apply printed no journal");
            string run = "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply";
            Assert.AreEqual(2480, Queue());

            // Held open by another program: readable, not writable, not read-only, so only the restore's copy fails.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            RunResult failed;
            using (new FileStream(DotAssetPaths.Absolute(Mat), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                failed = RunDispatcher.Run("UndoRun", new string[0], run, false);
            }
            Assert.AreEqual(1, failed.ExitCode, string.Join("\n", failed.Lines.ToArray()));
            Assert.AreEqual(2480, Queue(), "the failed undo changed the material");

            RunResult retried = RunDispatcher.Run("UndoRun", new string[0], run, false);
            Assert.AreEqual(0, retried.ExitCode, string.Join("\n", retried.Lines.ToArray()));
            Assert.AreEqual(before, Queue());
            Assert.AreEqual(2, RunDispatcher.Run("UndoRun", new string[0], run, false).ExitCode, "a second undo is refused");
        }

        [Test]
        public void UndoRefusesAPathItCannotRead()
        {
            // Unreadable now cannot be told apart from changed, so nothing is restored until it can be read.
            RunResult edit = RunDispatcher.Run("EditMaterial", new string[0], "-spec=\"" + spec + "\" -apply", false);
            Assert.AreEqual(0, edit.ExitCode, string.Join("\n", edit.Lines.ToArray()));
            string journal = edit.Outputs.Select(o => o.Replace('\\', '/')).FirstOrDefault(o => o.EndsWith("/journal.json"));
            string run = "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply";
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            RunResult refused;
            using (new FileStream(DotAssetPaths.Absolute(Mat), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                refused = RunDispatcher.Run("UndoRun", new string[0], run, false);
            }
            Assert.AreEqual(1, refused.ExitCode, string.Join("\n", refused.Lines.ToArray()));
            Assert.IsTrue(refused.Lines.Any(l => l.Contains("could not be read")), string.Join("\n", refused.Lines.ToArray()));
            Assert.AreEqual(2480, Queue(), "the refused undo changed the material");
            Assert.AreEqual(0, RunDispatcher.Run("UndoRun", new string[0], run, false).ExitCode, "readable again, the undo goes through");
        }
    }
}
