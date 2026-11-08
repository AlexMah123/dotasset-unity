using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Every Migrate run without an EditMode test of its own: its dry run plans and writes nothing, -apply does the
    // change, read back from what Unity loads. MoveAsset and UndoRun have theirs in the journal tests.
    public sealed class MigrateRunTests : RunFixture
    {
        const string GhostGuid = "d07a55e7d07a55e7d07a55e7d07a55e7";

        static Material NewMaterial(string path)
        {
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        static string Prefab<T>(string name, System.Action<T> set) where T : Component
        {
            string path = Folder + "/" + name + ".prefab";
            var go = new GameObject(name);
            go.AddComponent<BoxCollider>();
            set(go.AddComponent<T>());
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return path;
        }

        [Test]
        public void DeleteAssetDeletesWhatNothingUsesAndRefusesTheRest()
        {
            string unused = Folder + "/Unused.mat";
            NewMaterial(unused);
            string args = "-scandir=" + Folder + " -permanent";
            RunResult dry = Ok("DeleteAsset", args, unused);
            Assert.IsTrue(Planned(dry, "Path", unused), "the dry run did not plan the delete");
            Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(unused)), "the dry run deleted");
            Ok("DeleteAsset", args + " -apply", unused);
            Assert.IsFalse(File.Exists(DotAssetPaths.Absolute(unused)));

            RunResult used = Run("DeleteAsset", args + " -apply", Red);
            Assert.AreEqual(1, used.ExitCode, "Crate uses Red.mat\n" + string.Join("\n", used.Lines.ToArray()));
            Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(Red)), "the refused delete deleted Red.mat");
        }

        // R5-9: with no -scandir, a material in an embedded package that uses the texture counts as a user, so the
        // delete is refused and names it. Embedded and local packages are project files a delete can break.
        [Test]
        public void DeleteAssetSeesAUserInAnEmbeddedPackage()
        {
            string png = Folder + "/Held.png";
            WritePng(png);
            const string holder = "Packages/com.alexmah.dotasset.tests/DotAssetHolder.mat";
            var mat = new Material(Shader.Find("Unlit/Texture"));
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(png);
            AssetDatabase.CreateAsset(mat, holder);
            try
            {
                RunResult r = Run("DeleteAsset", "-permanent -apply", png);
                string lines = string.Join("\n", r.Lines.ToArray());
                Assert.AreEqual(1, r.ExitCode, lines);
                StringAssert.Contains(holder, lines);
                Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(png)), "the texture was deleted");
            }
            finally
            {
                AssetDatabase.DeleteAsset(holder);
            }
        }

        // R5-5: a misspelt key is refused like in every other spec run, not read as "nothing to delete"
        [Test]
        public void DeleteAssetRefusesAMisspeltSpecKey()
        {
            string unused = Folder + "/Unused.mat";
            NewMaterial(unused);
            RunResult r = Run("DeleteAsset", "-scandir=" + Folder + " -permanent -apply " + Spec(@"{'Assets': [], 'Asets': ['" + unused + @"']}"));
            Assert.AreEqual(2, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            StringAssert.Contains("Asets", string.Join("\n", r.Lines.ToArray()));
            Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(unused)));
        }

        // Smoke.unity open beside the active scene, with Green.mat on its crate's renderer and not saved: the only use of
        // Green.mat, and one the files on disk do not show.
        void WithGreenInAnUnsavedScene(string green, System.Action check)
        {
            Material material = NewMaterial(green);
            var open = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(Scene, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                open.GetRootGameObjects().Select(g => g.GetComponentInChildren<MeshRenderer>()).First(m => m != null).sharedMaterial = material;
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(open);
                check();
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(open, true);
            }
        }

        // R5-1: an asset that unsaved work open in the editor uses is kept, -force or not, and the run names the scene
        [Test]
        public void DeleteAssetKeepsWhatAnUnsavedOpenSceneUses()
        {
            string green = Folder + "/Green.mat";
            WithGreenInAnUnsavedScene(green, () =>
            {
                foreach (string force in new[] { "", " -force" })
                {
                    RunResult r = Run("DeleteAsset", "-scandir=" + Folder + " -permanent -apply" + force, green);
                    string lines = string.Join("\n", r.Lines.ToArray());
                    Assert.AreEqual(1, r.ExitCode, "DeleteAsset" + force + "\n" + lines);
                    StringAssert.Contains(Scene, lines);
                    Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(green)), "deleted by DeleteAsset" + force);
                }
            });
        }

        // R5-1: AuditUnusedAsset counts that use too, so its fix spec does not list the asset
        [Test]
        public void AuditUnusedAssetCountsAnUnsavedOpenScene()
        {
            string green = Folder + "/Green.mat";
            WithGreenInAnUnsavedScene(green, () =>
            {
                JObject report = Report("AuditUnusedAsset", AnyIssues, "-scandir=" + Folder);
                Assert.IsFalse(HasIssue(report, "U1", green), report.ToString());
            });
        }

        [Test]
        public void DuplicateAssetCopiesWithANewGuid()
        {
            AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue = 2460;
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadAssetAtPath<Material>(Red));
            string blue = Folder + "/Blue.mat";
            Ok("DuplicateAsset", "-from=" + Red + " -to=" + blue);
            Assert.IsFalse(File.Exists(DotAssetPaths.Absolute(blue)), "the dry run copied");
            Ok("DuplicateAsset", "-from=" + Red + " -to=" + blue + " -apply");
            Assert.AreEqual(2460, AssetDatabase.LoadAssetAtPath<Material>(blue).renderQueue);
            Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(Red), AssetDatabase.AssetPathToGUID(blue));
        }

        [Test]
        public void RemapReferencePointsUsersAtTheNewAsset()
        {
            string blue = Folder + "/Blue.mat";
            NewMaterial(blue);
            string args = "-from=" + Red + " -to=" + blue + " -scandir=" + Folder;
            Ok("RemapReference", args);
            Assert.AreEqual(Red, MaterialOf(Crate), "the dry run remapped");
            Ok("RemapReference", args + " -apply");
            Assert.AreEqual(blue, MaterialOf(Crate));
        }

        static string Curve(string path)
        {
            string keys = "";
            for (int t = 0; t < 2; t++)
            {
                keys += "      - serializedVersion: 3\n        time: " + t + "\n        value: " + t + "\n        inSlope: 0\n"
                    + "        outSlope: 0\n        tangentMode: 0\n        weightedMode: 0\n        inWeight: 0.33333334\n"
                    + "        outWeight: 0.33333334\n";
            }
            return "  - curve:\n      serializedVersion: 2\n      m_Curve:\n" + keys + "      m_PreInfinity: 2\n      m_PostInfinity: 2\n"
                + "      m_RotationOrder: 4\n    attribute: m_IsActive\n    path: " + path + "\n    classID: 1\n    script: {fileID: 0}\n";
        }

        static string[] Animated(string clip)
        {
            return AnimationUtility.GetCurveBindings(AssetDatabase.LoadAssetAtPath<AnimationClip>(clip)).Select(b => b.path).OrderBy(p => p).ToArray();
        }

        [Test]
        public void RebindAnimationRenamesThePathAndItsChildren()
        {
            string clip = Folder + "/Wave.anim";
            string curves = Curve("Body/Arm") + Curve("Body/Arm/Hand") + Curve("Body/Leg");
            File.WriteAllText(DotAssetPaths.Absolute(clip), "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!74 &7400000\nAnimationClip:\n"
                + "  m_ObjectHideFlags: 0\n  m_Name: Wave\n  serializedVersion: 7\n  m_Legacy: 0\n  m_FloatCurves:\n" + curves
                + "  m_PPtrCurves: []\n  m_SampleRate: 60\n  m_EditorCurves:\n" + curves + "  m_Events: []\n");
            AssetDatabase.ImportAsset(clip);
            string args = "-from=Body/Arm -to=Body/ArmL -scandir=" + Folder;
            RunResult dry = Ok("RebindAnimation", args);
            Assert.IsTrue(dry.Plan.Any(p => (string)p["Target"] == clip && (string)p["Before"] == "Body/Arm"), "the dry run did not plan Wave.anim");
            Assert.AreEqual(new[] { "Body/Arm", "Body/Arm/Hand", "Body/Leg" }, Animated(clip), "the dry run rebound");
            Ok("RebindAnimation", args + " -apply");
            Assert.AreEqual(new[] { "Body/ArmL", "Body/ArmL/Hand", "Body/Leg" }, Animated(clip));
        }

        static JToken Stale(string material)
        {
            return Export("MaterialExport", material)["StaleProperties"] ?? new JArray();
        }

        [Test]
        public void RenameShaderPropertyRenamesAndRenamesBack()
        {
            string tint = Folder + "/Tint.mat";
            Ok("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + tint + @"', 'Properties': {'_Color': [0.25, 0.5, 0.75, 1]}}]}"));
            RunResult dry = Ok("RenameShaderProperty", "-from=_Color -to=_DotAssetTint", tint);
            Assert.IsTrue(Planned(dry, "Target", tint), "the dry run did not plan Tint.mat");
            Assert.IsFalse(Stale(tint).Any(p => (string)p == "_DotAssetTint"), "the dry run renamed");
            Ok("RenameShaderProperty", "-from=_Color -to=_DotAssetTint -apply", tint);
            Assert.IsTrue(Stale(tint).Any(p => (string)p == "_DotAssetTint"), "Tint.mat does not save _DotAssetTint");
            Ok("RenameShaderProperty", "-from=_DotAssetTint -to=_Color -apply", tint);
            Color color = AssetDatabase.LoadAssetAtPath<Material>(tint).GetColor("_Color");
            Assert.AreEqual(0.5f, color.g, 0.001f, "the color did not survive the rename and back: " + color);
        }

        [Test]
        public void ResaveAssetKeepsWhatTheAssetsHold()
        {
            string before = Text(Crate);
            JToken hierarchy = Export("PrefabExport", Crate)["Hierarchy"];
            RunResult dry = Ok("ResaveAsset", "", Folder);
            Assert.IsTrue(dry.Plan.Any(p => (string)p["Action"] == "resave" && (string)p["Path"] == Crate), "the dry run did not plan Crate");
            Assert.AreEqual(before, Text(Crate), "the dry run wrote");
            Ok("ResaveAsset", "-apply", Folder);
            Assert.IsTrue(JToken.DeepEquals(hierarchy, Export("PrefabExport", Crate)["Hierarchy"]), "the resave changed Crate");
        }

        [Test]
        public void RemoveMissingScriptsTakesOnlyTheMissingOne()
        {
            string ghost = Prefab<RunTestOld>("Ghost", c => { });
            Rewrite(ghost, AssetDatabase.AssetPathToGUID(ScriptPath<RunTestOld>()), GhostGuid);
            JObject report = Report("AuditMissingReference", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "R2", ghost), report.ToString());
            Assert.AreEqual("RemoveMissingScripts", (string)report["FixRun"]);
            Ok("RemoveMissingScripts", "", ghost);
            StringAssert.Contains(GhostGuid, Text(ghost), "the dry run removed it");
            Ok("RemoveMissingScripts", "-apply", ghost);
            StringAssert.DoesNotContain(GhostGuid, Text(ghost));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(ghost).GetComponent<BoxCollider>(), "the BoxCollider went along");
            Report("AuditMissingReference", 0, "", Folder);
        }

        [Test]
        public void RemapScriptMovesTheComponentAndKeepsItsValues()
        {
            string unit = Prefab<RunTestOld>("Unit", c => c.hp = 7);
            string args = "-from=" + ScriptPath<RunTestOld>() + " -to=" + ScriptPath<RunTestNew>() + " -scandir=" + Folder;
            RunResult dry = Ok("RemapScript", args);
            Assert.IsTrue(dry.Plan.Any(p => (string)p["Action"] == "rewrite" && (string)p["Path"] == unit), "the dry run did not plan Unit");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(unit).GetComponent<RunTestOld>(), "the dry run remapped");
            Ok("RemapScript", args + " -apply");
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(unit);
            Assert.IsNull(root.GetComponent<RunTestOld>());
            Assert.AreEqual(7, root.GetComponent<RunTestNew>().hp);
        }

        [Test]
        public void RenameFieldRenamesTheSavedKey()
        {
            string field = Prefab<RunTestField>("Field", c => c.health = 7);
            Rewrite(field, "  health: 7", "  hp: 7");
            string script = ScriptPath<RunTestField>();
            Assert.AreEqual(2, Run("RenameField", "-script=" + script + " -from=health -to=hp -scandir=" + Folder).ExitCode,
                "the class still has health, the rename must be refused");
            string args = "-script=" + script + " -from=hp -to=health -scandir=" + Folder;
            RunResult dry = Ok("RenameField", args);
            Assert.IsTrue(dry.Plan.Any(p => (string)p["Action"] == "rewrite" && (string)p["Path"] == field), "the dry run did not plan Field");
            StringAssert.Contains("  hp: 7", Text(field), "the dry run renamed");
            Ok("RenameField", args + " -apply");
            StringAssert.Contains("  health: 7", Text(field));
            Assert.AreEqual(7, AssetDatabase.LoadAssetAtPath<GameObject>(field).GetComponent<RunTestField>().health);
        }

        [Test]
        public void RenameSerializeReferenceTypeBringsTheDataBack()
        {
            string asset = Folder + "/Ref.asset";
            var holder = ScriptableObject.CreateInstance<RunTestRefHolder>();
            holder.item = new RunTestRefItem { x = 7 };
            AssetDatabase.CreateAsset(holder, asset);
            Rewrite(asset, "class: RunTestRefItem", "class: RunTestRefGone");
            JObject report = Report("AuditMissingReference", 3, "", Folder);
            Assert.IsTrue(HasIssue(report, "R5", asset), report.ToString());
            string args = "-from=\"DotAsset.Tests DotAsset.Tests.RunTestRefGone\" -to=DotAsset.Tests.RunTestRefItem -scandir=" + Folder;
            RunResult dry = Ok("RenameSerializeReferenceType", args);
            Assert.IsTrue(dry.Plan.Any(p => (string)p["Action"] == "rewrite" && (string)p["Path"] == asset), "the dry run did not plan Ref.asset");
            StringAssert.Contains("class: RunTestRefGone", Text(asset), "the dry run renamed");
            Ok("RenameSerializeReferenceType", args + " -apply");
            var item = AssetDatabase.LoadAssetAtPath<RunTestRefHolder>(asset).item as RunTestRefItem;
            Assert.IsNotNull(item, "the data did not come back as RunTestRefItem");
            Assert.AreEqual(7, item.x);
        }
    }
}
