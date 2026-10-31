using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // A Migrate run that ignores ctx.DryRun and writes anyway: saves, creates, moves, deletes and rewrites assets in the
    // folder it is given. Without -apply the framework must refuse every one of those writes.
    // Usage: dotasset.py SmokeDryWrite --args "-folder=Assets/DotAssetSmoke"
    public sealed class SmokeDryWriteCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Smoke test only: writes during a dry run, which the framework must refuse"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "folder" }; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            string folder = ctx.Args.Get("folder") ?? "Assets/DotAssetSmoke";
            var red = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Red.mat");
            if (red == null)
            {
                ctx.Log.Info("no " + folder + "/Red.mat");
                return ExitCode.Refused;
            }

            // A save the way the package's own commands write: RunJournal.Keep first, which refuses during a dry run.
            int queue = red.renderQueue;
            red.renderQueue = queue == 2470 ? 2471 : 2470;
            EditorUtility.SetDirty(red);
            try
            {
                RunJournal.Keep(folder + "/Red.mat");
                AssetDatabase.SaveAssetIfDirty(red);
            }
            catch (InvalidOperationException e)
            {
                ctx.Log.Info("Keep threw: " + e.Message);
            }
            // Put the material back in memory, so the editor holds no unsaved change for the steps after this one.
            red.renderQueue = queue;
            EditorUtility.ClearDirty(red);

            string moved = AssetDatabase.MoveAsset(folder + "/Red.mat", folder + "/DryMoved.mat");
            ctx.Log.Info("move: " + (string.IsNullOrEmpty(moved) ? "done" : moved));
            AssetDatabase.CreateAsset(new Material(red), folder + "/DryWrite.mat");
            AssetDatabase.DeleteAsset(folder + "/Red.mat");
            try
            {
                DotAssetPaths.ReplaceText(DotAssetPaths.Absolute(folder + "/Red.mat"), "dry run\n");
            }
            catch (InvalidOperationException e)
            {
                ctx.Log.Info("ReplaceText threw: " + e.Message);
            }
            return ExitCode.Success;
        }
    }
}
