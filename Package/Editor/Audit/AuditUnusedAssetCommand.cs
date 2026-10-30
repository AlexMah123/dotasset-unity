using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Lists assets nothing in the project reaches. Roots, taken from the whole project whatever -scandir says: every
    // scene (only the enabled Build Settings scenes with -buildonly), everything under a Resources, StreamingAssets,
    // Editor, Editor Default Resources or Gizmos folder, every GUID ProjectSettings/*.asset names (render pipeline,
    // preloaded assets, package settings) and Addressables entries. What the roots use, directly or not, is used.
    //   U1  nothing reachable uses the asset
    //   U2  a scene outside Build Settings, with -buildonly
    // Code, shaders, plugins and files Unity does not import as an asset type are never listed: they are used by name.
    // Fix spec: DeleteAsset {"Assets": [...]} for U1, which checks the references again before it deletes.
    // Usage: dotasset.py AuditUnusedAsset --args "-scandir=Assets/Art -buildonly"
    // Docs/Audit.md
    public sealed class AuditUnusedAssetCommand : DotAssetCommand
    {
        static readonly string[] RootFolders = { "Resources", "StreamingAssets", "Editor", "Editor Default Resources", "Gizmos" };
        static readonly string[] NeverListed =
        {
            ".cs", ".asmdef", ".asmref", ".rsp", ".dll", ".so", ".a", ".aar", ".jar", ".bundle", ".jslib", ".jspre",
            ".shader", ".shadergraph", ".shadersubgraph", ".compute", ".hlsl", ".cginc", ".glslinc", ".raytrace",
        };
        static readonly Regex Guid = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);
        static readonly Regex AddressableEntry = new Regex(@"m_GUID: ([0-9a-f]{32})", RegexOptions.Compiled);

        public override string Summary
        {
            get { return "Assets that no scene, Resources folder, project setting or Addressables entry reaches"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "rules", "buildonly" }; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        static bool InRootFolder(string path)
        {
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (RootFolders.Contains(parts[i]))
                {
                    return true;
                }
            }
            return false;
        }

        static bool Listable(string path)
        {
            if (AssetScan.HasExtension(path, NeverListed))
            {
                return false;
            }
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type != null && type != typeof(DefaultAsset) && type != typeof(MonoScript) && !typeof(Shader).IsAssignableFrom(type);
        }

        static void AddGuid(string guid, HashSet<string> roots)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            if (AssetDatabase.IsValidFolder(path))
            {
                roots.UnionWith(AssetScan.All(new[] { path }));
            }
            else
            {
                roots.Add(path);
            }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("U1", "nothing reachable uses the asset");
            report.Rule("U2", "scene outside Build Settings (-buildonly)");
            Func<string, bool> on = report.RuleFilter();
            if (on == null)
            {
                return ExitCode.Refused;
            }
            bool buildOnly = ctx.Args.Has("buildonly");

            List<string> project = AssetScan.All(new[] { "Assets" });
            bool text = AssetScan.IsForceText();
            var buildScenes = new HashSet<string>(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path), StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < project.Count; i++)
            {
                string path = project[i];
                if (i % 500 == 0)
                {
                    ctx.Progress(i, project.Count, "finding roots");
                }
                bool scene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                if ((scene && (!buildOnly || buildScenes.Contains(path))) || InRootFolder(path))
                {
                    roots.Add(path);
                }
                else if (text && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    // Addressables groups keep their entries as GUID strings, which GetDependencies does not follow.
                    string yaml = File.ReadAllText(DotAssetPaths.Absolute(path));
                    if (yaml.IndexOf("m_SerializeEntries:", StringComparison.Ordinal) >= 0)
                    {
                        roots.Add(path);
                        foreach (Match m in AddressableEntry.Matches(yaml))
                        {
                            AddGuid(m.Groups[1].Value, roots);
                        }
                    }
                }
            }
            string settings = Path.Combine(DotAssetPaths.ProjectRoot, "ProjectSettings");
            foreach (string file in Directory.GetFiles(settings, "*.asset"))
            {
                foreach (Match m in Guid.Matches(File.ReadAllText(file)))
                {
                    AddGuid(m.Groups[1].Value, roots);
                }
            }
            ctx.Log.Info(roots.Count + " root asset(s), following their dependencies");
            var used = new HashSet<string>(AssetDatabase.GetDependencies(roots.ToArray(), true), StringComparer.Ordinal);

            List<string> scanned = AssetScan.All(AssetScan.Roots(ctx.Args));
            report.Checked = scanned.Count;
            var unused = new JArray();
            long bytes = 0;
            foreach (string path in scanned)
            {
                bool scene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                if (scene && buildOnly && !buildScenes.Contains(path))
                {
                    if (on("U2") && !used.Contains(path))
                    {
                        report.Add("U2", "Info", path, "scene is not in Build Settings and nothing in the build uses it");
                    }
                    continue;
                }
                if (used.Contains(path) || !Listable(path) || !on("U1"))
                {
                    continue;
                }
                long size = new FileInfo(DotAssetPaths.Absolute(path)).Length;
                bytes += size;
                JObject issue = report.Add("U1", "Warning", path, "nothing reachable uses it (" + size + " bytes)");
                issue["Bytes"] = size;
                unused.Add(path);
            }
            report.Extra["Roots"] = roots.Count;
            report.Extra["BuildOnly"] = buildOnly;
            report.Extra["UnusedBytes"] = bytes;
            report.FixSpec = new JObject { ["Assets"] = unused };
            report.FixRun = "DeleteAsset";
            return report.Finish();
        }
    }
}
