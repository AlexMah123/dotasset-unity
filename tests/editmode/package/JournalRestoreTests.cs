using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [Test]
        public void FailedRestoreSaysTheFileIsMissing()
        {
            // The run deleted the file and its backup cannot be copied back: the message says it is gone for now.
            string journal = Path.Combine(Path.GetTempPath(), "DotAssetRestore" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            string gone = Folder + "/Gone.txt";
            var entries = new List<RunJournal.Entry> { new RunJournal.Entry { Path = gone, Existed = true, Backup = "1" } };
            List<string> problems = RunJournal.Restore(journal, entries, new RunLog("JournalRestoreTests", false));
            Directory.Delete(journal, true);
            Assert.AreEqual(1, problems.Count, string.Join("\n", problems.ToArray()));
            StringAssert.EndsWith("it is missing from disk until the run is undone", problems[0]);
        }

        [Test]
        public void MoveAssetRefusesAFolderWithUnsavedWork()
        {
            // A failed move rolls back by copying from the journal, which would lose the edit, so the move is refused.
            AssetDatabase.CreateFolder(Folder, "Sub");
            string mat = Folder + "/Sub/Unsaved.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), mat);
            var loaded = AssetDatabase.LoadAssetAtPath<Material>(mat);
            loaded.renderQueue = 2470;
            EditorUtility.SetDirty(loaded);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            RunResult move = RunDispatcher.Run("MoveAsset", new string[0], "-from=" + Folder + "/Sub -to=" + Folder + "/Moved -apply", false);
            Assert.AreEqual(1, move.ExitCode, string.Join("\n", move.Lines.ToArray()));
            Assert.IsTrue(move.Lines.Any(l => l.Contains("unsaved changes")), string.Join("\n", move.Lines.ToArray()));
            Assert.IsTrue(AssetDatabase.IsValidFolder(Folder + "/Sub"), "the folder moved");
            Assert.AreEqual(2470, AssetDatabase.LoadAssetAtPath<Material>(mat).renderQueue, "the unsaved edit is gone");
        }

        [Test]
        public void MetaOnlyUndoIgnoresAReadOnlySourceFile()
        {
            // An import settings change journals only the .meta, so a read-only texture does not block its undo.
            string path = Folder + "/Rock_n.png";
            var texture = new Texture2D(2, 2);
            File.WriteAllBytes(DotAssetPaths.Absolute(path), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            string spec = Path.Combine(Path.GetTempPath(), "DotAssetRestore" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(spec, "{\"Targets\": [{\"AssetPath\": \"" + path + "\", \"Texture\": {\"TextureType\": \"NormalMap\"}}]}");
            RunResult edit;
            RunResult undo;
            try
            {
                edit = RunDispatcher.Run("EditImporter", new string[0], "-spec=\"" + spec + "\" -apply", false);
                Assert.AreEqual(0, edit.ExitCode, string.Join("\n", edit.Lines.ToArray()));
                Assert.AreEqual(TextureImporterType.NormalMap, ((TextureImporter)AssetImporter.GetAtPath(path)).textureType);
                string journal = edit.Outputs.Select(o => o.Replace('\\', '/')).FirstOrDefault(o => o.EndsWith("/journal.json"));
                Assert.IsNotNull(journal, "EditImporter -apply printed no journal");
                File.SetAttributes(DotAssetPaths.Absolute(path), FileAttributes.ReadOnly);
                undo = RunDispatcher.Run("UndoRun", new string[0], "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply", false);
            }
            finally
            {
                File.SetAttributes(DotAssetPaths.Absolute(path), FileAttributes.Normal);
                File.Delete(spec);
            }
            Assert.AreEqual(0, undo.ExitCode, string.Join("\n", undo.Lines.ToArray()));
            Assert.AreEqual(TextureImporterType.Default, ((TextureImporter)AssetImporter.GetAtPath(path)).textureType);
        }

        [Test]
        public void RollBackDeletesAReadOnlyFilesBackup()
        {
            // A backup keeps the read-only attribute of its file, the rolled back journal still goes.
            string path = Folder + "/Held.txt";
            File.WriteAllText(DotAssetPaths.Absolute(path), "same");
            File.SetAttributes(DotAssetPaths.Absolute(path), FileAttributes.ReadOnly);
            var journal = new RunJournal("JournalRestoreTests");
            string dir = DotAssetPaths.Combine(RunJournal.Root, journal.Id);
            List<string> problems;
            try
            {
                RunJournal.Current = journal;
                RunJournal.Keep(path);
                RunJournal.Current = null;
                journal.RollBack(new RunLog("JournalRestoreTests", false), out problems);
            }
            finally
            {
                RunJournal.Current = null;
                File.SetAttributes(DotAssetPaths.Absolute(path), FileAttributes.Normal);
            }
            bool left = Directory.Exists(dir);
            bool recovered = RunJournal.RecoverInterrupted().Contains(journal.Id);
            DeleteJournal(dir);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
            Assert.IsFalse(left, "the rolled back journal is still there");
            Assert.IsFalse(recovered, "a rolled back journal was finalized as interrupted");
        }

        [Test]
        public void RollBackThatCannotDeleteItsFolderLeavesNoLog()
        {
            // Another program holds a backup, so the folder stays: without its journal.log, or the next run would take
            // the rolled back run for one that died.
            string path = Folder + "/Kept.txt";
            File.WriteAllText(DotAssetPaths.Absolute(path), "same");
            var journal = new RunJournal("JournalRestoreTests");
            string dir = DotAssetPaths.Combine(RunJournal.Root, journal.Id);
            List<string> problems;
            RunJournal.Current = journal;
            RunJournal.Keep(path);
            RunJournal.Current = null;
            List<FileStream> held = Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "journal.log")
                .Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
            try
            {
                journal.RollBack(new RunLog("JournalRestoreTests", false), out problems);
            }
            finally
            {
                held.ForEach(f => f.Dispose());
            }
            bool log = File.Exists(DotAssetPaths.Combine(dir, "journal.log"));
            bool recovered = RunJournal.RecoverInterrupted().Contains(journal.Id);
            DeleteJournal(dir);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
            Assert.IsFalse(log, "journal.log outlived the rollback");
            Assert.IsFalse(recovered, "a rolled back journal was finalized as interrupted");
        }

        static void DeleteJournal(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
        }
    }
}
