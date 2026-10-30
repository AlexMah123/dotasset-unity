using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // Puts the open editor into a state a run must wait for or be refused in, and takes it out again after -seconds,
    // across domain reloads too: -pause stops the queue taking tasks, -play enters play mode, -compile changes a
    // script under Assets/DotAssetSmoke so Unity recompiles and reloads once the run is done.
    // Usage: dotasset.py SmokeEditorState --args "-play -seconds=8"
    [InitializeOnLoad]
    public sealed class SmokeEditorStateCommand : DotAssetCommand
    {
        const string ResumeKey = "DotAsset.Smoke.ResumeQueueAt";
        const string StopPlayKey = "DotAsset.Smoke.StopPlayAt";

        static SmokeEditorStateCommand()
        {
            EditorApplication.update += Tick;
        }

        public override string Summary
        {
            get { return "Smoke test only: pauses the queue, enters play mode or triggers a compile, for -seconds"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "pause", "play", "compile", "seconds" }; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float resume = SessionState.GetFloat(ResumeKey, 0f);
            if (resume > 0f && now >= resume)
            {
                SessionState.EraseFloat(ResumeKey);
                TaskQueue.Enabled = true;
            }
            float stop = SessionState.GetFloat(StopPlayKey, 0f);
            if (stop > 0f && now >= stop)
            {
                if (EditorApplication.isPlaying)
                {
                    SessionState.EraseFloat(StopPlayKey);
                    EditorApplication.ExitPlaymode();
                }
                else if (now >= stop + 60)
                {
                    // Play mode never started, a compile error for instance. Nothing to stop.
                    SessionState.EraseFloat(StopPlayKey);
                }
            }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (!ctx.InEditor)
            {
                ctx.Log.Error("SmokeEditorState needs the editor open");
                return ExitCode.Refused;
            }
            float at = (float)(EditorApplication.timeSinceStartup + ctx.Args.GetInt("seconds", 8));
            if (ctx.Args.Has("pause"))
            {
                SessionState.SetFloat(ResumeKey, at);
                TaskQueue.Enabled = false;
            }
            else if (ctx.Args.Has("play"))
            {
                SessionState.SetFloat(StopPlayKey, at);
                EditorApplication.EnterPlaymode();
            }
            else if (ctx.Args.Has("compile"))
            {
                string path = "Assets/DotAssetSmoke/SmokeTouch.cs";
                File.WriteAllText(DotAssetPaths.Absolute(path),
                    "// " + System.DateTime.Now.Ticks + "\ninternal static class DotAssetSmokeTouch\n{\n}\n");
                AssetDatabase.ImportAsset(path);
            }
            else
            {
                ctx.Log.Error("give -pause, -play or -compile");
                return ExitCode.Refused;
            }
            return ExitCode.Success;
        }
    }
}
