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

        static double[] Column(JToken curve, string key)
        {
            return curve["Keys"].Select(k => (double)k[key]).ToArray();
        }

        [Test]
        public void EditAnimationClipSetsKeysCurvesEventsAndSettings()
        {
            string wave = NewClip();
            string keys = @"{'Op': 'SetKeys', 'Path': 'Body/Arm', 'Type': 'Transform', 'Property': 'm_LocalPosition.x',
                    'Keys': [{'Time': 0.5, 'Value': 4}, {'Time': 1, 'Value': 3}]},
                {'Op': 'SetCurve', 'Path': 'Body', 'Type': 'Transform', 'Property': 'm_LocalScale.y',
                    'Keys': [{'Time': 0, 'Value': 1}, {'Time': 2, 'Value': 1.5}]}";
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + wave + @"', 'Settings': {'LoopTime': false, 'CycleOffset': 0.5}, 'Ops': [" + keys + @",
                {'Op': 'SetObjectCurve', 'Path': 'Body', 'Type': 'MeshRenderer', 'Property': 'm_Materials.Array.data[0]', 'Keys': [{'Time': 0, 'Value': '" + Red + @"'}]},
                {'Op': 'SetObjectCurve', 'Path': 'Icon', 'Type': 'SpriteRenderer', 'Property': 'm_Sprite', 'Keys': [{'Time': 0, 'Value': null}]},
                {'Op': 'SetCurve', 'Path': 'Body/Trail', 'Type': 'GameObject', 'Property': 'm_IsActive',
                    'Keys': [{'Time': 0, 'Value': true}, {'Time': 1, 'Value': false, 'InMode': 'Constant', 'OutMode': 'Constant'}]},
                {'Op': 'RemoveEvent', 'Time': 0.25, 'Function': 'Step'},
                {'Op': 'SetEvent', 'Time': 1.5, 'Function': 'Land', 'String': 'dust'}]}]}");
            string before = Text(wave);
            RunResult dry = Ok("EditAnimationClip", spec);
            Assert.IsNotEmpty(dry.Plan, "the dry run planned nothing");
            Assert.AreEqual(before, Text(wave), "the dry run wrote the clip");
            Assert.IsTrue((bool)Export("AnimationClipExport", wave)["Clips"][0]["Settings"]["LoopTime"], "the dry run left its change in memory");

            Ok("EditAnimationClip", spec + " -apply");
            JToken clip = Export("AnimationClipExport", wave)["Clips"].Single();
            Assert.IsFalse((bool)clip["Settings"]["LoopTime"]);
            Assert.AreEqual(0.5, (double)clip["Settings"]["CycleOffset"]);
            JToken x = clip["Curves"].First(c => (string)c["Property"] == "m_LocalPosition.x");
            Assert.AreEqual(new[] { 0.0, 0.5, 1.0 }, Column(x, "Time"), "SetKeys did not merge");
            Assert.AreEqual(new[] { 0.0, 4.0, 3.0 }, Column(x, "Value"));
            Assert.AreEqual(new[] { 1.0, 1.5 }, Column(clip["Curves"].First(c => (string)c["Property"] == "m_LocalScale.y"), "Value"));
            JToken swap = clip["ObjectCurves"].First(c => (string)c["Type"] == "MeshRenderer");
            Assert.AreEqual(new[] { 0.0 }, Column(swap, "Time"), "SetObjectCurve kept the old key");
            // A new object curve passes the binding check, a bool is a 0 or 1 key.
            Assert.IsTrue(clip["ObjectCurves"].Any(c => (string)c["Property"] == "m_Sprite"), clip["ObjectCurves"].ToString());
            JToken active = clip["Curves"].First(c => (string)c["Property"] == "m_IsActive");
            Assert.AreEqual(new[] { 1.0, 0.0 }, Column(active, "Value"));
            Assert.AreEqual("Constant", (string)active["Keys"][1]["InMode"]);
            JToken land = clip["Events"].Single();
            Assert.AreEqual(new object[] { 1.5, "Land", "dust" }, new object[] { (double)land["Time"], (string)land["Function"], (string)land["String"] });

            // The same keys again: nothing changes, nothing is saved.
            string after = Text(wave);
            Ok("EditAnimationClip", Spec(@"{'Targets': [{'AssetPath': '" + wave + @"', 'Settings': {'LoopTime': false}, 'Ops': [" + keys + "]}]}") + " -apply");
            Assert.AreEqual(after, Text(wave), "an edit that changed nothing saved the clip");
        }

        [Test]
        public void EditAnimationClipWithAMistypedPropertySavesNothing()
        {
            string wave = NewClip();
            string before = Text(wave);
            RunResult r = Run("EditAnimationClip", Spec(@"{'Targets': [{'AssetPath': '" + wave + @"', 'Settings': {'LoopTime': false}, 'Ops': [
                {'Op': 'SetCurve', 'Path': 'Body', 'Type': 'Transform', 'Property': 'm_LocalPositon.y', 'Keys': [{'Time': 0, 'Value': 1}]}]}]}") + " -apply");
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, wave);
            StringAssert.Contains("m_LocalPositon.y", lines);
            Assert.AreEqual(before, Text(wave));
            Assert.IsTrue(AnimationUtility.GetAnimationClipSettings(AssetDatabase.LoadAssetAtPath<AnimationClip>(wave)).loopTime,
                "the failed target kept its change in memory");
        }

        [Test]
        public void EditAnimationClipPicksAClipInsideAController()
        {
            AssetDatabase.AddObjectToAsset(new AnimationClip { name = "Inner" }, Door);
            AssetDatabase.AddObjectToAsset(new AnimationClip { name = "Other" }, Door);
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(Door));
            string ops = "'Ops': [{'Op': 'SetEvent', 'Time': 0, 'Function': 'Open'}]";
            RunResult r = Run("EditAnimationClip", Spec("{'Targets': [{'AssetPath': '" + Door + "', " + ops + "}]}") + " -apply");
            Assert.AreEqual(1, r.ExitCode, "two clips and no \"Clip\" was taken: " + string.Join("\n", r.Lines.ToArray()));
            Ok("EditAnimationClip", Spec("{'Targets': [{'AssetPath': '" + Door + "', 'Clip': 'Inner', " + ops + "}]}") + " -apply");
            JToken[] clips = Export("AnimationClipExport", Door)["Clips"].ToArray();
            Assert.AreEqual("Open", (string)clips.First(c => (string)c["Name"] == "Inner")["Events"][0]["Function"]);
            Assert.IsEmpty(clips.First(c => (string)c["Name"] == "Other")["Events"]);
        }
    }
}
