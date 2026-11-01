using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Headless entry point. The wrapper launches:
    //   Unity -batchmode -nographics -projectPath <dir> -executeMethod DotAsset.Cli.Run
    //         -dotassetRun <RunName> [-dotassetAssetsB64 <base64 csv>] [-dotassetArgsB64 <base64 args>] [-dotassetResult <file>] -logFile <file>
    // Plain -dotassetAssets / -dotassetArgs work too when calling by hand. Base64 keeps quoting intact across shells.
    // The process exits with the run's ExitCode.
    public static class Cli
    {
        // Entry for dotasset.py --serve: returns without exiting, so headless Unity stays up and serves the task queue.
        public static void Serve()
        {
            TaskQueue.Serve();
        }

        public static void Run()
        {
            int code = (int)ExitCode.Failed;
            string resultPath = null;
            string run = null;
            string extra = null;
            try
            {
                string[] argv = Environment.GetCommandLineArgs();
                resultPath = Value(argv, "-dotassetResult");
                run = Value(argv, "-dotassetRun");
                string assets = Decode(Value(argv, "-dotassetAssetsB64")) ?? Value(argv, "-dotassetAssets") ?? string.Empty;
                extra = Decode(Value(argv, "-dotassetArgsB64")) ?? Value(argv, "-dotassetArgs") ?? string.Empty;

                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

                // No editor check here: Unity itself refuses to open a project another instance holds,
                // so reaching this line means the project is ours.
                RunJournal.TaskId = "headless";
                RunResult result = RunDispatcher.Run(run, RunArgs.SplitList(assets), extra, false);
                if (!string.IsNullOrEmpty(resultPath))
                {
                    Json.WriteFile(resultPath, result.ToJson());
                }
                code = result.ExitCode;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = (int)ExitCode.Failed;
                WriteThrown(resultPath, run, extra, e);
            }
            EditorApplication.Exit(code);
        }

        // A result for a run that threw past the dispatcher, so the wrapper does not have to guess from the Unity log.
        // A run that could write may have, its journal close and rollback run outside the run's own try.
        static void WriteThrown(string resultPath, string run, string extra, Exception e)
        {
            if (string.IsNullOrEmpty(resultPath))
            {
                return;
            }
            string error = "the run threw before it could report: " + e.Message;
            var result = new RunResult
            {
                RunName = run,
                ExitCode = (int)ExitCode.Failed,
                Error = error,
                Summary = (run ?? "run") + " failed: " + error,
                Errors = 1,
                Lines = { "E " + error },
            };
            if (run != null && Groups.CouldWrite(run, extra))
            {
                result.Outcome = "unknown";
            }
            try
            {
                Json.WriteFile(resultPath, result.ToJson());
            }
            catch (Exception)
            {
                // No result file: the wrapper falls back to the Unity log, which has the exception.
            }
        }

        static string Value(string[] argv, string key)
        {
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (string.Equals(argv[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return argv[i + 1];
                }
            }
            return null;
        }

        static string Decode(string b64)
        {
            if (string.IsNullOrEmpty(b64))
            {
                return null;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
    }
}
