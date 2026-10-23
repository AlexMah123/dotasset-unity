using System.Collections.Generic;
using UnityEngine;

namespace AssetWorkbench
{
    // The log of one run. Every line is kept for the result JSON and the run log file.
    // Headless: every line also goes to the Unity log, without stack traces.
    // In the editor: only warnings, errors and the summary reach the Console, the rest stays in the run log
    // and the Asset Workbench window.
    public sealed class RunLog
    {
        public readonly List<string> Lines = new List<string>();
        public int Warnings { get; private set; }
        public int Errors { get; private set; }
        public string FirstError { get; private set; }

        readonly string prefix;
        readonly bool echoInfo;

        public RunLog(string runName, bool inEditor)
        {
            prefix = "[AssetWorkbench] " + runName + ": ";
            echoInfo = !inEditor;
        }

        public void Info(string message)
        {
            Lines.Add("I " + message);
            if (echoInfo)
            {
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}{1}", prefix, message);
            }
        }

        public void Warn(string message)
        {
            Warnings++;
            Lines.Add("W " + message);
            Debug.LogFormat(LogType.Warning, LogOption.NoStacktrace, null, "{0}{1}", prefix, message);
        }

        public void Error(string message)
        {
            Errors++;
            if (FirstError == null)
            {
                FirstError = message;
            }
            Lines.Add("E " + message);
            Debug.LogFormat(LogType.Error, LogOption.NoStacktrace, null, "{0}{1}", prefix, message);
        }

        // Errors raised by Unity itself while a target is written, captured by the dispatcher's log hook.
        internal void EngineError(string message)
        {
            Errors++;
            if (FirstError == null)
            {
                FirstError = message;
            }
            Lines.Add("E [Unity] " + message);
        }

        internal void EngineWarning(string message)
        {
            Warnings++;
            Lines.Add("W [Unity] " + message);
        }
    }
}
