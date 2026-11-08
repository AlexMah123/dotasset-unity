using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace DotAsset.Tests
{
    // N12: a save Unity could not write (another program holds the file) is a failed target, not a success with a
    // warning, and the change does not stay behind in memory.
    public sealed class SaveFailureTests : RunFixture
    {
        [Test]
        public void ASaveUnityCouldNotWriteFailsTheTarget()
        {
            // Unity logs the failed write as an error.
            LogAssert.ignoreFailingMessages = true;
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Red + @"', 'RenderQueue': 2450}]}");
            int queue = AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue;
            byte[] before = File.ReadAllBytes(Absolute(Red));
            RunResult r;
            using (new FileStream(Absolute(Red), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("EditMaterial", spec + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, Red, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(Red)), "the file changed after all");
            Assert.AreEqual(queue, AssetDatabase.LoadAssetAtPath<Material>(Red).renderQueue, "the edit stayed in memory");
        }

        [Test]
        public void RemapReferenceFailsAHolderUnityCouldNotWrite()
        {
            // S2: a material holding a texture is saved by the generic branch, not through a PrefabSession.
            LogAssert.ignoreFailingMessages = true;
            string oldTex = Folder + "/Old.png";
            string newTex = Folder + "/New.png";
            string holder = Folder + "/Holder.mat";
            WritePng(oldTex);
            WritePng(newTex);
            var mat = new Material(Shader.Find("Unlit/Texture"));
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(oldTex);
            AssetDatabase.CreateAsset(mat, holder);
            byte[] before = File.ReadAllBytes(Absolute(holder));
            RunResult r;
            using (new FileStream(Absolute(holder), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("RemapReference", "-from=" + oldTex + " -to=" + newTex + " -scandir=" + Folder + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, holder, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(holder)), "the file changed after all");
            Assert.AreEqual(oldTex, AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<Material>(holder).mainTexture), "the remap stayed in memory");
        }

        [Test]
        public void ARunThatSavedNothingOffersNoUndo()
        {
            // R5-7: the run kept the prefab, the save failed, nothing changed on disk, so there is nothing to undo.
            LogAssert.ignoreFailingMessages = true;
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [{'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Lid'}]}]}");
            byte[] before = File.ReadAllBytes(Absolute(Crate));
            RunResult r;
            using (new FileStream(Absolute(Crate), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("EditPrefab", spec + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(Crate)), "the prefab changed after all");
            Assert.IsFalse(lines.Contains("undo with"), lines);
            Assert.IsFalse(r.Outputs.Any(o => o.EndsWith("journal.json")), "a journal with nothing to undo was kept\n" + lines);
        }

        [Test]
        public void EditImporterFailsAMetaUnityCouldNotWrite()
        {
            // S2: the importer's settings go to the .meta, so a held .meta is a save Unity could not write.
            LogAssert.ignoreFailingMessages = true;
            string png = Folder + "/Flat.png";
            WritePng(png);
            string meta = Absolute(png) + ".meta";
            string spec = Spec(@"{'Targets': [{'AssetPath': '" + png + @"', 'Texture': {'TextureType': 'NormalMap'}}]}");
            byte[] before = File.ReadAllBytes(meta);
            RunResult r;
            using (new FileStream(meta, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("EditImporter", spec + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, png, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(meta), "the .meta changed after all");
            var importer = (TextureImporter)AssetImporter.GetAtPath(png);
            Assert.AreNotEqual(TextureImporterType.NormalMap, importer.textureType, "the setting stayed in memory");
        }

        // R5-16: the four runs review 4 (S2) left unprobed. Each holds the one file the run would write, or for a run
        // that only writes new paths, the file in its way, and expects a failed target with nothing changed.
        [Test]
        public void RemoveMissingScriptsFailsAPrefabUnityCouldNotWrite()
        {
            LogAssert.ignoreFailingMessages = true;
            string ghost = Folder + "/Ghost.prefab";
            var go = new GameObject("Ghost");
            go.AddComponent<BoxCollider>();
            go.AddComponent<RunTestOld>();
            PrefabUtility.SaveAsPrefabAsset(go, ghost);
            Object.DestroyImmediate(go);
            Rewrite(ghost, AssetDatabase.AssetPathToGUID(ScriptPath<RunTestOld>()), "d07a55e7d07a55e7d07a55e7d07a55e7");
            byte[] before = File.ReadAllBytes(Absolute(ghost));
            RunResult r;
            using (new FileStream(Absolute(ghost), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("RemoveMissingScripts", "-apply", ghost);
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, ghost, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(ghost)), "the prefab changed after all");
        }

        [Test]
        public void RemapReferenceFailsAModelUnityCouldNotWrite()
        {
            LogAssert.ignoreFailingMessages = true;
            string blue = Folder + "/Blue.mat";
            string model = Folder + "/Tri.obj";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), blue);
            File.WriteAllText(Absolute(model), "o Tri\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Mat0\nf 1 2 3\n");
            AssetDatabase.ImportAsset(model);
            var importer = (ModelImporter)AssetImporter.GetAtPath(model);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Mat0"), AssetDatabase.LoadAssetAtPath<Material>(Red));
            importer.SaveAndReimport();
            string meta = Absolute(model) + ".meta";
            byte[] before = File.ReadAllBytes(meta);
            RunResult r;
            using (new FileStream(meta, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("RemapReference", "-from=" + Red + " -to=" + blue + " -scandir=" + Folder + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, model, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(meta), "the .meta changed after all");
            var map = ((ModelImporter)AssetImporter.GetAtPath(model)).GetExternalObjectMap();
            Assert.IsTrue(map.Values.All(o => AssetDatabase.GetAssetPath(o) == Red), "the remap stayed in memory");
        }

        [Test]
        public void DuplicateAssetFailsASourceAnotherProgramLocks()
        {
            LogAssert.ignoreFailingMessages = true;
            string copy = Folder + "/Copy.mat";
            RunResult r;
            using (new FileStream(Absolute(Red), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                r = Run("DuplicateAsset", "-from=" + Red + " -to=" + copy + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            Assert.IsFalse(File.Exists(Absolute(copy)), "a copy was left\n" + lines);
            Assert.IsFalse(File.Exists(Absolute(copy) + ".meta"), "a .meta was left\n" + lines);
        }

        [Test]
        public void CreateAssetFailsAFileInTheWayUnityCouldNotWrite()
        {
            // A file another program is still writing, not imported yet: the AssetDatabase sees nothing at the path.
            LogAssert.ignoreFailingMessages = true;
            string stray = Folder + "/Stray.mat";
            File.WriteAllText(Absolute(stray), "not imported yet");
            byte[] before = File.ReadAllBytes(Absolute(stray));
            RunResult r;
            using (new FileStream(Absolute(stray), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                r = Run("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + stray + @"', 'Properties': {}}]}") + " -apply");
            }
            string lines = string.Join("\n", r.Lines.ToArray());
            Assert.AreEqual(1, r.ExitCode, lines);
            CollectionAssert.Contains(r.FailedTargets, stray, lines);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(stray)), "the file changed after all");
        }
    }
}
