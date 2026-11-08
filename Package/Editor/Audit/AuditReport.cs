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
                string fixPath = FixPath(reportPath);
                Json.WriteFile(fixPath, FixSpec);
                json["FixSpec"] = DotAssetPaths.ToProjectRelative(fixPath);
                json["FixRun"] = FixRun;
                ctx.Log.Info("fix spec for " + FixRun + ": " + DotAssetPaths.ToProjectRelative(fixPath));
            }
            else
            {
                // A fix spec left from an earlier run at the same -report path would look current.
                string stale = FixPath(reportPath);
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

        static string FixPath(string reportPath)
        {
            return Path.ChangeExtension(reportPath, null) + ".fix.json";
        }

        // Why an audit cannot write its report at -report, or null. An audit does not write the project, so a path
        // under Assets/, Packages/ or ProjectSettings/ is refused. Nor does it replace a file DotAsset did not write:
        // a file there that is not a report, or a <stem>.fix.json that the report at that path does not name.
        // Checked with the arguments, so the dry run refuses too.
        public static string PathRefusal(string explicitPath)
        {
            if (string.IsNullOrEmpty(explicitPath))
            {
                return null;
            }
            string report;
            try
            {
                report = FullPath(explicitPath);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
            {
                return "-report=" + explicitPath + " is not a usable path: " + e.Message;
            }
            string relative = DotAssetPaths.ToProjectRelative(report);
            foreach (string root in new[] { "Assets", "Packages", "ProjectSettings" })
            {
                if (string.Equals(relative, root, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return "-report=" + explicitPath + " is inside " + root + "/, an audit does not write the project. Leave -report out "
                        + "(reports go to Library/DotAsset/Audit) or give a path outside Assets/, Packages/ and ProjectSettings/";
                }
            }
            JObject existing = null;
            string error;
            if (File.Exists(report) && !(Json.TryReadFile(report, out existing, out error) && IsReport(existing)))
            {
                return "-report=" + explicitPath + " is a file that is not a DotAsset report, the audit does not write over it. Give another path";
            }
            string fix = FixPath(report);
            if (File.Exists(fix))
            {
                JValue named = existing == null ? null : existing["FixSpec"] as JValue;
                bool ours;
                try
                {
                    ours = named != null && named.Type == JTokenType.String && string.Equals(FullPath((string)named), fix, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
                {
                    ours = false;
                }
                if (!ours)
                {
                    return fix + " is not the fix spec of a DotAsset report at -report=" + explicitPath
                        + ", the audit would replace or delete it. Move it or give another path";
                }
            }
            return null;
        }

        static string FullPath(string path)
        {
            return DotAssetPaths.Normalize(Path.GetFullPath(DotAssetPaths.Absolute(path)));
        }

        static bool IsReport(JObject json)
        {
            return json["ReportType"] is JValue && json["ExporterVersion"] is JValue && json["Issues"] is JArray;
        }
    }
}
