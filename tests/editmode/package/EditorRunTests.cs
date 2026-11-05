using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DotAsset.Tests
{
    // A run from a caller other than the editor queue (the Unity CLI adapter): it waits for a run still settling,
    // and confirm is -apply.
    public sealed class EditorRunTests
    {
        const string Folder = "Assets/DotAssetEditorRunTest";
        const string ScenePath = Folder + "/Open.unity";
        Scene scene;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetEditorRunTest");
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath, true));
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            SceneSession.SavedOpenScenes.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            SceneSession.SavedOpenScenes.Clear();
            if (scene.IsValid())
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.DeleteAsset(Folder);
        }

        static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.IsTrue(task.IsCompleted, "the run did not end within 10 seconds");
        }

        [UnityTest]
        public IEnumerator WaitsForARunStillSettling()
        {
            bool settled = false;
            bool settledFirst = false;
            SceneSession.SavedOpenScenes.Add(ScenePath);
            Assert.IsTrue(SceneSettle.Begin(new RunResult(), r => settled = true));
            System.Action<RunResult> started = r => settledFirst = settled;
            RunDispatcher.RunFinished += started;
            try
            {
                Task<RunResult> run = EditorRun.RunAsync("CommandCatalogExport", null, string.Empty, false);
                Assert.IsFalse(run.IsCompleted, "ran while a run was settling");
                yield return Wait(run);
                Assert.AreEqual(0, run.Result.ExitCode, EditorRun.Describe(run.Result));
                Assert.IsTrue(settledFirst, "the run ran before the settle ended");
            }
            finally
            {
                RunDispatcher.RunFinished -= started;
            }
        }

        [UnityTest]
        public IEnumerator ConfirmIsApply()
        {
            // A move of a missing asset, refused before anything is written, applied or not. Its error reaches the console.
            LogAssert.ignoreFailingMessages = true;
            string args = "-from=" + Folder + "/Missing.mat -to=" + Folder + "/Moved.mat -apply=false";
            Task<RunResult> plan = EditorRun.RunAsync("MoveAsset", null, args, false);
            yield return Wait(plan);
            Assert.IsTrue(plan.Result.DryRun, EditorRun.Describe(plan.Result));
            Task<RunResult> apply = EditorRun.RunAsync("MoveAsset", null, args, true);
            yield return Wait(apply);
            Assert.IsFalse(apply.Result.DryRun, EditorRun.Describe(apply.Result));
            Assert.AreNotEqual(0, apply.Result.ExitCode);
            StringAssert.StartsWith("exit " + apply.Result.ExitCode + ", " + apply.Result.Summary + "\n", EditorRun.Describe(apply.Result));
        }

        [UnityTest]
        public IEnumerator ConfirmOnARunThatTakesNoApplyIsIgnored()
        {
            // N5: an Export run takes no -apply, so confirm must not add one it refuses
            Task<RunResult> run = EditorRun.RunAsync("CommandCatalogExport", null, string.Empty, true);
            yield return Wait(run);
            Assert.AreEqual(0, run.Result.ExitCode, EditorRun.Describe(run.Result));
        }
    }
}
