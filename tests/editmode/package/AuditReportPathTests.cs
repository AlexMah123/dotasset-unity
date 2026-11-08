using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // R5-2: an Audit's -report path. An audit never writes the project, so a report inside Assets/, Packages/ or
    // ProjectSettings/ is refused, and so is a file there that DotAsset did not write: a file that is not a report,
    // or a <stem>.fix.json that no report at that path names.
    public sealed class AuditReportPathTests : RunFixture
    {
        string outside;

        [SetUp]
        public void MakeOutside()
        {
            outside = Path.Combine(Path.GetTempPath(), "DotAssetReportTest" + System.Guid.NewGuid().ToString("N")).Replace('\\', '/');
            Directory.CreateDirectory(outside);
        }

        [TearDown]
        public void DeleteOutside()
        {
            Directory.Delete(outside, true);
        }

        static RunResult Audit(string report)
        {
            return Run("AuditMaterial", "-report=\"" + report + "\"", Folder);
        }

        static string Lines(RunResult r)
        {
            return string.Join("\n", r.Lines.ToArray());
        }

        [Test]
        public void AReportInsideTheProjectIsRefused()
        {
            string[] reports =
            {
                Folder + "/Report.json",
                "ProjectSettings/DotAssetReport.json",
                "Packages/DotAssetReport.json",
                "Library/../" + Folder + "/Climb.json",
            };
            var failures = new System.Collections.Generic.List<string>();
            foreach (string report in reports)
            {
                string written = Path.GetFullPath(Absolute(report));
                try
                {
                    RunResult r = Audit(report);
                    if (r.ExitCode != 2 || File.Exists(written))
                    {
                        failures.Add(report + ": exit " + r.ExitCode + (File.Exists(written) ? ", written" : string.Empty) + "\n" + Lines(r));
                    }
                }
                finally
                {
                    // The old code writes these into the test project, take them out again.
                    foreach (string file in new[] { written, Path.ChangeExtension(written, null) + ".fix.json" })
                    {
                        File.Delete(file);
                        File.Delete(file + ".meta");
                    }
                }
            }
            Assert.IsEmpty(failures, string.Join("\n\n", failures.ToArray()));
        }

        [Test]
        public void AFileThatIsNotAReportIsNotWrittenOver()
        {
            string notes = outside + "/notes.json";
            string fix = outside + "/notes.fix.json";
            File.WriteAllText(notes, "keep me");
            File.WriteAllText(fix, "keep me too");
            RunResult r = Audit(notes);
            Assert.AreEqual(2, r.ExitCode, Lines(r));
            Assert.AreEqual("keep me", File.ReadAllText(notes));
            Assert.AreEqual("keep me too", File.ReadAllText(fix));
        }

        [Test]
        public void AFixSpecNoReportNamesIsKept()
        {
            string report = outside + "/new.json";
            string fix = outside + "/new.fix.json";
            File.WriteAllText(fix, "{\"Targets\": []}");
            RunResult r = Audit(report);
            Assert.AreEqual(2, r.ExitCode, Lines(r));
            Assert.AreEqual("{\"Targets\": []}", File.ReadAllText(fix));
            Assert.IsFalse(File.Exists(report));
        }

        // Not red: the same -report path run twice replaces its own report and fix spec, and drops the fix spec when
        // nothing is left to fix.
        [Test]
        public void ItsOwnReportAndFixSpecAreReplaced()
        {
            string mine = outside + "/mine.json";
            string fix = outside + "/mine.fix.json";
            string args = "-report=\"" + mine + "\"";
            Ok("EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'EnableKeywords': ['_DOTASSET_STRAY']}]}") + " -apply");
            JObject first = Report("AuditMaterial", 3, args, Folder);
            Assert.IsTrue(File.Exists(fix), first.ToString());
            Ok("EditMaterial", FixSpec(first) + " -apply");
            JObject second = Report("AuditMaterial", AnyIssues, args, Folder);
            Assert.IsFalse(HasIssue(second, "M5", Red), second.ToString());
            Assert.AreEqual(second["FixSpec"] != null, File.Exists(fix), second.ToString());
        }
    }
}
