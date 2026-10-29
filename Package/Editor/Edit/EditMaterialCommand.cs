using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Changes materials through the shader's property names: values, textures, keywords, render queue, shader, variant parent.
    // Usage: dotasset.py EditMaterial --args '-spec="C:/temp/mat.json" -apply'
    // Docs/Edit.md
    public sealed class EditMaterialCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Material shader properties, keywords, render queue, shader and variant parent"; }
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
            JArray targets = Spec.LoadTargets(ctx);
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
                string refused = ReadOnly.Reason(path) ?? OpenWork.UnsavedIn(path);
                if (refused != null)
                {
                    ctx.FailTarget(path, refused);
                    continue;
                }
                if (AssetDatabase.IsForeignAsset(mat) || AssetImporter.GetAtPath(path) is ModelImporter)
                {
                    // A material that lives inside a model comes out of its importer, a write would be lost on the next import.
                    ctx.FailTarget(path, "the material at that path is imported from a model, extract it or remap it with EditImporter first");
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
