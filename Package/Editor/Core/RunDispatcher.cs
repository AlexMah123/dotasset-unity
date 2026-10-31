using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DotAsset
{
    // Outcome of one run, the same shape on both paths: the queue writes it to done/<id>.json,
    // the headless entry writes it to the -dotassetResult file. The wrapper prints it.
    public sealed class RunResult
    {
        public string RunName;
        public int ExitCode;
        public bool DryRun;
        public string Summary;
        public string Error;
        public int Warnings;
        public int Errors;
        public double Seconds;
        public string LogFile;
        public List<string> Outputs = new List<string>();
        public List<string> FailedTargets = new List<string>();
        public List<string> Lines = new List<string>();
        public List<JObject> Plan = new List<JObject>();
        public bool PlanTruncated;
        public JObject PlanTotals;

        public JObject ToJson()
        {
            var json = new JObject
            {
                ["DotAssetVersion"] = DotAssetVersion.Version,
                ["RunName"] = RunName,
                ["ExitCode"] = ExitCode,
                ["DryRun"] = DryRun,
                ["Summary"] = Summary,
                ["Error"] = Error,
                ["Warnings"] = Warnings,
                ["Errors"] = Errors,
                ["Seconds"] = Math.Round(Seconds, 2),
                ["LogFile"] = LogFile,
                ["Outputs"] = new JArray(Outputs.Select(o => (object)o).ToArray()),
                ["FailedTargets"] = new JArray(FailedTargets.Select(o => (object)o).ToArray()),
                ["Lines"] = new JArray(Lines.Select(o => (object)o).ToArray()),
            };
            // Dry runs only, so a writing run's result keeps its old shape.
            if (DryRun)
            {
                json["Plan"] = new JArray(Plan.Select(o => (object)o).ToArray());
                json["PlanTruncated"] = PlanTruncated;
                json["PlanTotals"] = PlanTotals;
            }
            return json;
        }
    }

    public static class RunDispatcher
    {
        const int KeepLogs = 200;

        // Set while a run executes. Commands are never re-entered.
        public static bool IsRunning { get; private set; }

        public static event Action<RunResult> RunFinished;

        static int engineLogMuted;

        sealed class MuteScope : IDisposable
        {
            bool disposed;

            public void Dispose()
            {
                if (!disposed)
                {
                    disposed = true;
                    engineLogMuted--;
                }
            }
        }

        // Unity log lines inside this scope are not counted as the run's warnings or errors.
        public static IDisposable MuteEngineLog()
        {
            engineLogMuted++;
            return new MuteScope();
        }

        public static RunResult Run(string runName, IList<string> assets, string extraArgs, bool inEditor)
        {
            var watch = Stopwatch.StartNew();
            var log = new RunLog(runName ?? "(none)", inEditor);
            var result = new RunResult { RunName = runName };
            var ctx = new RunContext
            {
                RunName = runName,
                Assets = (assets ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => DotAssetPaths.Normalize(a.Trim())).ToList(),
                Args = RunArgs.Parse(extraArgs),
                Log = log,
                InEditor = inEditor,
                Group = Groups.Resolve(runName),
            };

            log.Info("DotAsset " + DotAssetVersion.Version + ", Unity " + Application.unityVersion + ", " + (inEditor ? "editor" : "batchmode"));
            ExitCode code;
            if (IsRunning)
            {
                log.Error("another run is still executing");
                code = ExitCode.Refused;
            }
            else
            {
                code = Execute(ctx);
            }

            result.ExitCode = (int)code;
            result.DryRun = ctx.DryRun;
            result.Outputs.AddRange(ctx.Outputs);
            result.FailedTargets.AddRange(ctx.FailedTargets);
            result.Plan.AddRange(ctx.Plan);
            result.PlanTruncated = ctx.PlanTruncated;
            result.PlanTotals = new JObject
            {
                ["Changes"] = ctx.PlanChangeCount,
                ["Files"] = ctx.PlanFileCount,
                ["Targets"] = ctx.PlanTargetCount,
            };
            result.Seconds = watch.Elapsed.TotalSeconds;
            result.Summary = BuildSummary(ctx, code, result.Seconds);
            log.Info(result.Summary);
            result.Warnings = log.Warnings;
            result.Errors = log.Errors;
            result.Lines.AddRange(log.Lines);
            bool failed = code == ExitCode.Failed || code == ExitCode.Refused;
            if (failed)
            {
                result.Error = ctx.FailedTargets.Count > 0
                    ? runName + " failed for " + string.Join(", ", ctx.FailedTargets) + ". " + (log.FirstError ?? string.Empty)
                    : log.FirstError ?? result.Summary;
            }
            result.LogFile = WriteLogFile(runName, result);

            if (inEditor)
            {
                string line = "[DotAsset] " + result.Summary + "  (log: " + DotAssetPaths.ToProjectRelative(result.LogFile ?? "") + ")";
                LogType type = failed ? LogType.Error : code == ExitCode.IssuesFound || log.Warnings > 0 ? LogType.Warning : LogType.Log;
                UnityEngine.Debug.LogFormat(type, LogOption.NoStacktrace, null, "{0}", line);
            }

            var handler = RunFinished;
            if (handler != null)
            {
                try
                {
                    handler(result);
                }
                catch (Exception)
                {
                }
            }
            return result;
        }

        static ExitCode Execute(RunContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.RunName))
            {
                ctx.Log.Error("no run name given");
                return ExitCode.Refused;
            }

            DotAssetCommand cmd = CommandRegistry.Find(ctx.RunName);
            if (cmd == null)
            {
                ctx.Log.Error("unknown run '" + ctx.RunName + "'. Known runs: " + string.Join(", ", CommandRegistry.All.Keys.OrderBy(k => k)));
                return ExitCode.Refused;
            }
            ctx.RunName = cmd.Name;
            ctx.Group = cmd.Group;

            if (!cmd.TakesAssetList && ctx.Assets.Count > 0)
            {
                ctx.Log.Error(cmd.Name + " takes no AssetList, its targets come from " + (cmd.Arguments.Contains("spec") ? "the spec" : "its arguments") + ". Got: " + string.Join(", ", ctx.Assets));
                return ExitCode.Refused;
            }

            if (ctx.Args.UnclosedQuote)
            {
                ctx.Log.Error("an argument has an unclosed quote, everything after it would be swallowed into its value: " + ctx.Args.Raw);
                return ExitCode.Refused;
            }
            List<string> badSwitches = ctx.Args.NormalizeSwitches();
            if (badSwitches.Count > 0)
            {
                ctx.Log.Error(string.Join(", ", badSwitches.Select(b => "-" + b)) + " is a switch: give it alone, or =true / =false");
                return ExitCode.Refused;
            }
            List<string> badIntegers = ctx.Args.BadIntegers();
            if (badIntegers.Count > 0)
            {
                ctx.Log.Error(string.Join(", ", badIntegers.Select(b => "-" + b + "=" + ctx.Args.Get(b, ""))) + " needs a whole number of 0 or more, e.g. -" + badIntegers[0] + "=100");
                return ExitCode.Refused;
            }

            if (ctx.Args.Stray.Count > 0)
            {
                ctx.Log.Error("stray argument" + (ctx.Args.Stray.Count > 1 ? "s " : " ") + string.Join(", ", ctx.Args.Stray.Select(t => "'" + t + "'"))
                    + ", every argument starts with - (e.g. -apply)");
                return ExitCode.Refused;
            }

            var accepted = new HashSet<string>(cmd.Arguments.Concat(CommandRegistry.CommonArguments(cmd.Group)), StringComparer.OrdinalIgnoreCase);
            var unknown = ctx.Args.Keys.Where(k => !accepted.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                ctx.Log.Error("unknown argument" + (unknown.Count > 1 ? "s " : " ") + string.Join(", ", unknown.Select(u => "-" + u))
                    + ". " + cmd.Name + " accepts: " + (accepted.Count == 0 ? "no arguments" : string.Join(", ", accepted.OrderBy(a => a).Select(a => "-" + a))));
                return ExitCode.Refused;
            }

            if (ctx.DryRun)
            {
                ctx.Log.Info("dry run, nothing is written. Add -apply to write.");
            }

            Application.LogCallback hook = (message, stack, type) =>
            {
                if (engineLogMuted > 0 || (message != null && message.StartsWith("[DotAsset]", StringComparison.Ordinal)))
                {
                    return;
                }
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    ctx.Log.EngineError(FirstLine(message));
                }
                else if (type == LogType.Warning)
                {
                    ctx.Log.EngineWarning(FirstLine(message));
                }
            };

            // Writing runs keep every file they touch as it was, for -atomic and UndoRun.
            RunJournal journal = null;
            if (!ctx.DryRun && cmd.Group != CommandGroup.Export && cmd.Group != CommandGroup.Audit)
            {
                journal = new RunJournal(ctx.RunName);
                RunJournal.Current = journal;
            }

            IsRunning = true;
            Application.logMessageReceived += hook;
            if (ctx.DryRun)
            {
                RunJournal.BeginDryRun(ctx.Log);
            }
            ExitCode code;
            try
            {
                code = cmd.Execute(ctx);
            }
            catch (Exception e)
            {
                ctx.Log.Error("unhandled " + e.GetType().Name + ": " + e.Message);
                ctx.Log.Info(e.StackTrace ?? string.Empty);
                code = ExitCode.Failed;
            }
            finally
            {
                Application.logMessageReceived -= hook;
                if (ctx.DryRun)
                {
                    RunJournal.EndDryRun();
                }
                RunJournal.Current = null;
                IsRunning = false;
                ctx.ClearProgress();
            }

            // A run that logged an error of its own did not fully succeed, whatever it returned:
            // e.g. one AssetList entry that does not exist among several that do.
            if ((code == ExitCode.Success || code == ExitCode.IssuesFound) && ctx.Log.OwnErrors > 0)
            {
                ctx.Log.Info("the run logged " + ctx.Log.OwnErrors + " error(s), exit code 1");
                code = ExitCode.Failed;
            }

            if (journal != null && journal.Count > 0)
            {
                // Migrate rewrites references across many files, a half-done migration is worse than none, so it is
                // atomic unless -atomic=false. Edit and Import are atomic only when asked.
                bool atomic = ctx.Args.Has("atomic") || (cmd.Group == CommandGroup.Migrate && !ctx.Args.Off("atomic"));
                if (code == ExitCode.Failed && atomic)
                {
                    // Rollback writes are not counted against the run, it has failed already.
                    List<string> left;
                    using (MuteEngineLog())
                    {
                        left = journal.RollBack(ctx.Log);
                    }
                    foreach (string problem in left)
                    {
                        ctx.Log.Warn("-atomic could not roll back " + problem);
                    }
                    ctx.Log.Info(left.Count == 0
                        ? "-atomic: a target failed, every file the run wrote is back as it was"
                        : "-atomic: a target failed, files rolled back except the " + left.Count + " above");
                }
                else
                {
                    ctx.AddOutput(DotAssetPaths.Absolute(journal.Close()));
                    ctx.Log.Info("undo with: UndoRun --args \"-run=" + journal.Id + " -apply\"");
                }
            }

            if (cmd.Group == CommandGroup.Export && code == ExitCode.Success)
            {
                var missing = ctx.Outputs.Where(o => !File.Exists(o)).ToList();
                foreach (string m in missing)
                {
                    ctx.Log.Error("missing output: " + m);
                }
                if (missing.Count > 0)
                {
                    code = ExitCode.Failed;
                }
            }
            return code;
        }

        static string BuildSummary(RunContext ctx, ExitCode code, double seconds)
        {
            string outcome;
            switch (code)
            {
                case ExitCode.Success:
                    outcome = ctx.DryRun ? "dry run passed" : "succeeded";
                    break;
                case ExitCode.IssuesFound:
                    outcome = "finished, issues found";
                    break;
                case ExitCode.Refused:
                    outcome = "refused";
                    break;
                default:
                    outcome = "failed";
                    break;
            }
            var sb = new StringBuilder();
            sb.Append(ctx.RunName ?? "(none)").Append(' ').Append(outcome);
            if (ctx.Outputs.Count > 0)
            {
                sb.Append(", ").Append(ctx.Outputs.Count).Append(ctx.Outputs.Count == 1 ? " output" : " outputs");
            }
            sb.Append(", ").Append(ctx.Log.Warnings).Append(" warning(s), ").Append(ctx.Log.Errors).Append(" error(s), ");
            sb.Append(seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('s');
            return sb.ToString();
        }

        static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }
            int nl = message.IndexOf('\n');
            return nl < 0 ? message : message.Substring(0, nl).TrimEnd('\r');
        }

        static string WriteLogFile(string runName, RunResult result)
        {
            try
            {
                Directory.CreateDirectory(DotAssetPaths.LogRoot);
                string path = DotAssetPaths.Combine(DotAssetPaths.LogRoot, DotAssetPaths.Timestamp() + "_" + Sanitize(runName ?? "none") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".log");
                File.WriteAllText(path, string.Join("\n", result.Lines) + "\n", new UTF8Encoding(false));
                var old = new DirectoryInfo(DotAssetPaths.LogRoot).GetFiles("*.log").OrderByDescending(f => f.Name).Skip(KeepLogs);
                foreach (var f in old)
                {
                    f.Delete();
                }
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }
}
