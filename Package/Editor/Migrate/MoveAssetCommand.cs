using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Assetops
{
    // Moves or renames assets and folders through the AssetDatabase, so GUIDs and every reference survive.
    // Also flags what a GUID cannot carry: Resources.Load paths and other path strings in code.
    // Usage: assetops.py MoveAsset --args '-from=Assets/Old/Gun.prefab -to=Assets/Weapons/Gun.prefab -apply'
    //        assetops.py MoveAsset --args '-spec="C:/temp/moves.json" -apply'    {"Moves": [{"From": ..., "To": ...}]}
    // Docs/Migrate.md
    public sealed class MoveAssetCommand : AssetopsCommand
    {
        public override string Summary
        {
            get { return "Moves or renames assets and folders, keeping GUIDs, and flags path strings in code"; }
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
            foreach (var move in moves)
            {
                string from = move.Key;
                string to = move.Value;
                string label = from + " -> " + to;
                bool isFolder = AssetDatabase.IsValidFolder(from);
                if (!isFolder && AssetDatabase.GetMainAssetTypeAtPath(from) == null)
                {
                    ctx.FailTarget(label, "nothing at " + from);
                    continue;
                }
                if (AssetDatabase.IsValidFolder(to) || AssetDatabase.GetMainAssetTypeAtPath(to) != null)
                {
                    ctx.FailTarget(label, to + " already exists");
                    continue;
                }
                string parent = Path.GetDirectoryName(to).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent))
                {
                    ctx.Log.Info((ctx.DryRun ? "would create folder " : "creating folder ") + parent);
                }
                else
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
                    continue;
                }
                string error;
                if (!CreateAssetCommand.EnsureFolder(parent, out error))
                {
                    ctx.FailTarget(label, error);
                    continue;
                }
                string result = AssetDatabase.MoveAsset(from, to);
                if (!string.IsNullOrEmpty(result))
                {
                    ctx.FailTarget(label, result);
                    continue;
                }
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
                    text = File.ReadAllText(AssetopsPaths.Absolute(script));
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
                pairs.Add(new KeyValuePair<string, string>(AssetopsPaths.Normalize(from).TrimEnd('/'), AssetopsPaths.Normalize(to).TrimEnd('/')));
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
                    pairs.Add(new KeyValuePair<string, string>(AssetopsPaths.Normalize(f).TrimEnd('/'), AssetopsPaths.Normalize(t).TrimEnd('/')));
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
