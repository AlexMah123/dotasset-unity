using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DotAsset
{
    // Everything a command sees of the run that invoked it.
    public sealed class RunContext
    {
        public string RunName { get; internal set; }
        public CommandGroup Group { get; internal set; }

        // Entry assets from the positional AssetList. Spec-driven runs name their targets in the spec instead.
        public IReadOnlyList<string> Assets { get; internal set; }

        public RunArgs Args { get; internal set; }
        public RunLog Log { get; internal set; }

        // True inside the open editor (queue path), false headless (-batchmode path).
        public bool InEditor { get; internal set; }

        // Files the run produced. Export runs must list one per entry asset, the dispatcher checks they exist.
        public readonly List<string> Outputs = new List<string>();

        public readonly List<string> FailedTargets = new List<string>();

        // What a dry run would change: {"Target", "Property", "Before", "After"} or {"Action", "Path", "Detail"}.
        public readonly List<JObject> Plan = new List<JObject>();
        public const int MaxPlan = 500;
        public bool PlanTruncated { get; private set; }

        // Totals over every planned entry, the ones past MaxPlan included.
        public int PlanChangeCount { get; private set; }
        public int PlanFileCount { get; private set; }
        readonly HashSet<string> planTargets = new HashSet<string>();

        public int PlanTargetCount
        {
            get { return planTargets.Count; }
        }

        // Edit and Migrate write only with -apply. Import and Audit accept it and ignore it.
        public bool Apply
        {
            get { return Args.Has("apply"); }
        }

        public bool DryRun
        {
            get { return Groups.IsDryRunByDefault(Group) && !Apply; }
        }

        int targetErrorMark;
        int lastBucket = -1;
        bool progressShown;

        // Call before writing a target, then TargetFailed() tells whether anything logged an error since,
        // including errors Unity itself raised while the target was written.
        public void BeginTarget()
        {
            targetErrorMark = Log.Errors;
        }

        public bool TargetFailed()
        {
            return Log.Errors > targetErrorMark;
        }

        // Call right before writing a target to disk: false (with the reason) when anything failed since BeginTarget.
        public bool ReadyToSave(out string error)
        {
            error = TargetFailed() ? TargetError : null;
            return error == null;
        }

        // Call right after a target reached disk. An error Unity logs during the save cannot undo the write, so it is
        // reported as a warning on a target that counts as written.
        public void WarnIfSavedWithErrors(string target)
        {
            if (TargetFailed())
            {
                Log.Warn(target + " was saved, but Unity logged an error while saving it: " + TargetError);
            }
        }

        // The first error logged since BeginTarget, the reason a failed target gives.
        public string TargetError
        {
            get
            {
                return targetErrorMark < Log.ErrorMessages.Count ? Log.ErrorMessages[targetErrorMark] : Log.FirstError;
            }
        }

        // The one failure line every write command uses:
        //   <RunName> failed for <target>. Reason: <first error logged while writing it>
        // Nothing of the target is saved when this is called.
        public void FailTarget(string target, string reason)
        {
            FailedTargets.Add(target);
            Log.Error(RunName + " failed for " + target + ". Reason: " + reason);
        }

        // Records the property changes a dry run would make to target. Does nothing when the run writes.
        public void PlanChanges(string target, List<JObject> changes)
        {
            if (!DryRun || changes == null)
            {
                return;
            }
            foreach (JObject change in changes)
            {
                var entry = new JObject { ["Target"] = target };
                entry.Merge(change);
                PlanChangeCount++;
                planTargets.Add(target);
                AddPlan(entry);
            }
        }

        // Records a file a dry run would create, move, delete or rewrite. Does nothing when the run writes.
        public void PlanFile(string action, string path, string detail)
        {
            if (!DryRun)
            {
                return;
            }
            var entry = new JObject { ["Action"] = action, ["Path"] = path };
            if (detail != null)
            {
                entry["Detail"] = detail;
            }
            PlanFileCount++;
            planTargets.Add(path);
            AddPlan(entry);
        }

        void AddPlan(JObject entry)
        {
            if (Plan.Count < MaxPlan)
            {
                Plan.Add(entry);
            }
            else
            {
                PlanTruncated = true;
            }
        }

        public void AddOutput(string absolutePath)
        {
            Outputs.Add(DotAssetPaths.Normalize(absolutePath));
            Log.Info("output: " + DotAssetPaths.ToProjectRelative(absolutePath));
        }

        // Progress for long runs. A progress bar in the editor, a log line every 10 percent headless.
        public void Progress(int done, int total, string label)
        {
            if (total <= 0)
            {
                return;
            }
            if (InEditor)
            {
                EditorUtility.DisplayProgressBar("DotAsset: " + RunName, label, done / (float)total);
                progressShown = true;
                return;
            }
            int bucket = done * 10 / total;
            if (bucket != lastBucket)
            {
                lastBucket = bucket;
                Log.Info("progress " + done + "/" + total);
            }
        }

        internal void ClearProgress()
        {
            if (progressShown)
            {
                EditorUtility.ClearProgressBar();
                progressShown = false;
            }
        }
    }
}
