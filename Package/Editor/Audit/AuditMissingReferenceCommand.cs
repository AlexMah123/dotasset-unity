using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Finds references that point at nothing.
    //   R1  a GUID that no asset in the project has (the asset was deleted, or its .meta lost)
    //   R2  a script GUID that no script has: a Missing Script component or ScriptableObject
    //   R3  a reference that loads as nothing (-deep, loads every asset): catches deleted sub-objects inside
    //       existing assets, which the text pass cannot see, and repeats R1 hits
    //   R4  a prefab instance whose source prefab is missing (-deep)
    //   R5  [SerializeReference] data whose type no compiled class has, read as null until RenameSerializeReferenceType
    //       repoints it. Hits outside scenes are confirmed by Unity, which also honours [MovedFrom]
    // The text pass reads YAML (Force Text) without loading anything, the -deep pass loads each asset.
    // Fix spec: R2 entries grouped by GUID, for RemapScript or RemoveMissingScripts.
    // Usage: dotasset.py AuditMissingReference [Assets/Prefabs,...] --args "-scandir=Assets -deep"
    // Docs/Audit.md
    public sealed class AuditMissingReferenceCommand : DotAssetCommand
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
            report.Rule("R5", "[SerializeReference] data whose type no compiled class has");

            Func<string, bool> yaml = p => AssetScan.HasExtension(p, AssetScan.YamlExtensions);
            List<string> files = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, yaml, ctx.Log) : AssetScan.All(AssetScan.Roots(ctx.Args), yaml);
            report.Checked = files.Count;
            var missingScripts = new Dictionary<string, List<string>>();
            var missingTypes = new Dictionary<string, List<string>>();
            // Per run: in the editor the class outlives the run, and assets come and go between runs.
            guidExists.Clear();

            if (AssetScan.IsForceText())
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 100 == 0)
                    {
                        ctx.Progress(i, files.Count, files[i]);
                    }
                    TextPass(ctx, files[i], report, missingScripts, missingTypes);
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

            if (missingTypes.Count > 0)
            {
                var groups = new JArray();
                foreach (var kv in missingTypes.OrderByDescending(kv => kv.Value.Count))
                {
                    string asm, ns, cls;
                    ManagedTypes.Parse(kv.Key, out asm, out ns, out cls);
                    groups.Add(new JObject
                    {
                        ["Type"] = kv.Key,
                        ["Files"] = new JArray(kv.Value.Distinct().OrderBy(f => f).Select(f => (object)f).ToArray()),
                        ["Candidates"] = new JArray(ManagedTypes.Candidates(cls).Select(c => (object)c).ToArray()),
                        ["RenameSerializeReferenceType"] = "-from=\"" + kv.Key + "\" -to=<the type that should be there> -apply",
                    });
                }
                report.Extra["MissingTypes"] = groups;
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

        static void TextPass(RunContext ctx, string path, AuditReport report, Dictionary<string, List<string>> missingScripts, Dictionary<string, List<string>> missingTypes)
        {
            string text;
            try
            {
                text = File.ReadAllText(DotAssetPaths.Absolute(path));
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
            MissingTypes(path, text, report, missingTypes);
            var headers = DocHeader.Matches(text).Cast<Match>().ToList();
            foreach (Match m in Reference.Matches(text))
            {
                string guid = m.Groups[2].Value;
                if (guid.StartsWith("0000000000000000", StringComparison.Ordinal))
                {
                    // Built-in resources.
                    continue;
                }
                if (GuidExists(guid))
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

        static void MissingTypes(string path, string text, AuditReport report, Dictionary<string, List<string>> missingTypes)
        {
            if (text.IndexOf("RefIds:", StringComparison.Ordinal) < 0)
            {
                return;
            }
            var names = new HashSet<string>();
            foreach (Match m in ManagedTypes.Entry.Matches(text))
            {
                string cls = m.Groups[1].Value;
                string ns = m.Groups[2].Value;
                string asm = m.Groups[3].Value;
                // {class: , ns: , asm: } marks the null reference.
                if (cls.Length > 0 && ManagedTypes.Resolve(asm, ns, cls) == null)
                {
                    names.Add(ManagedTypes.Name(asm, ns, cls));
                }
            }
            if (names.Count > 0 && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                // Unity knows the types it cannot load, [MovedFrom] renames included. Scenes would need opening.
                var unresolved = new HashSet<string>();
                using (RunDispatcher.MuteEngineLog())
                {
                    foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (o == null || !SerializationUtility.HasManagedReferencesWithMissingTypes(o))
                        {
                            continue;
                        }
                        foreach (ManagedReferenceMissingType missing in SerializationUtility.GetManagedReferencesWithMissingTypes(o))
                        {
                            unresolved.Add(ManagedTypes.Name(missing.assemblyName, missing.namespaceName, missing.className));
                        }
                    }
                }
                names.IntersectWith(unresolved);
            }
            foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal))
            {
                List<string> list;
                if (!missingTypes.TryGetValue(name, out list))
                {
                    list = new List<string>();
                    missingTypes.Add(name, list);
                }
                list.Add(path);
                JObject issue = report.Add("R5", "Error", path, "[SerializeReference] data of type " + name + ", which no compiled class has, reads as null");
                issue["Type"] = name;
            }
        }

        // GUIDToAssetPath alone is not enough: in an open editor it keeps answering with the old path of an asset
        // deleted earlier in the session. The path must still hold an asset.
        static readonly Dictionary<string, bool> guidExists = new Dictionary<string, bool>();

        static bool GuidExists(string guid)
        {
            bool exists;
            if (!guidExists.TryGetValue(guid, out exists))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                exists = !string.IsNullOrEmpty(path) && AssetDatabase.GetMainAssetTypeAtPath(path) != null;
                guidExists[guid] = exists;
            }
            return exists;
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
