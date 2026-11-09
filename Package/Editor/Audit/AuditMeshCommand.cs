using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotAsset
{
    // Checks how meshes are used in prefabs and scenes.
    //   G1  a mesh renderer with an empty material slot, or a submesh with no slot
    //   G2  an LODGroup level with no renderer, or one listing a renderer that is gone
    //   G3  a mesh over -triangles (default 10000) in a renderer that no LODGroup lists
    //   G4  a non-convex MeshCollider under a non-kinematic Rigidbody, which Unity does not support
    // An issue on an object that a prefab instance brings along belongs to the source prefab and is reported there
    // once, when the scan includes that prefab. No fix spec: every fix is a choice (a material, an LOD setup, convex
    // or kinematic), made with EditPrefab or EditScene on the Object the issue names.
    // Usage: dotasset.py AuditMesh [Assets/Prefabs] --args "-scandir=Assets -triangles=20000"
    // Docs/Audit.md
    public sealed class AuditMeshCommand : DotAssetCommand
    {
        const int DefaultTriangles = 10000;

        public override string Summary
        {
            get { return "Empty material slots, empty LOD levels, large meshes without LODs, non-convex MeshColliders on moving Rigidbodies"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "rules", "triangles" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("G1", "mesh renderer with an empty material slot, or a submesh with no slot");
            report.Rule("G2", "LODGroup level with no renderer, or listing a renderer that is gone");
            report.Rule("G3", "mesh over -triangles in a renderer that no LODGroup lists");
            report.Rule("G4", "non-convex MeshCollider under a non-kinematic Rigidbody");

            Func<string, bool> on = report.RuleFilter();
            if (on == null)
            {
                return ExitCode.Refused;
            }
            int triangles = DefaultTriangles;
            string given = ctx.Args.Get("triangles");
            if (given != null)
            {
                if (!int.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out triangles) || triangles < 0)
                {
                    ctx.Log.Error("-triangles=" + given + " is not a triangle count (a whole number, 0 or more)");
                    return ExitCode.Refused;
                }
            }
            report.Extra["Triangles"] = triangles;

            Func<string, bool> isTarget = p => AssetScan.HasExtension(p, ".prefab", ".unity");
            List<string> paths = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, isTarget, ctx.Log).Where(isTarget).ToList() : AssetScan.All(AssetScan.Roots(ctx.Args), isTarget);
            report.Checked = paths.Count;
            var check = new Checker(report, on, triangles, new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase));

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 20 == 0)
                {
                    ctx.Progress(i, paths.Count, path);
                }
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    CheckScene(ctx, path, check);
                    continue;
                }
                var root = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                if (root == null)
                {
                    ctx.Log.Warn(path + " did not load as a prefab, not checked");
                    continue;
                }
                check.Hierarchy(path, new[] { root });
            }
            return report.Finish();
        }

        static void CheckScene(RunContext ctx, string path, Checker check)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            bool wasListed = scene.IsValid() && !scene.isLoaded;
            bool openedByUs = false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                string error;
                scene = SceneSession.OpenForRun(path, out error);
                if (!scene.IsValid())
                {
                    ctx.Log.Warn(error + ", not checked");
                    return;
                }
                openedByUs = true;
            }
            else if (scene.isDirty)
            {
                ctx.Log.Warn(path + " has unsaved changes in the editor, the audit checks them");
            }
            try
            {
                check.Hierarchy(path, scene.GetRootGameObjects());
            }
            finally
            {
                if (openedByUs)
                {
                    SceneSession.CloseAfterRun(scene, wasListed);
                }
            }
        }

        sealed class Checker
        {
            readonly AuditReport report;
            readonly Func<string, bool> on;
            readonly long triangles;
            readonly HashSet<string> scanned;

            public Checker(AuditReport report, Func<string, bool> on, long triangles, HashSet<string> scanned)
            {
                this.report = report;
                this.on = on;
                this.triangles = triangles;
                this.scanned = scanned;
            }

            public void Hierarchy(string path, IEnumerable<GameObject> roots)
            {
                foreach (GameObject root in roots)
                {
                    foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                        {
                            Add(path, "G1", "Warning", renderer, Slots);
                            Add(path, "G3", "Info", renderer, Large);
                        }
                    }
                    foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                    {
                        Add(path, "G2", "Warning", group, Levels);
                    }
                    foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                    {
                        Add(path, "G4", "Error", collider, NonConvex);
                    }
                }
            }

            void Add<T>(string path, string rule, string severity, T component, Func<T, string> find) where T : Component
            {
                if (!on(rule))
                {
                    return;
                }
                string message = find(component);
                if (message == null || Inherited(component, find))
                {
                    return;
                }
                string obj = HierarchyPath.Of(component.gameObject);
                JObject issue = report.Add(rule, severity, path, obj + ": " + message);
                issue["Object"] = obj;
            }

            // The object comes from a prefab the scan includes, and has the same issue there: that prefab reports it.
            bool Inherited<T>(T component, Func<T, string> find) where T : Component
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(component))
                {
                    return false;
                }
                T source = PrefabUtility.GetCorrespondingObjectFromSource(component);
                return source != null && scanned.Contains(AssetDatabase.GetAssetPath(source)) && find(source) != null;
            }

            static string Slots(Renderer renderer)
            {
                Material[] materials = renderer.sharedMaterials;
                var empty = new List<string>();
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null)
                    {
                        empty.Add(i.ToString(CultureInfo.InvariantCulture));
                    }
                }
                var parts = new List<string>();
                if (empty.Count > 0)
                {
                    parts.Add((empty.Count == 1 ? "material slot " : "material slots ") + string.Join(", ", empty) + " of " + materials.Length
                        + (empty.Count == 1 ? " is" : " are") + " empty or missing");
                }
                Mesh mesh = MeshOf(renderer);
                if (mesh != null && materials.Length < mesh.subMeshCount)
                {
                    parts.Add("mesh " + mesh.name + " has " + mesh.subMeshCount + " submeshes and the renderer " + materials.Length
                        + " material slot" + (materials.Length == 1 ? "" : "s") + ", the rest are not drawn");
                }
                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }

            static string Levels(LODGroup group)
            {
                LOD[] lods = group.GetLODs();
                var parts = new List<string>();
                for (int i = 0; i < lods.Length; i++)
                {
                    Renderer[] renderers = lods[i].renderers ?? new Renderer[0];
                    int gone = renderers.Count(r => r == null);
                    if (gone == renderers.Length)
                    {
                        parts.Add("LOD " + i + " has no renderer");
                    }
                    else if (gone > 0)
                    {
                        parts.Add("LOD " + i + " lists " + gone + " renderer" + (gone == 1 ? "" : "s") + " that " + (gone == 1 ? "is" : "are") + " gone");
                    }
                }
                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }

            string Large(Renderer renderer)
            {
                Mesh mesh = MeshOf(renderer);
                if (mesh == null)
                {
                    return null;
                }
                long count = ImporterExportCommand.Triangles(mesh);
                if (count <= triangles)
                {
                    return null;
                }
                foreach (LODGroup group in renderer.GetComponentsInParent<LODGroup>(true))
                {
                    if (group.GetLODs().Any(lod => lod.renderers != null && Array.IndexOf(lod.renderers, renderer) >= 0))
                    {
                        return null;
                    }
                }
                return "mesh " + mesh.name + " has " + count + " triangles and no LODGroup lists this renderer";
            }

            // The collider belongs to the nearest Rigidbody on its own GameObject or above it.
            static string NonConvex(MeshCollider collider)
            {
                if (collider.convex)
                {
                    return null;
                }
                Rigidbody[] bodies = collider.GetComponentsInParent<Rigidbody>(true);
                if (bodies.Length == 0 || bodies[0].isKinematic)
                {
                    return null;
                }
                string owner = bodies[0].gameObject == collider.gameObject ? "its Rigidbody" : "the Rigidbody on " + HierarchyPath.Of(bodies[0].gameObject);
                return "non-convex MeshCollider and " + owner + " is not kinematic. Unity does not support it and logs an error in play mode, "
                    + "make the collider convex or the Rigidbody kinematic";
            }

            static Mesh MeshOf(Renderer renderer)
            {
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    return skinned.sharedMesh;
                }
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
        }
    }
}
