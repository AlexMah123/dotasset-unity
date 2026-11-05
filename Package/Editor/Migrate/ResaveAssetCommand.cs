using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Rewrites assets through the current serializer: after [FormerlySerializedAs] on a renamed field, after a
    // package upgrade, or to normalize YAML before a diff. Pick assets by AssetList, by -dependson (every asset
    // under -scandir that directly uses that asset, e.g. a script), and by -type.
    // Usage: dotasset.py ResaveAsset --args "-dependson=Assets/Scripts/Inventory.cs -apply"
    // Docs/Migrate.md
    public sealed class ResaveAssetCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Force-reserializes assets, e.g. every asset using a script after a field rename"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "dependson", "scandir", "type", "metaonly" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (ctx.Assets.Count > 0)
            {
                foreach (string p in AssetScan.Expand(ctx.Assets, null, ctx.Log))
                {
                    paths.Add(p);
                }
            }
            List<string> dependsOn = ctx.Args.GetList("dependson");
            if (dependsOn.Count > 0)
            {
                foreach (string d in dependsOn)
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(d) == null)
                    {
                        ctx.Log.Error("-dependson: no asset at " + d);
                        return ExitCode.Refused;
                    }
                }
                var index = AssetScan.Referencers(AssetScan.Roots(ctx.Args), ctx);
                foreach (string d in dependsOn)
                {
                    foreach (string user in AssetScan.ReferencersOf(index, d))
                    {
                        paths.Add(user);
                    }
                }
            }
            else if (ctx.Args.Has("scandir") && ctx.Assets.Count == 0)
            {
                foreach (string p in AssetScan.All(AssetScan.Roots(ctx.Args)))
                {
                    paths.Add(p);
                }
            }
            if (paths.Count == 0 && ctx.Assets.Count == 0 && dependsOn.Count == 0 && !ctx.Args.Has("scandir"))
            {
                ctx.Log.Error("ResaveAsset needs AssetList, -dependson or -scandir");
                return ExitCode.Refused;
            }

            string typeName = ctx.Args.Get("type");
            if (typeName != null)
            {
                string error;
                Type type = TypeResolver.Find(typeName, typeof(Object), out error);
                if (type == null)
                {
                    ctx.Log.Error(error);
                    return ExitCode.Refused;
                }
                paths.RemoveWhere(p =>
                {
                    Type main = AssetDatabase.GetMainAssetTypeAtPath(p);
                    return main == null || !type.IsAssignableFrom(main);
                });
            }
            // Code and folders have nothing to reserialize.
            paths.RemoveWhere(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || AssetDatabase.IsValidFolder(p));
            // A resave rewrites the file from disk. One the editor holds unsaved changes for would leave the editor
            // and the file disagreeing, and one side's changes lost.
            foreach (string p in paths.OrderBy(p => p, StringComparer.Ordinal).ToList())
            {
                string refused = ReadOnly.Reason(p) ?? OpenWork.UnsavedIn(p);
                if (refused != null)
                {
                    ctx.FailTarget(p, refused);
                    paths.Remove(p);
                }
            }

            var list = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (string p in list.Take(200))
            {
                ctx.Log.Info((ctx.DryRun ? "would resave " : "resave ") + p);
            }
            if (list.Count > 200)
            {
                ctx.Log.Info("... and " + (list.Count - 200) + " more");
            }
            if (list.Count == 0)
            {
                ctx.Log.Warn("no assets matched");
                return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
            }
            if (ctx.DryRun)
            {
                foreach (string p in list)
                {
                    ctx.PlanFile("resave", p, null);
                }
                ctx.Log.Info(list.Count + " asset(s) would be resaved");
                return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
            }
            bool metaOnly = ctx.Args.Has("metaonly");
            var options = metaOnly ? ForceReserializeAssetsOptions.ReserializeMetadata : ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata;
            // Kept before the reserialize, not left to the save hook. An imported file (texture, model, audio) is only
            // read, its resave writes the .meta alone, so the journal copies just that.
            foreach (string path in list)
            {
                if (metaOnly || IsForeign(path))
                {
                    RunJournal.KeepMeta(path);
                }
                else
                {
                    RunJournal.Keep(path);
                }
            }
            RunJournal.MetaOnlySaves = true;
            try
            {
                AssetDatabase.ForceReserializeAssets(list, options);
            }
            finally
            {
                RunJournal.MetaOnlySaves = false;
            }
            ctx.Log.Info("resaved " + list.Count + " asset(s)");
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // Importers of formats Unity reads but never writes (PNG, FBX, WAV, MP4, TTF). Not IsForeignAsset, which is
        // true for a prefab too. Anything else counts as native, which journals the whole file.
        internal static bool IsForeign(string path)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            return importer is TextureImporter || importer is ModelImporter || importer is AudioImporter
                || importer is VideoClipImporter || importer is TrueTypeFontImporter;
        }
    }
}
