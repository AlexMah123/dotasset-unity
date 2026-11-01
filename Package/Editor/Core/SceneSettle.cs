using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace DotAsset
{
    // A run that saved a scene open in the editor is reported once Unity has settled: some of what was just added
    // is completed by Unity on a later frame (a new Light gets its baking state) and that marks the scene dirty
    // again. Each update saves such a scene again. The run is done after QuietSeconds without a change, at most
    // MaxSeconds after it ended. Callers wait for Busy to clear before the next run, so it never finds the scene dirty.
    // Unity's follow-up records no undo, a person's edit starts a new undo group. Once the group moves, nothing
    // is saved again and the run ends at once: the scene now holds their unsaved work, not the run's.
    // Used by the editor queue (TaskQueue) and by any other caller that runs inside the open editor.
    internal static class SceneSettle
    {
        sealed class Settling
        {
            public RunResult Result;
            public Action<RunResult> Done;
            public List<string> Scenes;
            public double Started;
            public double LastDirty;
            public int UndoGroup;
            public readonly List<string> SavedAgain = new List<string>();
        }

        const double QuietSeconds = 0.4;
        const double MaxSeconds = 3.0;
        static Settling current;

        public static bool Busy
        {
            get { return current != null; }
        }

        // Takes the scenes the run just saved (SceneSession.SavedOpenScenes). Returns false when there are none,
        // done is then never called. Otherwise done gets the result, with what the settle did added, once the
        // scenes have settled. A domain reload in the meantime drops the settle and done is never called.
        public static bool Begin(RunResult result, Action<RunResult> done)
        {
            if (SceneSession.SavedOpenScenes.Count == 0)
            {
                return false;
            }
            if (current != null)
            {
                throw new InvalidOperationException("a run started while the previous one was still settling");
            }
            double now = EditorApplication.timeSinceStartup;
            current = new Settling
            {
                Result = result,
                Done = done,
                Scenes = new List<string>(SceneSession.SavedOpenScenes),
                Started = now,
                LastDirty = now,
                UndoGroup = Undo.GetCurrentGroup(),
            };
            SceneSession.SavedOpenScenes.Clear();
            EditorApplication.update += Tick;
            return true;
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            Settling settling = current;
            bool edited = Undo.GetCurrentGroup() != settling.UndoGroup;
            var leftDirty = new List<string>();
            foreach (string path in settling.Scenes)
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                {
                    if (edited)
                    {
                        leftDirty.Add(path);
                        continue;
                    }
                    settling.LastDirty = now;
                    if (EditorSceneManager.SaveScene(scene) && !settling.SavedAgain.Contains(path))
                    {
                        settling.SavedAgain.Add(path);
                    }
                    RunJournal.Rehash(path);
                }
            }
            if (!edited && now - settling.LastDirty < QuietSeconds && now - settling.Started < MaxSeconds)
            {
                return;
            }
            foreach (string path in settling.SavedAgain)
            {
                settling.Result.Lines.Add("I saved " + path + " again after Unity updated it");
            }
            foreach (string path in leftDirty)
            {
                settling.Result.Lines.Add("W " + path + " was edited in the editor right after the run saved it. Those changes are left unsaved");
                settling.Result.Warnings++;
            }
            EditorApplication.update -= Tick;
            current = null;
            settling.Done(settling.Result);
        }
    }
}
