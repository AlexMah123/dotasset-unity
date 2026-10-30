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
                ctx.Log.Error("no journal " + id + " in Library/DotAsset/Journal. Only the newest 30 writing runs are kept, rolled back -atomic runs keep none");
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
            ctx.Log.Info((ctx.DryRun ? "would undo " : "undoing ") + runName + " run " + json["Id"] + ", " + entries.Count + " path(s)"
                + ((bool?)json["Interrupted"] == true ? ", a run that stopped before it finished" : string.Empty));

            // Every path must still be as the run left it, and free to write.
            foreach (RunJournal.Entry e in entries)
            {
                string reason = RunJournal.Hash(e.Path) != e.After
                    ? "changed after " + runName + " wrote it, the undo would lose that change"
                    : RunJournal.Blocked(e.Path) ?? OpenWork.UnsavedIn(e.Path);
                if (reason != null)
                {
                    ctx.FailTarget(e.Path, reason);
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
            if (RunJournal.Current != null)
            {
                json["UndoneBy"] = RunJournal.Current.Id;
                Json.WriteFile(DotAssetPaths.Combine(dir, RunJournal.FileName), json);
            }
            return problems.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
