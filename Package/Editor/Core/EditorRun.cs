using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // A run started inside the open editor by a caller other than the editor queue (the Unity CLI adapter in
    // Editor/UnityCli). It waits for a run still settling, and is reported once the open scenes it saved have
    // settled, as a queued run is. Call it on the main thread, it continues there.
    internal static class EditorRun
    {
        public static async Task<RunResult> RunAsync(string runName, IList<string> assets, string extraArgs, bool apply)
        {
            // Checked again after each wait, in the same update as the run, so no queued task slips in between.
            while (SceneSettle.Busy)
            {
                await NextUpdate();
            }
            SceneSession.SavedOpenScenes.Clear();
            // A key given twice keeps the last value, so -apply here wins over an -apply=false in the arguments.
            // Only Edit and Migrate take -apply, any other run refuses it as an unknown argument.
            DotAssetCommand cmd = CommandRegistry.Find(runName);
            if (apply && cmd != null && Groups.IsDryRunByDefault(cmd.Group))
            {
                extraArgs = (extraArgs ?? string.Empty) + " -apply";
            }
            RunResult result = RunDispatcher.Run(runName, assets, extraArgs, !Application.isBatchMode);
            var settled = new TaskCompletionSource<RunResult>();
            if (!SceneSettle.Begin(result, settled.SetResult))
            {
                return result;
            }
            return await settled.Task;
        }

        // The run as one text, for a caller that can only report a failure as a message: the summary, then its
        // lines and outputs.
        public static string Describe(RunResult result)
        {
            var text = new StringBuilder();
            text.Append("exit ").Append(result.ExitCode).Append(", ").Append(result.Summary);
            foreach (string line in result.Lines)
            {
                text.Append('\n').Append(line);
            }
            foreach (string output in result.Outputs)
            {
                text.Append("\noutput ").Append(output);
            }
            return text.ToString();
        }

        static Task NextUpdate()
        {
            var next = new TaskCompletionSource<bool>();
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                EditorApplication.update -= tick;
                next.SetResult(true);
            };
            EditorApplication.update += tick;
            return next.Task;
        }
    }
}
