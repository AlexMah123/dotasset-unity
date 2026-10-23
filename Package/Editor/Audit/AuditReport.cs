using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AssetWorkbench
{
    // Report shared by every audit:
    //   {"ReportType", "Rules": {"T1": "..."}, "Checked", "IssueCount", "Counts": {"T1": 3},
    //    "Issues": [{"Rule", "Severity", "Asset", "Message", ...}], "FixSpec": "<path>"}
    // Fixable issues also go into a spec next to the report (<report>.fix.json) that the matching Edit or
    // Migrate run takes as is. Review it, then run that command with -apply.
    sealed class AuditReport
    {
        readonly RunContext ctx;
        readonly JObject rules = new JObject();
        readonly JArray issues = new JArray();
        readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        public int Checked;
        public JObject FixSpec;
        public string FixRun;
        public readonly JObject Extra = new JObject();

        public AuditReport(RunContext ctx)
        {
            this.ctx = ctx;
        }

        public int IssueCount
        {
            get { return issues.Count; }
        }

        public void Rule(string id, string description)
        {
            rules[id] = description;
        }

        public JObject Add(string rule, string severity, string asset, string message)
        {
            var issue = new JObject { ["Rule"] = rule, ["Severity"] = severity, ["Asset"] = asset, ["Message"] = message };
            issues.Add(issue);
            int n;
            counts.TryGetValue(rule, out n);
            counts[rule] = n + 1;
            return issue;
        }

        public ExitCode Finish()
        {
            string reportPath = WorkbenchPaths.AuditReportFile(ctx.RunName, ctx.Args.Get("report"));
            var json = new JObject
            {
                ["ExporterVersion"] = WorkbenchVersion.Version,
                ["ReportType"] = ctx.RunName,
                ["UnityVersion"] = Application.unityVersion,
                ["CreatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Rules"] = rules,
                ["Checked"] = Checked,
                ["IssueCount"] = issues.Count,
                ["Counts"] = JObject.FromObject(counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value)),
            };
            foreach (var kv in Extra)
            {
                json[kv.Key] = kv.Value;
            }
            json["Issues"] = issues;

            if (FixSpec != null && FixSpec.Properties().Any(p => p.Value is JArray && ((JArray)p.Value).Count > 0))
            {
                string fixPath = Path.ChangeExtension(reportPath, null) + ".fix.json";
                Json.WriteFile(fixPath, FixSpec);
                json["FixSpec"] = WorkbenchPaths.ToProjectRelative(fixPath);
                json["FixRun"] = FixRun;
                ctx.Log.Info("fix spec for " + FixRun + ": " + WorkbenchPaths.ToProjectRelative(fixPath));
            }
            Json.WriteFile(reportPath, json);
            ctx.AddOutput(reportPath);

            foreach (var kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                ctx.Log.Info(kv.Key + ": " + kv.Value + "  " + (string)rules[kv.Key]);
            }
            ctx.Log.Info("checked " + Checked + ", " + issues.Count + " issue(s)");
            return issues.Count > 0 ? ExitCode.IssuesFound : ExitCode.Success;
        }
    }
}
