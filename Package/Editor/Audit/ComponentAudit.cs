using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotAsset
{
    // The prefab and scene scan shared by the audits that check components (AuditMesh, AuditAudio).
    // Targets are .prefab and .unity files from AssetList, or under -scandir. A scene open in the editor is read as it
    // is, with a warning when it has unsaved changes, and one that is not open is opened for the run and closed after.
    // An issue on an object a prefab instance brings along belongs to the source prefab: when the scan includes that
    // prefab and the check finds the issue there too, only the prefab reports it, so a scene full of instances reports
    // each problem once.
    sealed class ComponentAudit
    {
        readonly RunContext ctx;
        readonly AuditReport report;
        readonly Func<string, bool> on;
        readonly List<string> paths;
        readonly HashSet<string> scanned;
        string current;

        public ComponentAudit(RunContext ctx, AuditReport report, Func<string, bool> on)
        {
            this.ctx = ctx;
            this.report = report;
            this.on = on;
            Func<string, bool> isTarget = p => AssetScan.HasExtension(p, ".prefab", ".unity");
            paths = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, isTarget, ctx.Log).Where(isTarget).ToList() : AssetScan.All(AssetScan.Roots(ctx.Args), isTarget);
            scanned = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        }

        // Calls check on every prefab root and every scene root GameObject, and sets the report's Checked.
        public void Run(Action<GameObject> check)
        {
            report.Checked = paths.Count;
            for (int i = 0; i < paths.Count; i++)
            {
                current = paths[i];
                if (i % 20 == 0)
                {
                    ctx.Progress(i, paths.Count, current);
                }
                if (current.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    RunScene(check);
                    continue;
                }
                var root = AssetDatabase.LoadMainAssetAtPath(current) as GameObject;
                if (root == null)
                {
                    ctx.Log.Warn(current + " did not load as a prefab, not checked");
                    continue;
                }
                check(root);
            }
        }

        void RunScene(Action<GameObject> check)
        {
            Scene scene = SceneManager.GetSceneByPath(current);
            bool wasListed = scene.IsValid() && !scene.isLoaded;
            bool openedByUs = false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                string error;
                scene = SceneSession.OpenForRun(current, out error);
                if (!scene.IsValid())
                {
                    ctx.Log.Warn(error + ", not checked");
                    return;
                }
                openedByUs = true;
            }
            else if (scene.isDirty)
            {
                ctx.Log.Warn(current + " has unsaved changes in the editor, the audit checks them");
            }
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    check(root);
                }
            }
            finally
            {
                if (openedByUs)
                {
                    SceneSession.CloseAfterRun(scene, wasListed);
                }
            }
        }

        // Reports what find says is wrong with the component, as an issue on the file being checked with the
        // GameObject as Object. Returns the issue for extra fields, or null when the rule is off, find returns null,
        // or the source prefab reports it.
        public JObject Add<T>(string rule, string severity, T component, Func<T, string> find) where T : Component
        {
            if (!on(rule))
            {
                return null;
            }
            string message = find(component);
            if (message == null || Inherited(component, find))
            {
                return null;
            }
            string obj = HierarchyPath.Of(component.gameObject);
            JObject issue = report.Add(rule, severity, current, obj + ": " + message);
            issue["Object"] = obj;
            return issue;
        }

        bool Inherited<T>(T component, Func<T, string> find) where T : Component
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(component))
            {
                return false;
            }
            T source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            return source != null && scanned.Contains(AssetDatabase.GetAssetPath(source)) && find(source) != null;
        }
    }
}
