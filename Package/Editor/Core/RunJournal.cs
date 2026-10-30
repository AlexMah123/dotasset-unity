using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace DotAsset
{
    // The files a writing run touched, kept as they were before the run, so the whole run can be rolled back (-atomic)
    // or undone later (UndoRun). Lives in Library/DotAsset/Journal/<id>, the newest KeepJournals are kept.
    // A file reaches the journal before it is written: through JournalHook for writes that go through the
    // AssetDatabase, through an explicit Keep for those that do not (text rewrites, reserialization, importers).
    // Each entry is also appended to journal.log once its copies are made. A run that dies before Close leaves only the
    // log, and the next run finalizes it into journal.json marked Interrupted, so UndoRun can restore it.
    public sealed class RunJournal
    {
        const int KeepJournals = 30;
        public const string FileName = "journal.json";
        const string LogName = "journal.log";

        // The journal of the run executing now. Null outside a writing run and while a rollback restores files.
        public static RunJournal Current { get; internal set; }

        // The folder of the journal closed last, so a save Unity makes after the run (TaskQueue.Settle) updates it.
        static string lastClosed;

        public sealed class Entry
        {
            public string Path;
            public bool Existed;
            public bool Folder;
            // File name of the copy in the journal folder (a folder keeps only its .meta there), null when absent.
            public string Backup;
            public bool HadMeta;
            public string After;
        }

        public string Id { get; private set; }
        readonly string runName;
        readonly List<Entry> entries = new List<Entry>();
        readonly HashSet<string> kept = new HashSet<string>(StringComparer.Ordinal);

        public RunJournal(string runName)
        {
            this.runName = runName;
            // Sortable to the millisecond, so -run=last finds the newest run even when runs follow within a second.
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            while (Directory.Exists(Dir))
            {
                Id += "x";
            }
            Recover();
        }

        public static string Root
        {
            get { return DotAssetPaths.Combine(DotAssetPaths.Root, "Journal"); }
        }

        public int Count
        {
            get { return entries.Count; }
        }

        string Dir
        {
            get { return DotAssetPaths.Combine(Root, Id); }
        }

        // Call before path is written, created, moved or deleted. Calling it again for the same path does nothing.
        public static void Keep(string path)
        {
            if (Current != null)
            {
                Current.Add(path);
            }
        }

        void Add(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            path = DotAssetPaths.Normalize(path).TrimEnd('/');
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - 5);
            }
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return;
            }
            if (!kept.Add(path))
            {
                return;
            }
            string abs = DotAssetPaths.Absolute(path);
            var entry = new Entry { Path = path };
            entries.Add(entry);
            if (Directory.Exists(abs))
            {
                entry.Existed = true;
                entry.Folder = true;
                entry.Backup = entries.Count.ToString();
                entry.HadMeta = Copy(abs + ".meta", entry.Backup + ".meta");
                // A folder about to be moved or deleted takes everything in it along.
                foreach (string child in Directory.GetFileSystemEntries(abs).OrderBy(c => c, StringComparer.Ordinal))
                {
                    if (!child.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(path + "/" + System.IO.Path.GetFileName(child));
                    }
                }
                return;
            }
            if (File.Exists(abs))
            {
                entry.Existed = true;
                entry.Backup = entries.Count.ToString();
                Copy(abs, entry.Backup);
                entry.HadMeta = Copy(abs + ".meta", entry.Backup + ".meta");
            }
            Append(entry);
        }

        // One line per entry, written after its copies, so a line always has its backup. A folder's line comes before
        // its children's, which Restore handles newest first.
        void Append(Entry entry)
        {
            Directory.CreateDirectory(Dir);
            string file = DotAssetPaths.Combine(Dir, LogName);
            if (!File.Exists(file))
            {
                File.WriteAllText(file, new JObject { ["Id"] = Id, ["RunName"] = runName }.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            }
            File.AppendAllText(file, JObject.FromObject(entry).ToString(Newtonsoft.Json.Formatting.None) + "\n");
        }

        // Finalizes the journals of runs that died before Close: journal.json from the log, each path's state now as
        // After, marked Interrupted. The journal of the run executing now is left alone.
        static void Recover()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            string current = Current == null ? null : DotAssetPaths.Normalize(Current.Dir);
            foreach (string dir in Directory.GetDirectories(Root).Select(DotAssetPaths.Normalize))
            {
                string log = DotAssetPaths.Combine(dir, LogName);
                if (dir == current || !File.Exists(log) || File.Exists(DotAssetPaths.Combine(dir, FileName)))
                {
                    continue;
                }
                try
                {
                    string[] lines = File.ReadAllLines(log);
                    JObject header = JObject.Parse(lines[0]);
                    var list = new JArray();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        JObject e;
                        try
                        {
                            e = JObject.Parse(lines[i]);
                        }
                        catch (Exception)
                        {
                            // The last line, cut off by the crash: its file was not written yet.
                            continue;
                        }
                        e["After"] = Hash((string)e["Path"]);
                        list.Add(e);
                    }
                    Json.WriteFile(DotAssetPaths.Combine(dir, FileName), new JObject
                    {
                        ["Id"] = header["Id"],
                        ["RunName"] = header["RunName"],
                        ["Interrupted"] = true,
                        ["Entries"] = list,
                    });
                    File.Delete(log);
                }
                catch (Exception)
                {
                    // An unreadable log stays, pruned with its folder later.
                }
            }
        }

        bool Copy(string abs, string name)
        {
            if (!File.Exists(abs))
            {
                return false;
            }
            Directory.CreateDirectory(Dir);
            File.Copy(abs, DotAssetPaths.Combine(Dir, name), true);
            return true;
        }

        // Writes journal.json with each file's state after the run and returns its path.
        public string Close()
        {
            Directory.CreateDirectory(Dir);
            foreach (Entry e in entries)
            {
                e.After = Hash(e.Path);
            }
            string file = DotAssetPaths.Combine(Dir, FileName);
            Json.WriteFile(file, new JObject
            {
                ["Id"] = Id,
                ["RunName"] = runName,
                ["Entries"] = new JArray(entries.Select(e => (object)JObject.FromObject(e)).ToArray()),
            });
            File.Delete(DotAssetPaths.Combine(Dir, LogName));
            lastClosed = Dir;
            Prune();
            return file;
        }

        // Restores what the run wrote and drops the journal. Returns the paths that could not be restored, with why.
        public List<string> RollBack(RunLog log)
        {
            List<string> problems = Restore(Dir, entries, log);
            try
            {
                Directory.Delete(Dir, true);
            }
            catch (Exception)
            {
                // A journal left behind is pruned later.
            }
            return problems;
        }

        // Puts entries back as they were before their run, newest first. A scene open in the editor or a prefab open
        // in Prefab Mode is left alone, Unity would write its loaded copy back. Returns "path: reason" per failure.
        public static List<string> Restore(string dir, List<Entry> list, RunLog log)
        {
            var problems = new List<string>();
            var reimport = new List<string>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Entry e = list[i];
                string blocked = Blocked(e.Path);
                if (blocked != null)
                {
                    problems.Add(e.Path + ": " + blocked);
                    continue;
                }
                string abs = DotAssetPaths.Absolute(e.Path);
                try
                {
                    Keep(e.Path);
                    if (!e.Existed)
                    {
                        if ((File.Exists(abs) || Directory.Exists(abs)) && !AssetDatabase.DeleteAsset(e.Path))
                        {
                            FileUtil.DeleteFileOrDirectory(abs);
                            FileUtil.DeleteFileOrDirectory(abs + ".meta");
                        }
                        continue;
                    }
                    if (e.Folder)
                    {
                        Directory.CreateDirectory(abs);
                    }
                    else
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs));
                        File.Copy(DotAssetPaths.Combine(dir, e.Backup), abs, true);
                        reimport.Add(e.Path);
                    }
                    if (e.HadMeta)
                    {
                        File.Copy(DotAssetPaths.Combine(dir, e.Backup + ".meta"), abs + ".meta", true);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(e.Path + ": " + ex.Message);
                }
            }
            AssetDatabase.Refresh();
            foreach (string path in reimport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            log.Info("restored " + (list.Count - problems.Count) + " of " + list.Count + " journaled path(s)");
            return problems;
        }

        // Why path cannot be restored now, or null.
        public static string Blocked(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.path == path)
                {
                    return "open in the editor, Unity would write its loaded copy back. Close the scene first";
                }
            }
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == path)
            {
                return "open in Prefab Mode. Close it first";
            }
            return null;
        }

        // The file and its .meta as one hash, "folder" or "absent".
        public static string Hash(string path)
        {
            string abs = DotAssetPaths.Absolute(path);
            if (Directory.Exists(abs))
            {
                return "folder";
            }
            if (!File.Exists(abs))
            {
                return "absent";
            }
            using (var sha = SHA1.Create())
            {
                byte[] bytes = File.ReadAllBytes(abs);
                byte[] meta = File.Exists(abs + ".meta") ? File.ReadAllBytes(abs + ".meta") : new byte[0];
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                sha.TransformFinalBlock(meta, 0, meta.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }

        // A save after the run (TaskQueue.Settle) changed path again. Its new state is what an undo must find.
        public static void Rehash(string path)
        {
            if (lastClosed == null)
            {
                return;
            }
            string file = DotAssetPaths.Combine(lastClosed, FileName);
            JObject json = Load(file);
            if (json == null)
            {
                return;
            }
            foreach (JToken e in json["Entries"] ?? new JArray())
            {
                if ((string)e["Path"] == path)
                {
                    e["After"] = Hash(path);
                    Json.WriteFile(file, json);
                    return;
                }
            }
        }

        // The journal folder for id, or for the newest journal when id is "last". Null when there is none.
        public static string Find(string id)
        {
            if (!Directory.Exists(Root))
            {
                return null;
            }
            Recover();
            if (id == "last")
            {
                return Directory.GetDirectories(Root).Select(DotAssetPaths.Normalize)
                    .Where(d => File.Exists(DotAssetPaths.Combine(d, FileName)))
                    .OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            }
            string dir = DotAssetPaths.Combine(Root, id);
            return File.Exists(DotAssetPaths.Combine(dir, FileName)) ? dir : null;
        }

        public static JObject Load(string file)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(file));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static List<Entry> EntriesOf(JObject json)
        {
            return (json["Entries"] ?? new JArray()).Select(e => e.ToObject<Entry>()).ToList();
        }

        static void Prune()
        {
            foreach (string old in Directory.GetDirectories(Root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(KeepJournals))
            {
                try
                {
                    Directory.Delete(old, true);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // Every AssetDatabase write during a writing run passes through here first, so the journal keeps the file as it was.
    public sealed class JournalHook : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            foreach (string path in paths)
            {
                RunJournal.Keep(path);
            }
            return paths;
        }

        static void OnWillCreateAsset(string path)
        {
            RunJournal.Keep(path);
        }

        static AssetMoveResult OnWillMoveAsset(string from, string to)
        {
            RunJournal.Keep(from);
            RunJournal.Keep(to);
            return AssetMoveResult.DidNotMove;
        }

        static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            RunJournal.Keep(path);
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
