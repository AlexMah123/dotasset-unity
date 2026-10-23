using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AssetWorkbench
{
    // Finding assets, and the reverse dependency index Unity does not keep.
    public static class AssetScan
    {
        public static readonly string[] YamlExtensions =
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".overrideController", ".anim", ".playable",
            ".mask", ".physicMaterial", ".physicsMaterial2D", ".lighting", ".renderTexture", ".spriteatlas",
            ".flare", ".guiskin", ".fontsettings", ".signal", ".terrainlayer", ".brush", ".mixer",
        };

        // -scandir=Assets/A,Assets/B, default Assets
        public static List<string> Roots(RunArgs args)
        {
            var roots = args.GetList("scandir").Select(r => WorkbenchPaths.Normalize(r).TrimEnd('/')).ToList();
            if (roots.Count == 0)
            {
                roots.Add("Assets");
            }
            return roots;
        }

        public static bool Under(string path, IEnumerable<string> roots)
        {
            foreach (string root in roots)
            {
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // Every asset file under the roots, folders excluded.
        public static List<string> All(IEnumerable<string> roots, Func<string, bool> filter = null)
        {
            var list = roots.ToList();
            return AssetDatabase.GetAllAssetPaths()
                .Where(p => Under(p, list) && !AssetDatabase.IsValidFolder(p) && (filter == null || filter(p)))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        public static bool HasExtension(string path, params string[] extensions)
        {
            string ext = Path.GetExtension(path);
            return extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        // Entry assets as given, folders expanded to the assets beneath them that pass the filter.
        public static List<string> Expand(IEnumerable<string> assetsOrFolders, Func<string, bool> filter, RunLog log)
        {
            var result = new List<string>();
            foreach (string raw in assetsOrFolders)
            {
                string path = WorkbenchPaths.Normalize(raw).TrimEnd('/');
                if (AssetDatabase.IsValidFolder(path))
                {
                    result.AddRange(All(new[] { path }, filter));
                    continue;
                }
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                {
                    log.Error("no asset at " + path);
                    continue;
                }
                result.Add(path);
            }
            return result.Distinct().ToList();
        }

        // dependency path -> assets under the roots that reference it directly
        public static Dictionary<string, List<string>> Referencers(IEnumerable<string> roots, RunContext ctx)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> candidates = All(roots);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i % 200 == 0)
                {
                    ctx.Progress(i, candidates.Count, "indexing references");
                }
                string path = candidates[i];
                foreach (string dep in AssetDatabase.GetDependencies(path, false))
                {
                    if (dep == path)
                    {
                        continue;
                    }
                    List<string> list;
                    if (!map.TryGetValue(dep, out list))
                    {
                        list = new List<string>();
                        map.Add(dep, list);
                    }
                    list.Add(path);
                }
            }
            return map;
        }

        public static List<string> ReferencersOf(Dictionary<string, List<string>> index, string path)
        {
            List<string> list;
            return index.TryGetValue(path, out list) ? list : new List<string>();
        }

        public static bool IsForceText()
        {
            return EditorSettings.serializationMode == SerializationMode.ForceText;
        }
    }

    public static class Spec
    {
        // -spec=<path>, absolute or relative to the project root
        public static bool Load(RunContext ctx, out JObject spec)
        {
            spec = null;
            string path = ctx.Args.Get("spec");
            if (string.IsNullOrEmpty(path))
            {
                ctx.Log.Error(ctx.RunName + " needs -spec=<path to a JSON spec>, see Docs/" + ctx.Group + ".md");
                return false;
            }
            string error;
            if (!Json.TryReadFile(WorkbenchPaths.Absolute(path), out spec, out error))
            {
                ctx.Log.Error(error);
                return false;
            }
            return true;
        }

        public static JArray Targets(RunContext ctx, JObject spec, string key = "Targets")
        {
            var targets = Json.Get(spec, key) as JArray;
            if (targets == null || targets.Count == 0)
            {
                ctx.Log.Error("the spec has no \"" + key + "\" array");
                return null;
            }
            return targets;
        }

        // Spec keys a target may carry. Anything else is refused, a misspelt key must not be silently ignored.
        public static bool CheckKeys(RunContext ctx, JToken target, string label, params string[] allowed)
        {
            var obj = target as JObject;
            if (obj == null)
            {
                ctx.Log.Error(label + " is not an object");
                return false;
            }
            var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            var unknown = obj.Properties().Select(p => p.Name).Where(n => !set.Contains(n) && !n.StartsWith("//", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
            {
                ctx.Log.Error(label + ": unknown key" + (unknown.Count > 1 ? "s " : " ") + string.Join(", ", unknown) + ". Allowed: " + string.Join(", ", allowed));
                return false;
            }
            return true;
        }
    }
}
