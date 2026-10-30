using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DotAsset
{
    // Deletes assets only when nothing under -scandir still references them and the project does not name them in its
    // settings, Build Settings, Build Profiles, Addressables or AssetBundles (-force overrides). Assets in a Resources
    // or StreamingAssets folder get a warning: code may load them by name, which no reference shows.
    // Goes to the OS trash by default, -permanent deletes outright. -spec takes {"Assets": [...]}, the fix spec of
    // AuditUnusedAsset, in place of or beside AssetList.
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
            get { return new[] { "scandir", "force", "permanent", "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var given = new List<string>(ctx.Assets);
            if (ctx.Args.Has("spec"))
            {
                JObject spec;
                if (!Spec.Load(ctx, out spec))
                {
                    return ExitCode.Refused;
                }
                var list = spec["Assets"] as JArray;
                if (list == null || list.Any(t => t.Type != JTokenType.String))
                {
                    ctx.Log.Error("-spec needs {\"Assets\": [\"Assets/...\", ...]}");
                    return ExitCode.Refused;
                }
                given.AddRange(list.Select(t => (string)t));
                if (given.Count == 0)
                {
                    ctx.Log.Info("the spec lists no assets, nothing to delete");
                    return ExitCode.Success;
                }
            }
            if (given.Count == 0)
            {
                ctx.Log.Error("DeleteAsset needs the assets to delete in AssetList or -spec");
                return ExitCode.Refused;
            }
            var targets = new List<string>();
            foreach (string raw in given)
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
            // The project's own references count wherever -scandir points: their referencer is a setting, not an asset.
            foreach (var named in ProjectRoots.Named())
            {
                List<string> list;
                if (!index.TryGetValue(named.Key, out list))
                {
                    list = new List<string>();
                    index.Add(named.Key, list);
                }
                list.Add(named.Value);
            }
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
                string byName = inside.FirstOrDefault(LoadedByName);
                if (byName != null)
                {
                    ctx.Log.Warn(byName + " is in a Resources or StreamingAssets folder, code may load it by name, which no reference shows");
                }
                if (ctx.DryRun)
                {
                    ctx.Log.Info("would delete " + path + (inside.Count > 1 ? " (" + inside.Count + " assets)" : string.Empty));
                    ctx.PlanFile("delete", path, inside.Count > 1 ? inside.Count + " assets" : null);
                    continue;
                }
                RunJournal.Keep(path);
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

        static bool LoadedByName(string path)
        {
            string[] parts = path.Split('/');
            return parts.Take(parts.Length - 1).Any(p => p == "Resources" || p == "StreamingAssets");
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
