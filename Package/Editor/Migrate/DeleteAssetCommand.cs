using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace AssetWorkbench
{
    // Deletes assets only when nothing under -scandir still references them (-force overrides).
    // Goes to the OS trash by default, -permanent deletes outright.
    // Usage: uaw.py DeleteAsset Assets/Old/Unused.mat[,Assets/Old/Folder] --args "-apply"
    // Docs/Migrate.md
    public sealed class DeleteAssetCommand : WorkbenchCommand
    {
        public override string Summary
        {
            get { return "Deletes assets that nothing references, to the trash by default"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "force", "permanent" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (ctx.Assets.Count == 0)
            {
                ctx.Log.Error("DeleteAsset needs the assets to delete in AssetList");
                return ExitCode.Refused;
            }
            var targets = new List<string>();
            foreach (string raw in ctx.Assets)
            {
                string path = WorkbenchPaths.Normalize(raw).TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                {
                    ctx.FailTarget(path, "nothing at that path");
                    continue;
                }
                targets.Add(path);
            }
            ctx.Log.Info("indexing references under " + string.Join(", ", AssetScan.Roots(ctx.Args)));
            var index = AssetScan.Referencers(AssetScan.Roots(ctx.Args), ctx);
            bool force = ctx.Args.Has("force");
            foreach (string path in targets)
            {
                // A folder counts as referenced when anything outside it uses anything inside it.
                List<string> inside = AssetDatabase.IsValidFolder(path) ? AssetScan.All(new[] { path }) : new List<string> { path };
                var users = inside
                    .SelectMany(p => AssetScan.ReferencersOf(index, p).Select(r => new { Asset = p, User = r }))
                    .Where(u => !AssetScan.Under(u.User, new[] { path }) && !targets.Contains(u.User))
                    .ToList();
                foreach (var u in users.Take(20))
                {
                    ctx.Log.Warn(u.Asset + " is referenced by " + u.User);
                }
                if (users.Count > 20)
                {
                    ctx.Log.Warn("... and " + (users.Count - 20) + " more reference(s)");
                }
                if (users.Count > 0 && !force)
                {
                    ctx.FailTarget(path, users.Count + " reference(s) remain, fix them or add -force");
                    continue;
                }
                if (ctx.DryRun)
                {
                    ctx.Log.Info("would delete " + path + (inside.Count > 1 ? " (" + inside.Count + " assets)" : string.Empty));
                    continue;
                }
                bool ok = ctx.Args.Has("permanent") ? AssetDatabase.DeleteAsset(path) : AssetDatabase.MoveAssetToTrash(path);
                if (!ok)
                {
                    ctx.FailTarget(path, "Unity refused the delete");
                    continue;
                }
                ctx.Log.Info((ctx.Args.Has("permanent") ? "deleted " : "moved to trash ") + path);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
