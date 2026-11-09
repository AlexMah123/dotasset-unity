using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace DotAsset
{
    // The file loop of the Migrate runs that rewrite YAML text on disk: RemapScript, RenameField and
    // RenameSerializeReferenceType. Each file is all or nothing. One that cannot be read or written, is read-only, is
    // loaded in the editor or has a conflict fails alone and stays as it was. A dry run plans each file instead of
    // writing it. The written files are imported once the loop is done.
    internal static class YamlRewrite
    {
        // Returns the file's new text with the number of changes in hits, 0 leaves the file alone.
        // A conflict fails the file.
        public delegate string Rewriter(string text, out int hits, out string conflict);

        // line: the log line for a file's changes, " in <path>" is added. planned: the plan entry's detail.
        public static void Files(RunContext ctx, IList<string> paths, bool progress, Rewriter rewrite,
            Func<int, string> line, Func<int, string> planned, out int changedFiles, out int changedHits)
        {
            changedFiles = 0;
            changedHits = 0;
            var written = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (progress && i % 100 == 0)
                {
                    ctx.Progress(i, paths.Count, path);
                }
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
                int hits;
                string conflict;
                string rewritten = rewrite(text, out hits, out conflict);
                if (conflict != null)
                {
                    ctx.FailTarget(path, conflict);
                    continue;
                }
                if (hits == 0)
                {
                    continue;
                }
                string busy = ReadOnly.Reason(path) ?? AssetScan.OpenInEditor(path);
                if (busy != null)
                {
                    // The file is rewritten on disk. A loaded copy would keep the old text and write it back on its next save.
                    ctx.FailTarget(path, busy);
                    continue;
                }
                changedFiles++;
                changedHits += hits;
                ctx.Log.Info(line(hits) + " in " + path);
                if (ctx.DryRun)
                {
                    ctx.PlanFile("rewrite", path, planned(hits));
                    continue;
                }
                try
                {
                    RunJournal.Keep(path);
                    DotAssetPaths.ReplaceText(abs, rewritten);
                    written.Add(path);
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                }
            }
            foreach (string path in written)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
