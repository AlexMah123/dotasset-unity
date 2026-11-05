using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // The journal keeps the newest 30 folders. One that still has work to undo, an interrupted run or a rollback
    // that could not finish, is kept past the cap until UndoRun has undone it.
    public sealed class JournalPruneTests : RunFixture
    {
        readonly List<string> made = new List<string>();

        string Fake(string name, long seq, JObject extra)
        {
            string dir = DotAssetPaths.Combine(RunJournal.Root, "DotAssetPruneTest-" + name);
            Directory.CreateDirectory(dir);
            var json = new JObject { ["Id"] = "DotAssetPruneTest-" + name, ["Seq"] = seq, ["RunName"] = "EditAsset" };
            json.Merge(extra);
            json["Entries"] = new JArray();
            File.WriteAllText(DotAssetPaths.Combine(dir, RunJournal.FileName), json.ToString());
            made.Add(dir);
            return dir;
        }

        [TearDown]
        public void DeleteFakes()
        {
            foreach (string dir in made)
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            made.Clear();
        }

        [Test]
        public void PruneKeepsJournalsWithWorkLeftToUndo()
        {
            // The oldest four, behind 31 newer ones, so all four are past the cap.
            string interrupted = Fake("interrupted", 1, new JObject { ["Interrupted"] = true });
            string incomplete = Fake("incomplete", 2, new JObject { ["RollbackIncomplete"] = true });
            string undone = Fake("undone", 3, new JObject { ["Interrupted"] = true, ["UndoneBy"] = "x" });
            string plain = Fake("plain", 4, new JObject());
            for (int i = 0; i < 31; i++)
            {
                Fake("filler" + i, 100 + i, new JObject());
            }

            // Any writing run prunes when its journal closes.
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Folder', 'Path': '" + Folder + @"/Pruned'}]}"));

            Assert.IsTrue(Directory.Exists(interrupted), "an interrupted run's journal was pruned, it can no longer be undone");
            Assert.IsTrue(Directory.Exists(incomplete), "a journal whose rollback could not finish was pruned");
            Assert.IsFalse(Directory.Exists(undone), "an undone journal was kept past the cap");
            Assert.IsFalse(Directory.Exists(plain), "a finished journal was kept past the cap");
        }
    }
}
