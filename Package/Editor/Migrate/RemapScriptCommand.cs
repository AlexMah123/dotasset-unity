using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Points every MonoBehaviour and ScriptableObject that uses one script at another, by rewriting the m_Script
    // GUID in the YAML. This is the fix for "Missing Script" after a .cs file lost its .meta, or for moving a class
    // into another file or assembly. The old script does not need to exist: AuditMissingReference lists the GUIDs.
    // Needs Asset Serialization Mode = Force Text. Field data is kept as long as the new class has the same fields.
    // Usage: assetops.py RemapScript --args '-from=<old guid or script path> -to=Assets/Scripts/New.cs -apply'
    // Docs/Migrate.md
    public sealed class RemapScriptCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Repoints components and ScriptableObjects from one script GUID to another script"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "scandir", "fromfileid" }; }
        }

        static string OpenInEditor(string path)
        {
            if (Application.isBatchMode)
            {
                return null;
            }
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                return scene.IsValid() && scene.isLoaded ? "the scene is open in the editor, close it first" : null;
            }
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == path)
            {
                return "the prefab is open in Prefab Mode, close it first";
            }
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o != null && EditorUtility.IsDirty(o))
                {
                    return "it has unsaved changes in the editor, save or discard them first";
                }
            }
            return null;
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

            // m_Script: {fileID: 11500000, guid: <old>, type: 3}
            var pattern = new Regex(@"(m_Script:\s*\{fileID:\s*)" + Regex.Escape(fromFileId) + @"(,\s*guid:\s*)" + oldGuid + @"(,\s*type:\s*3\})", RegexOptions.Compiled);
            string replacement = "${1}" + newFileId + "${2}" + newGuid + "${3}";

            var files = AssetScan.All(AssetScan.Roots(ctx.Args), p => AssetScan.HasExtension(p, AssetScan.YamlExtensions));
            int changedFiles = 0;
            int changedRefs = 0;
            var toImport = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, files.Count, path);
                }
                string abs = AssetOpsPaths.Absolute(path);
                string text;
                try
                {
                    text = File.ReadAllText(abs);
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                    continue;
                }
                if (text.IndexOf(oldGuid, StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                int hits = pattern.Matches(text).Count;
                if (hits == 0)
                {
                    continue;
                }
                string busy = OpenInEditor(path);
                if (busy != null)
                {
                    // The file is rewritten on disk. A loaded copy would keep the old GUID and write it back on its next save.
                    ctx.FailTarget(path, busy);
                    continue;
                }
                changedFiles++;
                changedRefs += hits;
                ctx.Log.Info((ctx.DryRun ? "would repoint " : "repointed ") + hits + " script reference(s) in " + path);
                if (ctx.DryRun)
                {
                    continue;
                }
                try
                {
                    File.WriteAllText(abs, pattern.Replace(text, replacement), new UTF8Encoding(false));
                    toImport.Add(path);
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                }
            }
            foreach (string path in toImport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            ctx.Log.Info((ctx.DryRun ? "would change " : "changed ") + changedRefs + " reference(s) in " + changedFiles + " file(s) to " + to);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
