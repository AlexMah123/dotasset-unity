using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // Waits for the next save of an open scene, then, while the run that saved it settles, renames its first root
    // object as a person would (-edit) or reloads the scripting domain (-reload).
    // Usage: dotasset.py SmokeAfterSave Assets/S.unity --args "-edit"
    public sealed class SmokeAfterSaveCommand : DotAssetCommand
    {
        public const string Marker = "SmokeEdited";

        static string watched;
        static DateTime savedAt;
        static bool reload;

        public override string Summary
        {
            get { return "Smoke test only: edits an open scene, or reloads scripts, right after its next save"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "edit", "reload" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (ctx.Assets.Count != 1 || ctx.Args.Has("edit") == ctx.Args.Has("reload"))
            {
                ctx.Log.Error("give one open scene and either -edit or -reload");
                return ExitCode.Refused;
            }
            string path = ctx.Assets[0];
            if (!SceneManager.GetSceneByPath(path).isLoaded)
            {
                ctx.FailTarget(path, "not open in the editor");
                return ExitCode.Failed;
            }
            watched = path;
            savedAt = File.GetLastWriteTimeUtc(path);
            reload = ctx.Args.Has("reload");
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
            ctx.Log.Info("waiting for the next save of " + path);
            return ExitCode.Success;
        }

        static void Watch()
        {
            if (File.GetLastWriteTimeUtc(watched) == savedAt)
            {
                return;
            }
            EditorApplication.update -= Watch;
            if (reload)
            {
                EditorUtility.RequestScriptReload();
                return;
            }
            Scene scene = SceneManager.GetSceneByPath(watched);
            GameObject root = scene.GetRootGameObjects()[0];
            // A click or key press starts a new undo group before the edit, as here.
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(root, "Rename");
            root.name += " " + Marker;
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
