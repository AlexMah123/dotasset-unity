using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace DotAsset
{
    // Copies an asset or folder to a new path with new GUIDs, e.g. a reference copy before a risky edit.
    // Usage: dotasset.py DuplicateAsset --args '-from=Assets/Prefabs/Gun.prefab -to=Assets/Prefabs/Gun_Backup.prefab -apply'
    // Docs/Migrate.md
    public sealed class DuplicateAssetCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Copies an asset or folder to a new path with new GUIDs"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<KeyValuePair<string, string>> copies;
            if (!MoveAssetCommand.Pairs(ctx, "Copies", out copies))
            {
                return ExitCode.Refused;
            }
            foreach (var copy in copies)
            {
                string label = copy.Key + " -> " + copy.Value;
                if (!AssetDatabase.IsValidFolder(copy.Key) && AssetDatabase.GetMainAssetTypeAtPath(copy.Key) == null)
                {
                    ctx.FailTarget(label, "nothing at " + copy.Key);
                    continue;
                }
                if (AssetDatabase.IsValidFolder(copy.Value) || AssetDatabase.GetMainAssetTypeAtPath(copy.Value) != null)
                {
                    ctx.FailTarget(label, copy.Value + " already exists");
                    continue;
                }
                if (AssetDatabase.IsValidFolder(copy.Key) && copy.Value.StartsWith(copy.Key + "/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.FailTarget(label, "cannot copy a folder into itself");
                    continue;
                }
                string readOnly = ReadOnly.Reason(copy.Value);
                if (readOnly != null)
                {
                    ctx.FailTarget(label, readOnly);
                    continue;
                }
                if (ctx.DryRun)
                {
                    ctx.Log.Info("would copy " + label);
                    ctx.PlanFile("copy", copy.Key, copy.Value);
                    continue;
                }
                string error;
                CreateAssetCommand.BeginFolderTracking();
                if (!CreateAssetCommand.EnsureFolder(Path.GetDirectoryName(copy.Value).Replace('\\', '/'), out error))
                {
                    CreateAssetCommand.RemoveCreatedFolders(ctx);
                    ctx.FailTarget(label, error);
                    continue;
                }
                ctx.BeginTarget();
                if (!AssetDatabase.CopyAsset(copy.Key, copy.Value))
                {
                    CreateAssetCommand.RemoveCreatedFolders(ctx);
                    ctx.FailTarget(label, ctx.TargetFailed() ? ctx.TargetError : "Unity refused the copy");
                    continue;
                }
                ctx.WarnIfSavedWithErrors(label);
                ctx.Log.Info("copied " + label);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
