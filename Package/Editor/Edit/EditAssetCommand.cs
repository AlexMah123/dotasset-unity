using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Writes serialized properties of an asset object: a ScriptableObject, or the main object or a named
    // sub-asset of any other asset file. Same Properties and ArrayOps format as AssetExport produces.
    // Usage: assetops.py EditAsset --args '-spec="C:/temp/items.json" -apply'
    // Docs/Edit.md
    public sealed class EditAssetCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Serialized properties of ScriptableObjects and other asset objects"; }
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
                if (!Spec.CheckKeys(ctx, targets[i], "Targets[" + i + "]", "AssetPath", "Sub", "SubType", "Properties", "ArrayOps"))
                {
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            foreach (JToken target in targets)
            {
                string path = Json.Str(target, "AssetPath");
                string label = path + (Json.Has(target, "Sub") ? " :: " + Json.Str(target, "Sub") : string.Empty);
                string error;
                Object obj = Resolve(target, out error);
                if (obj == null)
                {
                    ctx.FailTarget(label, error);
                    continue;
                }
                if (AssetDatabase.IsForeignAsset(obj))
                {
                    // A texture, model or audio clip comes out of its importer, a write would be lost on the next import.
                    ctx.FailTarget(label, path + " is imported from a source file, change its import settings with EditImporter");
                    continue;
                }
                var properties = Json.Get(target, "Properties") as JObject;
                var arrayOps = Json.Get(target, "ArrayOps") as JArray;
                if (properties == null && arrayOps == null)
                {
                    ctx.FailTarget(label, "needs \"Properties\" or \"ArrayOps\"");
                    continue;
                }

                ctx.BeginTarget();
                using (var snapshot = new AssetSnapshot(obj))
                {
                    var wc = new WriteContext { Scope = RefScope.None, UseUndo = false };
                    bool ok = PropertyIO.Write(obj, properties, arrayOps, wc);
                    if (!ok || ctx.TargetFailed())
                    {
                        ctx.FailTarget(label, wc.Errors.Count > 0 ? wc.Errors[0] : ctx.TargetError);
                        continue;
                    }
                    if (properties != null)
                    {
                        ctx.Log.Info((ctx.DryRun ? "would set " : "set ") + string.Join(", ", properties.Properties().Select(p => p.Name)) + " on " + label);
                    }
                    if (ctx.DryRun)
                    {
                        continue;
                    }
                    EditorUtility.SetDirty(obj);
                    AssetDatabase.SaveAssetIfDirty(obj);
                    snapshot.Commit();
                    written++;
                }
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("wrote " + written + " asset object(s)");
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static Object Resolve(JToken target, out string error)
        {
            error = null;
            string path = Json.Str(target, "AssetPath");
            if (string.IsNullOrEmpty(path))
            {
                error = "needs \"AssetPath\"";
                return null;
            }
            System.Type main = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (main == null)
            {
                error = "no asset at " + path;
                return null;
            }
            if (main == typeof(GameObject))
            {
                error = path + " is a prefab or model, use EditPrefab";
                return null;
            }
            if (main == typeof(SceneAsset))
            {
                error = path + " is a scene, use EditScene";
                return null;
            }
            string sub = Json.Str(target, "Sub");
            if (sub == null)
            {
                return AssetDatabase.LoadMainAssetAtPath(path);
            }
            string subTypeName = Json.Str(target, "SubType");
            System.Type subType = null;
            if (subTypeName != null)
            {
                subType = TypeResolver.Find(subTypeName, typeof(Object), out error);
                if (subType == null)
                {
                    return null;
                }
            }
            var matches = AssetDatabase.LoadAllAssetsAtPath(path)
                .Where(o => o != null && o.name == sub && (subType == null || subType.IsInstanceOfType(o)))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
            error = matches.Count == 0
                ? "no sub-asset '" + sub + "' in " + path
                : matches.Count + " sub-assets are named '" + sub + "', add \"SubType\"";
            return null;
        }
    }
}
