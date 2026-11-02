using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Every Export run on the fixture's assets: what it reports, and that none of them writes a byte.
    public sealed class ExportRunTests : RunFixture
    {
        [Test]
        public void PrefabExportListsTheHierarchyAndItsValues()
        {
            JObject crate = Export("PrefabExport", Crate);
            Assert.IsNotNull(Component(crate, "Crate", "BoxCollider"));
            Assert.AreEqual(5, (double)Component(crate, "Crate", "Rigidbody")["Properties"]["m_Mass"]);
            JToken materials = Component(crate, "Crate/Mesh", "MeshRenderer")["Properties"]["m_Materials"];
            Assert.AreEqual(new[] { Red }, materials.Select(m => (string)m).ToArray());
        }

        [Test]
        public void SceneExportListsThePrefabInstance()
        {
            JObject scene = Export("SceneExport", Scene);
            JToken crate = Node(scene, "Crate");
            Assert.IsNotNull(crate, scene.ToString());
            Assert.IsNotNull(crate["PrefabInstance"], "the Crate in the scene is not exported as a prefab instance");
        }

        [Test]
        public void MaterialExportReadsQueueAndFullProperties()
        {
            JObject red = Export("MaterialExport", Red);
            // A new material keeps the shader's queue: -1 stored, the resolved queue beside it.
            Assert.AreEqual(-1, (int)red["RenderQueue"]);
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, (int)red["ResolvedRenderQueue"]);
            // Every property of a new material is at the shader default: the short export leaves them out, -full lists them.
            Assert.AreEqual(0, ((JObject)red["Properties"]).Count, red.ToString());
            Assert.IsNotNull(red["PropertiesOmitted"]);
            Assert.Greater(((JObject)Export("MaterialExport", Red, "-full")["Properties"]).Count, 0, "-full lists no properties");
        }

        [Test]
        public void AnimatorControllerExportListsParametersStatesAndTransitions()
        {
            JObject door = Export("AnimatorControllerExport", Door);
            Assert.AreEqual(new[] { "Open" }, door["Parameters"].Select(p => (string)p["Name"]).ToArray());
            JToken[] states = door["Layers"][0]["StateMachine"]["States"].ToArray();
            CollectionAssert.AreEquivalent(new[] { "Closed", "Opened" }, states.Select(s => (string)s["Path"]).ToArray());
            JToken transition = states.First(s => (string)s["Path"] == "Closed")["Transitions"].Single();
            Assert.AreEqual("Opened", (string)transition["To"]);
            Assert.AreEqual("If", (string)transition["Conditions"][0]["Mode"]);
        }

        [Test]
        public void AssetExportListsTheMainObject()
        {
            JObject red = Export("AssetExport", Red);
            JToken main = red["Objects"].FirstOrDefault(o => (bool?)o["Main"] == true);
            Assert.IsNotNull(main, red.ToString());
            Assert.IsNotNull(main["Properties"], "the material's properties are missing");
        }

        [Test]
        public void AssetReferenceExportFindsTheUsers()
        {
            JObject users = Export("AssetReferenceExport", Red, "-scandir=" + Folder);
            StringAssert.Contains(Crate, users.ToString());
            StringAssert.DoesNotContain(Door, users.ToString());
        }

        [Test]
        public void ImporterExportSummarizesATexture()
        {
            string png = Folder + "/Rock.png";
            WritePng(png);
            JObject rock = Export("ImporterExport", png);
            Assert.AreEqual("Default", (string)rock["Summary"]["TextureType"]);
            Assert.IsNotNull(rock["Properties"], "the importer's properties are missing");
        }

        [Test]
        public void TypeCatalogExportListsDefaults()
        {
            JObject types = Export("TypeCatalogExport", null, "-filter=Rigidbody");
            JToken rigidbody = types["Types"].FirstOrDefault(t => (string)t["FullName"] == "UnityEngine.Rigidbody");
            Assert.IsNotNull(rigidbody, "Rigidbody is not listed");
            JToken mass = rigidbody["Properties"].FirstOrDefault(p => (string)p["Path"] == "m_Mass");
            Assert.AreEqual(1, (double)mass["Default"]);
            Assert.IsFalse(types["Types"].Any(t => (string)t["FullName"] == "UnityEngine.BoxCollider"), "the filter let BoxCollider through");
        }

        [Test]
        public void CommandCatalogExportListsEveryRunInItsGroup()
        {
            JObject catalog = Export("CommandCatalogExport", null);
            var listed = catalog["Commands"].ToDictionary(c => (string)c["RunName"], c => (string)c["Group"]);
            Type[] commands = TypeCache.GetTypesDerivedFrom<DotAssetCommand>()
                .Where(t => t.Assembly == typeof(DotAssetCommand).Assembly && !t.IsAbstract).ToArray();
            Assert.Greater(commands.Length, 30);
            foreach (Type t in commands)
            {
                string run = t.Name.Substring(0, t.Name.Length - "Command".Length);
                Assert.IsTrue(listed.ContainsKey(run), run + " is not listed");
                Assert.AreEqual(Groups.Resolve(run).ToString(), listed[run], run);
            }
        }

        static Dictionary<string, string> Fingerprint()
        {
            using (var md5 = MD5.Create())
            {
                return Directory.GetFiles(DotAssetPaths.Absolute(Folder), "*", SearchOption.AllDirectories)
                    .ToDictionary(f => f, f => Convert.ToBase64String(md5.ComputeHash(File.ReadAllBytes(f))));
            }
        }

        [Test]
        public void ExportsWriteNothing()
        {
            // A custom queue Unity would reset when it validates the material, so an export that let it would save it.
            var red = AssetDatabase.LoadAssetAtPath<Material>(Red);
            red.renderQueue = 2460;
            AssetDatabase.SaveAssetIfDirty(red);
            Dictionary<string, string> before = Fingerprint();
            Export("PrefabExport", Folder);
            Export("SceneExport", Folder);
            Export("MaterialExport", Folder);
            Export("AnimatorControllerExport", Folder);
            Export("AssetExport", Red);
            Export("AssetReferenceExport", Red, "-scandir=" + Folder);
            CollectionAssert.AreEquivalent(before, Fingerprint());
        }
    }
}
