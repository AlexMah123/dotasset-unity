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
    }
}
