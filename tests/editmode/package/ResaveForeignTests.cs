using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotAsset.Tests
{
    // Which assets ResaveAsset journals by their .meta alone: only formats Unity reads but never writes.
    public sealed class ResaveForeignTests
    {
        const string Folder = "Assets/DotAssetResaveTest";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetResaveTest");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void TextureIsForeign()
        {
            string path = Folder + "/Image.png";
            var texture = new Texture2D(2, 2);
            File.WriteAllBytes(DotAssetPaths.Absolute(path), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            Assert.IsTrue(ResaveAssetCommand.IsForeign(path));
        }

        [Test]
        public void PrefabSceneAndMaterialAreNative()
        {
            string prefab = Folder + "/Crate.prefab";
            var go = new GameObject("Crate");
            PrefabUtility.SaveAsPrefabAsset(go, prefab);
            Object.DestroyImmediate(go);
            string scene = Folder + "/Level.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scene, true));
            string material = Folder + "/Plain.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), material);
            Assert.IsFalse(ResaveAssetCommand.IsForeign(prefab), "a prefab is imported, but its file is written in full");
            Assert.IsFalse(ResaveAssetCommand.IsForeign(scene));
            Assert.IsFalse(ResaveAssetCommand.IsForeign(material));
        }
    }
}
