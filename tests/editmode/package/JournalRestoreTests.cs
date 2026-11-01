using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotAsset.Tests
{
    // What keeps a rollback or undo from restoring: work open in the editor, and a path it cannot write.
    public sealed class JournalRestoreTests
    {
        const string Folder = "Assets/DotAssetRestoreTest";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetRestoreTest");
        }

        [TearDown]
        public void TearDown()
        {
            StageUtility.GoToMainStage();
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.path.StartsWith(Folder + "/"))
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void OpenSceneBlocksItsFolder()
        {
            string path = Folder + "/Open.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path, true));
            EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            StringAssert.StartsWith("open in the editor", RunJournal.Blocked(path));
            StringAssert.StartsWith("holds " + path + ", open in the editor", RunJournal.Blocked(Folder));
            Assert.IsNull(RunJournal.Blocked(Folder + "Other"));
        }

        [Test]
        public void PrefabModeBlocksItsFolder()
        {
            string path = Folder + "/Open.prefab";
            var go = new GameObject("Open");
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            if (PrefabStageUtility.OpenPrefab(path) == null)
            {
                Assert.Ignore("Prefab Mode does not open in batch EditMode, the smoke test covers it with the editor open");
            }
            StringAssert.StartsWith("holds " + path + ", open in Prefab Mode", RunJournal.Blocked(Folder));
        }

        [Test]
        public void RestoreStopsAtAFileItCannotDelete()
        {
            string journal = Path.Combine(Path.GetTempPath(), "DotAssetRestore" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            File.WriteAllText(Path.Combine(journal, "1"), "before");
            string kept = Folder + "/Kept.txt";
            string created = Folder + "/Created.txt";
            File.WriteAllText(DotAssetPaths.Absolute(kept), "after");
            File.WriteAllText(DotAssetPaths.Absolute(created), "new");
            var entries = new List<RunJournal.Entry>
            {
                new RunJournal.Entry { Path = kept, Existed = true, Backup = "1" },
                new RunJournal.Entry { Path = created, Existed = false },
            };
            // The refresh at the end of Restore imports the held file, Unity logs its own errors about it.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            List<string> problems;
            using (new FileStream(DotAssetPaths.Absolute(created), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                problems = RunJournal.Restore(journal, entries, new RunLog("JournalRestoreTests", false));
            }
            Directory.Delete(journal, true);
            Assert.AreEqual(1, problems.Count, string.Join("\n", problems.ToArray()));
            StringAssert.StartsWith(created + ": ", problems[0]);
            Assert.AreEqual("after", File.ReadAllText(DotAssetPaths.Absolute(kept)), "the entry after the failure is left alone");
        }

        [Test]
        public void ReadOnlyFileTheRunNeverWroteDoesNotBlock()
        {
            // A run kept two files and failed writing the read-only one: the rollback still puts the other one back.
            string journal = Path.Combine(Path.GetTempPath(), "DotAssetRestore" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            File.WriteAllText(Path.Combine(journal, "1"), "before");
            File.WriteAllText(Path.Combine(journal, "2"), "same");
            string written = Folder + "/Written.txt";
            string held = Folder + "/Held.txt";
            File.WriteAllText(DotAssetPaths.Absolute(written), "after");
            File.WriteAllText(DotAssetPaths.Absolute(held), "same");
            File.SetAttributes(DotAssetPaths.Absolute(held), FileAttributes.ReadOnly);
            var entries = new List<RunJournal.Entry>
            {
                new RunJournal.Entry { Path = written, Existed = true, Backup = "1" },
                new RunJournal.Entry { Path = held, Existed = true, Backup = "2" },
            };
            List<string> problems;
            string blocked;
            try
            {
                blocked = RunJournal.Blocked(held);
                problems = RunJournal.Restore(journal, entries, new RunLog("JournalRestoreTests", false));
            }
            finally
            {
                File.SetAttributes(DotAssetPaths.Absolute(held), FileAttributes.Normal);
                Directory.Delete(journal, true);
            }
            StringAssert.StartsWith("read-only on disk", blocked, "an undo's own check still names a read-only file");
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
            Assert.AreEqual("before", File.ReadAllText(DotAssetPaths.Absolute(written)));
            Assert.AreEqual("same", File.ReadAllText(DotAssetPaths.Absolute(held)));
        }
    }
}
