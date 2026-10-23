using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Assetops
{
    // Everything the workbench writes lives under <Project>/Library/Assetops, which version control ignores.
    //   Export/   export JSON, one file per asset per run
    //   Audit/    audit reports and their fix specs
    //   Logs/     one log per run
    //   Queue/    task queue between the wrapper and the open editor, see TaskQueueProtocol
    public static class AssetopsPaths
    {
        public static string ProjectRoot
        {
            get { return Normalize(Path.GetDirectoryName(Application.dataPath)); }
        }

        public static string Root
        {
            get { return Combine(ProjectRoot, "Library/Assetops"); }
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
            string root = string.IsNullOrEmpty(outDir) ? ExportRoot : Absolute(outDir);
            return Unique(Combine(root, Normalize(assetPath) + "_" + Timestamp()), ".json");
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
