using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Every Edit run: its dry run saves nothing, -apply saves the spec, a failed op saves nothing of its target.
    public sealed class EditRunTests : RunFixture
    {
        [Test]
        public void EditPrefabAddsSetsAndRemoves()
        {
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [
                {'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid'},
                {'Op': 'Set', 'Path': 'Crate/Lid', 'Component': 'Transform', 'Properties': {'m_LocalPosition': [0, 1, 0]}},
                {'Op': 'RemoveComponent', 'Path': 'Crate', 'Type': 'BoxCollider'}]}]}");
            Ok("EditPrefab", spec);
            JObject crate = Export("PrefabExport", Crate);
            Assert.IsNull(Node(crate, "Crate/Lid"), "the dry run saved Crate/Lid");
            Assert.IsNotNull(Component(crate, "Crate", "BoxCollider"), "the dry run removed the BoxCollider");

            Ok("EditPrefab", spec + " -apply");
            crate = Export("PrefabExport", Crate);
            JToken lid = Component(crate, "Crate/Lid", "Transform");
            Assert.IsNotNull(lid, "no Crate/Lid");
            Assert.AreEqual(new[] { 0.0, 1.0, 0.0 }, lid["Properties"]["m_LocalPosition"].Select(v => (double)v).ToArray());
            Assert.IsNull(Component(crate, "Crate", "BoxCollider"));
        }

        [Test]
        public void EditPrefabWithABadOpSavesNothing()
        {
            string before = Text(Crate);
            RunResult r = Run("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [
                {'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid'},
                {'Op': 'Rename', 'Path': 'Crate/DoesNotExist', 'NewName': 'X'}]}]}") + " -apply");
            Assert.AreEqual(1, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            CollectionAssert.Contains(r.FailedTargets, Crate);
            Assert.AreEqual(before, Text(Crate));
        }

        [Test]
        public void EditSceneAddsAnObjectAndOverridesTheInstance()
        {
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Scene + @"', 'Ops': [
                {'Op': 'AddGameObject', 'Name': 'Light', 'Components': [{'Type': 'Light', 'Properties': {'m_Intensity': 2}}]},
                {'Op': 'Set', 'Path': 'Crate', 'Component': 'Rigidbody', 'Properties': {'m_Mass': 9}}]}]}");
            Ok("EditScene", spec);
            Assert.IsNull(Node(Export("SceneExport", Scene), "Light"), "the dry run saved the Light");

            Ok("EditScene", spec + " -apply");
            JObject scene = Export("SceneExport", Scene);
            JToken light = Component(scene, "Light", "Light");
            Assert.IsNotNull(light, "no Light");
            Assert.AreEqual(2, (double)light["Properties"]["m_Intensity"]);
            JToken mass = Node(scene, "Crate")["PrefabInstance"]["Overrides"].FirstOrDefault(o => (string)o["Property"] == "m_Mass");
            Assert.AreEqual("9", (string)mass["Value"]);
        }

        [Test]
        public void EditAssetSetsAProperty()
        {
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'Properties': {'m_CustomRenderQueue': 2460}}]}");
            int before = AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue;
            Ok("EditAsset", spec);
            Assert.AreEqual(before, AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, "the dry run kept the queue");
            Ok("EditAsset", spec + " -apply");
            StringAssert.Contains("m_CustomRenderQueue: 2460", Text(Red));
        }

        [Test]
        public void EditMaterialSetsQueueAndKeywords()
        {
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'RenderQueue': 2450, 'EnableInstancing': true,
                'EnableKeywords': ['_DOTASSET_TEST']}]}");
            Ok("EditMaterial", spec);
            Assert.IsFalse(AssetDatabase.LoadAssetAtPath<Material>(Red).enableInstancing, "the dry run kept instancing");
            Ok("EditMaterial", spec + " -apply");
            JObject red = Export("MaterialExport", Red);
            Assert.AreEqual(2450, (int)red["RenderQueue"]);
            CollectionAssert.Contains(red["Keywords"].Select(k => (string)k).ToArray(), "_DOTASSET_TEST");
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<Material>(Red).enableInstancing);
        }

        [Test]
        public void EditImporterChangesTheTextureType()
        {
            string png = Folder + "/Rock.png";
            WritePng(png);
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + png + @"', 'Texture': {'TextureType': 'NormalMap'}}]}");
            RunResult dry = Ok("EditImporter", spec);
            Assert.IsNotEmpty(dry.Plan, "the dry run planned nothing");
            Assert.AreEqual(TextureImporterType.Default, ((TextureImporter)AssetImporter.GetAtPath(png)).textureType);
            Ok("EditImporter", spec + " -apply");
            Assert.AreEqual("NormalMap", (string)Export("ImporterExport", png)["Summary"]["TextureType"]);
        }

        [Test]
        public void EditImporterRefusesAnEnumGivenAsANumberOrAList()
        {
            // N8: Enum.TryParse takes "7" and "Compressed, CompressedHQ", neither names a TextureImporterCompression
            string png = Folder + "/Rock.png";
            WritePng(png);
            foreach (string value in new[] { "7", "Compressed, CompressedHQ" })
            {
                string spec = Spec(@"{'Targets': [{'AssetPath': '" + png + @"', 'Texture': {'Platforms': {'Standalone': {'Compression': '" + value + @"'}}}}]}");
                RunResult r = Run("EditImporter", spec);
                Assert.AreNotEqual(0, r.ExitCode, value + " was taken: " + string.Join("\n", r.Lines.ToArray()));
                StringAssert.Contains("Compression", string.Join("\n", r.Lines.ToArray()), value);
            }
            Ok("EditImporter", Spec(@"{'Targets': [{'AssetPath': '" + png + @"', 'Texture': {'Platforms': {'Standalone': {'Compression': 'compressedhq'}}}}]}"));
        }

        static string[] Parameters()
        {
            return Export("AnimatorControllerExport", Door)["Parameters"].Select(p => (string)p["Name"]).ToArray();
        }

        [Test]
        public void EditAnimatorControllerAddsATransitionAndRenamesAParameter()
        {
            string ops = @"{'Op': 'AddTransition', 'From': 'Opened', 'To': 'Closed', 'Conditions': [{'Parameter': 'Open', 'Mode': 'IfNot'}]},
                {'Op': 'RenameParameter', 'Name': 'Open', 'NewName': 'IsOpen'}";
            Ok("EditAnimatorController", Spec(@"{'Targets': [{'AssetPath': '" + Door + @"', 'Ops': [" + ops + @",
                {'Op': 'AddState', 'Path': 'Ghost'}]}]}"));
            Assert.AreEqual(new[] { "Open" }, Parameters(), "the dry run renamed the parameter");

            Ok("EditAnimatorController", Spec(@"{'Targets': [{'AssetPath': '" + Door + @"', 'Ops': [" + ops + "]}]}") + " -apply");
            Assert.AreEqual(new[] { "IsOpen" }, Parameters());
            JToken[] states = Export("AnimatorControllerExport", Door)["Layers"][0]["StateMachine"]["States"].ToArray();
            Assert.IsFalse(states.Any(s => (string)s["Path"] == "Ghost"), "the dry run's state leaked into the -apply");
            JToken back = states.First(s => (string)s["Path"] == "Opened")["Transitions"].Single();
            Assert.AreEqual("Closed", (string)back["To"]);
            Assert.AreEqual("IsOpen", (string)back["Conditions"][0]["Parameter"]);
            Assert.AreEqual("IfNot", (string)back["Conditions"][0]["Mode"]);
            string forward = (string)states.First(s => (string)s["Path"] == "Closed")["Transitions"][0]["Conditions"][0]["Parameter"];
            Assert.AreEqual("IsOpen", forward, "the rename left a condition on the old name");
        }
    }
}
