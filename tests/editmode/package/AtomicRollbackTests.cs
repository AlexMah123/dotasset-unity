using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // The rollback RunDispatcher does when a target of an atomic run fails: two targets, the first one written, the
    // second one failing. Atomic, the first is back as it was and no journal is left. Not atomic, it stays written,
    // which also shows the first target was written before the rollback undid it.
    public sealed class AtomicRollbackTests : RunFixture
    {
        const string Missing = Folder + "/Missing.mat";

        static void AssertFailed(RunResult r, string what)
        {
            Assert.AreEqual(1, r.ExitCode, what + "\n" + string.Join("\n", r.Lines.ToArray()));
        }

        static void AssertNoJournal(RunResult r)
        {
            Assert.IsFalse(r.Outputs.Any(o => o.EndsWith("journal.json")), "a rolled back run kept its journal: " + string.Join(", ", r.Outputs));
        }

        [Test]
        public void MigrateRunIsAtomicByDefault()
        {
            string before = Text(Red);
            string args = "-from=_Color -to=_DotAssetTint -apply";
            RunResult atomic = Run("RenameShaderProperty", args, Red, Missing);
            AssertFailed(atomic, "a missing second target");
            Assert.AreEqual(before, Text(Red), "the failed Migrate run left Red.mat renamed");
            AssertNoJournal(atomic);

            AssertFailed(Run("RenameShaderProperty", args + " -atomic=false", Red, Missing), "-atomic=false with a missing target");
            Assert.AreNotEqual(before, Text(Red), "with -atomic=false the target that worked was not written");
        }

        [Test]
        public void EditRunRollsBackWithAtomic()
        {
            string before = Text(Red);
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'RenderQueue': 2450},
                {'AssetPath': '" + Missing + @"', 'RenderQueue': 2450}]}");
            RunResult atomic = Run("EditMaterial", spec + " -apply -atomic");
            AssertFailed(atomic, "a missing second target");
            Assert.AreEqual(before, Text(Red), "the failed -atomic Edit run left Red.mat written");
            Assert.AreNotEqual(2450, AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, "the loaded Red.mat kept the queue");
            AssertNoJournal(atomic);

            AssertFailed(Run("EditMaterial", spec + " -apply"), "without -atomic, a missing target");
            Assert.AreEqual(2450, AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, "without -atomic the target that worked was not written");
        }

        [Test]
        public void ImportRunRollsBackWithAtomic()
        {
            string blue = Folder + "/Blue.mat";
            string spec = Spec(@"{'Assets': [
                {'Kind': 'Material', 'Path': '" + blue + @"', 'Properties': {}},
                {'Kind': 'Prefab', 'Path': '" + Folder + @"/Broken.prefab', 'Root': 'Broken',
                 'Ops': [{'Op': 'Rename', 'Path': 'Broken/DoesNotExist', 'NewName': 'X'}]}]}");
            RunResult atomic = Run("CreateAsset", spec + " -atomic");
            AssertFailed(atomic, "a bad op in the second asset");
            Assert.IsFalse(File.Exists(Absolute(blue)), "the failed -atomic Import run left Blue.mat");
            Assert.IsFalse(File.Exists(Absolute(blue) + ".meta"), "the failed -atomic Import run left Blue.mat.meta");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(blue), "Unity still loads Blue.mat");
            AssertNoJournal(atomic);

            AssertFailed(Run("CreateAsset", spec), "without -atomic, a bad op");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(blue), "without -atomic the asset that worked was not made");
        }
    }
}
