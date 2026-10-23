using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AssetOps
{
    // Edits scenes with the same ops as EditPrefab. A scene open in the editor is edited in place on one undo
    // group: a failed or dry run reverts it, an applied run is one Ctrl+Z. A closed scene opens additively and
    // closes again. The scene is saved only when every op of the target succeeded.
    // Usage: assetops.py EditScene --args '-spec="C:/temp/scene.json" -apply'
    // Docs/Edit.md
    public sealed class EditSceneCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Scene GameObjects, components, properties and prefab instances, all or nothing per scene"; }
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
                using (SceneSession session = SceneSession.Open(path, ctx.RunName, out error))
                {
                    if (session == null)
                    {
                        ctx.FailTarget(path, error);
                        continue;
                    }
                    var editor = new HierarchyEditor(session.Scene, session.UseUndo, ctx.Log);
                    if (!editor.Apply(ops) || ctx.TargetFailed())
                    {
                        ctx.FailTarget(path, editor.Errors.Count > 0 ? editor.Errors[0] : ctx.Log.FirstError);
                        continue;
                    }
                    if (ctx.DryRun)
                    {
                        ctx.Log.Info(path + ": " + editor.Changes + " op(s) would apply");
                        continue;
                    }
                    if (!session.Save(out error) || ctx.TargetFailed())
                    {
                        ctx.FailTarget(path, error ?? ctx.Log.FirstError);
                        continue;
                    }
                    written++;
                    ctx.Log.Info("saved " + path + " (" + editor.Changes + " op(s))");
                }
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("saved " + written + " scene(s)");
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
