using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace DotAsset.Tests
{
    // The Build Settings scene list after a build scene is moved and the move undone, read live, not from
    // EditorBuildSettings.asset, which Unity writes only when it exits.
    public sealed class BuildSceneUndoTests
    {
        const string Folder = "Assets/DotAssetBuildSceneTest";
        const string From = Folder + "/Level.unity";
        const string To = Folder + "/Moved.unity";
        EditorBuildSettingsScene[] before;

        [SetUp]
        public void SetUp()
        {
            before = EditorBuildSettings.scenes;
            AssetDatabase.CreateFolder("Assets", "DotAssetBuildSceneTest");
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), From, true));
            EditorBuildSettings.scenes = before.Concat(new[] { new EditorBuildSettingsScene(From, true) }).ToArray();
        }

        [TearDown]
        public void TearDown()
        {
            EditorBuildSettings.scenes = before;
            AssetDatabase.DeleteAsset(Folder);
        }

        static string Listed()
        {
            return string.Join(", ", EditorBuildSettings.scenes.Select(s => s.path).ToArray());
        }

        // Dec 5: before 0.11.0 the list kept the moved path, nothing journaled the list.
        [Test]
        public void UndoingAMoveListsTheSceneAtItsPathAgain()
        {
            string guid = AssetDatabase.AssetPathToGUID(From);
            RunResult move = RunDispatcher.Run("MoveAsset", new string[0], "-from=" + From + " -to=" + To + " -apply", false);
            Assert.AreEqual(0, move.ExitCode, string.Join("\n", move.Lines.ToArray()));
            string afterMove = Listed();
            string journal = move.Outputs.Select(o => o.Replace('\\', '/')).FirstOrDefault(o => o.EndsWith("/journal.json"));
            Assert.IsNotNull(journal, "MoveAsset -apply printed no journal");
            RunResult undo = RunDispatcher.Run("UndoRun", new string[0], "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply", false);
            Assert.AreEqual(0, undo.ExitCode, string.Join("\n", undo.Lines.ToArray()));
            string message = "after the move: " + afterMove + "\nafter the undo: " + Listed();
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(From), "the undo gave the scene a new GUID");
            Assert.IsTrue(EditorBuildSettings.scenes.Any(s => s.path == From), message);
            Assert.IsFalse(EditorBuildSettings.scenes.Any(s => s.path == To), message);
        }

        [Test]
        public void ARunThatLeavesTheListAloneKeepsNoListAndIsUndoneAfterTheListChanged()
        {
            string text = Folder + "/Note.txt";
            File.WriteAllText(DotAssetPaths.Absolute(text), "note");
            AssetDatabase.ImportAsset(text);
            RunResult move = RunDispatcher.Run("MoveAsset", new string[0], "-from=" + text + " -to=" + Folder + "/Moved.txt -apply", false);
            Assert.AreEqual(0, move.ExitCode, string.Join("\n", move.Lines.ToArray()));
            string journal = move.Outputs.Select(o => o.Replace('\\', '/')).First(o => o.EndsWith("/journal.json"));
            Assert.IsFalse(RunJournal.EntriesOf(RunJournal.Load(journal)).Any(e => e.Path == RunJournal.BuildSceneList), "the list is journaled although the move left it alone");
            EditorBuildSettings.scenes = before;
            RunResult undo = RunDispatcher.Run("UndoRun", new string[0], "-run=" + Path.GetFileName(Path.GetDirectoryName(journal)) + " -apply", false);
            Assert.AreEqual(0, undo.ExitCode, string.Join("\n", undo.Lines.ToArray()));
            Assert.IsTrue(File.Exists(DotAssetPaths.Absolute(text)));
        }
    }
}
