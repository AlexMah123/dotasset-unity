using System.Collections.Generic;
using UnityEditor;

namespace AssetOps
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

        // Edit and Migrate write only with -apply. Import and Audit ignore it.
        public bool Apply
        {
            get { return Args.Has("apply"); }
        }

        public bool DryRun
        {
            get { return Groups.IsDryRunByDefault(Group) && !Apply; }
        }

        int targetErrorMark;

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

        public void AddOutput(string absolutePath)
        {
            Outputs.Add(AssetOpsPaths.Normalize(absolutePath));
            Log.Info("output: " + AssetOpsPaths.ToProjectRelative(absolutePath));
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
                EditorUtility.DisplayProgressBar("AssetOps: " + RunName, label, done / (float)total);
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

        int lastBucket = -1;
        bool progressShown;

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
