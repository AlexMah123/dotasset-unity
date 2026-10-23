using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace AssetWorkbench
{
    // Copies an asset or folder to a new path with new GUIDs, e.g. a reference copy before a risky edit.
    // Usage: uaw.py DuplicateAsset --args '-from=Assets/Prefabs/Gun.prefab -to=Assets/Prefabs/Gun_Backup.prefab -apply'
    // Docs/Migrate.md
    public sealed class DuplicateAssetCommand : WorkbenchCommand
    {
        public override string Summary
        {
            get { return "Copies an asset or folder to a new path with new GUIDs"; }
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
                if (ctx.DryRun)
                {
                    ctx.Log.Info("would copy " + label);
                    continue;
                }
                string error;
                if (!CreateAssetCommand.EnsureFolder(Path.GetDirectoryName(copy.Value).Replace('\\', '/'), out error))
                {
                    ctx.FailTarget(label, error);
                    continue;
                }
                if (!AssetDatabase.CopyAsset(copy.Key, copy.Value))
                {
                    ctx.FailTarget(label, "Unity refused the copy");
                    continue;
                }
                ctx.Log.Info("copied " + label);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
