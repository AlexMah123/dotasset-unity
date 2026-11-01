using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DotAsset.Tests
{
    // The settle after a run that saved a scene open in the editor, outside the editor queue as the CLI adapter runs it.
    public sealed class SceneSettleTests
    {
        const string Folder = "Assets/DotAssetSettleTest";
        const string Path = Folder + "/Open.unity";
        Scene scene;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "DotAssetSettleTest");
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), Path, true));
            scene = EditorSceneManager.OpenScene(Path, OpenSceneMode.Additive);
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

        // Unity's own follow-up after the save: a change with no undo that marks the scene dirty.
        void DirtyWithoutUndo()
        {
            SceneManager.MoveGameObjectToScene(new GameObject("Late"), scene);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        static IEnumerator WaitWhileBusy()
        {
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (SceneSettle.Busy && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.IsFalse(SceneSettle.Busy, "the settle did not end within 10 seconds");
        }

        [Test]
        public void NoSavedSceneMeansNoSettle()
        {
            Assert.IsFalse(SceneSettle.Begin(new RunResult(), r => Assert.Fail("done called without a settle")));
            Assert.IsFalse(SceneSettle.Busy);
        }

        [UnityTest]
        public IEnumerator SavesAgainAfterUnityUpdatesTheScene()
        {
            SceneSession.SavedOpenScenes.Add(Path);
            RunResult done = null;
            Assert.IsTrue(SceneSettle.Begin(new RunResult(), r => done = r));
            Assert.AreEqual(0, SceneSession.SavedOpenScenes.Count, "Begin takes the saved scenes");
            DirtyWithoutUndo();
            yield return WaitWhileBusy();
            Assert.IsNotNull(done, "done was not called");
            Assert.IsFalse(scene.isDirty);
            CollectionAssert.Contains(done.Lines, "I saved " + Path + " again after Unity updated it");
            Assert.AreEqual(0, done.Warnings);
        }

        [UnityTest]
        public IEnumerator LeavesAPersonsEditUnsaved()
        {
            SceneSession.SavedOpenScenes.Add(Path);
            RunResult done = null;
            Assert.IsTrue(SceneSettle.Begin(new RunResult(), r => done = r));
            Undo.IncrementCurrentGroup();
            var go = new GameObject("Edit");
            SceneManager.MoveGameObjectToScene(go, scene);
            Undo.RegisterCreatedObjectUndo(go, "Edit");
            EditorSceneManager.MarkSceneDirty(scene);
            yield return WaitWhileBusy();
            Assert.IsNotNull(done, "done was not called");
            Assert.IsTrue(scene.isDirty, "the person's edit was saved");
            CollectionAssert.Contains(done.Lines, "W " + Path + " was edited in the editor right after the run saved it. Those changes are left unsaved");
            Assert.AreEqual(1, done.Warnings);
        }
    }
}
