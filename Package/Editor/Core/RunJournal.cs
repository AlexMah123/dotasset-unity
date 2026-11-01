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

        // The folder of the journal closed last, so a save Unity makes after the run (SceneSettle) updates it.
        // Reset at each run start, a run that closes no journal must not update an earlier run's.
        static string lastClosed;

        internal static void BeginRun()
        {
            lastClosed = null;
        }

        // The log of the Edit or Migrate dry run executing now, null otherwise. JournalHook and DotAssetPaths.ReplaceText
        // refuse its writes to Assets/ and Packages/, so a command that forgets to check ctx.DryRun still writes nothing.
        static RunLog dryRun;
        // Assets a dry run created anyway: OnWillCreateAsset cannot stop a create, EndDryRun deletes them.
        static readonly List<string> dryRunCreated = new List<string>();

        // With a second's slack for file system timestamps.
        static DateTime dryRunStarted;

        internal static void BeginDryRun(RunLog log)
        {
            dryRun = log;
            dryRunStarted = DateTime.UtcNow.AddSeconds(-1);
            dryRunCreated.Clear();
        }

        internal static void EndDryRun()
        {
            dryRun = null;
            foreach (string path in dryRunCreated)
            {
                if (AssetDatabase.DeleteAsset(path))
                {
                    continue;
                }
                if (File.Exists(DotAssetPaths.Absolute(path)))
                {
                    UnityEngine.Debug.LogWarning("[DotAsset] a dry run created " + path + " and it could not be removed");
                }
            }
            dryRunCreated.Clear();
        }

        // True, with an error in the dry run's log, when a dry run is executing and path is a project file it must not
        // write. Library/, Temp/ and anything outside the project stay writable (exports, logs, the queue).
        internal static bool RefusedInDryRun(string path, string action)
        {
            if (dryRun == null || string.IsNullOrEmpty(path))
            {
                return false;
            }
            string rel = DotAssetPaths.ToProjectRelative(DotAssetPaths.Absolute(path));
            if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            dryRun.Error("the dry run tried to " + action + " " + rel + ", refused. A dry run writes nothing, add -apply to write");
            return true;
        }

        static void ThrowInDryRun(string path)
        {
            string target = BuildSceneOf(path ?? string.Empty) ?? path;
            if (RefusedInDryRun(target, "write"))
            {
                throw new InvalidOperationException("a dry run writes nothing, " + target + " was not written");
            }
        }

        internal static bool InDryRun
        {
            get { return dryRun != null; }
        }

        // A save during a dry run: refused and logged. A .meta Unity created during the run, importing a new file, is
        // let through: that is Unity importing, not the run writing, and refusing it only fails the run.
        internal static bool SaveRefusedInDryRun(string path)
        {
            if (path != null && path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                string meta = DotAssetPaths.Absolute(path);
                if (File.Exists(meta) && File.GetCreationTimeUtc(meta) >= dryRunStarted)
                {
                    return false;
                }
            }
            return RefusedInDryRun(path, "save");
        }

        // A create during a dry run: refused, logged, and deleted again by EndDryRun. Unity calls OnWillCreateAsset before
        // a created asset is written, but also when it imports a file already on disk (a refresh, ImportAsset) and only
        // its .meta is new. That file is not the run's to delete, so it is let through.
        internal static void CreateInDryRun(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            string asset = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - ".meta".Length) : path;
            string absolute = DotAssetPaths.Absolute(asset);
            if (dryRunCreated.Contains(asset) || File.Exists(absolute) || Directory.Exists(absolute))
            {
                return;
            }
            if (RefusedInDryRun(asset, "create"))
            {
                dryRunCreated.Add(asset);
            }
        }

        public sealed class Entry
        {
            public string Path;
            public bool Existed;
            public bool Folder;
            // File name of the copy in the journal folder (a folder keeps only its .meta there), null when absent.
            public string Backup;
            public bool HadMeta;
            // Only the .meta was kept, for a write that changes nothing else (import settings).
            public bool MetaOnly;
            public string After;
        }

        public string Id { get; private set; }
        readonly string runName;
        readonly string caller;
        readonly List<Entry> entries = new List<Entry>();
        readonly HashSet<string> kept = new HashSet<string>(StringComparer.Ordinal);

        public RunJournal(string runName)
        {
            this.runName = runName;
            caller = Caller;
            // UTC to the millisecond for a readable id. The order comes from Seq, a clock set back or a DST change
            // would turn a name order around.
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            while (Directory.Exists(Dir))
            {
                Id += "x";
            }
            RecoverInterrupted();
            Seq = Directory.Exists(Root) ? Directory.GetDirectories(Root).Select(SeqOf).DefaultIfEmpty(0).Max() + 1 : 1;
        }

        // Counts up across runs, newest highest. Journals written before 0.9.5 have none and count as 0, the oldest.
        public long Seq { get; private set; }

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
        // During a dry run it throws instead, so the write that follows never happens: SaveAssetIfDirty does not pass
        // through JournalHook on every Unity version, this call does.
        public static void Keep(string path)
        {
            ThrowInDryRun(path);
            if (Current != null)
            {
                Current.Add(path, false);
            }
        }

        // An entry for a scene's place in the Build Settings scene list, which is not a file under Assets/. Existed says
        // whether the scene was in the list before, its state is the whole list, so an undo is refused once the list
        // changed (another edit, or on Unity 6 another active build profile).
        public const string BuildScenePrefix = "BuildSettings:";

        // The entry for the whole scene list, kept before a run's first write: moving or deleting a scene changes the
        // list as a side effect, and putting the file back does not put its place in the list back. Its backup is the
        // list as JSON. Dropped at Close when the run left the list as it was, so a later list change never blocks an
        // undo of a run that did not touch it.
        public const string BuildSceneList = BuildScenePrefix;

        public static void KeepBuildScene(string scenePath)
        {
            Keep(BuildScenePrefix + DotAssetPaths.Normalize(scenePath));
        }

        static string BuildSceneOf(string path)
        {
            return path.StartsWith(BuildScenePrefix, StringComparison.Ordinal) ? path.Substring(BuildScenePrefix.Length) : null;
        }

        // Set around a write that changes only the .meta of the paths kept with KeepMeta. ForceReserializeAssets hands
        // the save hook the asset path, not the .meta, and that full Keep must not copy the file after all.
        public static bool MetaOnlySaves;

        // Keep for a write that changes only the .meta (import settings): the asset file itself is not copied.
        public static void KeepMeta(string path)
        {
            ThrowInDryRun(path);
            if (Current != null)
            {
                Current.Add(path, true);
            }
        }

        void Add(string path, bool metaOnly)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            if (path == BuildSceneList)
            {
                KeepSceneList();
                return;
            }
            string buildScene = BuildSceneOf(path);
            if (buildScene != null)
            {
                if (kept.Add(path))
                {
                    KeepSceneList();
                    var scene = new Entry { Path = path, Existed = EditorBuildSettings.scenes.Any(s => s.path == buildScene) };
                    entries.Add(scene);
                    Append(scene);
                }
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
                // A full Keep after a .meta-only one adds the file, still as it was: only the .meta was written since.
                Entry earlier = metaOnly || MetaOnlySaves ? null : entries.Find(e => e.Path == path && e.MetaOnly);
                if (earlier != null)
                {
                    Copy(DotAssetPaths.Absolute(path), earlier.Backup);
                    earlier.MetaOnly = false;
                    Append(earlier);
                }
                return;
            }
            KeepSceneList();
            string abs = DotAssetPaths.Absolute(path);
            var entry = new Entry { Path = path };
            entries.Add(entry);
            if (Directory.Exists(abs))
            {
                entry.Existed = true;
                entry.Folder = true;
                entry.Backup = entries.Count.ToString();
                entry.HadMeta = Copy(abs + ".meta", entry.Backup + ".meta");
                Append(entry);
                // A folder about to be moved or deleted takes everything in it along.
                foreach (string child in Directory.GetFileSystemEntries(abs).OrderBy(c => c, StringComparer.Ordinal))
                {
                    if (!child.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(path + "/" + System.IO.Path.GetFileName(child), false);
                    }
                }
                return;
            }
            if (File.Exists(abs))
            {
                entry.Existed = true;
                entry.Backup = entries.Count.ToString();
                entry.MetaOnly = metaOnly;
                if (!metaOnly)
                {
                    Copy(abs, entry.Backup);
                }
                entry.HadMeta = Copy(abs + ".meta", entry.Backup + ".meta");
            }
            Append(entry);
        }

        // First in the journal, so Restore, newest first, sets the list last, once the scene files are back.
        void KeepSceneList()
        {
            if (!kept.Add(BuildSceneList))
            {
                return;
            }
            var entry = new Entry { Path = BuildSceneList, Existed = true };
            entries.Add(entry);
            entry.Backup = entries.Count.ToString();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(DotAssetPaths.Combine(Dir, entry.Backup), SceneList().ToString(Newtonsoft.Json.Formatting.None));
            Append(entry);
        }

        static JArray SceneList()
        {
            return new JArray(EditorBuildSettings.scenes.Select(s => (object)new JObject { ["Path"] = s.path, ["Enabled"] = s.enabled }).ToArray());
        }

        static string SceneListHash(JArray list)
        {
            string scenes = string.Join("\n", list.Select(s => (string)s["Path"] + ((bool)s["Enabled"] ? "" : " (disabled)")).ToArray());
            using (var sha = SHA1.Create())
            {
                return "scenes " + Hex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(scenes)));
            }
        }

        // The scene list entry of a run that left the list as it was, which needs no undo.
        static bool SceneListUnchanged(string dir, string path, string backup, string now)
        {
            if (path != BuildSceneList)
            {
                return false;
            }
            try
            {
                return SceneListHash(JArray.Parse(File.ReadAllText(DotAssetPaths.Combine(dir, backup)))) == now;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // One line per entry, written after its copies, so a line always has its backup. A folder's line comes before
        // its children's, which Restore handles newest first. An entry written again replaces its earlier line.
        void Append(Entry entry)
        {
            Directory.CreateDirectory(Dir);
            string file = DotAssetPaths.Combine(Dir, LogName);
            if (!File.Exists(file))
            {
                var header = new JObject { ["Id"] = Id, ["Seq"] = Seq, ["RunName"] = runName };
                if (TaskId != null)
                {
                    header["Task"] = TaskId;
                }
                if (caller != null)
                {
                    header["Caller"] = caller;
                }
                File.WriteAllText(file, header.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            }
            File.AppendAllText(file, JObject.FromObject(entry).ToString(Newtonsoft.Json.Formatting.None) + "\n");
        }

        // The queue task the run executing now serves, "headless" for a run Cli.Run started, null for a run from the
        // window. journal.log's header keeps it, so a recovered journal names the task that died.
        public static string TaskId;

        // Who started the run executing now, the wrapper's --caller (DOTASSET_CALLER), or null. The journal keeps it,
        // so UndoRun -run=last never takes another caller's run.
        public static string Caller;

        // Finalizes the journals of runs that died before Close: journal.json from the log, each path's state now as
        // After, marked Interrupted. The journal of the run executing now is left alone. Called when the queue starts
        // and before every run, so the state is taken before anyone edits those files. Returns the ids finalized.
        public static List<string> RecoverInterrupted()
        {
            var finalized = new List<string>();
            if (!Directory.Exists(Root))
            {
                return finalized;
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
                    var at = new Dictionary<string, int>(StringComparer.Ordinal);
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
                        string path = (string)e["Path"];
                        e["After"] = Hash(path);
                        if (SceneListUnchanged(dir, path, (string)e["Backup"], (string)e["After"]))
                        {
                            continue;
                        }
                        int index;
                        if (at.TryGetValue(path, out index))
                        {
                            list[index] = e;
                        }
                        else
                        {
                            at[path] = list.Count;
                            list.Add(e);
                        }
                    }
                    var json = new JObject
                    {
                        ["Id"] = header["Id"],
                        ["Seq"] = header["Seq"] ?? 0,
                        ["RunName"] = header["RunName"],
                        ["Interrupted"] = true,
                        ["Entries"] = list,
                    };
                    if (header["Task"] != null)
                    {
                        json["Task"] = header["Task"];
                    }
                    if (header["Caller"] != null)
                    {
                        json["Caller"] = header["Caller"];
                    }
                    Json.WriteFile(DotAssetPaths.Combine(dir, FileName), json);
                    File.Delete(log);
                    finalized.Add((string)header["Id"]);
                }
                catch (Exception)
                {
                    // An unreadable log stays, pruned with its folder later.
                }
            }
            return finalized;
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
            entries.RemoveAll(e => SceneListUnchanged(Dir, e.Path, e.Backup, e.After));
            string file = DotAssetPaths.Combine(Dir, FileName);
            var json = new JObject
            {
                ["Id"] = Id,
                ["Seq"] = Seq,
                ["RunName"] = runName,
                ["Entries"] = new JArray(entries.Select(e => (object)JObject.FromObject(e)).ToArray()),
            };
            if (caller != null)
            {
                json["Caller"] = caller;
            }
            Json.WriteFile(file, json);
            File.Delete(DotAssetPaths.Combine(Dir, LogName));
            lastClosed = Dir;
            Prune();
            return file;
        }

        // Close, with a warning instead of an exception: the journal.log stays then, and the next run finishes it as
        // interrupted. The path of journal.json, or null.
        public string TryClose(RunLog log)
        {
            try
            {
                return Close();
            }
            catch (Exception e)
            {
                log.Warn("the journal could not be closed (" + e.Message + "), the next run finishes it as interrupted");
                return null;
            }
        }

        // Restores what the run wrote. With everything back the journal is dropped and null returned. When a path was
        // blocked or failed, the journal is closed instead, every path as it is now, so UndoRun can finish the
        // rollback later: problems holds "path: reason" and the path of journal.json is returned.
        public string RollBack(RunLog log, out List<string> problems)
        {
            problems = Restore(Dir, entries, log);
            if (problems.Count > 0)
            {
                return TryClose(log);
            }
            try
            {
                DeleteDir(Dir);
            }
            catch (Exception)
            {
                // A journal left behind is pruned later.
            }
            return null;
        }

        // A backup keeps the read-only attribute of the file it copies, which Directory.Delete refuses.
        static void DeleteDir(string dir)
        {
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
        }

        // Puts entries back as they were before their run, newest first. Nothing is restored when any path is
        // Blocked, and the first path that fails stops the rest, so a move is not left half undone without notice. A
        // file still as it was before the run is left as it is, read-only or not.
        // Returns "path: reason" per blocked path or for the failure, empty when every path is back.
        public static List<string> Restore(string dir, List<Entry> list, RunLog log)
        {
            var problems = new List<string>();
            foreach (Entry e in list)
            {
                string blocked = Blocked(e.Path, !AlreadyBack(dir, e), e.MetaOnly);
                if (blocked != null)
                {
                    problems.Add(e.Path + ": " + blocked);
                }
            }
            if (problems.Count > 0)
            {
                log.Info("restored nothing, " + problems.Count + " of " + list.Count + " journaled path(s) blocked");
                return problems;
            }
            var reimport = new List<string>();
            int restored = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Entry e = list[i];
                string buildScene = BuildSceneOf(e.Path);
                if (e.Path == BuildSceneList)
                {
                    Keep(e.Path);
                    JArray before = JArray.Parse(File.ReadAllText(DotAssetPaths.Combine(dir, e.Backup)));
                    if (SceneListHash(before) != Hash(BuildSceneList))
                    {
                        EditorBuildSettings.scenes = before.Select(s => new EditorBuildSettingsScene((string)s["Path"], (bool)s["Enabled"])).ToArray();
                    }
                    restored++;
                    continue;
                }
                if (buildScene != null)
                {
                    Keep(e.Path);
                    var scenes = EditorBuildSettings.scenes.ToList();
                    if (!e.Existed)
                    {
                        scenes.RemoveAll(s => s.path == buildScene);
                    }
                    else if (!scenes.Any(s => s.path == buildScene))
                    {
                        scenes.Add(new EditorBuildSettingsScene(buildScene, true));
                    }
                    EditorBuildSettings.scenes = scenes.ToArray();
                    restored++;
                    continue;
                }
                if (AlreadyBack(dir, e))
                {
                    // Imported again all the same: a failed save can leave the editor's copy changed.
                    reimport.Add(e.Path);
                    restored++;
                    continue;
                }
                string abs = DotAssetPaths.Absolute(e.Path);
                string problem = null;
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
                        if (File.Exists(abs) || Directory.Exists(abs))
                        {
                            problem = "could not be deleted";
                        }
                    }
                    else
                    {
                        if (e.Folder)
                        {
                            Directory.CreateDirectory(abs);
                        }
                        else
                        {
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs));
                            if (!e.MetaOnly)
                            {
                                File.Copy(DotAssetPaths.Combine(dir, e.Backup), abs, true);
                            }
                            reimport.Add(e.Path);
                        }
                        if (e.HadMeta)
                        {
                            File.Copy(DotAssetPaths.Combine(dir, e.Backup + ".meta"), abs + ".meta", true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                }
                if (problem != null)
                {
                    // Deleted or moved away by the run, and not back: say so, a move's other half may be deleted already.
                    bool missing = e.Existed && !File.Exists(abs) && !Directory.Exists(abs);
                    problems.Add(e.Path + ": " + problem + (missing ? ", it is missing from disk until the run is undone" : string.Empty));
                    break;
                }
                restored++;
            }
            AssetDatabase.Refresh();
            foreach (string path in reimport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            log.Info("restored " + restored + " of " + list.Count + " journaled path(s)" + (problems.Count > 0 ? ", stopped at the first failure" : string.Empty));
            return problems;
        }

        // Why path cannot be restored now, or null: a scene open in the editor or the prefab in Prefab Mode, at path or
        // inside it when path is a folder. Unity would write its loaded copy back, or lose it with the folder. Also a
        // read-only file there, which would stop the restore with half the paths back, unless readOnly is false. With
        // metaOnly only the .meta counts, the restore never writes the file itself.
        public static string Blocked(string path, bool readOnly = true, bool metaOnly = false)
        {
            var roots = new[] { path };
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path) && AssetScan.Under(scene.path, roots))
                {
                    return (scene.path == path ? string.Empty : "holds " + scene.path + ", ")
                        + "open in the editor, Unity would write its loaded copy back. Close the scene first";
                }
            }
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && AssetScan.Under(stage.assetPath, roots))
            {
                return (stage.assetPath == path ? string.Empty : "holds " + stage.assetPath + ", ") + "open in Prefab Mode. Close it first";
            }
            if (readOnly && BuildSceneOf(path) == null)
            {
                string abs = DotAssetPaths.Absolute(path);
                string held = metaOnly ? ReadOnlyOnDisk(abs + ".meta") : ReadOnlyOnDisk(abs);
                if (held != null)
                {
                    return (held == path ? string.Empty : "holds " + held + ", ") + "read-only on disk. Clear the attribute first";
                }
            }
            return null;
        }

        // True when restoring the file entry would copy only the bytes already there: the run kept it but never wrote
        // it, as when that write failed on a read-only file. Restore skips it, a read-only copy would refuse the copy.
        static bool AlreadyBack(string dir, Entry e)
        {
            if (!e.Existed || e.Folder || BuildSceneOf(e.Path) != null)
            {
                return false;
            }
            string abs = DotAssetPaths.Absolute(e.Path);
            if (!e.MetaOnly && !SameBytes(abs, DotAssetPaths.Combine(dir, e.Backup)))
            {
                return false;
            }
            return !e.HadMeta || SameBytes(abs + ".meta", DotAssetPaths.Combine(dir, e.Backup + ".meta"));
        }

        static bool SameBytes(string a, string b)
        {
            try
            {
                if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length)
                {
                    return false;
                }
                return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
            }
            catch (Exception)
            {
                // Unreadable counts as different, which restores it as before.
                return false;
            }
        }

        // The project path of a read-only file at abs, its .meta, or a file under it when abs is a folder, or null.
        // Restoring would stop at it with half the paths back.
        static string ReadOnlyOnDisk(string abs)
        {
            try
            {
                var files = new List<string> { abs, abs + ".meta" };
                if (Directory.Exists(abs))
                {
                    files.AddRange(Directory.GetFiles(abs, "*", SearchOption.AllDirectories));
                }
                foreach (string file in files)
                {
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    {
                        return DotAssetPaths.ToProjectRelative(file);
                    }
                }
            }
            catch (Exception)
            {
                // A folder that cannot be listed fails in the restore itself, with its reason.
            }
            return null;
        }

        // A path that could not be read hashes to "unreadable: <reason>". It never counts as unchanged, not even
        // against itself.
        public const string Unreadable = "unreadable";

        // Journals before 0.10.1 gave every folder this hash, whatever was in it. Now it starts a folder's hash.
        public const string LegacyFolder = "folder";

        // The file and its .meta as one hash, "folder <hash>" or "absent". For a Build Settings entry, the scene list.
        // Never throws.
        public static string Hash(string path)
        {
            if (BuildSceneOf(path) != null)
            {
                return SceneListHash(SceneList());
            }
            string abs = DotAssetPaths.Absolute(path);
            try
            {
                if (Directory.Exists(abs))
                {
                    return FolderHash(abs);
                }
                if (!File.Exists(abs))
                {
                    return "absent";
                }
                using (var sha = SHA1.Create())
                {
                    AddFile(sha, abs);
                    AddFile(sha, abs + ".meta");
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return Hex(sha.Hash);
                }
            }
            catch (Exception e)
            {
                return Unreadable + ": " + e.Message;
            }
        }

        // The relative path, length and last write time of every file under the folder, and the folder's own .meta.
        // Size and time stand for content, as in git's index: reading every file of a large folder costs too much at
        // each close and undo. A false "changed" only refuses an undo, the safe direction.
        static string FolderHash(string abs)
        {
            string root = abs.TrimEnd('/', '\\');
            var lines = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                lines.Add(DotAssetPaths.Normalize(file.Substring(root.Length + 1)) + " " + info.Length + " " + info.LastWriteTimeUtc.Ticks);
            }
            lines.Sort(StringComparer.Ordinal);
            using (var sha = SHA1.Create())
            {
                byte[] list = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray()));
                sha.TransformBlock(list, 0, list.Length, null, 0);
                AddFile(sha, root + ".meta");
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return LegacyFolder + " " + Hex(sha.Hash);
            }
        }

        // Streams the file into the hash, nothing when it does not exist. Shares the file, so a writer holding it open
        // does not stop the read.
        static void AddFile(HashAlgorithm sha, string abs)
        {
            if (!File.Exists(abs))
            {
                return;
            }
            using (var stream = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
            }
        }

        static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        // A save after the run (SceneSettle) changed path again. Its new state is what an undo must find, for the
        // path and for every journaled folder it lies in.
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
            bool changed = false;
            foreach (JToken e in json["Entries"] ?? new JArray())
            {
                string entry = (string)e["Path"];
                if (entry == path || path.StartsWith(entry + "/", StringComparison.Ordinal))
                {
                    e["After"] = Hash(entry);
                    changed = true;
                }
            }
            if (changed)
            {
                Json.WriteFile(file, json);
            }
        }

        // The journal folder for id, or for the newest journal when id is "last". Null when there is none.
        public static string Find(string id)
        {
            if (!Directory.Exists(Root))
            {
                return null;
            }
            RecoverInterrupted();
            if (id == "last")
            {
                return Newest(Directory.GetDirectories(Root).Select(DotAssetPaths.Normalize)
                    .Where(d => File.Exists(DotAssetPaths.Combine(d, FileName)))).FirstOrDefault();
            }
            string dir = DotAssetPaths.Combine(Root, id);
            return File.Exists(DotAssetPaths.Combine(dir, FileName)) ? dir : null;
        }

        // The id of the Interrupted journal the queue task taskId left, or null. Matched by task, not by run name,
        // so a crashed headless run of the same name is never taken for it.
        public static string InterruptedOf(string taskId)
        {
            if (string.IsNullOrEmpty(taskId) || !Directory.Exists(Root))
            {
                return null;
            }
            foreach (string dir in Newest(Directory.GetDirectories(Root).Select(DotAssetPaths.Normalize)))
            {
                JObject json = Load(DotAssetPaths.Combine(dir, FileName));
                if (json != null && (bool?)json["Interrupted"] == true && (string)json["Task"] == taskId)
                {
                    return (string)json["Id"];
                }
            }
            return null;
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
            foreach (string old in Newest(Directory.GetDirectories(Root)).Skip(KeepJournals).ToList())
            {
                try
                {
                    DeleteDir(old);
                }
                catch (Exception)
                {
                }
            }
        }

        // Journal folders newest first: by Seq, then by name for journals of the same Seq (those from before 0.9.5).
        static IEnumerable<string> Newest(IEnumerable<string> dirs)
        {
            return dirs.Select(d => new { Dir = d, Seq = SeqOf(d) })
                .OrderByDescending(x => x.Seq).ThenByDescending(x => x.Dir, StringComparer.Ordinal).Select(x => x.Dir);
        }

        // A journal's Seq, read from the first properties of journal.json or the log's header without parsing the
        // entries. 0 when it has none.
        static long SeqOf(string dir)
        {
            foreach (string name in new[] { FileName, LogName })
            {
                string file = DotAssetPaths.Combine(dir, name);
                if (!File.Exists(file))
                {
                    continue;
                }
                try
                {
                    using (var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(
                        new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))))
                    {
                        while (reader.Read())
                        {
                            if (reader.Depth == 1 && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                            {
                                if ((string)reader.Value == "Seq")
                                {
                                    reader.Read();
                                    return Convert.ToInt64(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
                                }
                                if ((string)reader.Value == "Entries")
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
                return 0;
            }
            return 0;
        }
    }

    // Every AssetDatabase write during a writing run passes through here first, so the journal keeps the file as it was.
    // During a dry run the same writes are refused: saves dropped, moves and deletes failed, creates removed afterwards.
    public sealed class JournalHook : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            if (RunJournal.InDryRun)
            {
                return paths.Where(p => !RunJournal.SaveRefusedInDryRun(p)).ToArray();
            }
            foreach (string path in paths)
            {
                // Import settings arrive as the .meta alone, the asset file is not written.
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    RunJournal.KeepMeta(path);
                }
                else
                {
                    RunJournal.Keep(path);
                }
            }
            return paths;
        }

        static void OnWillCreateAsset(string path)
        {
            if (RunJournal.InDryRun)
            {
                RunJournal.CreateInDryRun(path);
                return;
            }
            RunJournal.Keep(path);
        }

        static AssetMoveResult OnWillMoveAsset(string from, string to)
        {
            if (RunJournal.RefusedInDryRun(from, "move") || RunJournal.RefusedInDryRun(to, "move to"))
            {
                return AssetMoveResult.FailedMove;
            }
            RunJournal.Keep(from);
            RunJournal.Keep(to);
            return AssetMoveResult.DidNotMove;
        }

        static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (RunJournal.RefusedInDryRun(path, "delete"))
            {
                return AssetDeleteResult.FailedDelete;
            }
            RunJournal.Keep(path);
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
