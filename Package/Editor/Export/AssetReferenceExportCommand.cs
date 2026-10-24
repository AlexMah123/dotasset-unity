using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DotAsset
{
    // What an asset uses and what uses it. Unity keeps the first direction only, the second comes from
    // indexing every asset under -scandir (default Assets), so a project-wide answer costs one pass.
    // Usage: dotasset.py AssetReferenceExport Assets/Materials/Red.mat[,more] [--args "-scandir=Assets -recursive"]
    // Docs/Export.md
    public sealed class AssetReferenceExportCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Dependencies of an asset and the assets that reference it"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "recursive" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (ctx.Assets.Count == 0)
            {
                ctx.Log.Error("AssetReferenceExport needs the assets to look up in AssetList");
                return ExitCode.Refused;
            }
            List<string> entries = AssetScan.Expand(ctx.Assets, null, ctx.Log);
            if (entries.Count == 0)
            {
                ctx.Log.Error("nothing to look up under " + string.Join(", ", ctx.Assets));
                return ExitCode.Failed;
            }
            List<string> roots = AssetScan.Roots(ctx.Args);
            ctx.Log.Info("indexing references under " + string.Join(", ", roots));
            Dictionary<string, List<string>> index = AssetScan.Referencers(roots, ctx);
            bool recursive = ctx.Args.Has("recursive");

            foreach (string path in entries)
            {
                JObject json = ExportHelper.Header(ctx, path);
                json["Type"] = TypeResolver.DisplayName(AssetDatabase.GetMainAssetTypeAtPath(path));
                var direct = AssetDatabase.GetDependencies(path, false).Where(d => d != path).OrderBy(d => d).ToList();
                json["Dependencies"] = new JArray(direct.Select(d => (object)d).ToArray());
                if (recursive)
                {
                    var all = AssetDatabase.GetDependencies(path, true).Where(d => d != path).OrderBy(d => d).ToList();
                    json["DependenciesRecursive"] = new JArray(all.Select(d => (object)d).ToArray());
                }
                var referencers = AssetScan.ReferencersOf(index, path).OrderBy(r => r).ToList();
                json["ReferencedBy"] = new JArray(referencers.Select(r => (object)r).ToArray());
                json["ReferencedByScope"] = new JArray(roots.Select(r => (object)r).ToArray());
                if (recursive)
                {
                    var seen = new HashSet<string>(referencers);
                    var queue = new Queue<string>(referencers);
                    while (queue.Count > 0)
                    {
                        foreach (string next in AssetScan.ReferencersOf(index, queue.Dequeue()))
                        {
                            if (next != path && seen.Add(next))
                            {
                                queue.Enqueue(next);
                            }
                        }
                    }
                    json["ReferencedByRecursive"] = new JArray(seen.OrderBy(r => r).Select(r => (object)r).ToArray());
                }
                ExportHelper.Save(ctx, path, json);
            }
            return ExitCode.Success;
        }
    }
}
