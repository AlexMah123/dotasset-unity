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
        public void AnimationClipExportListsCurvesEventsAndSettings()
        {
            string wave = NewClip();
            JToken clip = Export("AnimationClipExport", wave)["Clips"].Single();
            Assert.AreEqual("Wave", (string)clip["Name"]);
            Assert.AreEqual(30, (double)clip["FrameRate"]);
            Assert.AreEqual(1, (double)clip["Length"]);
            Assert.IsTrue((bool)clip["Settings"]["LoopTime"], clip.ToString());
            JToken x = clip["Curves"].Single();
            Assert.AreEqual(new[] { "Body/Arm", "Transform", "m_LocalPosition.x" }, new[] { (string)x["Path"], (string)x["Type"], (string)x["Property"] });
            Assert.AreEqual(new[] { 0.0, 1.0 }, x["Keys"].Select(k => (double)k["Time"]).ToArray());
            Assert.AreEqual(new[] { 0.0, 2.0 }, x["Keys"].Select(k => (double)k["Value"]).ToArray());
            JToken swap = clip["ObjectCurves"].Single();
            Assert.AreEqual(new[] { "Body", "MeshRenderer", "m_Materials.Array.data[0]" }, new[] { (string)swap["Path"], (string)swap["Type"], (string)swap["Property"] });
            Assert.AreEqual(0.5, (double)swap["Keys"][0]["Time"]);
            Assert.AreEqual(Red, (string)swap["Keys"][0]["Value"]);
            JToken step = clip["Events"].Single();
            Assert.AreEqual(new object[] { 0.25, "Step", 3 }, new object[] { (double)step["Time"], (string)step["Function"], (int)step["Int"] });
            // -nokeys keeps every curve and counts its keys, for a long clip.
            JToken bare = Export("AnimationClipExport", wave, "-nokeys")["Clips"][0]["Curves"][0];
            Assert.IsNull(bare["Keys"], bare.ToString());
            Assert.AreEqual(2, (int)bare["KeyCount"]);
        }

        [Test]
        public void AnimationClipExportReadsTheClipsInsideAController()
        {
            AssetDatabase.AddObjectToAsset(new AnimationClip { name = "Inner" }, Door);
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(Door));
            Assert.AreEqual(new[] { "Inner" }, Export("AnimationClipExport", Door)["Clips"].Select(c => (string)c["Name"]).ToArray());
        }

        static Type TimelineType(string name)
        {
            Type t = Type.GetType("UnityEngine.Timeline." + name + ", Unity.Timeline");
            Assert.IsNotNull(t, "the test project has no Timeline package, add com.unity.timeline to its manifest");
            return t;
        }

        // The test assembly does not reference Timeline, so the timeline is built through its public API by reflection.
        [Test]
        public void TimelineExportListsTracksClipsAndMarkers()
        {
            Type timelineType = TimelineType("TimelineAsset");
            Type trackType = TimelineType("TrackAsset");
            Type animationTrack = TimelineType("AnimationTrack");
            Type emitter = TimelineType("SignalEmitter");
            string path = Folder + "/Intro.playable";
            string hit = Folder + "/Hit.signal";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance(TimelineType("SignalAsset")), hit);
            UnityEngine.Object signal = AssetDatabase.LoadMainAssetAtPath(hit);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance(timelineType), path);
            UnityEngine.Object timeline = AssetDatabase.LoadMainAssetAtPath(path);
            var wave = AssetDatabase.LoadAssetAtPath<AnimationClip>(NewClip());

            var createTrack = timelineType.GetMethod("CreateTrack", new[] { typeof(Type), trackType, typeof(string) });
            object group = createTrack.Invoke(timeline, new object[] { TimelineType("GroupTrack"), null, "Group" });
            object body = createTrack.Invoke(timeline, new object[] { animationTrack, group, "Body" });
            object walk = animationTrack.GetMethod("CreateClip", new[] { typeof(AnimationClip) }).Invoke(body, new object[] { wave });
            TimelineType("TimelineClip").GetProperty("start").SetValue(walk, 1.0);
            object step = trackType.GetMethod("CreateMarker", new[] { typeof(Type), typeof(double) }).Invoke(body, new object[] { emitter, 0.5 });
            emitter.GetProperty("asset").SetValue(step, signal);
            object lights = createTrack.Invoke(timeline, new object[] { TimelineType("ActivationTrack"), null, "Lights" });
            trackType.GetMethod("CreateDefaultClip", Type.EmptyTypes).Invoke(lights, null);
            trackType.GetProperty("muted").SetValue(lights, true);
            timelineType.GetMethod("CreateMarkerTrack", Type.EmptyTypes).Invoke(timeline, null);
            object markers = timelineType.GetProperty("markerTrack").GetValue(timeline);
            object end = trackType.GetMethod("CreateMarker", new[] { typeof(Type), typeof(double) }).Invoke(markers, new object[] { emitter, 2.0 });
            emitter.GetProperty("asset").SetValue(end, signal);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssetIfDirty(timeline);
            Dictionary<string, string> before = Fingerprint();

            // The folder also holds Hit.signal and Wave.anim, only the timeline is exported.
            JObject intro = Export("TimelineExport", Folder);
            Assert.AreEqual(path, (string)intro["AssetPath"]);
            Assert.AreEqual(60, (double)intro["FrameRate"], intro.ToString());
            Assert.AreEqual(new[] { "Group", "Group/Body", "Lights" }, intro["Tracks"].Select(t => (string)t["Path"]).ToArray());
            JToken[] tracks = intro["Tracks"].ToArray();
            Assert.AreEqual("GroupTrack", (string)tracks[0]["Type"]);
            Assert.IsNull(tracks[0]["BindingType"], tracks[0].ToString());
            Assert.AreEqual(new[] { "AnimationTrack", "Animator" }, new[] { (string)tracks[1]["Type"], (string)tracks[1]["BindingType"] });
            JToken clip = tracks[1]["Clips"].Single();
            Assert.AreEqual(new object[] { "Wave", 1.0, 1.0, 2.0, "AnimationPlayableAsset", Folder + "/Wave.anim" },
                new object[] { (string)clip["Name"], (double)clip["Start"], (double)clip["Duration"], (double)clip["End"], (string)clip["AssetType"], (string)clip["Clip"] });
            JToken marker = tracks[1]["Markers"].Single();
            Assert.AreEqual(new object[] { 0.5, "SignalEmitter", hit }, new object[] { (double)marker["Time"], (string)marker["Type"], (string)marker["Signal"] });
            Assert.IsTrue((bool)tracks[2]["Muted"], tracks[2].ToString());
            Assert.AreEqual("GameObject", (string)tracks[2]["BindingType"]);
            Assert.AreEqual("ActivationPlayableAsset", (string)tracks[2]["Clips"].Single()["AssetType"]);
            JToken last = intro["Markers"].Single();
            Assert.AreEqual(new object[] { 2.0, hit }, new object[] { (double)last["Time"], (string)last["Signal"] });
            CollectionAssert.AreEquivalent(before, Fingerprint());
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
            NewClip();
            Dictionary<string, string> before = Fingerprint();
            Export("PrefabExport", Folder);
            Export("SceneExport", Folder);
            Export("MaterialExport", Folder);
            Export("AnimatorControllerExport", Folder);
            // The folder holds Wave.anim and Door.controller without clips: only Wave.anim is exported.
            Export("AnimationClipExport", Folder);
            Export("AssetExport", Red);
            Export("AssetReferenceExport", Red, "-scandir=" + Folder);
            CollectionAssert.AreEquivalent(before, Fingerprint());
        }
    }
}
