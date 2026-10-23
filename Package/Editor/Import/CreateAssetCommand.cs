using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Creates assets from a spec, in order, so later entries can reference earlier ones:
    //   {"Kind": "Folder", "Path": "Assets/Data/Items"}
    //   {"Kind": "ScriptableObject", "Type": "ItemDefinition", "Path": "Assets/Data/Items/Sword.asset", "Properties": {...}}
    //   {"Kind": "Material", "Path": "Assets/Mat/Red.mat", "Shader": "Universal Render Pipeline/Lit", "Properties": {...}}
    //   {"Kind": "Prefab", "Path": "Assets/Prefabs/Crate.prefab", "Root": "Crate", "Components": [...], "Ops": [...]}
    //   {"Kind": "PrefabVariant", "Path": "Assets/Prefabs/RedCrate.prefab", "Base": "Assets/Prefabs/Crate.prefab", "Ops": [...]}
    //   {"Kind": "AnimatorController", "Path": "Assets/Anim/Door.controller", "Ops": [...]}
    //   {"Kind": "Scene", "Path": "Assets/Scenes/Test.unity", "Setup": "Empty" | "Default", "Ops": [...]}
    // An existing path is skipped with a warning, never overwritten. Each entry is all or nothing.
    // Usage: assetops.py CreateAsset --args '-spec="C:/temp/create.json"'
    // Docs/Import.md
    public sealed class CreateAssetCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Creates folders, ScriptableObjects, materials, prefabs, variants, controllers and scenes from a spec"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JObject spec;
            if (!Spec.Load(ctx, out spec))
            {
                return ExitCode.Refused;
            }
            JArray entries = Spec.Targets(ctx, spec, "Assets");
            if (entries == null)
            {
                return ExitCode.Refused;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (!Spec.CheckKeys(ctx, entries[i], "Assets[" + i + "]", AllowedKeys(Json.Str(entries[i], "Kind"))))
                {
                    return ExitCode.Refused;
                }
            }

            int created = 0;
            int existed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                JToken entry = entries[i];
                string kind = Json.Str(entry, "Kind");
                string path = AssetOpsPaths.Normalize(Json.Str(entry, "Path"));
                ctx.Progress(i, entries.Count, path);
                if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/") || path == "Assets"))
                {
                    ctx.FailTarget(path ?? "Assets[" + i + "]", "\"Path\" must be under Assets/");
                    continue;
                }
                if (kind == "Folder" ? AssetDatabase.IsValidFolder(path) : AssetDatabase.GetMainAssetTypeAtPath(path) != null)
                {
                    ctx.Log.Warn(path + " already exists, skipped");
                    existed++;
                    continue;
                }
                ctx.BeginTarget();
                string error = null;
                bool ok;
                switch (kind)
                {
                    case "Folder":
                        ok = EnsureFolder(path, out error);
                        break;
                    case "ScriptableObject":
                        ok = CreateScriptableObject(ctx, entry, path, out error);
                        break;
                    case "Material":
                        ok = CreateMaterial(ctx, entry, path, out error);
                        break;
                    case "Prefab":
                        ok = CreatePrefab(ctx, entry, path, out error);
                        break;
                    case "PrefabVariant":
                        ok = CreateVariant(ctx, entry, path, out error);
                        break;
                    case "AnimatorController":
                        ok = CreateController(ctx, entry, path, out error);
                        break;
                    case "Scene":
                        ok = CreateScene(ctx, entry, path, out error);
                        break;
                    default:
                        ok = false;
                        error = "\"Kind\" is Folder, ScriptableObject, Material, Prefab, PrefabVariant, AnimatorController or Scene, got " + (kind ?? "nothing");
                        break;
                }
                if (!ok || ctx.TargetFailed())
                {
                    ctx.FailTarget(path, error ?? ctx.TargetError);
                    continue;
                }
                created++;
                ctx.Log.Info("created " + kind + " " + path);
            }
            ctx.Log.Info("created " + created + ", already existed " + existed + ", failed " + ctx.FailedTargets.Count);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static string[] AllowedKeys(string kind)
        {
            var keys = new List<string> { "Kind", "Path" };
            switch (kind)
            {
                case "ScriptableObject":
                    keys.AddRange(new[] { "Type", "Properties", "ArrayOps" });
                    break;
                case "Material":
                    keys.AddRange(MaterialWriter.Keys);
                    break;
                case "Prefab":
                    keys.AddRange(new[] { "Root", "Components", "RectTransform", "Ops" });
                    break;
                case "PrefabVariant":
                    keys.AddRange(new[] { "Base", "Ops" });
                    break;
                case "AnimatorController":
                    keys.AddRange(new[] { "Ops" });
                    break;
                case "Scene":
                    keys.AddRange(new[] { "Setup", "Ops", "AddToBuild" });
                    break;
            }
            return keys.ToArray();
        }

        public static bool EnsureFolder(string path, out string error)
        {
            error = null;
            path = (path ?? string.Empty).TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path))
            {
                return true;
            }
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = "'" + path + "' is not under Assets/";
                return false;
            }
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!EnsureFolder(parent, out error))
            {
                return false;
            }
            string guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            if (string.IsNullOrEmpty(guid))
            {
                error = "Unity could not create folder " + path;
                return false;
            }
            return true;
        }

        static bool ParentFolder(string path, out string error)
        {
            return EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'), out error);
        }

        static bool CreateScriptableObject(RunContext ctx, JToken entry, string path, out string error)
        {
            System.Type type = TypeResolver.Find(Json.Str(entry, "Type"), typeof(ScriptableObject), out error);
            if (type == null)
            {
                return false;
            }
            if (type.IsAbstract)
            {
                error = type.Name + " is abstract";
                return false;
            }
            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
            {
                error = "Unity could not create a " + type.Name;
                return false;
            }
            var wc = new WriteContext();
            var properties = Json.Get(entry, "Properties") as JObject;
            var arrayOps = Json.Get(entry, "ArrayOps") as JArray;
            if ((properties != null || arrayOps != null) && !PropertyIO.Write(instance, properties, arrayOps, wc))
            {
                Object.DestroyImmediate(instance);
                error = wc.Errors[0];
                return false;
            }
            if (!ParentFolder(path, out error))
            {
                Object.DestroyImmediate(instance);
                return false;
            }
            AssetDatabase.CreateAsset(instance, path);
            return true;
        }

        static bool CreateMaterial(RunContext ctx, JToken entry, string path, out string error)
        {
            error = null;
            string shaderName = Json.Str(entry, "Shader");
            Shader shader = null;
            if (shaderName != null)
            {
                shader = shaderName.StartsWith("Assets/") || shaderName.StartsWith("Packages/") ? AssetDatabase.LoadAssetAtPath<Shader>(shaderName) : Shader.Find(shaderName);
            }
            else if (Json.Has(entry, "Parent"))
            {
                var parent = AssetDatabase.LoadAssetAtPath<Material>(Json.Str(entry, "Parent"));
                shader = parent != null ? parent.shader : null;
            }
            else
            {
                shader = DefaultShader();
            }
            if (shader == null)
            {
                error = "no shader '" + (shaderName ?? Json.Str(entry, "Parent") ?? "default") + "'";
                return false;
            }
            var mat = new Material(shader);
            var errors = new List<string>();
            if (!MaterialWriter.Apply(mat, entry, errors, ctx.Log))
            {
                Object.DestroyImmediate(mat);
                error = errors[0];
                return false;
            }
            if (!ParentFolder(path, out error))
            {
                Object.DestroyImmediate(mat);
                return false;
            }
            AssetDatabase.CreateAsset(mat, path);
            return true;
        }

        // The render pipeline's default material shader, or Standard without one.
        static Shader DefaultShader()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null)
            {
                return pipeline.defaultMaterial.shader;
            }
            return Shader.Find("Standard");
        }

        static bool CreatePrefab(RunContext ctx, JToken entry, string path, out string error)
        {
            error = null;
            if (!path.EndsWith(".prefab"))
            {
                error = "a prefab path ends in .prefab";
                return false;
            }
            string rootName = Json.Str(entry, "Root", Path.GetFileNameWithoutExtension(path));
            var scratch = new ScratchScene();
            var root = new GameObject(rootName);
            scratch.Adopt(root);
            if (Json.Bool(entry, "RectTransform", false))
            {
                root.AddComponent<RectTransform>();
            }
            try
            {
                var editor = new HierarchyEditor(root, path, ctx.Log);
                var ops = new JArray();
                var components = Json.Get(entry, "Components") as JArray;
                if (components != null)
                {
                    foreach (JToken c in components)
                    {
                        ops.Add(new JObject
                        {
                            ["Op"] = "AddComponent",
                            ["Path"] = HierarchyPath.Escape(rootName),
                            ["Type"] = c.Type == JTokenType.String ? c : Json.Get(c, "Type"),
                            ["Properties"] = c.Type == JTokenType.Object ? Json.Get(c, "Properties") : null,
                        });
                    }
                }
                var extra = Json.Get(entry, "Ops") as JArray;
                if (extra != null)
                {
                    foreach (JToken op in extra)
                    {
                        ops.Add(op);
                    }
                }
                foreach (JObject op in ops.OfType<JObject>())
                {
                    if (op["Properties"] != null && op["Properties"].Type == JTokenType.Null)
                    {
                        op.Remove("Properties");
                    }
                }
                if (!editor.Apply(ops))
                {
                    error = editor.Errors[0];
                    return false;
                }
                if (!ParentFolder(path, out error))
                {
                    return false;
                }
                bool success;
                PrefabUtility.SaveAsPrefabAsset(root, path, out success);
                if (!success)
                {
                    error = "Unity refused to save " + path;
                }
                return success;
            }
            finally
            {
                Object.DestroyImmediate(root);
                scratch.Dispose();
            }
        }

        static bool CreateVariant(RunContext ctx, JToken entry, string path, out string error)
        {
            error = null;
            string basePath = Json.Str(entry, "Base");
            var basePrefab = basePath != null ? AssetDatabase.LoadMainAssetAtPath(basePath) as GameObject : null;
            if (basePrefab == null)
            {
                error = "\"Base\" must name a prefab or model, got " + (basePath ?? "nothing");
                return false;
            }
            var scratch = new ScratchScene();
            var instance = (GameObject)(scratch.Scene.IsValid()
                ? PrefabUtility.InstantiatePrefab(basePrefab, scratch.Scene)
                : PrefabUtility.InstantiatePrefab(basePrefab));
            try
            {
                instance.name = Path.GetFileNameWithoutExtension(path);
                var ops = Json.Get(entry, "Ops") as JArray;
                if (ops != null)
                {
                    var editor = new HierarchyEditor(instance, path, ctx.Log);
                    if (!editor.Apply(ops))
                    {
                        error = editor.Errors[0];
                        return false;
                    }
                }
                if (!ParentFolder(path, out error))
                {
                    return false;
                }
                bool success;
                PrefabUtility.SaveAsPrefabAsset(instance, path, out success);
                if (!success)
                {
                    error = "Unity refused to save " + path;
                }
                return success;
            }
            finally
            {
                Object.DestroyImmediate(instance);
                scratch.Dispose();
            }
        }

        static bool CreateController(RunContext ctx, JToken entry, string path, out string error)
        {
            error = null;
            if (!ParentFolder(path, out error))
            {
                return false;
            }
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null)
            {
                error = "Unity could not create " + path;
                return false;
            }
            var ops = Json.Get(entry, "Ops") as JArray;
            if (ops == null || ops.Count == 0)
            {
                return true;
            }
            // Fill it through EditAnimatorController so the op set and its checks are the same.
            string specPath = AssetOpsPaths.Combine(AssetOpsPaths.Root, "Temp/create_" + System.Guid.NewGuid().ToString("N") + ".json");
            Json.WriteFile(specPath, new JObject { ["Targets"] = new JArray(new JObject { ["AssetPath"] = path, ["Ops"] = ops }) });
            try
            {
                var inner = new RunContext
                {
                    RunName = "EditAnimatorController",
                    Group = CommandGroup.Edit,
                    Assets = new List<string>(),
                    Args = RunArgs.Parse("-spec=\"" + specPath + "\" -apply"),
                    Log = ctx.Log,
                    InEditor = ctx.InEditor,
                };
                ExitCode code = new EditAnimatorControllerCommand().Execute(inner);
                if (code != ExitCode.Success)
                {
                    AssetDatabase.DeleteAsset(path);
                    error = ctx.TargetError ?? "the controller ops failed";
                    return false;
                }
                return true;
            }
            finally
            {
                File.Delete(specPath);
            }
        }

        static bool CreateScene(RunContext ctx, JToken entry, string path, out string error)
        {
            error = null;
            if (!path.EndsWith(".unity"))
            {
                error = "a scene path ends in .unity";
                return false;
            }
            NewSceneSetup setup = Json.Str(entry, "Setup", "Empty") == "Default" ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene;
            if (!ParentFolder(path, out error))
            {
                return false;
            }
            NewSceneMode mode = Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene previousActive = SceneManager.GetActiveScene();
            Scene scene;
            try
            {
                scene = EditorSceneManager.NewScene(setup, mode);
            }
            catch (Exception e)
            {
                error = "Unity could not create a scene next to the open ones (" + e.Message + "). If the editor shows an Untitled scene, save it first";
                return false;
            }
            try
            {
                // New GameObjects land in the active scene, keep them out of the user's.
                SceneManager.SetActiveScene(scene);
                var ops = Json.Get(entry, "Ops") as JArray;
                if (ops != null)
                {
                    var editor = new HierarchyEditor(scene, false, ctx.Log);
                    if (!editor.Apply(ops))
                    {
                        error = editor.Errors[0];
                        return false;
                    }
                }
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    error = "Unity refused to save " + path;
                    return false;
                }
                if (Json.Bool(entry, "AddToBuild", false))
                {
                    var list = EditorBuildSettings.scenes.ToList();
                    if (!list.Any(s => s.path == path))
                    {
                        list.Add(new EditorBuildSettingsScene(path, true));
                        EditorBuildSettings.scenes = list.ToArray();
                    }
                }
                return true;
            }
            finally
            {
                if (previousActive.IsValid() && previousActive.isLoaded && previousActive != scene)
                {
                    SceneManager.SetActiveScene(previousActive);
                }
                SceneSession.CloseAfterRun(scene);
            }
        }
    }
}
