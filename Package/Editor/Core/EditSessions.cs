using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // All-or-nothing writes. Each session holds one target. Save() writes it, disposing without Save() drops every change.

    // A prefab loaded into an isolated preview scene. Dropping it is free, nothing touched the asset.
    public sealed class PrefabSession : IDisposable
    {
        public GameObject Root { get; private set; }
        public string Path { get; private set; }

        PrefabSession()
        {
        }

        public static PrefabSession Open(string path, out string error)
        {
            error = null;
            var asset = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (asset == null)
            {
                error = "no prefab at " + path;
                return null;
            }
            PrefabAssetType type = PrefabUtility.GetPrefabAssetType(asset);
            if (type == PrefabAssetType.Model)
            {
                error = path + " is a model, its hierarchy comes from the importer. Make a prefab variant of it to edit";
                return null;
            }
            if (type == PrefabAssetType.NotAPrefab || type == PrefabAssetType.MissingAsset)
            {
                error = path + " is not an editable prefab";
                return null;
            }
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == path && stage.scene.isDirty)
            {
                error = path + " is open in Prefab Mode with unsaved changes. Save or discard them first";
                return null;
            }
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                error = "Unity could not load the contents of " + path;
                return null;
            }
            return new PrefabSession { Root = root, Path = path };
        }

        public bool Save(out string error)
        {
            error = null;
            bool success;
            PrefabUtility.SaveAsPrefabAsset(Root, Path, out success);
            if (!success)
            {
                error = "Unity refused to save " + Path;
            }
            return success;
        }

        public void Dispose()
        {
            if (Root != null)
            {
                PrefabUtility.UnloadPrefabContents(Root);
                Root = null;
            }
        }
    }

    // A scene edited in place.
    //   Not loaded: opened additively, closed without saving when dropped.
    //   Loaded in the editor: every change is recorded on one undo group, dropped by reverting the group,
    //   and after Save() the whole run is a single Ctrl+Z.
    public sealed class SceneSession : IDisposable
    {
        public Scene Scene { get; private set; }
        public bool OpenedByUs { get; private set; }
        public bool UseUndo
        {
            get { return !OpenedByUs; }
        }

        int undoGroup;
        bool saved;
        bool wasListed;
        Scene previousActive;

        SceneSession()
        {
        }

        public static SceneSession Open(string path, string label, out string error)
        {
            error = null;
            if (AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(SceneAsset))
            {
                error = "no scene at " + path;
                return null;
            }
            var session = new SceneSession();
            Scene scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded)
            {
                if (scene.isDirty)
                {
                    error = path + " is open in the editor with unsaved changes. Save or discard them first";
                    return null;
                }
                session.OpenedByUs = false;
            }
            else
            {
                // Listed in the Hierarchy but unloaded: load it for the run, and leave it listed afterwards.
                session.wasListed = scene.IsValid();
                scene = OpenForRun(path, out error);
                if (!scene.IsValid())
                {
                    return null;
                }
                session.OpenedByUs = true;
            }
            session.Scene = scene;
            // New GameObjects land in the active scene. Make it this one, so creating objects never touches
            // another open scene, then hand the active scene back when the session ends.
            session.previousActive = SceneManager.GetActiveScene();
            if (session.previousActive != scene)
            {
                SceneManager.SetActiveScene(scene);
            }
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("AssetOps: " + label);
            session.undoGroup = Undo.GetCurrentGroup();
            return session;
        }

        public bool Save(out string error)
        {
            error = null;
            if (!EditorSceneManager.SaveScene(Scene))
            {
                error = "Unity refused to save " + Scene.path;
                return false;
            }
            saved = true;
            if (!OpenedByUs)
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
            return true;
        }

        // Headless there is nobody's layout to keep, the scene replaces the untitled one.
        // In the editor it opens next to the user's scenes and closes again afterwards.
        public static Scene OpenForRun(string path, out string error)
        {
            error = null;
            Scene scene;
            try
            {
                scene = EditorSceneManager.OpenScene(path, Application.isBatchMode ? OpenSceneMode.Single : OpenSceneMode.Additive);
            }
            catch (Exception e)
            {
                error = "Unity could not open " + path + ": " + e.Message;
                return default(Scene);
            }
            if (!scene.IsValid())
            {
                error = "Unity could not open " + path + (Application.isBatchMode ? string.Empty : ". If the editor shows an Untitled scene, save it first, Unity cannot open a scene next to it");
            }
            return scene;
        }

        // keepListed: the scene was in the Hierarchy (unloaded) before the run, unload it but keep the entry.
        public static void CloseAfterRun(Scene scene, bool keepListed = false)
        {
            if (!scene.IsValid())
            {
                return;
            }
            if (Application.isBatchMode)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            else
            {
                EditorSceneManager.CloseScene(scene, !keepListed);
            }
        }

        public void Dispose()
        {
            if (previousActive.IsValid() && previousActive.isLoaded && previousActive != Scene)
            {
                SceneManager.SetActiveScene(previousActive);
            }
            if (OpenedByUs)
            {
                CloseAfterRun(Scene, wasListed);
                return;
            }
            if (!saved)
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }
    }

    public static class GameObjects
    {
        // A new GameObject placed straight into the target scene. Created hidden and not saved first, so the
        // scene that happens to be active is never touched, and without the editor's default parent.
        public static GameObject Spawn(string name, Scene target)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
            if (target.IsValid() && go.scene != target)
            {
                SceneManager.MoveGameObjectToScene(go, target);
            }
            go.hideFlags = HideFlags.None;
            return go;
        }
    }

    // Somewhere to build a new prefab without touching the user's scenes.
    // Headless the active scene is the empty untitled one, nobody's work, so nothing is set up.
    // In the editor a new empty scene opens next to the user's and becomes active while the prefab is built;
    // when Unity refuses that (an Untitled scene is open) a preview scene takes the objects instead.
    public sealed class ScratchScene : IDisposable
    {
        Scene scene;
        Scene previous;
        bool preview;
        bool disposed;

        public ScratchScene()
        {
            if (Application.isBatchMode)
            {
                return;
            }
            previous = SceneManager.GetActiveScene();
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
            }
            catch (Exception)
            {
                scene = EditorSceneManager.NewPreviewScene();
                preview = true;
            }
        }

        public Scene Scene
        {
            get { return scene; }
        }

        // A root GameObject in the scratch scene, or in the active (untitled) scene headless.
        public GameObject Spawn(string name)
        {
            return GameObjects.Spawn(name, scene.IsValid() ? scene : SceneManager.GetActiveScene());
        }

        public void Dispose()
        {
            if (disposed || Application.isBatchMode)
            {
                return;
            }
            disposed = true;
            if (preview)
            {
                EditorSceneManager.ClosePreviewScene(scene);
                return;
            }
            if (previous.IsValid() && previous.isLoaded)
            {
                SceneManager.SetActiveScene(previous);
            }
            if (scene.IsValid())
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    // Any other asset object. A clone taken up front is copied back when the session is dropped.
    // Importers are snapshotted through EditorJsonUtility instead, Object.Instantiate does not clone them.
    public sealed class AssetSnapshot : IDisposable
    {
        readonly Object target;
        readonly Object clone;
        readonly string json;
        readonly string name;
        readonly HideFlags hideFlags;
        bool committed;

        public AssetSnapshot(Object target)
        {
            this.target = target;
            name = target.name;
            hideFlags = target.hideFlags;
            if (target is AssetImporter)
            {
                json = EditorJsonUtility.ToJson(target);
            }
            else
            {
                clone = Object.Instantiate(target);
                clone.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        public void Commit()
        {
            committed = true;
        }

        public void Dispose()
        {
            if (!committed && target != null)
            {
                if (json != null)
                {
                    EditorJsonUtility.FromJsonOverwrite(json, target);
                }
                else if (clone != null)
                {
                    EditorUtility.CopySerialized(clone, target);
                    // The clone is named "<name>(Clone)" and hidden, CopySerialized may carry both over.
                    target.name = name;
                    target.hideFlags = hideFlags;
                }
                EditorUtility.ClearDirty(target);
            }
            if (clone != null)
            {
                Object.DestroyImmediate(clone);
            }
        }
    }
}
