using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Assetops
{
    static class ExportHelper
    {
        // Every export starts with the same header. ExporterVersion and ExportType are always there.
        public static JObject Header(RunContext ctx, string assetPath)
        {
            var json = new JObject
            {
                ["ExporterVersion"] = AssetopsVersion.Version,
                ["ExportType"] = ctx.RunName,
                ["UnityVersion"] = Application.unityVersion,
                ["ExportedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (assetPath != null)
            {
                json["AssetPath"] = assetPath;
                json["Guid"] = AssetDatabase.AssetPathToGUID(assetPath);
                // Changes whenever the asset or anything it depends on changes. Compare it to tell a stale export.
                json["DependencyHash"] = AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
            }
            var options = new JObject();
            foreach (string key in ctx.Args.Keys)
            {
                options[key] = ctx.Args.Get(key) ?? (JToken)true;
            }
            if (options.Count > 0)
            {
                json["Options"] = options;
            }
            return json;
        }

        public static string Save(RunContext ctx, string assetPath, JObject json)
        {
            string file = assetPath != null
                ? AssetopsPaths.ExportFileFor(assetPath, ctx.Args.Get("outdir"))
                : AssetopsPaths.ExportFileNamed(ctx.RunName, ctx.Args.Get("outdir"));
            Json.WriteFile(file, json);
            ctx.AddOutput(file);
            return file;
        }

        // Folder entries expand to the assets beneath them that pass the filter. An empty list refuses the run.
        public static bool Entries(RunContext ctx, Func<string, bool> filter, out System.Collections.Generic.List<string> entries)
        {
            entries = null;
            if (ctx.Assets.Count == 0)
            {
                ctx.Log.Error(ctx.RunName + " needs the assets to export in AssetList, e.g. Assets/Prefabs/Player.prefab or a folder");
                return false;
            }
            entries = AssetScan.Expand(ctx.Assets, filter, ctx.Log);
            if (entries.Count == 0)
            {
                ctx.Log.Error("nothing to export under " + string.Join(", ", ctx.Assets));
                return false;
            }
            return true;
        }
    }
}
