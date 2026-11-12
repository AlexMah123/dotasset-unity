using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // A spec value of the wrong type fails its target and names the key, through every path that reads one: the
    // readers themselves, a hierarchy op, a material block, an array op and a CreateAsset entry. Nothing is saved.
    public sealed class SpecValueTests : RunFixture
    {
        static string Lines(RunResult r)
        {
            return "exit " + r.ExitCode + "\n" + string.Join("\n", r.Lines.ToArray());
        }

        static void Refused(RunResult r, string target, string message)
        {
            Assert.AreEqual(1, r.ExitCode, Lines(r));
            CollectionAssert.Contains(r.FailedTargets, target, Lines(r));
            StringAssert.Contains(message, Lines(r));
        }

        [Test]
        public void BoolAndIntReadOnlyTheirOwnType()
        {
            var spec = JObject.Parse(@"{'T': true, 'One': 1, 'Text': 'false', 'Zero': '0', 'Half': 0.5, 'List': [],
                'Three': 3, 'ThreeText': '3', 'Whole': 3.0, 'Last': 'last', 'Frac': 2.5, 'Flag': true, 'Huge': 1e20, 'None': null}");
            Assert.AreEqual(new[] { true, true, false, true, false }, new[]
            {
                Json.Bool(spec, "T", false), Json.Bool(spec, "One", false), Json.Bool(spec, "Text", true),
                Json.Bool(spec, "Missing", true), Json.Bool(spec, "None", false),
            });
            Assert.AreEqual(new[] { 3, 3, 3, 7, 7 }, new[]
            {
                Json.Int(spec, "Three", 0), Json.Int(spec, "ThreeText", 0), Json.Int(spec, "Whole", 0),
                Json.Int(spec, "Missing", 7), Json.Int(spec, "None", 7),
            });
            // FormatException, the base of SpecValueException, so this compiles against the code before the fix.
            foreach (string key in new[] { "Zero", "Half", "List", "ThreeText" })
            {
                Assert.Catch<System.FormatException>(() => Json.Bool(spec, key, true), key + " read as a bool");
            }
            foreach (string key in new[] { "Last", "Frac", "Flag", "Huge", "List" })
            {
                Assert.Catch<System.FormatException>(() => Json.Int(spec, key, 0), key + " read as an int");
            }
            StringAssert.Contains("Zero: \"0\" is not true or false", Assert.Catch<System.FormatException>(() => Json.Bool(spec, "Zero", true)).Message);
            StringAssert.Contains("Last: \"last\" is not a whole number", Assert.Catch<System.FormatException>(() => Json.Int(spec, "Last", 0)).Message);
        }

        [Test]
        public void ABoolOpValueOfTheWrongTypeFailsThePrefab()
        {
            string before = Text(Crate);
            RunResult r = Run("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [
                {'Op': 'SetGameObject', 'Path': 'Crate/Mesh', 'Active': '0'}]}]}") + " -apply");
            Refused(r, Crate, "Active: \"0\" is not true or false");
            Assert.AreEqual(before, Text(Crate));
        }

        [Test]
        public void AnIntOpValueOfTheWrongTypeFailsThePrefab()
        {
            string before = Text(Crate);
            RunResult r = Run("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [
                {'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid', 'SiblingIndex': 'last'}]}]}") + " -apply");
            Refused(r, Crate, "SiblingIndex: \"last\" is not a whole number");
            Assert.AreEqual(before, Text(Crate));
        }

        [Test]
        public void AMaterialValueOfTheWrongTypeFailsTheMaterial()
        {
            string before = Text(Red);
            RunResult r = Run("EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'EnableInstancing': true, 'RenderQueue': 'high'}]}") + " -apply");
            Refused(r, Red, "RenderQueue: \"high\" is not a whole number");
            Assert.AreEqual(before, Text(Red));
            Assert.IsFalse(AssetDatabase.LoadAssetAtPath<Material>(Red).enableInstancing, "the failed material kept instancing in memory");
        }

        [Test]
        public void AnArrayOpIndexOfTheWrongTypeFailsTheAsset()
        {
            string before = Text(Red);
            RunResult r = Run("EditAsset", Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'ArrayOps': [
                {'Property': 'm_ValidKeywords', 'Remove': 'first'}]}]}") + " -apply");
            Refused(r, Red, "Remove: \"first\" is not a whole number");
            Assert.AreEqual(before, Text(Red));
        }

        [Test]
        public void ACreateAssetValueOfTheWrongTypeCreatesNothing()
        {
            string built = Folder + "/Built.unity";
            RunResult r = Run("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Scene', 'Path': '" + built + @"', 'AddToBuild': 'yes'}]}"));
            Refused(r, built, "AddToBuild: \"yes\" is not true or false");
            Assert.IsFalse(System.IO.File.Exists(Absolute(built)), "the failed scene was written");
            Assert.IsFalse(EditorBuildSettings.scenes.Any(s => s.path == built), "the failed scene was added to the build");
        }

        // R7-2: Setup is Empty or Default, in any case like an enum name. Anything else made an empty scene.
        [Test]
        public void ASceneSetupIsEmptyOrDefaultAndNothingElse()
        {
            string lower = Folder + "/Lower.unity";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Scene', 'Path': '" + lower + @"', 'Setup': 'default'}]}"));
            StringAssert.Contains("Camera:", Text(lower), "\"default\" made an empty scene");
            string built = Folder + "/Built.unity";
            RunResult r = Run("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Scene', 'Path': '" + built + @"', 'Setup': 'DefaultGameObjects'}]}"));
            Refused(r, built, "Setup: \"DefaultGameObjects\" is not Empty or Default");
            Assert.IsFalse(System.IO.File.Exists(Absolute(built)), "the failed scene was written");
        }

        // N7-1: a clip's Settings were applied outside the per-op catch, so a wrong type in a reference there ended
        // the run without failing the clip, and the clips after it never ran.
        [Test]
        public void AClipSettingsValueOfTheWrongTypeFailsOnlyThatClip()
        {
            string wave = NewClip();
            string second = Folder + "/Second.anim";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'AnimationClip', 'Path': '" + second + @"', 'FrameRate': 30,
                'Ops': [{'Op': 'SetCurve', 'Path': 'Body', 'Type': 'Transform', 'Property': 'm_LocalPosition.y', 'Keys': [{'Time': 0, 'Value': 0}, {'Time': 1, 'Value': 1}]}]}]}"));
            string before = Text(wave);
            RunResult r = Run("EditAnimationClip", Spec(@"{'Targets': [
                {'AssetPath': '" + wave + @"', 'Settings': {'AdditiveReferencePoseClip': {'Asset': '" + second + @"', 'Missing': 'yes'}}},
                {'AssetPath': '" + second + @"', 'FrameRate': 24}]}") + " -apply");
            Refused(r, wave, "Missing: \"yes\" is not true or false");
            Assert.AreEqual(before, Text(wave));
            Assert.AreEqual(24f, AssetDatabase.LoadAssetAtPath<AnimationClip>(second).frameRate, "the second clip did not run\n" + Lines(r));
        }
    }
}
