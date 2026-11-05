using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // RemapReference on a model: the remap lives in the importer, so the write changes only the .meta, which the
    // journal has to keep for UndoRun to put it back.
    public sealed class RemapReferenceModelTests : RunFixture
    {
        const string Blue = Folder + "/Blue.mat";
        const string Model = Folder + "/Tri.obj";

        [Test]
        public void UndoRunRestoresAModelsRemap()
        {
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), Blue);
            File.WriteAllText(Absolute(Model), "o Tri\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Mat0\nf 1 2 3\n");
            AssetDatabase.ImportAsset(Model);
            var importer = (ModelImporter)AssetImporter.GetAtPath(Model);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Mat0"), AssetDatabase.LoadAssetAtPath<Material>(Red));
            importer.SaveAndReimport();
            byte[] before = File.ReadAllBytes(Absolute(Model) + ".meta");

            RunResult remap = Ok("RemapReference", "-from=" + Red + " -to=" + Blue + " -scandir=" + Folder + " -apply");
            CollectionAssert.AreNotEqual(before, File.ReadAllBytes(Absolute(Model) + ".meta"), "the remap did not change the .meta");
            string journal = remap.Outputs.FirstOrDefault(o => o.EndsWith("journal.json"));
            Assert.IsNotNull(journal, "the run kept no journal");
            Ok("UndoRun", "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Absolute(Model) + ".meta"), "UndoRun did not put the .meta back");
        }
    }
}
