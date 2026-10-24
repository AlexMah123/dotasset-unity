using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Removes "Missing Script" components from prefabs and scenes. Ones inside a prefab instance belong to the
    // source prefab and are reported, not touched. Run RemapScript first if the script still exists under a new GUID.
    // Usage: dotasset.py RemoveMissingScripts Assets/Prefabs[,Assets/Scenes/Main.unity] --args "-apply"
    //        dotasset.py RemoveMissingScripts --args "-scandir=Assets -apply"
    //        dotasset.py RemoveMissingScripts --args '-spec="<AuditMissingReference fix spec>" -apply'
    // Docs/Migrate.md
    public sealed class RemoveMissingScriptsCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Removes Missing Script components from prefabs and scenes"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            System.Func<string, bool> filter = p => AssetScan.HasExtension(p, ".prefab", ".unity");
            var entries = new List<string>(ctx.Assets);
            if (ctx.Args.Has("spec"))
            {
                // AuditMissingReference's fix spec: {"Files": [...]}
                Newtonsoft.Json.Linq.JObject spec;
                if (!Spec.Load(ctx, out spec))
                {
                    return ExitCode.Refused;
                }
                string strays = Json.UnknownKeys(spec, "Files");
                if (strays != null)
                {
                    ctx.Log.Error("spec: " + strays);
                    return ExitCode.Refused;
                }
                var files = Json.Get(spec, "Files") as Newtonsoft.Json.Linq.JArray;
                if (files == null)
                {
                    ctx.Log.Error("the spec has no \"Files\" array");
                    return ExitCode.Refused;
                }
                if (files.Any(f => f.Type != Newtonsoft.Json.Linq.JTokenType.String || string.IsNullOrEmpty((string)f)))
                {
                    ctx.Log.Error("\"Files\" is an array of asset path strings");
                    return ExitCode.Refused;
                }
                entries.AddRange(files.Select(f => (string)f));
                if (entries.Count == 0)
                {
                    // An emptied fix spec means "nothing to clean", never "clean the whole project".
                    ctx.Log.Info("the spec lists no files, nothing to do");
                    return ExitCode.Success;
                }
            }
            if (entries.Count == 0 && !ctx.Args.Has("scandir"))
            {
                ctx.Log.Error("RemoveMissingScripts needs AssetList, -spec or -scandir. It does not clean all of Assets unasked");
                return ExitCode.Refused;
            }
            foreach (string skipped in entries.Where(e => !AssetDatabase.IsValidFolder(e) && !filter(e)))
            {
                ctx.Log.Warn(skipped + " is not a prefab or scene, a missing script there needs RemapScript");
            }
            List<string> paths = entries.Count > 0
                ? AssetScan.Expand(entries, filter, ctx.Log).Where(filter).ToList()
                : AssetScan.All(AssetScan.Roots(ctx.Args), filter);
            int total = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                ctx.Progress(i, paths.Count, path);
                ctx.BeginTarget();
                string error;
                int removed;
                bool ok = path.EndsWith(".unity") ? Scene(ctx, path, out removed, out error) : Prefab(ctx, path, out removed, out error);
                if (!ok || (ctx.DryRun && ctx.TargetFailed()))
                {
                    ctx.FailTarget(path, error ?? ctx.TargetError);
                    continue;
                }
                if (!ctx.DryRun && removed > 0)
                {
                    ctx.WarnIfSavedWithErrors(path);
                }
                total += removed;
            }
            ctx.Log.Info((ctx.DryRun ? "would remove " : "removed ") + total + " missing script component(s) in " + paths.Count + " file(s) checked");
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static int Clean(RunContext ctx, string path, IEnumerable<GameObject> roots, bool useUndo, bool dryRun)
        {
            int removed = 0;
            foreach (GameObject go in roots.SelectMany(HierarchyPath.DepthFirst).ToList())
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing == 0)
                {
                    continue;
                }
                if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                    int inherited = source != null ? GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(source) : 0;
                    if (missing > inherited)
                    {
                        // Added as an override on this instance: they belong to this file, but are not removed here.
                        ctx.Log.Warn(path + ": " + HierarchyPath.Of(go) + " has " + (missing - inherited) + " missing-script component(s) added on the prefab instance, left as they are. "
                            + "DotAsset does not remove instance overrides, remove them in the Inspector, unpack the instance, or RemapScript their GUID");
                    }
                    if (inherited > 0)
                    {
                        ctx.Log.Warn(path + ": " + HierarchyPath.Of(go) + " has " + inherited + " missing script(s) from its source prefab " + (original != null ? AssetDatabase.GetAssetPath(original) : "?") + ", clean that prefab");
                    }
                    continue;
                }
                ctx.Log.Info(path + ": " + HierarchyPath.Of(go) + " has " + missing + " missing script(s)");
                if (!dryRun)
                {
                    if (useUndo)
                    {
                        // The full hierarchy record also captures the component list, a plain object record does not.
                        Undo.RegisterFullObjectHierarchyUndo(go, "Remove missing scripts");
                    }
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                }
                removed += missing;
            }
            return removed;
        }

        static bool Prefab(RunContext ctx, string path, out int removed, out string error)
        {
            removed = 0;
            using (PrefabSession session = PrefabSession.Open(path, out error))
            {
                if (session == null)
                {
                    return false;
                }
                removed = Clean(ctx, path, new[] { session.Root }, false, ctx.DryRun);
                if (removed == 0 || ctx.DryRun)
                {
                    return true;
                }
                return ctx.ReadyToSave(out error) && session.Save(out error);
            }
        }

        static bool Scene(RunContext ctx, string path, out int removed, out string error)
        {
            removed = 0;
            using (SceneSession session = SceneSession.Open(path, ctx.RunName, out error))
            {
                if (session == null)
                {
                    return false;
                }
                removed = Clean(ctx, path, session.Scene.GetRootGameObjects(), session.UseUndo, ctx.DryRun);
                if (removed == 0 || ctx.DryRun)
                {
                    return true;
                }
                return ctx.ReadyToSave(out error) && session.Save(out error);
            }
        }
    }
}
