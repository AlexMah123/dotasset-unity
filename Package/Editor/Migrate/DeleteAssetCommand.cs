using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace DotAsset
{
    // Deletes assets only when nothing under -scandir still references them (-force overrides).
    // Goes to the OS trash by default, -permanent deletes outright.
    // Usage: dotasset.py DeleteAsset Assets/Old/Unused.mat[,Assets/Old/Folder] --args "-apply"
    // Docs/Migrate.md
    public sealed class DeleteAssetCommand : DotAssetCommand
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
                string path = DotAssetPaths.Normalize(raw).TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                {
                    ctx.FailTarget(path, "nothing at that path");
                    continue;
                }
                string unsaved = OpenWork.UnsavedIn(path);
                if (unsaved != null)
                {
                    ctx.FailTarget(path, unsaved);
                    continue;
                }
                targets.Add(path);
            }
            // "Assets/Old,Assets/Old/A.mat": the folder takes A.mat with it, a second delete would fail after the first.
            targets = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            targets = targets.Where(t => !targets.Any(o => o != t && t.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase))).ToList();
            ctx.Log.Info("indexing references under " + string.Join(", ", AssetScan.Roots(ctx.Args)));
            var index = AssetScan.Referencers(AssetScan.Roots(ctx.Args), ctx);
            bool force = ctx.Args.Has("force");
            // A referencer that is itself deleted does not count, but only if it really goes: a target refused for
            // its own referencers keeps its references alive, so the set shrinks until it is stable.
            var going = new List<string>(targets);
            if (!force)
            {
                bool shrunk = true;
                while (shrunk)
                {
                    shrunk = false;
                    foreach (string path in going.ToList())
                    {
                        if (Users(index, path, going).Count > 0)
                        {
                            going.Remove(path);
                            shrunk = true;
                        }
                    }
                }
            }
            foreach (string path in targets)
            {
                // A folder counts as referenced when anything outside it uses anything inside it.
                List<string> inside = AssetDatabase.IsValidFolder(path) ? AssetScan.All(new[] { path }) : new List<string> { path };
                var users = Users(index, path, force ? targets : going);
                foreach (var u in users.Take(20))
                {
                    ctx.Log.Warn(u.Key + " is referenced by " + u.Value + (targets.Contains(u.Value) ? " (a target that stays, its own references remain)" : string.Empty));
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
                    ctx.PlanFile("delete", path, inside.Count > 1 ? inside.Count + " assets" : null);
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

        // Who uses path (or anything inside a folder path), not counting users inside the assets that go.
        static List<KeyValuePair<string, string>> Users(Dictionary<string, List<string>> index, string path, List<string> going)
        {
            List<string> inside = AssetDatabase.IsValidFolder(path) ? AssetScan.All(new[] { path }) : new List<string> { path };
            return inside
                .SelectMany(p => AssetScan.ReferencersOf(index, p).Select(r => new KeyValuePair<string, string>(p, r)))
                .Where(u => !AssetScan.Under(u.Value, going))
                .ToList();
        }
    }
}
