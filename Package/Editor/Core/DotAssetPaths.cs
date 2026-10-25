using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DotAsset
{
    // Everything DotAsset writes lives under <Project>/Library/DotAsset, which version control ignores.
    //   Export/   export JSON, one file per asset per run
    //   Audit/    audit reports and their fix specs
    //   Logs/     one log per run
    //   Queue/    task queue between the wrapper and the open editor, see TaskQueueProtocol
    public static class DotAssetPaths
    {
        public static string ProjectRoot
        {
            get { return Normalize(Path.GetDirectoryName(Application.dataPath)); }
        }

        public static string Root
        {
            get { return Combine(ProjectRoot, "Library/DotAsset"); }
        }

        public static string ExportRoot
        {
            get { return Combine(Root, "Export"); }
        }

        public static string AuditRoot
        {
            get { return Combine(Root, "Audit"); }
        }

        public static string LogRoot
        {
            get { return Combine(Root, "Logs"); }
        }

        public static string Timestamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        public static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        public static string Combine(string a, string b)
        {
            return Normalize(Path.Combine(a, b));
        }

        // Turns a project-relative path into an absolute one, leaves absolute paths alone.
        public static string Absolute(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            path = Normalize(path);
            return Path.IsPathRooted(path) ? path : Combine(ProjectRoot, path);
        }

        // Export/<AssetPath>_<timestamp>.json, e.g. Export/Assets/Prefabs/Player.prefab_20261023-201500.json
        // Two exports of the same asset in the same second get a -2, -3 suffix instead of overwriting.
        public static string ExportFileFor(string assetPath, string outDir)
        {
            return ExportFileNamed(Normalize(assetPath), outDir);
        }

        // Export/<name>_<timestamp>.json for exports not tied to one asset.
        public static string ExportFileNamed(string name, string outDir)
        {
            string root = string.IsNullOrEmpty(outDir) ? ExportRoot : Absolute(outDir);
            return Unique(Combine(root, name + "_" + Timestamp()), ".json");
        }

        public static string AuditReportFile(string runName, string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                return Absolute(explicitPath);
            }
            return Unique(Combine(AuditRoot, runName + "_" + Timestamp()), ".json");
        }

        // Every run writes a new timestamped file, so a busy project would collect them without end. Default-location
        // exports keep the newest KeepExportsPerAsset per asset, audit reports the newest KeepReportsPerAudit per audit
        // (with their fix specs). A folder given with -outdir or a path given with -report is the caller's, never pruned.
        public const int KeepExportsPerAsset = 10;
        public const int KeepReportsPerAudit = 20;

        // Deletes older files that differ from newFile only by timestamp, keeping the newest keep of them.
        public static void PruneOlder(string newFile, int keep)
        {
            try
            {
                string dir = Path.GetDirectoryName(newFile);
                string name = Path.GetFileName(newFile);
                var stamped = new Regex(@"^(.*)_\d{8}-\d{6}(-\d+)?\.json$");
                Match m = stamped.Match(name);
                if (string.IsNullOrEmpty(dir) || !m.Success || !Directory.Exists(dir))
                {
                    return;
                }
                var sibling = new Regex("^" + Regex.Escape(m.Groups[1].Value) + @"_\d{8}-\d{6}(-\d+)?\.json$");
                var old = new DirectoryInfo(dir).GetFiles("*.json")
                    .Where(f => sibling.IsMatch(f.Name))
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .Skip(keep);
                foreach (FileInfo f in old)
                {
                    f.Delete();
                    // An audit report's fix spec goes with it.
                    string fix = Path.ChangeExtension(f.FullName, null) + ".fix.json";
                    if (File.Exists(fix))
                    {
                        File.Delete(fix);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static string Unique(string stem, string extension)
        {
            string candidate = stem + extension;
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
                n++;
            }
            return candidate;
        }

        public static void EnsureParent(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        // Project-relative form for logs and JSON, absolute paths outside the project stay absolute.
        public static string ToProjectRelative(string absolutePath)
        {
            string normalized = Normalize(absolutePath);
            string root = ProjectRoot + "/";
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(root.Length);
            }
            return normalized;
        }
    }
}
