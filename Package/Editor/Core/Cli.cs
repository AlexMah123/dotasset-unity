using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Assetops
{
    // Headless entry point. The wrapper launches:
    //   Unity -batchmode -nographics -projectPath <dir> -executeMethod Assetops.Cli.Run
    //         -assetopsRun <RunName> [-assetopsAssetsB64 <base64 csv>] [-assetopsArgsB64 <base64 args>] [-assetopsResult <file>] -logFile <file>
    // Plain -assetopsAssets / -assetopsArgs work too when calling by hand. Base64 keeps quoting intact across shells.
    // The process exits with the run's ExitCode.
    public static class Cli
    {
        public static void Run()
        {
            int code = (int)ExitCode.Failed;
            string resultPath = null;
            try
            {
                string[] argv = Environment.GetCommandLineArgs();
                string run = Value(argv, "-assetopsRun");
                string assets = Decode(Value(argv, "-assetopsAssetsB64")) ?? Value(argv, "-assetopsAssets") ?? string.Empty;
                string extra = Decode(Value(argv, "-assetopsArgsB64")) ?? Value(argv, "-assetopsArgs") ?? string.Empty;
                resultPath = Value(argv, "-assetopsResult");

                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

                // No editor check here: Unity itself refuses to open a project another instance holds,
                // so reaching this line means the project is ours.
                RunResult result = RunDispatcher.Run(run, Split(assets), extra, false);
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

        internal static List<string> Split(string csv)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(csv))
            {
                return list;
            }
            foreach (string part in csv.Split(','))
            {
                string p = part.Trim();
                if (p.Length > 0)
                {
                    list.Add(p);
                }
            }
            return list;
        }
    }
}
