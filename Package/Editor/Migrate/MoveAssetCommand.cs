using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AssetOps
{
    // Moves or renames assets and folders through the AssetDatabase, so GUIDs and every reference survive.
    // Also flags what a GUID cannot carry: Resources.Load paths and other path strings in code.
    // Usage: assetops.py MoveAsset --args '-from=Assets/Old/Gun.prefab -to=Assets/Weapons/Gun.prefab -apply'
    //        assetops.py MoveAsset --args '-spec="C:/temp/moves.json" -apply'    {"Moves": [{"From": ..., "To": ...}]}
    // Docs/Migrate.md
    public sealed class MoveAssetCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Moves or renames assets and folders, keeping GUIDs, and flags path strings in code"; }
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
            List<KeyValuePair<string, string>> moves;
            if (!Pairs(ctx, "Moves", out moves))
            {
                return ExitCode.Refused;
            }
            string[] scripts = null;
            int moved = 0;
            // A dry run moves nothing, so it tracks the paths earlier moves would free and fill: a chain A to B,
            // B to C checks the same way it runs.
            var plan = new DryRunPaths();
            foreach (var move in moves)
            {
                string from = move.Key;
                string to = move.Value;
                string label = from + " -> " + to;
                bool isFolder = plan.IsFolder(from);
                if (!plan.Exists(from))
                {
                    ctx.FailTarget(label, "nothing at " + from);
                    continue;
                }
                if (plan.Exists(to))
                {
                    ctx.FailTarget(label, to + " already exists");
                    continue;
                }
                if (isFolder && to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.FailTarget(label, "cannot move a folder into itself");
                    continue;
                }
                string readOnly = ReadOnly.Reason(from) ?? ReadOnly.Reason(to);
                if (readOnly != null)
                {
                    ctx.FailTarget(label, readOnly);
                    continue;
                }
                string parent = Path.GetDirectoryName(to).Replace('\\', '/');
                if (!plan.IsFolder(parent))
                {
                    ctx.Log.Info((ctx.DryRun ? "would create folder " : "creating folder ") + parent);
                }
                else if (!plan.Touches(from) && !plan.Touches(to))
                {
                    string problem = AssetDatabase.ValidateMoveAsset(from, to);
                    if (!string.IsNullOrEmpty(problem))
                    {
                        ctx.FailTarget(label, problem);
                        continue;
                    }
                }

                scripts = scripts ?? AssetScan.All(new[] { "Assets" }, p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
                WarnPathStrings(ctx, from, to, isFolder, scripts);

                if (ctx.DryRun)
                {
                    ctx.Log.Info("would move " + label);
                    plan.Move(from, to);
                    continue;
                }
                string error;
                CreateAssetCommand.BeginFolderTracking();
                if (!CreateAssetCommand.EnsureFolder(parent, out error))
                {
                    CreateAssetCommand.RemoveCreatedFolders(ctx);
                    ctx.FailTarget(label, error);
                    continue;
                }
                ctx.BeginTarget();
                string result = AssetDatabase.MoveAsset(from, to);
                if (!string.IsNullOrEmpty(result))
                {
                    CreateAssetCommand.RemoveCreatedFolders(ctx);
                    ctx.FailTarget(label, result);
                    continue;
                }
                CreateAssetCommand.BeginFolderTracking();
                ctx.WarnIfSavedWithErrors(label);
                moved++;
                ctx.Log.Info("moved " + label);
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("moved " + moved + " of " + moves.Count);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // GUIDs keep serialized references intact. Strings do not: a Resources.Load("Weapons/Gun") call,
        // or an asset path in code, breaks silently when the file moves.
        static void WarnPathStrings(RunContext ctx, string from, string to, bool isFolder, string[] scripts)
        {
            var needles = new List<string> { from };
            string resourcesPath = ResourcesRelative(from, isFolder);
            if (resourcesPath != null && resourcesPath != ResourcesRelative(to, isFolder))
            {
                needles.Add("\"" + resourcesPath + "\"");
                needles.Add("\"" + resourcesPath + "/");
            }
            foreach (string script in scripts)
            {
                string text;
                try
                {
                    text = File.ReadAllText(AssetOpsPaths.Absolute(script));
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (string needle in needles)
                {
                    if (text.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    {
                        ctx.Log.Warn(script + " mentions " + needle + " as a string, it will not follow the move");
                        break;
                    }
                }
            }
        }

        // "Assets/Art/Resources/Weapons/Gun.prefab" -> "Weapons/Gun"
        static string ResourcesRelative(string path, bool isFolder)
        {
            int index = path.LastIndexOf("/Resources/", StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }
            string rest = path.Substring(index + "/Resources/".Length);
            if (!isFolder)
            {
                string ext = Path.GetExtension(rest);
                rest = rest.Substring(0, rest.Length - ext.Length);
            }
            return rest;
        }

        // The project as a dry run's earlier moves would leave it, contents of moved folders included. In a real run
        // nothing is recorded and every question goes straight to the AssetDatabase.
        sealed class DryRunPaths
        {
            readonly List<KeyValuePair<string, string>> moves = new List<KeyValuePair<string, string>>();

            public void Move(string from, string to)
            {
                moves.Add(new KeyValuePair<string, string>(from, to));
            }

            static bool Within(string path, string root, out string rest)
            {
                rest = null;
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                {
                    rest = string.Empty;
                    return true;
                }
                if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    rest = path.Substring(root.Length);
                    return true;
                }
                return false;
            }

            // Where path's content sits in the real project now, or null when an earlier move took it away.
            string Original(string path)
            {
                for (int i = moves.Count - 1; i >= 0; i--)
                {
                    string rest;
                    if (Within(path, moves[i].Value, out rest))
                    {
                        path = moves[i].Key + rest;
                    }
                    else if (Within(path, moves[i].Key, out rest))
                    {
                        return null;
                    }
                }
                return path;
            }

            public bool Touches(string path)
            {
                return Original(path) != path;
            }

            public bool Exists(string path)
            {
                string original = Original(path);
                return original != null && (AssetDatabase.IsValidFolder(original) || AssetDatabase.GetMainAssetTypeAtPath(original) != null);
            }

            public bool IsFolder(string path)
            {
                string original = Original(path);
                return original != null && AssetDatabase.IsValidFolder(original);
            }
        }

        // -from/-to, or a spec array of {"From", "To"}. Both ends must be under Assets/ or Packages/.
        public static bool Pairs(RunContext ctx, string specKey, out List<KeyValuePair<string, string>> pairs)
        {
            if (!PairsRaw(ctx, specKey, out pairs))
            {
                return false;
            }
            foreach (var pair in pairs)
            {
                foreach (string end in new[] { pair.Key, pair.Value })
                {
                    if (!end.StartsWith("Assets/", StringComparison.Ordinal) && !end.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        ctx.Log.Error("'" + end + "' is not a path under Assets/ or Packages/");
                        return false;
                    }
                }
            }
            return true;
        }

        static bool PairsRaw(RunContext ctx, string specKey, out List<KeyValuePair<string, string>> pairs)
        {
            pairs = new List<KeyValuePair<string, string>>();
            string from = ctx.Args.Get("from");
            string to = ctx.Args.Get("to");
            if (from != null || to != null)
            {
                if (from == null || to == null)
                {
                    ctx.Log.Error("give both -from and -to");
                    return false;
                }
                pairs.Add(new KeyValuePair<string, string>(AssetOpsPaths.Normalize(from).TrimEnd('/'), AssetOpsPaths.Normalize(to).TrimEnd('/')));
            }
            if (ctx.Args.Has("spec"))
            {
                JObject spec;
                if (!Spec.Load(ctx, out spec))
                {
                    return false;
                }
                JArray list = Spec.Targets(ctx, spec, specKey);
                if (list == null)
                {
                    return false;
                }
                foreach (JToken entry in list)
                {
                    if (!Spec.CheckKeys(ctx, entry, specKey + " entry", "From", "To"))
                    {
                        return false;
                    }
                    string f = Json.Str(entry, "From");
                    string t = Json.Str(entry, "To");
                    if (f == null || t == null)
                    {
                        ctx.Log.Error("every " + specKey + " entry needs \"From\" and \"To\"");
                        return false;
                    }
                    pairs.Add(new KeyValuePair<string, string>(AssetOpsPaths.Normalize(f).TrimEnd('/'), AssetOpsPaths.Normalize(t).TrimEnd('/')));
                }
            }
            if (pairs.Count == 0)
            {
                ctx.Log.Error(ctx.RunName + " needs -from and -to, or -spec with a \"" + specKey + "\" array");
                return false;
            }
            return true;
        }
    }
}
