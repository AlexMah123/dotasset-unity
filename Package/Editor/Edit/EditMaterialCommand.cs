using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetOps
{
    // Changes materials through the shader's property names: values, textures, keywords, render queue, shader, variant parent.
    // Usage: assetops.py EditMaterial --args '-spec="C:/temp/mat.json" -apply'
    // Docs/Edit.md
    public sealed class EditMaterialCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Material shader properties, keywords, render queue, shader and variant parent"; }
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
            var allowed = new List<string>(MaterialWriter.Keys) { "AssetPath" };
            for (int i = 0; i < targets.Count; i++)
            {
                if (!Spec.CheckKeys(ctx, targets[i], "Targets[" + i + "]", allowed.ToArray()))
                {
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            foreach (JToken target in targets)
            {
                string path = Json.Str(target, "AssetPath");
                var mat = path != null ? AssetDatabase.LoadAssetAtPath<Material>(path) : null;
                if (mat == null)
                {
                    ctx.FailTarget(path ?? "(no AssetPath)", "no material at that path");
                    continue;
                }
                ctx.BeginTarget();
                var errors = new List<string>();
                using (var snapshot = new AssetSnapshot(mat))
                {
                    bool ok = MaterialWriter.Apply(mat, target, errors, ctx.Log);
                    if (!ok || ctx.TargetFailed())
                    {
                        ctx.FailTarget(path, errors.Count > 0 ? errors[0] : ctx.TargetError);
                        continue;
                    }
                    if (ctx.DryRun)
                    {
                        ctx.Log.Info("would write " + path);
                        continue;
                    }
                    EditorUtility.SetDirty(mat);
                    AssetDatabase.SaveAssetIfDirty(mat);
                    snapshot.Commit();
                    ctx.WarnIfSavedWithErrors(path);
                    written++;
                    ctx.Log.Info("wrote " + path);
                }
            }
            ctx.Log.Info((ctx.DryRun ? "checked " : "wrote ") + (ctx.DryRun ? targets.Count - ctx.FailedTargets.Count : written) + " material(s)");
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
