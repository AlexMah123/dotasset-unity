using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // CreateAsset, which RunFixture runs before every test: each kind of asset is read back, and a second run with
    // the same paths skips what exists.
    public sealed class ImportRunTests : RunFixture
    {
        [Test]
        public void CreateAssetMadeEveryKind()
        {
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Red));
            var crate = AssetDatabase.LoadAssetAtPath<GameObject>(Crate);
            Assert.AreEqual(5, crate.GetComponent<Rigidbody>().mass);
            Assert.IsNotNull(crate.GetComponent<BoxCollider>());
            Assert.AreEqual(Red, MaterialOf(Crate));

            JObject heavy = Export("PrefabExport", Heavy);
            Assert.AreEqual(Crate, (string)heavy["VariantOf"]);
            Assert.AreEqual(50, AssetDatabase.LoadAssetAtPath<GameObject>(Heavy).GetComponent<Rigidbody>().mass);

            JToken machine = Export("AnimatorControllerExport", Door)["Layers"][0]["StateMachine"];
            Assert.AreEqual("Closed", (string)machine["DefaultState"]);
            Assert.AreEqual(new[] { "Closed", "Opened" }, machine["States"].Select(s => (string)s["Path"]).ToArray());

            JToken instance = Node(Export("SceneExport", Scene), "Crate")["PrefabInstance"];
            Assert.AreEqual(Crate, (string)instance["Source"]);
        }

        [Test]
        public void CreateAssetSkipsWhatExists()
        {
            string before = Text(Crate);
            Ok("CreateAsset", Spec(@"{'Assets': [
                {'Kind': 'Prefab', 'Path': '" + Crate + @"', 'Root': 'Crate', 'Components': ['SphereCollider']},
                {'Kind': 'Material', 'Path': '" + Folder + @"/Blue.mat', 'Properties': {}}]}"));
            Assert.AreEqual(before, Text(Crate), "the second CreateAsset rebuilt Crate");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Blue.mat"), "the new asset in the same spec was not made");
        }

        [Test]
        public void CreateAssetMakesAnimationClips()
        {
            string walk = Folder + "/Walk.anim";
            string old = Folder + "/Old.anim";
            Ok("CreateAsset", Spec(@"{'Assets': [
                {'Kind': 'AnimationClip', 'Path': '" + walk + @"', 'FrameRate': 24, 'Settings': {'LoopTime': true},
                 'Ops': [{'Op': 'SetCurve', 'Path': 'Body', 'Type': 'Transform', 'Property': 'm_LocalPosition.y',
                          'Keys': [{'Time': 0, 'Value': 0}, {'Time': 1, 'Value': 1}]},
                         {'Op': 'SetEvent', 'Time': 0.5, 'Function': 'Step'}]},
                {'Kind': 'AnimationClip', 'Path': '" + old + @"', 'Legacy': true}]}"));
            JToken clip = Export("AnimationClipExport", walk)["Clips"].Single();
            Assert.AreEqual(new object[] { "Walk", 24.0, true }, new object[] { (string)clip["Name"], (double)clip["FrameRate"], (bool)clip["Settings"]["LoopTime"] });
            Assert.AreEqual(new[] { 0.0, 1.0 }, clip["Curves"].Single()["Keys"].Select(k => (double)k["Value"]).ToArray());
            Assert.AreEqual("Step", (string)clip["Events"].Single()["Function"]);
            Assert.IsTrue((bool)Export("AnimationClipExport", old)["Clips"][0]["Legacy"]);
            // A mistyped property fails before the clip exists on disk, and a clip path ends in .anim.
            string bad = Folder + "/Bad.anim";
            RunResult r = Run("CreateAsset", Spec(@"{'Assets': [
                {'Kind': 'AnimationClip', 'Path': '" + bad + @"', 'Ops': [{'Op': 'SetCurve', 'Type': 'Transform', 'Property': 'm_LocalPositon.y', 'Keys': [{'Time': 0, 'Value': 1}]}]},
                {'Kind': 'AnimationClip', 'Path': '" + Folder + @"/Bad.clip'}]}"));
            Assert.AreEqual(1, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            Assert.AreEqual(2, r.FailedTargets.Count, string.Join("\n", r.Lines.ToArray()));
            Assert.IsFalse(System.IO.File.Exists(Absolute(bad)), "the failed clip was written");
        }
    }
}
