using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DotAsset
{
    // Puts back every file an earlier writing run wrote, created, moved or deleted, from that run's journal. Refused
    // as a whole when any of those files changed after the run, so an undo never throws away later work.
    // Usage: dotasset.py UndoRun --args "-run=last -apply"
    // Docs/Migrate.md
    public sealed class UndoRunCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Restores every file an earlier run wrote, from its journal"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "run" }; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            string id = ctx.Args.Get("run");
            if (string.IsNullOrEmpty(id))
            {
                ctx.Log.Error("UndoRun needs -run=<id>, the journal id a writing run prints (Library/DotAsset/Journal/<id>), or -run=last");
                return ExitCode.Refused;
            }
            string dir = RunJournal.Find(id);
            JObject json = dir == null ? null : RunJournal.Load(DotAssetPaths.Combine(dir, RunJournal.FileName));
            if (json == null)
            {
                ctx.Log.Error("no journal " + id + " in Library/DotAsset/Journal. Only the newest 30 journals are kept (an undo keeps one too), plus those of interrupted runs and unfinished rollbacks until they are undone. A rolled back -atomic run keeps none");
                return ExitCode.Refused;
            }
            string notLast = id == "last" ? RefusesLast(json, RunJournal.Caller) : null;
            if (notLast != null)
            {
                ctx.Log.Error(notLast);
                return ExitCode.Refused;
            }
            string undoneBy = (string)json["UndoneBy"];
            if (undoneBy != null)
            {
                ctx.Log.Error("run " + json["Id"] + " was already undone by run " + undoneBy + ". Undo that one to redo it");
                return ExitCode.Refused;
            }
            string runName = (string)json["RunName"];
            List<RunJournal.Entry> entries = RunJournal.EntriesOf(json);
            ctx.Log.Info((ctx.DryRun ? "would undo " : "undoing ") + runName + " run " + json["Id"] + ", " + entries.Count + " path(s)");
            if ((bool?)json["Interrupted"] == true)
            {
                // Its state after the run was taken when the next editor start or run found it, not when it died.
                ctx.Log.Warn("run " + json["Id"] + " stopped before it finished. The undo cannot see edits made outside Unity between the crash and the next Unity start, nor how far the last path it logged was written");
            }

            // Every path must still be as the run left it, and free to write.
            var journaled = new HashSet<string>(entries.Select(e => e.Path), System.StringComparer.Ordinal);
            foreach (RunJournal.Entry e in entries)
            {
                string reason = Changed(e, journaled, runName) ?? RunJournal.Blocked(e.Path, true, e.MetaOnly) ??OpenWork.UnsavedIn(e.Path);
                if (reason != null)
                {
                    ctx.FailTarget(e.Path == RunJournal.BuildSceneList ? "Build Settings scene list" : e.Path, reason);
                }
            }
            if (ctx.FailedTargets.Count > 0)
            {
                ctx.Log.Error("nothing was restored, the undo needs every path of the run");
                return ExitCode.Failed;
            }

            if (ctx.DryRun)
            {
                foreach (RunJournal.Entry e in entries.Where(e => !e.Folder || !e.Existed))
                {
                    if (e.Path == RunJournal.BuildSceneList)
                    {
                        ctx.PlanFile("restore", "Build Settings scene list", "as before " + runName);
                        continue;
                    }
                    if (e.Path.StartsWith(RunJournal.BuildScenePrefix, System.StringComparison.Ordinal))
                    {
                        ctx.PlanFile(e.Existed ? "add to build" : "remove from build", e.Path.Substring(RunJournal.BuildScenePrefix.Length),
                            "Build Settings scene list, as before " + runName);
                        continue;
                    }
                    string action = !e.Existed ? "delete" : e.After == "absent" ? "restore" : "rewrite";
                    ctx.PlanFile(action, e.Path, action == "delete" ? "created by " + runName : "as before " + runName);
                }
                return ExitCode.Success;
            }

            List<string> problems = RunJournal.Restore(dir, entries, ctx.Log);
            foreach (string problem in problems)
            {
                int colon = problem.IndexOf(": ", System.StringComparison.Ordinal);
                ctx.FailTarget(problem.Substring(0, colon), problem.Substring(colon + 2));
            }
            // A failed undo stays open, so it can be run again once the problem is fixed.
            if (problems.Count == 0 && RunJournal.Current != null)
            {
                json["UndoneBy"] = RunJournal.Current.Id;
                Json.WriteFile(DotAssetPaths.Combine(dir, RunJournal.FileName), json);
            }
            return problems.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // Why -run=last must not take the newest journal, or null. After an undo the newest run is that undo, so a
        // retry would redo the run. A run another caller started is theirs to undo. With no --caller on either side,
        // one caller working alone, it passes.
        internal static string RefusesLast(JObject newest, string caller)
        {
            string id = (string)newest["Id"];
            if ((string)newest["RunName"] == "UndoRun")
            {
                return "the newest run, " + id + ", is an undo, -run=last would redo what it undid. Pass -run=" + id
                    + " to redo on purpose, or the id of the run to undo";
            }
            string by = (string)newest["Caller"];
            if (by != caller)
            {
                return "the newest run, " + newest["RunName"] + " " + id + ", was started by " + (by == null ? "a caller without --caller" : "caller " + by)
                    + ", not by " + (caller == null ? "this caller (no --caller)" : "caller " + caller) + ". Pass -run=" + id + " to undo it anyway";
            }
            return null;
        }

        // Why the path is no longer as the run left it, or null. A path unreadable at either end never passes. A folder
        // journaled before 0.10.1 has the bare hash "folder": it passes only when everything in it belongs to the run,
        // since each of those has its own entry and hash check.
        static string Changed(RunJournal.Entry e, HashSet<string> journaled, string runName)
        {
            string now = RunJournal.Hash(e.Path);
            string after = e.After ?? string.Empty;
            if (now.StartsWith(RunJournal.Unreadable, System.StringComparison.Ordinal) || after.StartsWith(RunJournal.Unreadable, System.StringComparison.Ordinal))
            {
                return "could not be read (" + (now.StartsWith(RunJournal.Unreadable, System.StringComparison.Ordinal) ? now : after)
                    + "), the undo cannot tell whether it changed";
            }
            if (after == RunJournal.LegacyFolder && now.StartsWith(RunJournal.LegacyFolder + " ", System.StringComparison.Ordinal))
            {
                string stray = NotJournaledIn(e.Path, journaled);
                return stray == null ? null : stray + " is in the folder but not part of " + runName
                    + " (an older journal that kept no folder contents). Move it out with MoveAsset, then undo again";
            }
            return now == after ? null : "changed after " + runName + " wrote it, the undo would lose that change";
        }

        // The first file or folder under folder that is not an entry of the journal, or null.
        static string NotJournaledIn(string folder, HashSet<string> journaled)
        {
            string root = DotAssetPaths.Absolute(folder).TrimEnd('/');
            return System.IO.Directory.GetFileSystemEntries(root, "*", System.IO.SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase))
                .Select(p => folder + "/" + DotAssetPaths.Normalize(p.Substring(root.Length + 1)))
                .OrderBy(p => p, System.StringComparer.Ordinal)
                .FirstOrDefault(p => !journaled.Contains(p));
        }
    }
}
