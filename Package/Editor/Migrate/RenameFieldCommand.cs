using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Renames a serialized field in the YAML of every component and ScriptableObject that uses one script, or a script
    // that inherits the field, and in the prefab overrides that target them, through variants and nested prefabs. Run it after renaming the field in
    // C#, in place of [FormerlySerializedAs], or to bake one in before removing it. Top-level fields only, needs Force Text.
    // Usage: dotasset.py RenameField --args '-script=Assets/Scripts/Enemy.cs -from=hp -to=health -apply'
    // Docs/Migrate.md
    public sealed class RenameFieldCommand : DotAssetCommand
    {
        static readonly Regex Header = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?\r?$", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex SourceObject = new Regex(@"m_CorrespondingSourceObject: \{fileID: (-?\d+), guid: ([0-9a-f]{32})", RegexOptions.Compiled);
        static readonly Regex SourcePrefab = new Regex(@"m_SourcePrefab: \{fileID: \d+, guid: ([0-9a-f]{32})", RegexOptions.Compiled);
        static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        public override string Summary
        {
            get { return "Renames a serialized field of one script in assets and prefab overrides"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "script", "from", "to", "scandir" }; }
        }

        sealed class Doc
        {
            public int ClassId;
            public long Id;
            public bool Stripped;
            public int Start;
            public int End;
        }

        static List<Doc> Docs(string text)
        {
            var docs = new List<Doc>();
            MatchCollection heads = Header.Matches(text);
            for (int i = 0; i < heads.Count; i++)
            {
                Match m = heads[i];
                docs.Add(new Doc
                {
                    ClassId = int.Parse(m.Groups[1].Value),
                    Id = long.Parse(m.Groups[2].Value),
                    Stripped = m.Groups[3].Success,
                    Start = m.Index,
                    End = i + 1 < heads.Count ? heads[i + 1].Index : text.Length,
                });
            }
            return docs;
        }

        static FieldInfo FieldOf(Type type, string name)
        {
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (!AssetScan.IsForceText())
            {
                ctx.Log.Error("RenameField rewrites YAML, set Project Settings > Editor > Asset Serialization Mode to Force Text first");
                return ExitCode.Refused;
            }
            string scriptArg = ctx.Args.Get("script");
            string from = ctx.Args.Get("from");
            string to = ctx.Args.Get("to");
            if (scriptArg == null || from == null || to == null)
            {
                ctx.Log.Error("RenameField needs -script=<script path or GUID>, -from=<old field name> and -to=<new field name>");
                return ExitCode.Refused;
            }
            if (!Identifier.IsMatch(from) || !Identifier.IsMatch(to) || from == to)
            {
                ctx.Log.Error("-from and -to must be two different field names, nested paths are not supported");
                return ExitCode.Refused;
            }
            string scriptGuid = Regex.IsMatch(scriptArg, "^[0-9a-fA-F]{32}$") ? scriptArg.ToLowerInvariant() : AssetDatabase.AssetPathToGUID(scriptArg);
            if (string.IsNullOrEmpty(scriptGuid))
            {
                ctx.Log.Error("-script is neither a 32-digit GUID nor a script path: " + scriptArg);
                return ExitCode.Refused;
            }
            string scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuid);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            Type type = script != null ? script.GetClass() : null;
            var scriptGuids = new List<string> { scriptGuid };
            if (type != null)
            {
                FieldInfo renamed = FieldOf(type, to);
                if (renamed == null)
                {
                    ctx.Log.Warn(type.Name + " has no field named " + to + ", Unity will drop the renamed values on the next save");
                }
                // Every script that inherits the field from the class declaring it carries the same key, siblings of
                // the given script included: the C# rename in that class renamed it for all of them.
                Type declaring = renamed != null ? renamed.DeclaringType : type;
                var classes = new List<KeyValuePair<Type, string>> { new KeyValuePair<Type, string>(type, scriptPath) };
                foreach (MonoScript s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    Type c = s.GetClass();
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(s));
                    if (c != null && c != type && declaring.IsAssignableFrom(c) && !string.IsNullOrEmpty(guid) && !scriptGuids.Contains(guid))
                    {
                        scriptGuids.Add(guid);
                        classes.Add(new KeyValuePair<Type, string>(c, AssetDatabase.GUIDToAssetPath(guid)));
                    }
                }
                foreach (var c in classes)
                {
                    // Unity drops keys the class does not have on the next save, so the C# rename has to come first.
                    // A subclass that declares its own field of the old name makes the key ambiguous.
                    if (FieldOf(c.Key, from) != null)
                    {
                        ctx.Log.Error(c.Key.Name + " still has a field named " + from + ", rename it in C# to " + to + " first");
                        return ExitCode.Refused;
                    }
                    // All classes in a DLL share its GUID, so the documents cannot be told apart by script.
                    if (c.Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Log.Error(c.Key.Name + " is in " + c.Value + ", RenameField only handles scripts that are .cs files");
                        return ExitCode.Refused;
                    }
                    if (c.Key != type)
                    {
                        ctx.Log.Info(c.Key.Name + " (" + c.Value + ") inherits " + to + " from " + declaring.Name + ", renaming its key too");
                    }
                }
            }
            else
            {
                ctx.Log.Warn("no compiled class for " + scriptArg + ", the field names cannot be checked and subclasses are not followed");
            }

            // Pass 1: every object that is a component of these scripts, by (file GUID, fileID). An object inside a prefab
            // instance has fileID (source fileID ^ instance fileID) & long.MaxValue, stripped or not.
            var scriptRef = new Regex(@"m_Script: \{fileID: \d+, guid: (" + string.Join("|", scriptGuids) + "),");
            var targets = new Dictionary<string, HashSet<long>>();
            var instances = new List<KeyValuePair<string, KeyValuePair<long, string>>>();
            var strips = new List<KeyValuePair<string, KeyValuePair<long, KeyValuePair<long, string>>>>();
            var candidates = new List<string>();
            string overrideKey = "propertyPath: " + from;
            var files = AssetScan.All(AssetScan.Roots(ctx.Args), p => AssetScan.HasExtension(p, AssetScan.YamlExtensions));
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, files.Count, path);
                }
                string text;
                try
                {
                    text = File.ReadAllText(DotAssetPaths.Absolute(path));
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                    continue;
                }
                bool uses = scriptGuids.Exists(g => text.IndexOf(g, StringComparison.Ordinal) >= 0);
                bool overrides = text.IndexOf(overrideKey, StringComparison.Ordinal) >= 0;
                if (uses || overrides)
                {
                    candidates.Add(path);
                }
                if (!uses && text.IndexOf("m_SourcePrefab:", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                string guid = AssetDatabase.AssetPathToGUID(path);
                foreach (Doc d in Docs(text))
                {
                    string body = text.Substring(d.Start, d.End - d.Start);
                    if (d.Stripped)
                    {
                        Match s = SourceObject.Match(body);
                        if (s.Success)
                        {
                            strips.Add(new KeyValuePair<string, KeyValuePair<long, KeyValuePair<long, string>>>(guid,
                                new KeyValuePair<long, KeyValuePair<long, string>>(d.Id, new KeyValuePair<long, string>(long.Parse(s.Groups[1].Value), s.Groups[2].Value))));
                        }
                    }
                    else if (d.ClassId == 114 && scriptRef.IsMatch(body))
                    {
                        Add(targets, guid, d.Id);
                    }
                    else if (d.ClassId == 1001)
                    {
                        Match s = SourcePrefab.Match(body);
                        if (s.Success)
                        {
                            instances.Add(new KeyValuePair<string, KeyValuePair<long, string>>(guid, new KeyValuePair<long, string>(d.Id, s.Groups[1].Value)));
                        }
                    }
                }
            }
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var s in strips)
                {
                    HashSet<long> ids;
                    if (targets.TryGetValue(s.Value.Value.Value, out ids) && ids.Contains(s.Value.Value.Key))
                    {
                        grew |= Add(targets, s.Key, s.Value.Key);
                    }
                }
                foreach (var inst in instances)
                {
                    HashSet<long> ids;
                    if (targets.TryGetValue(inst.Value.Value, out ids))
                    {
                        foreach (long id in new List<long>(ids))
                        {
                            grew |= Add(targets, inst.Key, (id ^ inst.Value.Key) & long.MaxValue);
                        }
                    }
                }
            }

            // Pass 2: the keys in this script's documents, and the override paths that target one of its objects.
            var key = new Regex("^  " + from + ":", RegexOptions.Multiline);
            var clash = new Regex("^  " + to + ":", RegexOptions.Multiline);
            var modification = new Regex(@"(- target: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: \d+\}\r?\n\s*propertyPath: )" + from + @"(?=[.\r\n])");
            int changedFiles = 0;
            int changedKeys = 0;
            var toImport = new List<string>();
            foreach (string path in candidates)
            {
                string abs = DotAssetPaths.Absolute(path);
                string text;
                try
                {
                    text = File.ReadAllText(abs);
                }
                catch (Exception e)
                {
                    // Thrown here it would end the run before the files already written are imported.
                    ctx.FailTarget(path, e.Message);
                    continue;
                }
                var sb = new StringBuilder(text.Length);
                int hits = 0;
                string conflict = null;
                List<Doc> docs = Docs(text);
                if (docs.Count == 0)
                {
                    continue;
                }
                sb.Append(text, 0, docs[0].Start);
                foreach (Doc d in docs)
                {
                    string body = text.Substring(d.Start, d.End - d.Start);
                    if (d.ClassId == 114 && !d.Stripped && scriptRef.IsMatch(body) && key.IsMatch(body))
                    {
                        if (clash.IsMatch(body))
                        {
                            conflict = "object &" + d.Id + " already has a field named " + to;
                            break;
                        }
                        body = key.Replace(body, "  " + to + ":");
                        hits++;
                    }
                    body = modification.Replace(body, m =>
                    {
                        HashSet<long> ids;
                        if (targets.TryGetValue(m.Groups[3].Value, out ids) && ids.Contains(long.Parse(m.Groups[2].Value)))
                        {
                            hits++;
                            return m.Groups[1].Value + to;
                        }
                        return m.Value;
                    });
                    sb.Append(body);
                }
                if (conflict != null)
                {
                    ctx.FailTarget(path, conflict);
                    continue;
                }
                if (hits == 0)
                {
                    continue;
                }
                string busy = AssetScan.OpenInEditor(path);
                if (busy != null)
                {
                    ctx.FailTarget(path, busy);
                    continue;
                }
                changedFiles++;
                changedKeys += hits;
                ctx.Log.Info((ctx.DryRun ? "would rename " : "renamed ") + hits + " key(s) in " + path);
                if (ctx.DryRun)
                {
                    ctx.PlanFile("rewrite", path, hits + " key(s) " + from + " to " + to);
                    continue;
                }
                try
                {
                    RunJournal.Keep(path);
                    File.WriteAllText(abs, sb.ToString(), new UTF8Encoding(false));
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
            ctx.Log.Info((ctx.DryRun ? "would rename " : "renamed ") + changedKeys + " key(s) in " + changedFiles + " file(s), " + from + " to " + to);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static bool Add(Dictionary<string, HashSet<long>> map, string guid, long id)
        {
            HashSet<long> ids;
            if (!map.TryGetValue(guid, out ids))
            {
                ids = new HashSet<long>();
                map[guid] = ids;
            }
            return ids.Add(id);
        }
    }
}
