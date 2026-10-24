using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Finds references that point at nothing.
    //   R1  a GUID that no asset in the project has (the asset was deleted, or its .meta lost)
    //   R2  a script GUID that no script has: a Missing Script component or ScriptableObject
    //   R3  a reference that loads as nothing (-deep, loads every asset): catches deleted sub-objects inside
    //       existing assets, which the text pass cannot see, and repeats R1 hits
    //   R4  a prefab instance whose source prefab is missing (-deep)
    // The text pass reads YAML (Force Text) without loading anything, the -deep pass loads each asset.
    // Fix spec: R2 entries grouped by GUID, for RemapScript or RemoveMissingScripts.
    // Usage: assetops.py AuditMissingReference [Assets/Prefabs,...] --args "-scandir=Assets -deep"
    // Docs/Audit.md
    public sealed class AuditMissingReferenceCommand : AssetOpsCommand
    {
        static readonly Regex Reference = new Regex(@"\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]{32}),\s*type:\s*(\d+)\}", RegexOptions.Compiled);
        static readonly Regex DocHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

        public override string Summary
        {
            get { return "Missing assets, missing scripts, missing sub-objects and missing prefab sources"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "deep" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("R1", "reference to a GUID no asset has");
            report.Rule("R2", "Missing Script: script GUID no script has");
            report.Rule("R3", "reference that loads as nothing: a deleted sub-object, or the R1 cases again when both passes run");
            report.Rule("R4", "prefab instance whose source prefab is missing");

            Func<string, bool> yaml = p => AssetScan.HasExtension(p, AssetScan.YamlExtensions);
            List<string> files = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, yaml, ctx.Log) : AssetScan.All(AssetScan.Roots(ctx.Args), yaml);
            report.Checked = files.Count;
            var missingScripts = new Dictionary<string, List<string>>();

            if (AssetScan.IsForceText())
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 100 == 0)
                    {
                        ctx.Progress(i, files.Count, files[i]);
                    }
                    TextPass(ctx, files[i], report, missingScripts);
                }
            }
            else
            {
                ctx.Log.Warn("Asset Serialization Mode is not Force Text, the GUID pass is skipped and -deep is on");
            }

            if (ctx.Args.Has("deep") || !AssetScan.IsForceText())
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ctx.Progress(i, files.Count, "deep: " + files[i]);
                    DeepPass(ctx, files[i], report);
                }
            }

            if (missingScripts.Count > 0)
            {
                var groups = new JArray();
                foreach (var kv in missingScripts.OrderByDescending(kv => kv.Value.Count))
                {
                    groups.Add(new JObject
                    {
                        ["Guid"] = kv.Key,
                        ["Files"] = new JArray(kv.Value.Distinct().OrderBy(f => f).Select(f => (object)f).ToArray()),
                        ["RemapScript"] = "-from=" + kv.Key + " -to=<the script that should be there> -apply",
                    });
                }
                report.Extra["MissingScripts"] = groups;
                var fixFiles = missingScripts.Values.SelectMany(v => v).Distinct()
                    .Where(f => AssetScan.HasExtension(f, ".prefab", ".unity")).OrderBy(f => f).Select(f => (object)f).ToArray();
                if (fixFiles.Length > 0)
                {
                    // Only ScriptableObjects or controllers affected: nothing for RemoveMissingScripts, RemapScript is the fix.
                    report.FixSpec = new JObject
                    {
                        ["// note"] = "Prefabs and scenes with Missing Script components, for RemoveMissingScripts. ScriptableObjects and controllers cannot lose the component, RemapScript their GUIDs (MissingScripts in the report).",
                        ["Files"] = new JArray(fixFiles),
                    };
                    report.FixRun = "RemoveMissingScripts";
                }
            }
            return report.Finish();
        }

        static void TextPass(RunContext ctx, string path, AuditReport report, Dictionary<string, List<string>> missingScripts)
        {
            string text;
            try
            {
                text = File.ReadAllText(AssetOpsPaths.Absolute(path));
            }
            catch (Exception e)
            {
                ctx.Log.Warn("cannot read " + path + ": " + e.Message);
                return;
            }
            if (!text.StartsWith("%YAML", StringComparison.Ordinal))
            {
                return;
            }
            var reported = new HashSet<string>();
            var headers = DocHeader.Matches(text).Cast<Match>().ToList();
            foreach (Match m in Reference.Matches(text))
            {
                string guid = m.Groups[2].Value;
                if (guid.StartsWith("0000000000000000", StringComparison.Ordinal))
                {
                    // Built-in resources.
                    continue;
                }
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                {
                    continue;
                }
                int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                string line = text.Substring(lineStart, m.Index - lineStart).Trim();
                bool isScript = line.StartsWith("m_Script:", StringComparison.Ordinal);
                string field = line.TrimEnd(':', ' ', '-');
                string doc = headers.LastOrDefault(h => h.Index < m.Index)?.Groups[2].Value;
                string key = guid + "|" + (isScript ? doc : field);
                if (!reported.Add(key))
                {
                    continue;
                }
                if (isScript)
                {
                    List<string> list;
                    if (!missingScripts.TryGetValue(guid, out list))
                    {
                        list = new List<string>();
                        missingScripts.Add(guid, list);
                    }
                    list.Add(path);
                    JObject issue = report.Add("R2", "Error", path, "Missing Script (GUID " + guid + ")");
                    issue["Guid"] = guid;
                    issue["LocalId"] = doc;
                }
                else
                {
                    JObject issue = report.Add("R1", "Error", path, (field.Length > 0 ? field : "a field") + " references GUID " + guid + ", which no asset has");
                    issue["Guid"] = guid;
                    issue["FileID"] = m.Groups[1].Value;
                    issue["Field"] = field;
                    issue["LocalId"] = doc;
                }
            }
        }

        static void DeepPass(RunContext ctx, string path, AuditReport report)
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                // Scenes need opening, the text pass covers their GUIDs. A deep scene pass is SceneExport plus a grep for "Missing".
                return;
            }
            Object[] objects;
            using (RunDispatcher.MuteEngineLog())
            {
                objects = AssetDatabase.LoadAllAssetsAtPath(path);
            }
            foreach (Object o in objects)
            {
                if (o == null)
                {
                    continue;
                }
                var go = o as GameObject;
                if (go != null && PrefabUtility.IsAnyPrefabInstanceRoot(go) && PrefabUtility.IsPrefabAssetMissing(go))
                {
                    report.Add("R4", "Error", path, HierarchyPath.Of(go) + " is an instance of a prefab that no longer exists");
                }
                var so = new SerializedObject(o);
                SerializedProperty it = so.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }
                    if (ObjectRefs.IsMissing(it))
                    {
                        JObject issue = report.Add("R3", "Error", path, PropertyIO.Describe(o) + "." + it.propertyPath + " points at an object that is gone");
                        issue["Property"] = it.propertyPath;
                    }
                }
            }
        }
    }
}
