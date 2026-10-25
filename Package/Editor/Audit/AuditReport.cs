using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DotAsset
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

        // -rules=T1,T3 limits the audit to some of the rules registered with Rule(), none given means all.
        // Null after logging an unknown id, the run is then refused.
        public Func<string, bool> RuleFilter()
        {
            var chosen = new HashSet<string>(ctx.Args.GetList("rules"), StringComparer.OrdinalIgnoreCase);
            var known = rules.Properties().Select(p => p.Name).ToList();
            var bad = chosen.Where(r => !known.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (bad.Count > 0)
            {
                ctx.Log.Error("-rules: unknown " + string.Join(", ", bad) + ". Rules: " + string.Join(", ", known));
                return null;
            }
            return id => chosen.Count == 0 || chosen.Contains(id);
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
            string reportPath = DotAssetPaths.AuditReportFile(ctx.RunName, ctx.Args.Get("report"));
            var json = new JObject
            {
                ["ExporterVersion"] = DotAssetVersion.Version,
                ["ReportType"] = ctx.RunName,
                ["UnityVersion"] = Application.unityVersion,
                ["CreatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
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
                json["FixSpec"] = DotAssetPaths.ToProjectRelative(fixPath);
                json["FixRun"] = FixRun;
                ctx.Log.Info("fix spec for " + FixRun + ": " + DotAssetPaths.ToProjectRelative(fixPath));
            }
            else
            {
                // A fix spec left from an earlier run at the same -report path would look current.
                string stale = Path.ChangeExtension(reportPath, null) + ".fix.json";
                try
                {
                    if (File.Exists(stale))
                    {
                        File.Delete(stale);
                    }
                }
                catch (IOException e)
                {
                    ctx.Log.Warn("could not remove the old fix spec " + stale + ": " + e.Message);
                }
                catch (UnauthorizedAccessException e)
                {
                    ctx.Log.Warn("could not remove the old fix spec " + stale + ": " + e.Message);
                }
            }
            Json.WriteFile(reportPath, json);
            ctx.AddOutput(reportPath);
            if (string.IsNullOrEmpty(ctx.Args.Get("report")))
            {
                DotAssetPaths.PruneOlder(reportPath, DotAssetPaths.KeepReportsPerAudit);
            }

            foreach (var kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                ctx.Log.Info(kv.Key + ": " + kv.Value + "  " + (string)rules[kv.Key]);
            }
            ctx.Log.Info("checked " + Checked + ", " + issues.Count + " issue(s)");
            return issues.Count > 0 ? ExitCode.IssuesFound : ExitCode.Success;
        }
    }
}
