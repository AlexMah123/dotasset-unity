using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Points every MonoBehaviour and ScriptableObject that uses one script at another, by rewriting the m_Script
    // GUID in the YAML. This is the fix for "Missing Script" after a .cs file lost its .meta, or for moving a class
    // into another file or assembly. The old script does not need to exist: AuditMissingReference lists the GUIDs.
    // Needs Asset Serialization Mode = Force Text. Field data is kept as long as the new class has the same fields.
    // Usage: dotasset.py RemapScript --args '-from=<old guid or script path> -to=Assets/Scripts/New.cs -apply'
    // Docs/Migrate.md
    public sealed class RemapScriptCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Repoints components and ScriptableObjects from one script GUID to another script"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "scandir", "fromfileid" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (!AssetScan.IsForceText())
            {
                ctx.Log.Error("RemapScript rewrites YAML, set Project Settings > Editor > Asset Serialization Mode to Force Text first");
                return ExitCode.Refused;
            }
            string from = ctx.Args.Get("from");
            string to = ctx.Args.Get("to");
            if (from == null || to == null)
            {
                ctx.Log.Error("RemapScript needs -from=<old script GUID or path> and -to=<new script path>");
                return ExitCode.Refused;
            }
            string oldGuid = Regex.IsMatch(from, "^[0-9a-fA-F]{32}$") ? from.ToLowerInvariant() : AssetDatabase.AssetPathToGUID(from);
            if (string.IsNullOrEmpty(oldGuid))
            {
                ctx.Log.Error("-from is neither a 32-digit GUID nor a script path: " + from);
                return ExitCode.Refused;
            }
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(to);
            if (script == null)
            {
                ctx.Log.Error("-to: no script at " + to);
                return ExitCode.Refused;
            }
            if (script.GetClass() == null)
            {
                ctx.Log.Error(to + " defines no class Unity can serialize (the class must match the file name and compile)");
                return ExitCode.Refused;
            }
            string newGuid;
            long newFileId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out newGuid, out newFileId))
            {
                ctx.Log.Error("cannot read the GUID of " + to);
                return ExitCode.Refused;
            }
            if (newGuid == oldGuid)
            {
                ctx.Log.Error("-from and -to are the same script");
                return ExitCode.Refused;
            }
            string fromFileId = ctx.Args.Get("fromfileid", "11500000");

            Regex pattern = Pattern(fromFileId, oldGuid);
            string replacement = Replacement(newFileId, newGuid);

            var files = AssetScan.All(AssetScan.Roots(ctx.Args), p => AssetScan.HasExtension(p, AssetScan.YamlExtensions));
            int changedFiles, changedRefs;
            YamlRewrite.Files(ctx, files, true,
                (string text, out int hits, out string conflict) =>
                {
                    conflict = null;
                    hits = text.IndexOf(oldGuid, StringComparison.Ordinal) < 0 ? 0 : pattern.Matches(text).Count;
                    return hits == 0 ? text : pattern.Replace(text, replacement);
                },
                hits => (ctx.DryRun ? "would repoint " : "repointed ") + hits + " script reference(s)",
                hits => hits + " script reference(s) to " + to,
                out changedFiles, out changedRefs);
            ctx.Log.Info((ctx.DryRun ? "would change " : "changed ") + changedRefs + " reference(s) in " + changedFiles + " file(s) to " + to);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // m_Script: {fileID: 11500000, guid: <old>, type: 3}
        internal static Regex Pattern(string fromFileId, string oldGuid)
        {
            return new Regex(@"(m_Script:\s*\{fileID:\s*)" + Regex.Escape(fromFileId) + @"(,\s*guid:\s*)" + oldGuid + @"(,\s*type:\s*3\})", RegexOptions.Compiled);
        }

        internal static string Replacement(long newFileId, string newGuid)
        {
            return "${1}" + newFileId + "${2}" + newGuid + "${3}";
        }
    }
}
