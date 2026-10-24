using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AssetOps
{
    // Edits prefabs in an isolated preview scene: add, delete, rename, move GameObjects, add and remove components,
    // set properties and references, add nested prefab instances, revert or unpack instances.
    // The prefab is saved only when every op of the target succeeded. Op reference: HierarchyEditor, Docs/Edit.md.
    // Usage: assetops.py EditPrefab --args '-spec="C:/temp/prefab.json" -apply'
    // Docs/Edit.md
    public sealed class EditPrefabCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Prefab GameObjects, components, properties and nested instances, all or nothing per prefab"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JObject spec;
            if (!Spec.Load(ctx, out spec))
            {
                return ExitCode.Refused;
            }
            JArray targets = Spec.Targets(ctx, spec);
            if (targets == null)
            {
                return ExitCode.Refused;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                if (!Spec.CheckKeys(ctx, targets[i], "Targets[" + i + "]", "AssetPath", "Ops"))
                {
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                JToken target = targets[i];
                string path = Json.Str(target, "AssetPath") ?? "(no AssetPath)";
                var ops = Json.Get(target, "Ops") as JArray;
                ctx.Progress(i, targets.Count, path);
                if (ops == null)
                {
                    ctx.FailTarget(path, "needs an \"Ops\" array");
                    continue;
                }
                string error;
                ctx.BeginTarget();
                using (PrefabSession session = PrefabSession.Open(path, out error))
                {
                    if (session == null)
                    {
                        ctx.FailTarget(path, error);
                        continue;
                    }
                    var editor = new HierarchyEditor(session.Root, path, ctx.Log);
                    if (!editor.Apply(ops) || ctx.TargetFailed())
                    {
                        ctx.FailTarget(path, editor.Errors.Count > 0 ? editor.Errors[0] : ctx.TargetError);
                        continue;
                    }
                    if (ctx.DryRun)
                    {
                        ctx.Log.Info(path + ": " + editor.Changes + " op(s) would apply");
                        continue;
                    }
                    if (!session.Save(out error))
                    {
                        ctx.FailTarget(path, error ?? ctx.TargetError);
                        continue;
                    }
                    ctx.WarnIfSavedWithErrors(path);
                    written++;
                    ctx.Log.Info("saved " + path + " (" + editor.Changes + " op(s))");
                }
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("saved " + written + " prefab(s)");
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
