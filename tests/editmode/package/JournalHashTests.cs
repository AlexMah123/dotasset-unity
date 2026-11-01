using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // The state RunJournal.Hash records for a path, which UndoRun compares before it restores anything.
    public sealed class JournalHashTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "DotAssetHash" + Guid.NewGuid().ToString("N"), "Folder");
            Directory.CreateDirectory(dir);
            File.WriteAllText(dir + ".meta", "folderAsset: yes");
            File.WriteAllText(Path.Combine(dir, "A.mat"), "a");
            File.WriteAllText(Path.Combine(dir, "A.mat.meta"), "guid: 1");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(Path.GetDirectoryName(dir), true);
        }

        [Test]
        public void FolderHashSeesAFileAddedAndRemoved()
        {
            string before = RunJournal.Hash(dir);
            StringAssert.StartsWith(RunJournal.LegacyFolder + " ", before);
            string added = Path.Combine(dir, "Sub", "B.mat");
            Directory.CreateDirectory(Path.GetDirectoryName(added));
            File.WriteAllText(added, "b");
            Assert.AreNotEqual(before, RunJournal.Hash(dir));
            Directory.Delete(Path.GetDirectoryName(added), true);
            Assert.AreEqual(before, RunJournal.Hash(dir));
        }

        [Test]
        public void FolderHashSeesAnEditedFile()
        {
            string file = Path.Combine(dir, "A.mat");
            string before = RunJournal.Hash(dir);
            File.WriteAllText(file, "b");
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));
            Assert.AreNotEqual(before, RunJournal.Hash(dir), "same length, later write time");
            File.WriteAllText(file, "longer");
            Assert.AreNotEqual(before, RunJournal.Hash(dir), "other length");
        }

        [Test]
        public void FolderHashSeesItsOwnMeta()
        {
            string before = RunJournal.Hash(dir);
            File.WriteAllText(dir + ".meta", "folderAsset: no");
            Assert.AreNotEqual(before, RunJournal.Hash(dir));
        }

        [Test]
        public void FileHashIsTheFileAndMetaAsBefore()
        {
            // Journals written before the hash was streamed must still match.
            string file = Path.Combine(dir, "A.mat");
            using (var sha = SHA1.Create())
            {
                byte[] bytes = File.ReadAllBytes(file);
                byte[] meta = File.ReadAllBytes(file + ".meta");
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                sha.TransformFinalBlock(meta, 0, meta.Length);
                Assert.AreEqual(BitConverter.ToString(sha.Hash).Replace("-", string.Empty), RunJournal.Hash(file));
            }
        }

        [Test]
        public void LockedFileIsUnreadableNotAnException()
        {
            string file = Path.Combine(dir, "A.mat");
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                StringAssert.StartsWith(RunJournal.Unreadable + ": ", RunJournal.Hash(file));
            }
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                StringAssert.DoesNotStartWith(RunJournal.Unreadable, RunJournal.Hash(file), "a writer that shares the file");
            }
        }

        [Test]
        public void MissingPathIsAbsent()
        {
            Assert.AreEqual("absent", RunJournal.Hash(Path.Combine(dir, "None.mat")));
        }
    }
}
