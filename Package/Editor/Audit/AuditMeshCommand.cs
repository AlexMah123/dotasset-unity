using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DotAsset
{
    // Checks how meshes are used in prefabs and scenes.
    //   G1  a mesh renderer with an empty material slot, or a submesh with no slot
    //   G2  an LODGroup level with no renderer, or one listing a renderer that is gone
    //   G3  a mesh over -triangles (default 10000) in a renderer that no LODGroup lists
    //   G4  a non-convex MeshCollider under a non-kinematic Rigidbody, which Unity does not support
    // The scan and the prefab instance rule are ComponentAudit's. No fix spec: every fix is a choice (a material, an
    // LOD setup, convex or kinematic), made with EditPrefab or EditScene on the Object the issue names.
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

            var audit = new ComponentAudit(ctx, report, on);
            audit.Run(root =>
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    {
                        continue;
                    }
                    audit.Add("G1", "Warning", renderer, Slots);
                    JObject large = audit.Add("G3", "Info", renderer, r => Large(r, triangles));
                    if (large != null)
                    {
                        Mesh mesh = MeshOf(renderer);
                        large["Mesh"] = mesh.name;
                        large["Triangles"] = ImporterExportCommand.Triangles(mesh);
                    }
                }
                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    audit.Add("G2", "Warning", group, Levels);
                }
                foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    JObject moving = audit.Add("G4", "Error", collider, NonConvex);
                    if (moving != null)
                    {
                        moving["Rigidbody"] = HierarchyPath.Of(BodyOf(collider).gameObject);
                    }
                }
            });
            return report.Finish();
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

        static string Large(Renderer renderer, long limit)
        {
            Mesh mesh = MeshOf(renderer);
            if (mesh == null)
            {
                return null;
            }
            long count = ImporterExportCommand.Triangles(mesh);
            if (count <= limit)
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

        static string NonConvex(MeshCollider collider)
        {
            if (collider.convex)
            {
                return null;
            }
            Rigidbody body = BodyOf(collider);
            if (body == null || body.isKinematic)
            {
                return null;
            }
            string owner = body.gameObject == collider.gameObject ? "its Rigidbody" : "the Rigidbody on " + HierarchyPath.Of(body.gameObject);
            return "non-convex MeshCollider and " + owner + " is not kinematic. Unity does not support it and logs an error in play mode, "
                + "make the collider convex or the Rigidbody kinematic";
        }

        // The collider belongs to the nearest Rigidbody on its own GameObject or above it.
        static Rigidbody BodyOf(MeshCollider collider)
        {
            Rigidbody[] bodies = collider.GetComponentsInParent<Rigidbody>(true);
            return bodies.Length > 0 ? bodies[0] : null;
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
