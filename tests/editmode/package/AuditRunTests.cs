using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Every Audit run on a seeded issue: exit 3 with the issue, the fix spec its fix run takes, exit 0 after.
    // R2 and R5 of AuditMissingReference are in MigrateRunTests, next to the runs that fix them.
    public sealed class AuditRunTests : RunFixture
    {
        [Test]
        public void AuditMaterialFindsAStrayKeywordAndItsFixRemovesIt()
        {
            const string keyword = "_DOTASSET_STRAY";
            Ok("EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'EnableKeywords': ['" + keyword + @"']}]}") + " -apply");
            JObject report = Report("AuditMaterial", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "M5", Red), report.ToString());
            Assert.AreEqual("EditMaterial", (string)report["FixRun"]);
            Ok("EditMaterial", FixSpec(report) + " -apply");
            CollectionAssert.DoesNotContain(Export("MaterialExport", Red)["Keywords"].Select(k => (string)k).ToArray(), keyword);
            Assert.IsFalse(HasIssue(Report("AuditMaterial", AnyIssues, "", Folder), "M5", Red));
        }

        [Test]
        public void AuditMissingReferenceFindsADeletedMaterial()
        {
            Report("AuditMissingReference", 0, "", Folder);
            string green = Folder + "/Green.mat";
            string broken = Folder + "/Broken.prefab";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + green + @"', 'Properties': {}},
                {'Kind': 'Prefab', 'Path': '" + broken + @"', 'Root': 'Broken',
                 'Components': ['MeshFilter', {'Type': 'MeshRenderer', 'Properties': {'m_Materials': ['" + green + @"']}}]}]}"));
            AssetDatabase.DeleteAsset(green);
            JObject report = Report("AuditMissingReference", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "R1", broken), report.ToString());
        }

        [Test]
        public void AuditTextureFindsANormalMapImportedAsDefaultAndItsFixImportsIt()
        {
            string rock = Folder + "/Rock_n.png";
            WritePng(rock);
            JObject report = Report("AuditTexture", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "T1", rock), report.ToString());
            Assert.AreEqual("EditImporter", (string)report["FixRun"]);
            Ok("EditImporter", FixSpec(report) + " -apply");
            Assert.AreEqual("NormalMap", (string)Export("ImporterExport", rock)["Summary"]["TextureType"]);
            Assert.IsFalse(HasIssue(Report("AuditTexture", AnyIssues, "", Folder), "T1", rock));
        }

        [Test]
        public void AuditMeshFindsEmptySlotsEmptyLodsLargeMeshesAndNonConvexMeshColliders()
        {
            // PhysX may log the unsupported collider on the scene object, before any run sets this.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            string rig = Folder + "/Rig.prefab";
            string placed = Folder + "/Placed.unity";
            var quad = new Mesh { name = "Quad", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) }, subMeshCount = 2 };
            quad.SetTriangles(new[] { 0, 2, 1 }, 0);
            quad.SetTriangles(new[] { 1, 2, 3 }, 1);
            AssetDatabase.CreateAsset(quad, Folder + "/Quad.asset");
            var red = AssetDatabase.LoadAssetAtPath<Material>(Red);
            var root = new GameObject("Rig");
            try
            {
                root.AddComponent<Rigidbody>();
                MeshRenderer body = Child(root, "Body", quad, red, null);
                Child(root, "Big", quad, red, red);
                var hull = new GameObject("Hull");
                hull.transform.SetParent(root.transform);
                hull.AddComponent<MeshCollider>().sharedMesh = quad;
                root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(0.5f, new Renderer[] { body }), new LOD(0.1f, new Renderer[0]) });
                PrefabUtility.SaveAsPrefabAsset(root, rig);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Scene', 'Path': '" + placed + @"',
                'Ops': [{'Op': 'AddPrefabInstance', 'Prefab': '" + rig + @"', 'Name': 'Rig'}]}]}"));

            JObject report = Report("AuditMesh", 3, "-triangles=1", Folder);
            Assert.AreEqual(new[] { "Rig/Body" }, Objects(report, "G1", rig), report.ToString());
            Assert.AreEqual(new[] { "Rig" }, Objects(report, "G2", rig), report.ToString());
            Assert.AreEqual(new[] { "Rig/Big" }, Objects(report, "G3", rig), "Body is in the LODGroup\n" + report);
            Assert.AreEqual(new[] { "Rig/Hull" }, Objects(report, "G4", rig), report.ToString());
            Assert.IsFalse(report["Issues"].Any(i => (string)i["Asset"] != rig), "the scene's instance repeats the prefab's issues, or the fixture has one\n" + report);
            JToken large = report["Issues"].First(i => (string)i["Rule"] == "G3");
            Assert.AreEqual("Quad", (string)large["Mesh"], report.ToString());
            Assert.AreEqual(2, (int?)large["Triangles"], report.ToString());
            Assert.AreEqual("Rig", (string)report["Issues"].First(i => (string)i["Rule"] == "G4")["Rigidbody"], report.ToString());

            Ok("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + rig + @"', 'Ops': [{'Op': 'Set', 'Path': 'Rig', 'Component': 'Rigidbody', 'Properties': {'m_IsKinematic': true}}]}]}") + " -apply");
            Assert.IsEmpty(Objects(Report("AuditMesh", 3, "-triangles=1", Folder), "G4", rig), "a kinematic Rigidbody may carry a non-convex MeshCollider");
            Assert.IsEmpty(Objects(Report("AuditMesh", 3, "", Folder), "G3", rig), "two triangles are under the default -triangles");
        }

        [Test]
        public void AuditAudioFindsAnAudioSourceWithNoMixerGroupWhileTheProjectHasAMixer()
        {
            string mixer = Folder + "/Main.mixer";
            string speaker = Folder + "/Speaker.prefab";
            string hall = Folder + "/Hall.unity";
            // Unity has no public API that creates a mixer, the editor's own menu item calls this.
            typeof(EditorWindow).Assembly.GetType("UnityEditor.Audio.AudioMixerController")
                .GetMethod("CreateMixerControllerAtPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { mixer });
            UnityEngine.Audio.AudioMixerGroup master = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>(mixer).FindMatchingGroups("Master")[0];
            var root = new GameObject("Speaker");
            try
            {
                root.AddComponent<AudioSource>();
                var routed = new GameObject("Routed");
                routed.transform.SetParent(root.transform);
                routed.AddComponent<AudioSource>().outputAudioMixerGroup = master;
                PrefabUtility.SaveAsPrefabAsset(root, speaker);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Scene', 'Path': '" + hall + @"',
                'Ops': [{'Op': 'AddPrefabInstance', 'Prefab': '" + speaker + @"', 'Name': 'Speaker'}]}]}"));

            JObject report = Report("AuditAudio", 3, "", Folder);
            Assert.AreEqual(new[] { "Speaker" }, Objects(report, "A1", speaker), report.ToString());
            Assert.IsFalse(report["Issues"].Any(i => (string)i["Asset"] != speaker), "the scene's instance repeats the prefab's issue\n" + report);

            AssetDatabase.DeleteAsset(mixer);
            Report("AuditAudio", 0, "", Folder);
        }

        static MeshRenderer Child(GameObject parent, string name, Mesh mesh, params Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            return renderer;
        }

        static string[] Objects(JObject report, string rule, string asset)
        {
            return (report["Issues"] ?? new JArray()).Where(i => (string)i["Rule"] == rule && (string)i["Asset"] == asset).Select(i => (string)i["Object"]).ToArray();
        }

        [Test]
        public void AuditUnusedAssetListsWhatNothingUses()
        {
            string unused = Folder + "/Unused.mat";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + unused + @"', 'Properties': {}}]}"));
            JObject report = Report("AuditUnusedAsset", 3, "-scandir=" + Folder);
            Assert.IsTrue(HasIssue(report, "U1", unused), report.ToString());
            Assert.IsFalse(HasIssue(report, "U1", Red), "Crate uses Red.mat");
            Assert.AreEqual("DeleteAsset", (string)report["FixRun"]);
        }
    }
}
