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

        // Fails on 6000.0.85f1: the list keeps the moved path. MoveAsset journals no scene list, whole-list
        // journaling is planned for 0.11.0, which turns this test on.
        [Test]
        [Ignore("Dec 5: the scene list keeps the moved path after the undo, fixed in 0.11.0")]
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
    }
}
