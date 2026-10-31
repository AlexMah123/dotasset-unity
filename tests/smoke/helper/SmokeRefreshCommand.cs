using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // Imports what the smoke test wrote to disk while the editor is open, as a focused editor would on its own.
    // -check refreshes nothing and fails while a compile runs, or a script under the given folder has no class or was
    // written after the scripts now loaded were compiled.
    // Usage: dotasset.py SmokeRefresh --args "[-check -folder=Assets/DotAssetSmoke]"
    [InitializeOnLoad]
    public sealed class SmokeRefreshCommand : DotAssetCommand
    {
        // Set again by every domain reload, which follows every compile.
        static readonly DateTime LoadedAt = DateTime.UtcNow;

        static SmokeRefreshCommand()
        {
        }

        public override string Summary
        {
            get { return "Smoke test only: refreshes the AssetDatabase, or checks the scripts it wrote have compiled"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "check", "folder" }; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (!ctx.Args.Has("check"))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ctx.Log.Info("refreshed");
                return ExitCode.Success;
            }
            if (EditorApplication.isCompiling)
            {
                ctx.Log.Info("still compiling");
                return ExitCode.Failed;
            }
            string folder = ctx.Args.Get("folder") ?? "Assets";
            List<string> waiting = AssetDatabase.FindAssets("t:MonoScript", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                    && (AssetDatabase.LoadAssetAtPath<MonoScript>(p).GetClass() == null
                        || File.GetLastWriteTimeUtc(DotAssetPaths.Absolute(p)) >= LoadedAt))
                .ToList();
            if (waiting.Count > 0)
            {
                ctx.Log.Info("not compiled yet: " + string.Join(", ", waiting.ToArray()));
                return ExitCode.Failed;
            }
            ctx.Log.Info("every script under " + folder + " has its class");
            return ExitCode.Success;
        }
    }
}
