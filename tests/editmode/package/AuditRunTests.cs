using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace DotAsset.Tests
{
    // Every Audit run on a seeded issue: exit 3 with the issue, the fix spec its fix run takes, exit 0 after.
    // R2 and R5 of AuditMissingReference are in MigrateRunTests, next to the runs that fix them.
    public sealed class AuditRunTests : RunFixture
    {
        [Test]
        public void AuditMaterialFindsAStrayKeywordAndItsFixRemovesIt()
        {
            const string keyword = "_DOTASSET_STRAY";
            Ok("EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'EnableKeywords': ['" + keyword + @"']}]}") + " -apply");
            JObject report = Report("AuditMaterial", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "M5", Red), report.ToString());
            Assert.AreEqual("EditMaterial", (string)report["FixRun"]);
            Ok("EditMaterial", FixSpec(report) + " -apply");
            CollectionAssert.DoesNotContain(Export("MaterialExport", Red)["Keywords"].Select(k => (string)k).ToArray(), keyword);
            Assert.IsFalse(HasIssue(Report("AuditMaterial", AnyIssues, "", Folder), "M5", Red));
        }

        [Test]
        public void AuditMissingReferenceFindsADeletedMaterial()
        {
            Report("AuditMissingReference", 0, "", Folder);
            string green = Folder + "/Green.mat";
            string broken = Folder + "/Broken.prefab";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + green + @"', 'Properties': {}},
                {'Kind': 'Prefab', 'Path': '" + broken + @"', 'Root': 'Broken',
                 'Components': ['MeshFilter', {'Type': 'MeshRenderer', 'Properties': {'m_Materials': ['" + green + @"']}}]}]}"));
            AssetDatabase.DeleteAsset(green);
            JObject report = Report("AuditMissingReference", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "R1", broken), report.ToString());
        }

        [Test]
        public void AuditTextureFindsANormalMapImportedAsDefaultAndItsFixImportsIt()
        {
            string rock = Folder + "/Rock_n.png";
            WritePng(rock);
            JObject report = Report("AuditTexture", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "T1", rock), report.ToString());
            Assert.AreEqual("EditImporter", (string)report["FixRun"]);
            Ok("EditImporter", FixSpec(report) + " -apply");
            Assert.AreEqual("NormalMap", (string)Export("ImporterExport", rock)["Summary"]["TextureType"]);
            Assert.IsFalse(HasIssue(Report("AuditTexture", AnyIssues, "", Folder), "T1", rock));
        }

        [Test]
        public void AuditUnusedAssetListsWhatNothingUses()
        {
            string unused = Folder + "/Unused.mat";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + unused + @"', 'Properties': {}}]}"));
            JObject report = Report("AuditUnusedAsset", 3, "-scandir=" + Folder);
            Assert.IsTrue(HasIssue(report, "U1", unused), report.ToString());
            Assert.IsFalse(HasIssue(report, "U1", Red), "Crate uses Red.mat");
            Assert.AreEqual("DeleteAsset", (string)report["FixRun"]);
        }
    }
}
