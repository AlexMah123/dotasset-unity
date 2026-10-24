using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Any asset file read through Unity's serializer: ScriptableObjects, and the main object plus sub-assets of
    // anything else. ScriptableObject properties are the delta from a fresh instance (-full for everything).
    // Prefabs and scenes go through PrefabExport and SceneExport, materials read better through MaterialExport.
    // Usage: assetops.py AssetExport Assets/Data/Items[,Assets/Data/Loot.asset] [--args "-type=ItemDefinition -nosub"]
    // Docs/Export.md
    public sealed class AssetExportCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "ScriptableObjects and other assets with their sub-assets, through Unity's serializer"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "type", "nosub" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            System.Type filterType = null;
            string typeName = ctx.Args.Get("type");
            if (typeName != null)
            {
                string error;
                filterType = TypeResolver.Find(typeName, typeof(Object), out error);
                if (filterType == null)
                {
                    ctx.Log.Error(error);
                    return ExitCode.Refused;
                }
            }

            List<string> entries;
            if (!ExportHelper.Entries(ctx, p => Accept(p, filterType), out entries))
            {
                return ExportHelper.NoEntries(ctx);
            }
            bool failed = false;
            using (var defaults = new DefaultValues())
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    string path = entries[i];
                    ctx.Progress(i, entries.Count, path);
                    System.Type main = AssetDatabase.GetMainAssetTypeAtPath(path);
                    if (main == typeof(SceneAsset) || main == typeof(GameObject))
                    {
                        ctx.Log.Error(path + " is a " + (main == typeof(SceneAsset) ? "scene, use SceneExport" : "prefab or model, use PrefabExport"));
                        failed = true;
                        continue;
                    }
                    JObject json = ExportHelper.Header(ctx, path);
                    json["MainType"] = main != null ? TypeResolver.DisplayName(main) : null;
                    var objects = new JArray();
                    Object[] all = ctx.Args.Has("nosub")
                        ? new[] { AssetDatabase.LoadMainAssetAtPath(path) }
                        : AssetDatabase.LoadAllAssetsAtPath(path);
                    var opt = ReadOptions.FromArgs(ctx.Args, RefScope.None);
                    foreach (Object o in all.Where(o => o != null).OrderBy(o => AssetDatabase.IsMainAsset(o) ? 0 : 1))
                    {
                        if ((o.hideFlags & HideFlags.HideInHierarchy) != 0 && !AssetDatabase.IsMainAsset(o) && !ctx.Args.Has("full"))
                        {
                            // Hidden sub-objects, e.g. the states inside an Animator Controller, belong to their dedicated export.
                            continue;
                        }
                        var entry = new JObject
                        {
                            ["Name"] = o.name,
                            ["Type"] = TypeResolver.DisplayName(o.GetType()),
                            ["Main"] = AssetDatabase.IsMainAsset(o),
                        };
                        string guid;
                        long localId;
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out localId))
                        {
                            entry["LocalId"] = localId;
                        }
                        var so = o as ScriptableObject;
                        if (so != null)
                        {
                            MonoScript script = MonoScript.FromScriptableObject(so);
                            string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                            if (!string.IsNullOrEmpty(scriptPath))
                            {
                                entry["Script"] = scriptPath;
                            }
                        }
                        JObject defaultsFor = ctx.Args.Has("full") || so == null ? null : defaults.For(o.GetType());
                        entry["Properties"] = PropertyIO.Read(o, opt, defaultsFor);
                        objects.Add(entry);
                    }
                    json["Objects"] = objects;
                    ExportHelper.Save(ctx, path, json);
                }
            }
            return failed ? ExitCode.Failed : ExitCode.Success;
        }

        static bool Accept(string path, System.Type filterType)
        {
            System.Type main = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (main == null || main == typeof(SceneAsset) || main == typeof(GameObject) || main == typeof(MonoScript) || main == typeof(DefaultAsset))
            {
                return false;
            }
            return filterType == null || filterType.IsAssignableFrom(main);
        }
    }
}
