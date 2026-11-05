using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // Files in a registry package cannot change, so every writing run refuses them as a failed target, in the dry run
    // too, and RunJournal.Keep refuses them as a backstop for a run that does not check. Only dry runs are run here:
    // a run that wrongly applied would rewrite the package cache. The targets are URP's scene template files in
    // DotAssetTest-Medium: Gold.mat, which uses the Lit shader, and Standard.unity, which uses Gold.mat and the Volume
    // script. No registry package there has an .anim or a SerializeReference, so RebindAnimation and
    // RenameSerializeReferenceType are covered by the Keep backstop only.
    public sealed class ReadOnlyPackageTests : RunFixture
    {
        const string Templates = "Packages/com.unity.render-pipelines.universal/Editor/SceneTemplates";
        const string Gold = Templates + "/Standard/Materials/Gold.mat";
        const string Standard = Templates + "/Standard.unity";
        const string VolumeGuid = "172515602e62fb746b5d573b38a5fe58";

        static void AssertRefused(RunResult r, string target)
        {
            string lines = r.RunName + "\n" + string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, target, lines);
            Assert.IsTrue(r.Lines.Any(l => l.Contains("read-only")), "no read-only reason\n" + lines);
            Assert.IsFalse(Planned(r, "Path", target), "the dry run plans a write to " + target);
        }

        [Test]
        public void DeleteAssetRefusesAPackageFile()
        {
            AssertRefused(Run("DeleteAsset", "-scandir=" + Folder + " -permanent", Gold), Gold);
        }

        [Test]
        public void RenameShaderPropertyRefusesAPackageFile()
        {
            AssertRefused(Run("RenameShaderProperty", "-from=_BaseColor -to=_DotAssetTint", Gold), Gold);
        }

        [Test]
        public void ResaveAssetRefusesAPackageFile()
        {
            AssertRefused(Run("ResaveAsset", "", Gold), Gold);
        }

        [Test]
        public void RemapReferenceRefusesAPackageFile()
        {
            AssertRefused(Run("RemapReference", "-from=" + Gold + " -to=" + Red + " -scandir=" + Templates), Standard);
        }

        [Test]
        public void RemapReferenceRefusesAPackageMaterial()
        {
            const string shaders = "Packages/com.unity.render-pipelines.universal/Shaders";
            AssertRefused(Run("RemapReference", "-from=" + shaders + "/Lit.shader -to=" + shaders + "/SimpleLit.shader -scandir=" + Templates + "/Standard/Materials"), Gold);
        }

        [Test]
        public void RemapScriptRefusesAPackageFile()
        {
            AssertRefused(Run("RemapScript", "-from=" + VolumeGuid + " -to=" + ScriptPath<RunTestNew>() + " -scandir=" + Templates), Standard);
        }

        [Test]
        public void RenameFieldRefusesAPackageFile()
        {
            AssertRefused(Run("RenameField", "-script=" + VolumeGuid + " -from=m_Enabled -to=m_DotAssetEnabled -scandir=" + Templates), Standard);
        }

        [Test]
        public void SaveHookOutsideARunLeavesPackageAssetsToUnity()
        {
            // S1: the backstop is for runs. A save the user makes, with no run open, is Unity's to filter
            Assert.IsNull(RunJournal.Current, "no run may be open for this test");
            MethodInfo hook = typeof(JournalHook).GetMethod("OnWillSaveAssets", BindingFlags.NonPublic | BindingFlags.Static);
            string[] kept = null;
            Assert.DoesNotThrow(() => kept = (string[])hook.Invoke(null, new object[] { new[] { Gold } }));
            CollectionAssert.Contains(kept, Gold);
            Assert.DoesNotThrow(() => RunJournal.Keep(Gold), "Keep outside a run");
            Assert.DoesNotThrow(() => RunJournal.KeepMeta(Gold), "KeepMeta outside a run");
        }

        [Test]
        public void KeepRefusesAPackageFile()
        {
            var journal = new RunJournal("ReadOnlyPackageTests");
            RunJournal.Current = journal;
            try
            {
                Assert.Catch<Exception>(() => RunJournal.Keep(Gold), "Keep accepted a registry package file");
                Assert.Catch<Exception>(() => RunJournal.KeepMeta(Gold), "KeepMeta accepted a registry package file");
                Assert.AreEqual(0, journal.Count);
            }
            finally
            {
                RunJournal.Current = null;
                string dir = DotAssetPaths.Combine(RunJournal.Root, journal.Id);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }
    }
}
