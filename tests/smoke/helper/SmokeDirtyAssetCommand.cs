using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // Gives assets unsaved changes in the open editor, as editing in the Inspector would, or saves them (-save -apply).
    // Usage: dotasset.py SmokeDirtyAsset Assets/A.mat,Assets/B.controller,Assets/C.prefab [--args "-save -apply"]
    public sealed class SmokeDirtyAssetCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Smoke test only: leaves unsaved changes on assets in the editor, or saves them"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "save" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            bool save = ctx.Args.Has("save");
            foreach (string path in ctx.Assets)
            {
                Object main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null)
                {
                    ctx.FailTarget(path, "nothing at that path");
                    continue;
                }
                if (save)
                {
                    // A write, so it needs -apply like any run's, and goes through the journal first.
                    RunJournal.Keep(path);
                    if (main is GameObject)
                    {
                        PrefabUtility.SavePrefabAsset((GameObject)main);
                    }
                    else
                    {
                        AssetDatabase.SaveAssetIfDirty(main);
                    }
                    ctx.Log.Info("saved " + path);
                    continue;
                }
                var material = main as Material;
                var controller = main as AnimatorController;
                var prefab = main as GameObject;
                if (material != null)
                {
                    material.enableInstancing = !material.enableInstancing;
                }
                else if (controller != null)
                {
                    controller.AddParameter("Unsaved", AnimatorControllerParameterType.Float);
                }
                else if (prefab != null && prefab.GetComponent<Rigidbody>() != null)
                {
                    prefab.GetComponent<Rigidbody>().mass = 77;
                }
                else
                {
                    ctx.FailTarget(path, "no change defined for this kind of asset");
                    continue;
                }
                EditorUtility.SetDirty(main);
                if (!EditorUtility.IsDirty(main))
                {
                    ctx.FailTarget(path, "Unity did not mark it dirty");
                    continue;
                }
                ctx.Log.Info("left unsaved changes on " + path);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
