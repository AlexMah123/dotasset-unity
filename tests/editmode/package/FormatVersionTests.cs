using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // N3: every export, audit report and run result says which format it follows. FormatVersion goes up only on a
    // breaking change: a key removed or renamed, or its meaning changed. New keys may appear without it. The lists
    // below are the golden copy of what a caller can rely on, so a change to one is a change to the contract and
    // belongs in Version.md and Docs/AI-Guide.md.
    public sealed class FormatVersionTests : RunFixture
    {
        static readonly string[] ExportHeader =
        {
            "ExporterVersion", "FormatVersion", "ExportType", "UnityVersion", "ExportedUtc", "AssetPath", "Guid", "DependencyHash", "Options",
        };

        static readonly string[] ReportHeader =
        {
            "ExporterVersion", "FormatVersion", "ReportType", "UnityVersion", "CreatedUtc", "Rules", "Checked", "IssueCount", "Counts",
        };

        static readonly string[] ResultKeys =
        {
            "DotAssetVersion", "FormatVersion", "RunName", "ExitCode", "DryRun", "Summary", "Error", "Warnings", "Errors", "Seconds",
            "LogFile", "Outputs", "FailedTargets", "Lines",
        };

        static readonly string[] DryRunKeys = { "Plan", "PlanTruncated", "PlanTotals" };

        static string[] Keys(JObject json)
        {
            return json.Properties().Select(p => p.Name).ToArray();
        }

        [Test]
        public void AnExportHeaderHasTheFormatVersion()
        {
            JObject red = Export("MaterialExport", Red, "-full");
            CollectionAssert.AreEqual(ExportHeader, Keys(red).Take(ExportHeader.Length).ToArray(), red.ToString());
            Assert.AreEqual(1, (int?)red["FormatVersion"]);
        }

        [Test]
        public void AnAuditReportHeaderHasTheFormatVersion()
        {
            JObject report = Report("AuditMaterial", AnyIssues, "", Folder);
            CollectionAssert.AreEqual(ReportHeader, Keys(report).Take(ReportHeader.Length).ToArray(), report.ToString());
            Assert.AreEqual(1, (int?)report["FormatVersion"]);
        }

        [Test]
        public void ARunResultHasTheFormatVersion()
        {
            JObject written = Ok("MaterialExport", "", Red).ToJson();
            Assert.IsFalse((bool)written["DryRun"]);
            CollectionAssert.AreEquivalent(ResultKeys, Keys(written), written.ToString());
            Assert.AreEqual(1, (int?)written["FormatVersion"]);

            JObject dry = Ok("DeleteAsset", "-scandir=" + Folder, Door).ToJson();
            Assert.IsTrue((bool)dry["DryRun"]);
            CollectionAssert.AreEquivalent(ResultKeys.Concat(DryRunKeys).ToArray(), Keys(dry), dry.ToString());
            Assert.IsTrue(File.Exists(Absolute(Door)), "the DeleteAsset dry run deleted the controller");

            JObject unknown = new RunResult { RunName = "EditMaterial", Outcome = "unknown" }.ToJson();
            CollectionAssert.AreEquivalent(ResultKeys.Concat(new[] { "Outcome" }).ToArray(), Keys(unknown), unknown.ToString());
        }
    }
}
