using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetOps
{
    // Prefab hierarchy, components and properties as JSON. Variants and nested instances export as overrides.
    // Usage: assetops.py PrefabExport Assets/Prefabs/Player.prefab[,more or a folder] [--args "-expand -subtree=Player/UI"]
    // Docs/Export.md
    public sealed class PrefabExportCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Prefab hierarchy, components and their properties as JSON"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "expand", "subtree" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<string> entries;
            if (!ExportHelper.Entries(ctx, p => AssetScan.HasExtension(p, ".prefab"), out entries))
            {
                return ExitCode.Refused;
            }
            bool failed = false;
            using (var defaults = new DefaultValues())
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    ctx.Progress(i, entries.Count, entries[i]);
                    if (!ExportOne(ctx, entries[i], defaults))
                    {
                        failed = true;
                    }
                }
            }
            return failed ? ExitCode.Failed : ExitCode.Success;
        }

        static bool ExportOne(RunContext ctx, string path, DefaultValues defaults)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (asset == null)
            {
                ctx.Log.Error(path + " is not a prefab or model");
                return false;
            }
            PrefabAssetType type = PrefabUtility.GetPrefabAssetType(asset);
            JObject json = ExportHelper.Header(ctx, path);
            json["PrefabAssetType"] = type.ToString();

            // Models are read straight from the asset, their hierarchy comes from the importer.
            // Regular prefabs and variants load into an isolated scene, where instance information is complete.
            GameObject root = type == PrefabAssetType.Model ? asset : PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (type == PrefabAssetType.Variant)
                {
                    Object source = PrefabUtility.GetCorrespondingObjectFromSource(root);
                    json["VariantOf"] = source != null ? AssetDatabase.GetAssetPath(source) : null;
                }
                var opt = ReadOptions.FromArgs(ctx.Args, RefScope.ForRoot(root));
                var exporter = new HierarchyExporter(opt, defaults, ctx.Args.Has("full"), ctx.Args.Has("expand"));

                GameObject start = root;
                string subtree = ctx.Args.Get("subtree");
                if (!string.IsNullOrEmpty(subtree))
                {
                    string error;
                    start = HierarchyPath.Resolve(new List<GameObject> { root }, subtree, out error);
                    if (start == null)
                    {
                        ctx.Log.Error(path + ": " + error);
                        return false;
                    }
                }
                JArray nodes = exporter.Export(new[] { start });
                json["GameObjectCount"] = nodes.Count;
                json["Hierarchy"] = nodes;
            }
            finally
            {
                if (type != PrefabAssetType.Model)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            ExportHelper.Save(ctx, path, json);
            return true;
        }
    }
}
