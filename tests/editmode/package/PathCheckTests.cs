using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Paths the journal cannot keep right: a lowercase assets/ it used to skip, a .. that climbs out of the folder
    // the run names, and a CreateAsset entry refused only after earlier entries already exist.
    public sealed class PathCheckTests : RunFixture
    {
        const string Loose = Folder + "/Loose.mat";

        [Test]
        public void ALowercaseAssetsPathIsRefusedOrUndoable()
        {
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), Loose);
            RunResult r = Run("DeleteAsset", "-scandir=" + Folder + " -permanent -apply", "assets" + Loose.Substring("Assets".Length));
            if (r.ExitCode == 0)
            {
                string journal = r.Outputs.FirstOrDefault(o => o.EndsWith("journal.json"));
                Assert.IsNotNull(journal, "the delete ran but kept no journal, it cannot be undone\n" + string.Join("\n", r.Lines.ToArray()));
                Ok("UndoRun", "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply");
            }
            else
            {
                Assert.AreEqual(2, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            }
            Assert.IsTrue(File.Exists(Absolute(Loose)), "the material is gone");
        }

        [Test]
        public void ADotDotSegmentIsRefused()
        {
            string climbing = Folder + "/X/../Climbed.mat";
            RunResult r = Run("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + climbing + @"', 'Properties': {}}]}"));
            Assert.AreEqual(2, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            Assert.IsFalse(File.Exists(Absolute(Folder + "/Climbed.mat")), "the material was created");
            Assert.IsFalse(Directory.Exists(Absolute(Folder + "/X")), "the folder was created");
        }

        [Test]
        public void ABadKindOrPathLaterInTheSpecCreatesNothing()
        {
            string first = Folder + "/First.mat";
            foreach (string bad in new[] { "{'Kind': 'Nope', 'Path': '" + Folder + "/Second.mat'}", "{'Kind': 'Material', 'Path': 'Elsewhere/Second.mat', 'Properties': {}}" })
            {
                RunResult r = Run("CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + first + @"', 'Properties': {}}, " + bad + "]}"));
                Assert.AreEqual(2, r.ExitCode, bad + "\n" + string.Join("\n", r.Lines.ToArray()));
                Assert.IsFalse(File.Exists(Absolute(first)), "the first entry was created before the spec was refused: " + bad);
            }
        }
    }
}
