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

        static TaskQueue()
        {
            if (Application.isBatchMode)
            {
                return;
            }
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
            if (processing || RunDispatcher.IsRunning)
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
            HandleQuitRequest();
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
            string refusal = force ? null : UnsavedWork();
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

        static string UnsavedWork()
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
            return "unsaved changes in " + string.Join(", ", dirty) + ". Save them first, or quit with --force to discard them";
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
            try
            {
                result = RunDispatcher.Run(runName, assets, extra, true);
            }
            catch (Exception e)
            {
                result = Refusal(runName, "the run threw before it could report: " + e.Message);
                result.ExitCode = (int)ExitCode.Failed;
            }
            WriteDone(donePath, result);
            TryDelete(processingPath);
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
        static void SweepStaleProcessing()
        {
            foreach (string file in Directory.GetFiles(ProcessingDir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                JObject task;
                string error;
                string runName = Json.TryReadFile(file, out task, out error) ? Json.Str(task, "RunName") : null;
                var result = Refusal(runName, "the editor stopped while this task was running. Check the targets before rerunning, a write may have reached disk");
                result.ExitCode = (int)ExitCode.Failed;
                WriteDone(DotAssetPaths.Combine(DoneDir, id + ".json"), result);
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
