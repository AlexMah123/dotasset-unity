using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif

namespace DotAsset
{
    // Assets the project names outside any scene or asset: what project settings reference (the render pipeline,
    // preloaded assets, config objects), Build Settings and Build Profile scenes, Addressables entries, AssetBundle
    // names. No asset references them, so GetDependencies over the project never finds them.
    public static class ProjectRoots
    {
        static readonly Regex Guid = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);

        // asset path -> where the project names it
        public static Dictionary<string, string> Named()
        {
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var scene in BuildScenes())
            {
                Add(named, scene.Key, scene.Value);
            }
#if UNITY_6000_0_OR_NEWER
            foreach (string guid in AssetDatabase.FindAssets("t:BuildProfile"))
            {
                Add(named, AssetDatabase.GUIDToAssetPath(guid), "Build Profiles");
            }
#endif
            bool text = AssetScan.IsForceText();
            foreach (string file in Directory.GetFiles(Path.Combine(DotAssetPaths.ProjectRoot, "ProjectSettings"), "*.asset"))
            {
                string settings = "ProjectSettings/" + Path.GetFileName(file);
                // Read through the loaded object so binary serialization works too.
                try
                {
                    foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(settings))
                    {
                        if (o != null)
                        {
                            AddReferences(named, new SerializedObject(o), settings);
                        }
                    }
                }
                catch (Exception)
                {
                }
                if (text)
                {
                    foreach (Match m in Guid.Matches(File.ReadAllText(file)))
                    {
                        Add(named, AssetDatabase.GUIDToAssetPath(m.Groups[1].Value), settings);
                    }
                }
            }
            AddAddressables(named);
            foreach (string bundle in AssetDatabase.GetAllAssetBundleNames())
            {
                foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(bundle))
                {
                    Add(named, path, "AssetBundle " + bundle);
                }
            }
            return named;
        }

        // Enabled scenes of the build: Build Settings, and on Unity 6 the shared list and every Build Profile that
        // keeps its own. Scene path -> where it is listed.
        public static Dictionary<string, string> BuildScenes()
        {
            var scenes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddScenes(scenes, EditorBuildSettings.scenes, "Build Settings");
#if UNITY_6000_0_OR_NEWER
            AddScenes(scenes, EditorBuildSettings.globalScenes, "Build Settings");
            foreach (string guid in AssetDatabase.FindAssets("t:BuildProfile"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
                if (profile != null && profile.overrideGlobalScenes)
                {
                    AddScenes(scenes, profile.scenes, "Build Profile " + path);
                }
            }
#endif
            return scenes;
        }

        static void AddScenes(Dictionary<string, string> scenes, EditorBuildSettingsScene[] list, string source)
        {
            foreach (EditorBuildSettingsScene s in list)
            {
                if (s.enabled && !string.IsNullOrEmpty(s.path) && !scenes.ContainsKey(s.path))
                {
                    scenes.Add(s.path, source);
                }
            }
        }

        static void AddReferences(Dictionary<string, string> named, SerializedObject so, string source)
        {
            SerializedProperty p = so.GetIterator();
            while (p.Next(true))
            {
                if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null)
                {
                    Add(named, AssetDatabase.GetAssetPath(p.objectReferenceValue), source);
                }
            }
        }

        // Addressables keep their entries as GUID strings in the groups. Read through SerializedObject, so the
        // package needs no reference to Addressables.
        static void AddAddressables(Dictionary<string, string> named)
        {
            UnityEngine.Object settings;
            if (!EditorBuildSettings.TryGetConfigObject("com.unity.addressableassets", out settings) || settings == null)
            {
                return;
            }
            Add(named, AssetDatabase.GetAssetPath(settings), "Addressables");
            SerializedProperty groups = new SerializedObject(settings).FindProperty("m_GroupAssets");
            for (int i = 0; groups != null && groups.isArray && i < groups.arraySize; i++)
            {
                UnityEngine.Object group = groups.GetArrayElementAtIndex(i).objectReferenceValue;
                if (group == null)
                {
                    continue;
                }
                Add(named, AssetDatabase.GetAssetPath(group), "Addressables");
                SerializedProperty entries = new SerializedObject(group).FindProperty("m_SerializeEntries");
                for (int j = 0; entries != null && entries.isArray && j < entries.arraySize; j++)
                {
                    SerializedProperty guid = entries.GetArrayElementAtIndex(j).FindPropertyRelative("m_GUID");
                    if (guid != null)
                    {
                        Add(named, AssetDatabase.GUIDToAssetPath(guid.stringValue), "Addressables group " + group.name);
                    }
                }
            }
        }

        // A folder stands for everything inside it.
        static void Add(Dictionary<string, string> named, string path, string source)
        {
            if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)))
            {
                return;
            }
            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (string inside in AssetScan.All(new[] { path }))
                {
                    if (!named.ContainsKey(inside))
                    {
                        named.Add(inside, source);
                    }
                }
            }
            else if (!named.ContainsKey(path))
            {
                named.Add(path, source);
            }
        }
    }
}
