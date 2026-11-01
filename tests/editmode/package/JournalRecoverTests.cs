using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace DotAsset.Tests
{
    // A run that died before it closed its journal: the journal.log it left is finalized by the next start or run.
    public sealed class JournalRecoverTests
    {
        const string Folder = "Assets/DotAssetRecoverTest";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetRecoverTest");
        }

        [TearDown]
        public void TearDown()
        {
            RunJournal.Current = null;
            RunJournal.TaskId = null;
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void RecoverFinalizesALogAndNamesItsTask()
        {
            string path = Folder + "/Kept.txt";
            File.WriteAllText(DotAssetPaths.Absolute(path), "before");
            RunJournal.TaskId = "task-1";
            var journal = new RunJournal("RecoverTest");
            RunJournal.Current = journal;
            RunJournal.Keep(path);
            // The run dies here: Close is never called.
            RunJournal.Current = null;
            RunJournal.TaskId = null;
            string dir = DotAssetPaths.Combine(RunJournal.Root, journal.Id);
            try
            {
                string log = DotAssetPaths.Combine(dir, "journal.log");
                Assert.AreEqual("task-1", (string)JObject.Parse(File.ReadAllLines(log)[0])["Task"]);
                File.WriteAllText(DotAssetPaths.Absolute(path), "written before the crash");

                List<string> finalized = RunJournal.RecoverInterrupted();
                CollectionAssert.Contains(finalized, journal.Id);
                Assert.IsFalse(File.Exists(log), "the log is replaced by journal.json");
                JObject json = RunJournal.Load(DotAssetPaths.Combine(dir, RunJournal.FileName));
                Assert.AreEqual(true, (bool)json["Interrupted"]);
                Assert.AreEqual("task-1", (string)json["Task"]);
                Assert.AreEqual(RunJournal.Hash(path), (string)json["Entries"][0]["After"], "After is the state when it was recovered");
                CollectionAssert.DoesNotContain(RunJournal.RecoverInterrupted(), journal.Id, "a journal is finalized once");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
