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
    }
}
