using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetWorkbench
{
    // Scene hierarchy as JSON, in the same node format as PrefabExport. Prefab instances export as overrides.
    // Usage: uaw.py SceneExport Assets/Scenes/Main.unity [--args "-expand -subtree=Environment"]
    // Docs/Export.md
    public sealed class SceneExportCommand : WorkbenchCommand
    {
        public override string Summary
        {
            get { return "Scene hierarchy, components and prefab instance overrides as JSON"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "expand", "subtree" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<string> entries;
            if (!ExportHelper.Entries(ctx, p => AssetScan.HasExtension(p, ".unity"), out entries))
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
            if (AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(SceneAsset))
            {
                ctx.Log.Error(path + " is not a scene");
                return false;
            }
            Scene scene = SceneManager.GetSceneByPath(path);
            bool openedByUs = false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                string error;
                scene = SceneSession.OpenForRun(path, out error);
                if (!scene.IsValid())
                {
                    ctx.Log.Error(error);
                    return false;
                }
                openedByUs = true;
            }
            try
            {
                JObject json = ExportHelper.Header(ctx, path);
                json["BuildIndex"] = SceneUtility.GetBuildIndexByScenePath(path);
                if (!openedByUs && scene.isDirty)
                {
                    ctx.Log.Warn(path + " has unsaved changes in the editor, the export shows them");
                    json["UnsavedChanges"] = true;
                }
                var opt = ReadOptions.FromArgs(ctx.Args, RefScope.ForScene(scene));
                var exporter = new HierarchyExporter(opt, defaults, ctx.Args.Has("full"), ctx.Args.Has("expand"));

                IList<GameObject> roots = scene.GetRootGameObjects();
                string subtree = ctx.Args.Get("subtree");
                if (!string.IsNullOrEmpty(subtree))
                {
                    string error;
                    GameObject start = HierarchyPath.Resolve(roots, subtree, out error);
                    if (start == null)
                    {
                        ctx.Log.Error(path + ": " + error);
                        return false;
                    }
                    roots = new List<GameObject> { start };
                }
                json["RootCount"] = scene.rootCount;
                JArray nodes = exporter.Export(roots);
                json["GameObjectCount"] = nodes.Count;
                json["Hierarchy"] = nodes;
                ExportHelper.Save(ctx, path, json);
                return true;
            }
            finally
            {
                if (openedByUs)
                {
                    SceneSession.CloseAfterRun(scene);
                }
            }
        }
    }
}
