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
            try
            {
                string[] argv = Environment.GetCommandLineArgs();
                string run = Value(argv, "-dotassetRun");
                string assets = Decode(Value(argv, "-dotassetAssetsB64")) ?? Value(argv, "-dotassetAssets") ?? string.Empty;
                string extra = Decode(Value(argv, "-dotassetArgsB64")) ?? Value(argv, "-dotassetArgs") ?? string.Empty;
                resultPath = Value(argv, "-dotassetResult");

                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

                // No editor check here: Unity itself refuses to open a project another instance holds,
                // so reaching this line means the project is ours.
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
            }
            EditorApplication.Exit(code);
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
