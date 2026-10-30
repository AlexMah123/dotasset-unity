using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // File contract between the wrapper and the open editor, all under <Project>/Library/DotAsset/Queue:
    //   .alive                 heartbeat JSON (Pid, State, versions), rewritten every HeartbeatSeconds
    //   pending/<id>.json      the wrapper writes a task {Id, RunName, Assets[], ExtraArgs}
    //   processing/<id>.json   the editor claims a task by moving it here
    //   done/<id>.json         the editor writes the RunResult here, the wrapper reads and deletes it
    //   quit.json              the wrapper asks the editor to close {Force}, taken once no task is pending
    //   quit.result.json       the editor's answer {Quit, Error}, written before it exits
    //   openscene.json         the wrapper asks the editor to open scenes {Scenes[], Force}
    //   openscene.result.json  the editor's answer {Opened, Previous[], PreviousActive, Error}
    // Scripts~/dotasset.py holds the same constants. Change both together.
    public static class TaskQueueProtocol
    {
        public const string QueueRelative = "Library/DotAsset/Queue";
        public const string Pending = "pending";
        public const string Processing = "processing";
        public const string Done = "done";
        public const string AliveFile = ".alive";
        public const string QuitRequest = "quit.json";
        public const string QuitResult = "quit.result.json";
        public const string OpenSceneRequest = "openscene.json";
        public const string OpenSceneResult = "openscene.result.json";
        public const double HeartbeatSeconds = 2.0;
        public const double FreshnessSeconds = 15.0;
    }

    [InitializeOnLoad]
    public static class TaskQueue
    {
        static double nextHeartbeat;
        static double nextScan;
        static bool ready;
        static bool startAttempted;
        static bool processing;
        static string lastState;

        static string QueueRoot
        {
            get { return DotAssetPaths.Combine(DotAssetPaths.ProjectRoot, TaskQueueProtocol.QueueRelative); }
        }

        static string PendingDir
        {
            get { return DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.Pending); }
        }

        static string ProcessingDir
        {
            get { return DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.Processing); }
        }

        static string DoneDir
        {
            get { return DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.Done); }
        }

        static string AlivePath
        {
            get { return DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.AliveFile); }
        }

        // Disabled with the scripting define DOTASSET_NO_QUEUE, or per editor session from the window.
        public static bool Enabled
        {
            get
            {
#if DOTASSET_NO_QUEUE
                return false;
#else
                return !SessionState.GetBool("DotAsset.QueuePaused", false);
#endif
            }
            set
            {
                SessionState.SetBool("DotAsset.QueuePaused", !value);
                WriteHeartbeat();
            }
        }

        static bool hooked;

        static TaskQueue()
        {
            // Headless Unity serves the queue only when started through Serve. The flag survives domain reloads.
            if (Application.isBatchMode && !SessionState.GetBool("DotAsset.Served", false))
            {
                return;
            }
            Hook();
        }

        // Keeps a headless Unity running as a warm host for the queue, until a quit request.
        public static void Serve()
        {
            SessionState.SetBool("DotAsset.Served", true);
            Hook();
            UnityEngine.Debug.Log("[DotAsset] serving the task queue headless, pid " + Process.GetCurrentProcess().Id);
        }

        static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            EditorApplication.update += Tick;
            EditorApplication.quitting += OnQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        static void Start()
        {
            try
            {
                Directory.CreateDirectory(PendingDir);
                Directory.CreateDirectory(ProcessingDir);
                Directory.CreateDirectory(DoneDir);
                SweepStaleProcessing();
                DeleteOldDone();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[DotAsset] task queue could not start: " + e.Message);
                return;
            }
            ready = true;
            WriteHeartbeat();
        }

        static string CurrentState()
        {
            if (!ready)
            {
                return "starting";
            }
            if (!Enabled)
            {
                return "paused";
            }
            if (EditorApplication.isCompiling)
            {
                return "compiling";
            }
            if (EditorApplication.isUpdating)
            {
                return "importing";
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "playmode";
            }
            if (processing || settling != null || RunDispatcher.IsRunning)
            {
                return "busy";
            }
            return "ready";
        }

        static void Tick()
        {
            // Started from the first update after the domain loaded, not from EditorApplication.delayCall: an editor
            // that never gets focus (launched by --launch on a remote machine) was seen never to run its delay calls.
            if (!startAttempted)
            {
                startAttempted = true;
                Start();
            }
            double now = EditorApplication.timeSinceStartup;
            if (settling != null)
            {
                Settle(now);
            }
            string state = CurrentState();
            if (now >= nextHeartbeat || state != lastState)
            {
                WriteHeartbeat();
                nextHeartbeat = now + TaskQueueProtocol.HeartbeatSeconds;
            }
            if (state != "ready" || now < nextScan)
            {
                return;
            }
            nextScan = now + 0.25;

            string next = NextPending();
            if (next != null)
            {
                // The run blocks this thread, so the heartbeat says busy for as long as it takes.
                processing = true;
                WriteHeartbeat();
                try
                {
                    ProcessTask(next);
                }
                finally
                {
                    processing = false;
                }
                WriteHeartbeat();
                return;
            }
            HandleOpenSceneRequest();
            HandleQuitRequest();
        }

        // dotasset.py --open-scene, for whoever is not at the machine. The first scene replaces what is open, the
        // rest are added. Refused while an open scene has unsaved changes, unless forced. The answer lists what was
        // open before, so the caller can put it back.
        static void HandleOpenSceneRequest()
        {
            string requestPath = DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.OpenSceneRequest);
            if (!File.Exists(requestPath))
            {
                return;
            }
            JObject request;
            string refusal;
            bool read = Json.TryReadFile(requestPath, out request, out refusal);
            TryDelete(requestPath);

            var previous = new JArray();
            UnityEngine.SceneManagement.Scene active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                {
                    previous.Add(scene.path);
                }
            }

            var scenes = new List<string>();
            if (read)
            {
                var list = Json.Get(request, "Scenes") as JArray;
                if (list == null || list.Count == 0 || list.Any(s => s.Type != JTokenType.String))
                {
                    refusal = "\"Scenes\" must be a list of scene paths";
                }
                else
                {
                    scenes.AddRange(list.Select(s => DotAssetPaths.Normalize((string)s)));
                    string missing = scenes.FirstOrDefault(s => AssetDatabase.GetMainAssetTypeAtPath(s) != typeof(SceneAsset));
                    if (missing != null)
                    {
                        refusal = "no scene at " + missing;
                    }
                }
            }
            if (refusal == null && !Json.Bool(request, "Force", false))
            {
                refusal = UnsavedWork("open the scene");
            }
            if (refusal == null)
            {
                try
                {
                    if (UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null)
                    {
                        UnityEditor.SceneManagement.StageUtility.GoToMainStage();
                    }
                    for (int i = 0; i < scenes.Count; i++)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenes[i],
                            i == 0 ? UnityEditor.SceneManagement.OpenSceneMode.Single : UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    }
                }
                catch (Exception e)
                {
                    refusal = "Unity could not open " + string.Join(", ", scenes) + ": " + e.Message;
                }
            }
            try
            {
                Json.WriteFile(DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.OpenSceneResult), new JObject
                {
                    ["Opened"] = refusal == null,
                    ["Previous"] = previous,
                    ["PreviousActive"] = string.IsNullOrEmpty(active.path) ? null : active.path,
                    ["Error"] = refusal,
                });
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[DotAsset] could not answer the open scene request: " + e.Message);
            }
            if (refusal != null)
            {
                UnityEngine.Debug.LogWarning("[DotAsset] open scene request refused: " + refusal);
            }
        }

        // dotasset.py --quit. Refused while a scene or Prefab Mode has unsaved changes, unless forced, because
        // EditorApplication.Exit closes without asking.
        static void HandleQuitRequest()
        {
            string requestPath = DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.QuitRequest);
            if (!File.Exists(requestPath))
            {
                return;
            }
            JObject request;
            string error;
            bool force = Json.TryReadFile(requestPath, out request, out error) && Json.Bool(request, "Force", false);
            TryDelete(requestPath);
            string refusal = force ? null : UnsavedWork("quit");
            try
            {
                Json.WriteFile(DotAssetPaths.Combine(QueueRoot, TaskQueueProtocol.QuitResult), new JObject
                {
                    ["Quit"] = refusal == null,
                    ["Error"] = refusal,
                });
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[DotAsset] could not answer the quit request: " + e.Message);
                return;
            }
            if (refusal != null)
            {
                UnityEngine.Debug.LogWarning("[DotAsset] quit request refused: " + refusal);
                return;
            }
            UnityEngine.Debug.Log("[DotAsset] closing the editor on request" + (force ? ", unsaved changes discarded" : string.Empty));
            TryDelete(AlivePath);
            EditorApplication.Exit(0);
        }

        static string UnsavedWork(string action)
        {
            var dirty = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirty.Add(string.IsNullOrEmpty(scene.path) ? "an Untitled scene" : scene.path);
                }
            }
            UnityEditor.SceneManagement.PrefabStage stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty)
            {
                dirty.Add(stage.assetPath + " in Prefab Mode");
            }
            if (dirty.Count == 0)
            {
                return null;
            }
            return "unsaved changes in " + string.Join(", ", dirty) + ". Save them first, or " + action + " with --force to discard them";
        }

        static string NextPending()
        {
            try
            {
                if (!Directory.Exists(PendingDir))
                {
                    return null;
                }
                return new DirectoryInfo(PendingDir).GetFiles("*.json")
                    .OrderBy(f => f.CreationTimeUtc)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void ProcessTask(string pendingPath)
        {
            string id = Path.GetFileNameWithoutExtension(pendingPath);
            string processingPath = DotAssetPaths.Combine(ProcessingDir, id + ".json");
            string donePath = DotAssetPaths.Combine(DoneDir, id + ".json");
            try
            {
                File.Move(pendingPath, processingPath);
            }
            catch (Exception)
            {
                // The wrapper withdrew it, or the file is still being written. Try again next scan.
                return;
            }

            JObject task;
            string error;
            if (!Json.TryReadFile(processingPath, out task, out error))
            {
                WriteDone(donePath, Refusal(null, error));
                TryDelete(processingPath);
                return;
            }

            string runName = Json.Str(task, "RunName");
            var assets = new List<string>();
            var assetsToken = Json.Get(task, "Assets") as JArray;
            if (assetsToken != null)
            {
                // Json.Str-style reading: a number or object in the list must not throw before a result is written.
                assets.AddRange(assetsToken.Select(t => t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None)).Where(s => !string.IsNullOrEmpty(s)));
            }
            string extra = Json.Str(task, "ExtraArgs", string.Empty);

            RunResult result;
            SceneSession.SavedOpenScenes.Clear();
            try
            {
                result = RunDispatcher.Run(runName, assets, extra, !Application.isBatchMode);
            }
            catch (Exception e)
            {
                result = Refusal(runName, "the run threw before it could report: " + e.Message);
                result.ExitCode = (int)ExitCode.Failed;
            }
            if (SceneSession.SavedOpenScenes.Count > 0)
            {
                // Report the run only after Unity's own follow-up on those scenes, see Settle. The result waits on
                // disk, so a domain reload in the meantime still reports it (SweepStaleProcessing).
                settling = new Settling
                {
                    Result = result,
                    DonePath = donePath,
                    ProcessingPath = processingPath,
                    Scenes = new List<string>(SceneSession.SavedOpenScenes),
                    Started = EditorApplication.timeSinceStartup,
                    LastDirty = EditorApplication.timeSinceStartup,
                    UndoGroup = Undo.GetCurrentGroup(),
                };
                SceneSession.SavedOpenScenes.Clear();
                WriteDone(SettlingPath(processingPath), result);
                return;
            }
            WriteDone(donePath, result);
            TryDelete(processingPath);
        }

        sealed class Settling
        {
            public RunResult Result;
            public string DonePath;
            public string ProcessingPath;
            public List<string> Scenes;
            public double Started;
            public double LastDirty;
            public int UndoGroup;
            public readonly List<string> SavedAgain = new List<string>();
        }

        // A run that saved a scene open in the editor is reported once Unity has settled: some of what was just added
        // is completed by Unity on a later frame (a new Light gets its baking state) and that marks the scene dirty
        // again. Each update saves such a scene again. The run is done after SettleQuietSeconds without a change,
        // at most SettleMaxSeconds after it ended. The next task waits, so it never finds the scene dirty.
        // Unity's follow-up records no undo, a person's edit starts a new undo group. Once the group moves, nothing
        // is saved again and the run ends at once: the scene now holds their unsaved work, not the run's.
        const double SettleQuietSeconds = 0.4;
        const double SettleMaxSeconds = 3.0;
        static Settling settling;

        static void Settle(double now)
        {
            bool edited = Undo.GetCurrentGroup() != settling.UndoGroup;
            var leftDirty = new List<string>();
            foreach (string path in settling.Scenes)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                {
                    if (edited)
                    {
                        leftDirty.Add(path);
                        continue;
                    }
                    settling.LastDirty = now;
                    if (UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene) && !settling.SavedAgain.Contains(path))
                    {
                        settling.SavedAgain.Add(path);
                    }
                    RunJournal.Rehash(path);
                }
            }
            if (!edited && now - settling.LastDirty < SettleQuietSeconds && now - settling.Started < SettleMaxSeconds)
            {
                return;
            }
            foreach (string path in settling.SavedAgain)
            {
                settling.Result.Lines.Add("I saved " + path + " again after Unity updated it");
            }
            foreach (string path in leftDirty)
            {
                settling.Result.Lines.Add("W " + path + " was edited in the editor right after the run saved it. Those changes are left unsaved");
                settling.Result.Warnings++;
            }
            WriteDone(settling.DonePath, settling.Result);
            TryDelete(SettlingPath(settling.ProcessingPath));
            TryDelete(settling.ProcessingPath);
            settling = null;
        }

        // The result of a run still settling, next to its task in processing/. Not *.json, so it is never taken for a task.
        static string SettlingPath(string processingPath)
        {
            return Path.ChangeExtension(processingPath, ".settling");
        }

        static RunResult Refusal(string runName, string error)
        {
            return new RunResult
            {
                RunName = runName,
                ExitCode = (int)ExitCode.Refused,
                Error = error,
                Summary = (runName ?? "task") + " refused: " + error,
                Errors = 1,
                Lines = new List<string> { "E " + error },
            };
        }

        static void WriteDone(string path, RunResult result)
        {
            try
            {
                Json.WriteFile(path, result.ToJson());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[DotAsset] could not write " + path + ": " + e.Message);
            }
        }

        // A task left in processing/ means the editor stopped in the middle of it. Report it instead of rerunning it.
        // One that finished and was settling when the domain reloaded reports its own result.
        static void SweepStaleProcessing()
        {
            foreach (string file in Directory.GetFiles(ProcessingDir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                string donePath = DotAssetPaths.Combine(DoneDir, id + ".json");
                JObject settled;
                string unread;
                if (Json.TryReadFile(SettlingPath(file), out settled, out unread))
                {
                    var lines = Json.Get(settled, "Lines") as JArray;
                    if (lines != null)
                    {
                        lines.Add("W Unity reloaded before the run settled. Check the scenes it saved for unsaved changes");
                        settled["Warnings"] = Json.Int(settled, "Warnings", 0) + 1;
                    }
                    try
                    {
                        Json.WriteFile(donePath, settled);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError("[DotAsset] could not write " + donePath + ": " + e.Message);
                    }
                    TryDelete(SettlingPath(file));
                    TryDelete(file);
                    continue;
                }
                JObject task;
                string error;
                string runName = Json.TryReadFile(file, out task, out error) ? Json.Str(task, "RunName") : null;
                var result = Refusal(runName, "the editor stopped while this task was running. Check the targets before rerunning, a write may have reached disk");
                result.ExitCode = (int)ExitCode.Failed;
                WriteDone(donePath, result);
                TryDelete(file);
            }
        }

        static void DeleteOldDone()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var f in new DirectoryInfo(DoneDir).GetFiles("*.json"))
            {
                if (f.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(f.FullName);
                }
            }
        }

        static void WriteHeartbeat()
        {
            string state = CurrentState();
            lastState = state;
            try
            {
                Directory.CreateDirectory(QueueRoot);
                string tmp = AlivePath + ".tmp";
                File.WriteAllText(tmp, Heartbeat(state), new UTF8Encoding(false));
                if (File.Exists(AlivePath))
                {
                    File.Replace(tmp, AlivePath, null);
                }
                else
                {
                    File.Move(tmp, AlivePath);
                }
            }
            catch (Exception)
            {
                // A reader had the file open. The next heartbeat retries.
            }
        }

        static void OnBeforeReload()
        {
            // Keep .alive across a domain reload, the wrapper sees State=reloading and keeps waiting.
            ready = false;
            lastState = "reloading";
            try
            {
                File.WriteAllText(AlivePath, Heartbeat(lastState), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        static string Heartbeat(string state)
        {
            var alive = new JObject
            {
                ["Pid"] = Process.GetCurrentProcess().Id,
                ["State"] = state,
                ["UnityVersion"] = Application.unityVersion,
                ["DotAssetVersion"] = DotAssetVersion.Version,
                ["ProjectPath"] = DotAssetPaths.ProjectRoot,
                ["Served"] = Application.isBatchMode,
                ["UpdatedUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            return alive.ToString(Newtonsoft.Json.Formatting.None);
        }

        static void OnQuitting()
        {
            TryDelete(AlivePath);
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        // For the window.
        public static string State
        {
            get { return CurrentState(); }
        }

        public static int PendingCount
        {
            get
            {
                try
                {
                    return Directory.Exists(PendingDir) ? Directory.GetFiles(PendingDir, "*.json").Length : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }
    }
}
