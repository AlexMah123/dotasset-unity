using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Window > DotAsset: queue state, the runs of this session and earlier ones from their log files,
    // each run's full log.
    public sealed class DotAssetWindow : EditorWindow
    {
        [MenuItem("Window/DotAsset")]
        static void Open()
        {
            GetWindow<DotAssetWindow>("DotAsset");
        }

        static readonly List<RunResult> session = new List<RunResult>();

        [InitializeOnLoadMethod]
        static void Hook()
        {
            RunDispatcher.RunFinished -= OnRunFinished;
            RunDispatcher.RunFinished += OnRunFinished;
        }

        static void OnRunFinished(RunResult result)
        {
            session.Insert(0, result);
            if (session.Count > 50)
            {
                session.RemoveAt(session.Count - 1);
            }
            foreach (var w in Resources.FindObjectsOfTypeAll<DotAssetWindow>())
            {
                w.Repaint();
            }
        }

        Vector2 listScroll;
        Vector2 logScroll;
        string selectedLog;
        string selectedText;
        bool warningsOnly;
        double nextRefresh;
        string[] logFiles = new string[0];

        void OnEnable()
        {
            RefreshLogs();
        }

        void Update()
        {
            if (EditorApplication.timeSinceStartup > nextRefresh)
            {
                nextRefresh = EditorApplication.timeSinceStartup + 2;
                Repaint();
            }
        }

        void RefreshLogs()
        {
            try
            {
                logFiles = Directory.Exists(DotAssetPaths.LogRoot)
                    ? Directory.GetFiles(DotAssetPaths.LogRoot, "*.log").OrderByDescending(f => Path.GetFileName(f)).Take(100).ToArray()
                    : new string[0];
            }
            catch (Exception)
            {
                logFiles = new string[0];
            }
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Queue: " + TaskQueue.State + "   pending: " + TaskQueue.PendingCount + "   v" + DotAssetVersion.Version, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                bool enabled = TaskQueue.Enabled;
                bool toggled = GUILayout.Toggle(enabled, "Accept tasks", EditorStyles.toolbarButton);
                if (toggled != enabled)
                {
                    TaskQueue.Enabled = toggled;
                }
                warningsOnly = GUILayout.Toggle(warningsOnly, "Warnings and errors", EditorStyles.toolbarButton);
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    RefreshLogs();
                }
                if (GUILayout.Button(new GUIContent("Copy dotasset.py", "Copy the wrapper to the project root, next to Assets/"), EditorStyles.toolbarButton))
                {
                    CopyWrapper();
                }
                if (GUILayout.Button("Open folder", EditorStyles.toolbarButton))
                {
                    Directory.CreateDirectory(DotAssetPaths.Root);
                    EditorUtility.RevealInFinder(DotAssetPaths.Root);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(listScroll, GUILayout.Width(Mathf.Max(260, position.width * 0.38f))))
                {
                    listScroll = scroll.scrollPosition;
                    if (session.Count > 0)
                    {
                        GUILayout.Label("This session", EditorStyles.boldLabel);
                        foreach (RunResult r in session)
                        {
                            if (GUILayout.Button(Icon(r.ExitCode) + " " + r.Summary, EditorStyles.label))
                            {
                                Select(r.LogFile, string.Join("\n", r.Lines));
                            }
                        }
                        GUILayout.Space(6);
                    }
                    GUILayout.Label("Run logs", EditorStyles.boldLabel);
                    foreach (string file in logFiles)
                    {
                        if (GUILayout.Button(Path.GetFileNameWithoutExtension(file), EditorStyles.label))
                        {
                            string text;
                            try
                            {
                                text = File.ReadAllText(file);
                            }
                            catch (Exception e)
                            {
                                text = e.Message;
                            }
                            Select(file, text);
                        }
                    }
                    if (logFiles.Length == 0 && session.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No runs yet. Runs arrive from Scripts~/dotasset.py while this editor is open.", MessageType.Info);
                    }
                }

                using (var scroll = new EditorGUILayout.ScrollViewScope(logScroll))
                {
                    logScroll = scroll.scrollPosition;
                    if (selectedText != null)
                    {
                        GUILayout.Label(selectedLog != null ? DotAssetPaths.ToProjectRelative(selectedLog) : "", EditorStyles.miniLabel);
                        string shown = warningsOnly
                            ? string.Join("\n", selectedText.Split('\n').Where(l => l.StartsWith("W ") || l.StartsWith("E ")))
                            : selectedText;
                        EditorGUILayout.TextArea(shown, GUILayout.ExpandHeight(true));
                    }
                }
            }
        }

        // The package may live in Library/PackageCache under a hashed folder, a copy at the root is easier to call.
        static void CopyWrapper()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DotAssetWindow).Assembly);
            if (info == null)
            {
                EditorUtility.DisplayDialog("DotAsset", "Could not locate the package folder.", "OK");
                return;
            }
            string source = Path.Combine(info.resolvedPath, "Scripts~", "dotasset.py");
            string target = Path.Combine(DotAssetPaths.ProjectRoot, "dotasset.py");
            try
            {
                File.Copy(source, target, true);
                Debug.Log("[DotAsset] copied the wrapper to " + target);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("DotAsset", "Could not copy " + source + ": " + e.Message, "OK");
            }
        }

        void Select(string file, string text)
        {
            selectedLog = file;
            selectedText = text;
            logScroll = Vector2.zero;
        }

        static string Icon(int exitCode)
        {
            switch ((ExitCode)exitCode)
            {
                case ExitCode.Success:
                    return "ok ";
                case ExitCode.IssuesFound:
                    return "!! ";
                default:
                    return "xx ";
            }
        }
    }
}
