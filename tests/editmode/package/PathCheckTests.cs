using System.Collections.Generic;
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

        // R5-3: a path from a spec, -from/-to or AssetList with a ".." segment is refused with exit 2 in the dry run,
        // not found by the journal at -apply. A new run that reads -spec must get a case here.
        [Test]
        public void EveryRunRefusesADotDotPathInTheDryRun()
        {
            const string Climb = Folder + "/../DotAssetRunTest";
            string blue = Folder + "/Blue.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), blue);
            var cases = new List<string[]>
            {
                new[] { "MoveAsset", "-from=" + Climb + "/Red.mat -to=" + Folder + "/Moved.mat" },
                new[] { "MoveAsset", Spec(@"{'Moves': [{'From': '" + Red + @"', 'To': '" + Climb + @"/Moved.mat'}]}") },
                new[] { "DuplicateAsset", "-from=" + Red + " -to=" + Climb + "/Copy.mat" },
                new[] { "RemapReference", "-from=" + Climb + "/Red.mat -to=" + blue },
                new[] { "EditAsset", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Red.mat', 'Properties': {}}]}") },
                new[] { "EditMaterial", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Red.mat', 'RenderQueue': 2000}]}") },
                new[] { "EditImporter", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Red.mat', 'Properties': {}}]}") },
                new[] { "EditImporter", Spec(@"{'Targets': [{'AssetPaths': ['" + Climb + @"'], 'Properties': {}}]}") },
                new[] { "EditAnimatorController", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Door.controller', 'Ops': []}]}") },
                new[] { "EditAnimationClip", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Door.controller', 'Ops': []}]}") },
                new[] { "EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Crate.prefab', 'Ops': []}]}") },
                new[] { "EditScene", Spec(@"{'Targets': [{'AssetPath': '" + Climb + @"/Smoke.unity', 'Ops': []}]}") },
                new[] { "CreateAsset", Spec(@"{'Assets': [{'Kind': 'Material', 'Path': '" + Climb + @"/New.mat', 'Properties': {}}]}") },
                new[] { "DeleteAsset", Spec(@"{'Assets': ['" + Climb + @"/Red.mat']}") },
                new[] { "DeleteAsset", string.Empty, Climb + "/Red.mat" },
                new[] { "RemoveMissingScripts", Spec(@"{'Files': ['" + Climb + @"/Crate.prefab']}") },
                new[] { "RemoveMissingScripts", string.Empty, Climb + "/Crate.prefab" },
            };
            var failures = new List<string>();
            foreach (string[] c in cases)
            {
                RunResult r = Run(c[0], c[1], c.Skip(2).ToArray());
                string log = string.Join("\n", r.Lines.ToArray());
                if (r.ExitCode != 2 || !log.Contains("segment"))
                {
                    failures.Add(c[0] + " " + c[1] + " " + string.Join(",", c.Skip(2).ToArray()) + "\nexit " + r.ExitCode + "\n" + log);
                }
            }
            Assert.IsEmpty(failures, string.Join("\n\n", failures.ToArray()));
            var covered = new HashSet<string>(cases.Select(c => c[0]));
            foreach (DotAssetCommand cmd in CommandRegistry.All.Values.Where(cmd => cmd.Arguments.Contains("spec")))
            {
                Assert.IsTrue(covered.Contains(cmd.Name), cmd.Name + " reads -spec but has no case here");
            }
            Assert.IsTrue(File.Exists(Absolute(Red)), "Red.mat is gone");
        }

        // R5-6: a rename that changes only the case cannot be one move on a file system that ignores case. It is refused
        // with that reason and the two-move workaround, not as "already exists", and Red.mat keeps its name.
        [Test]
        public void ACaseOnlyRenameSaysHowToDoIt()
        {
            foreach (string apply in new[] { string.Empty, " -apply" })
            {
                RunResult r = Run("MoveAsset", "-from=" + Red + " -to=" + Folder + "/RED.mat" + apply);
                string log = string.Join("\n", r.Lines.ToArray());
                Assert.AreEqual(1, r.ExitCode, log);
                Assert.IsTrue(log.Contains("only the case") && log.Contains("temporary name"), log);
                Assert.IsFalse(log.Contains("already exists"), log);
                Assert.IsTrue(Directory.GetFiles(Absolute(Folder), "*.mat").Any(f => Path.GetFileName(f) == "Red.mat"), "Red.mat was renamed");
            }
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
