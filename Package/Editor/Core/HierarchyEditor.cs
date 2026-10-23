using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // GameObject operations shared by EditPrefab, EditScene and CreateAsset's prefabs. Ops run in order, later ops
    // see what earlier ones made, and the first failing op stops the list. The caller decides whether to save.
    //
    //   {"Op": "AddGameObject", "Parent": "Root", "Name": "Hand", "Components": ["BoxCollider", {"Type": "Light", "Properties": {...}}]}
    //   {"Op": "AddPrefabInstance", "Parent": "Root", "Prefab": "Assets/Gun.prefab", "Name": "Gun"}
    //   {"Op": "Delete", "Path": "Root/Old"}
    //   {"Op": "Rename", "Path": "Root/Old", "NewName": "New"}
    //   {"Op": "Reparent", "Path": "Root/Hand", "Parent": "Root/Arm", "SiblingIndex": 0, "WorldPositionStays": false}
    //   {"Op": "SetGameObject", "Path": "Root/Hand", "Active": false, "Tag": "Player", "Layer": "UI", "Static": ["BatchingStatic"]}
    //   {"Op": "AddComponent", "Path": "Root", "Type": "Rigidbody", "Properties": {"m_Mass": 2}}
    //   {"Op": "RemoveComponent", "Path": "Root", "Type": "Rigidbody", "Index": 0}
    //   {"Op": "Set", "Path": "Root", "Component": "Rigidbody", "Index": 0, "Properties": {...}, "ArrayOps": [...]}
    //   {"Op": "RevertOverrides", "Path": "Root/Gun", "Component": "Transform"}
    //   {"Op": "UnpackPrefab", "Path": "Root/Gun", "Completely": false}
    //   {"Op": "RemoveMissingScripts", "Path": "Root", "Recursive": true}
    public sealed class HierarchyEditor
    {
        readonly RefScope scope;
        readonly Scene scene;
        readonly GameObject prefabRoot;
        readonly string prefabPath;
        readonly bool useUndo;
        readonly RunLog log;

        public readonly List<string> Errors = new List<string>();
        public int Changes { get; private set; }

        // A scene: roots may be added. A prefab: everything stays under its one root.
        public HierarchyEditor(Scene scene, bool useUndo, RunLog log)
        {
            this.scene = scene;
            this.useUndo = useUndo;
            this.log = log;
            scope = new RefScope();
            Refresh();
        }

        public HierarchyEditor(GameObject prefabRoot, string prefabPath, RunLog log)
        {
            this.prefabRoot = prefabRoot;
            this.prefabPath = prefabPath;
            this.log = log;
            useUndo = false;
            scope = RefScope.ForRoot(prefabRoot);
        }

        public RefScope Scope
        {
            get { return scope; }
        }

        void Refresh()
        {
            if (prefabRoot != null)
            {
                return;
            }
            scope.Roots.Clear();
            scope.Roots.AddRange(scene.GetRootGameObjects());
        }

        public static readonly string[] OpNames =
        {
            "AddGameObject", "AddPrefabInstance", "Delete", "Rename", "Reparent", "SetGameObject",
            "AddComponent", "RemoveComponent", "Set", "RevertOverrides", "UnpackPrefab", "RemoveMissingScripts",
        };

        public bool Apply(JArray ops)
        {
            for (int i = 0; i < ops.Count; i++)
            {
                JToken op = ops[i];
                string name = Json.Str(op, "Op");
                string label = "Ops[" + i + "] " + (name ?? "(no Op)");
                int before = Errors.Count;
                try
                {
                    ApplyOne(name, op, label);
                }
                catch (Exception e)
                {
                    Errors.Add(label + ": " + e.Message);
                }
                if (Errors.Count > before)
                {
                    return false;
                }
                Changes++;
                Refresh();
            }
            return true;
        }

        void Fail(string label, string message)
        {
            Errors.Add(label + ": " + message);
        }

        GameObject Find(JToken op, string key, string label)
        {
            string path = Json.Str(op, key);
            if (path == null)
            {
                Fail(label, "needs \"" + key + "\"");
                return null;
            }
            string error;
            GameObject go = HierarchyPath.Resolve(scope.Roots, path, out error);
            if (go == null)
            {
                Fail(label, error);
            }
            return go;
        }

        // A missing or empty "Parent" means a new scene root. Prefabs have one root, so they require a parent.
        bool FindParent(JToken op, string label, out Transform parent)
        {
            parent = null;
            string path = Json.Str(op, "Parent");
            if (string.IsNullOrEmpty(path))
            {
                if (prefabRoot != null)
                {
                    Fail(label, "a prefab has a single root, give \"Parent\" (\"" + HierarchyPath.Of(prefabRoot) + "\" for the root)");
                    return false;
                }
                return true;
            }
            string error;
            GameObject go = HierarchyPath.Resolve(scope.Roots, path, out error);
            if (go == null)
            {
                Fail(label, error);
                return false;
            }
            parent = go.transform;
            return true;
        }

        void ApplyOne(string name, JToken op, string label)
        {
            switch (name)
            {
                case "AddGameObject":
                    AddGameObject(op, label);
                    return;
                case "AddPrefabInstance":
                    AddPrefabInstance(op, label);
                    return;
                case "Delete":
                    Delete(op, label);
                    return;
                case "Rename":
                    Rename(op, label);
                    return;
                case "Reparent":
                    Reparent(op, label);
                    return;
                case "SetGameObject":
                {
                    GameObject go = Find(op, "Path", label);
                    if (go != null)
                    {
                        SetGameObjectFields(go, op, label);
                    }
                    return;
                }
                case "AddComponent":
                    AddComponent(op, label);
                    return;
                case "RemoveComponent":
                    RemoveComponent(op, label);
                    return;
                case "Set":
                    Set(op, label);
                    return;
                case "RevertOverrides":
                    RevertOverrides(op, label);
                    return;
                case "UnpackPrefab":
                    UnpackPrefab(op, label);
                    return;
                case "RemoveMissingScripts":
                    RemoveMissingScripts(op, label);
                    return;
                default:
                    Fail(label, "unknown op. Ops: " + string.Join(", ", OpNames));
                    return;
            }
        }

        void Place(GameObject go, Transform parent, JToken op)
        {
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            else
            {
                // The editor may hand new objects a default parent, a scene root has none.
                go.transform.SetParent(null, false);
                if (prefabRoot == null && go.scene != scene)
                {
                    SceneManager.MoveGameObjectToScene(go, scene);
                }
            }
            if (Json.Has(op, "SiblingIndex"))
            {
                go.transform.SetSiblingIndex(Json.Int(op, "SiblingIndex", 0));
            }
        }

        void AddGameObject(JToken op, string label)
        {
            string goName = Json.Str(op, "Name");
            if (string.IsNullOrEmpty(goName))
            {
                Fail(label, "needs \"Name\"");
                return;
            }
            Transform parent;
            if (!FindParent(op, label, out parent))
            {
                return;
            }
            // Created straight in the target scene (the prefab's preview scene, or the edited scene), so no other
            // open scene is touched even for a moment.
            Scene target = prefabRoot != null ? prefabRoot.scene : scene;
            GameObject go = target.IsValid() ? ObjectFactory.CreateGameObject(target, HideFlags.None, goName) : new GameObject(goName);
            if (Json.Bool(op, "RectTransform", false) || (parent != null && parent is RectTransform))
            {
                go.AddComponent<RectTransform>();
            }
            Place(go, parent, op);
            if (useUndo)
            {
                Undo.RegisterCreatedObjectUndo(go, "Add " + goName);
            }
            if (!SetGameObjectFields(go, op, label))
            {
                return;
            }
            var components = Json.Get(op, "Components") as JArray;
            if (components != null)
            {
                foreach (JToken entry in components)
                {
                    string type = entry.Type == JTokenType.String ? (string)entry : Json.Str(entry, "Type");
                    var props = entry.Type == JTokenType.Object ? Json.Get(entry, "Properties") as JObject : null;
                    if (AddComponentTo(go, type, props, label) == null)
                    {
                        return;
                    }
                }
            }
            log.Info("added GameObject " + HierarchyPath.Of(go));
        }

        void AddPrefabInstance(JToken op, string label)
        {
            string prefabPath = Json.Str(op, "Prefab");
            var prefab = prefabPath != null ? AssetDatabase.LoadMainAssetAtPath(prefabPath) as GameObject : null;
            if (prefab == null)
            {
                Fail(label, "\"Prefab\" must name a prefab or model asset, got " + (prefabPath ?? "nothing"));
                return;
            }
            Transform parent;
            if (!FindParent(op, label, out parent))
            {
                return;
            }
            if (prefabRoot != null && prefabPath != null && string.Equals(prefabPath, this.prefabPath, StringComparison.OrdinalIgnoreCase))
            {
                Fail(label, "a prefab cannot contain an instance of itself");
                return;
            }
            GameObject instance = parent != null
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent)
                : (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            if (instance == null)
            {
                Fail(label, "Unity could not instantiate " + prefabPath);
                return;
            }
            string newName = Json.Str(op, "Name");
            if (!string.IsNullOrEmpty(newName))
            {
                instance.name = newName;
            }
            if (Json.Has(op, "SiblingIndex"))
            {
                instance.transform.SetSiblingIndex(Json.Int(op, "SiblingIndex", 0));
            }
            if (useUndo)
            {
                Undo.RegisterCreatedObjectUndo(instance, "Add " + instance.name);
            }
            SetGameObjectFields(instance, op, label);
            log.Info("added instance of " + prefabPath + " at " + HierarchyPath.Of(instance));
        }

        void Delete(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            if (go == prefabRoot)
            {
                Fail(label, "cannot delete the prefab root");
                return;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
            {
                Fail(label, "'" + HierarchyPath.Of(go) + "' belongs to a prefab instance. Delete the instance root, edit the source prefab, or UnpackPrefab first");
                return;
            }
            string path = HierarchyPath.Of(go);
            if (useUndo)
            {
                Undo.DestroyObjectImmediate(go);
            }
            else
            {
                Object.DestroyImmediate(go);
            }
            log.Info("deleted " + path);
        }

        void Rename(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            string newName = Json.Str(op, "NewName");
            if (go == null)
            {
                return;
            }
            if (string.IsNullOrEmpty(newName))
            {
                Fail(label, "needs \"NewName\"");
                return;
            }
            string before = HierarchyPath.Of(go);
            if (useUndo)
            {
                Undo.RecordObject(go, "Rename");
            }
            go.name = newName;
            log.Info("renamed " + before + " to " + HierarchyPath.Of(go));
        }

        void Reparent(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            if (go == prefabRoot)
            {
                Fail(label, "cannot reparent the prefab root");
                return;
            }
            Transform parent;
            if (!FindParent(op, label, out parent))
            {
                return;
            }
            if (parent != null && (parent == go.transform || parent.IsChildOf(go.transform)))
            {
                Fail(label, "cannot move '" + HierarchyPath.Of(go) + "' under itself");
                return;
            }
            bool stays = Json.Bool(op, "WorldPositionStays", false);
            string before = HierarchyPath.Of(go);
            if (useUndo)
            {
                Undo.SetTransformParent(go.transform, parent, stays, "Reparent");
            }
            else
            {
                go.transform.SetParent(parent, stays);
            }
            if (parent == null && prefabRoot == null && go.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            if (Json.Has(op, "SiblingIndex"))
            {
                if (useUndo)
                {
                    Undo.RegisterFullObjectHierarchyUndo(go.transform.parent != null ? go.transform.parent.gameObject : go, "Reorder");
                }
                go.transform.SetSiblingIndex(Json.Int(op, "SiblingIndex", 0));
            }
            log.Info("moved " + before + " to " + HierarchyPath.Of(go));
        }

        bool SetGameObjectFields(GameObject go, JToken op, string label)
        {
            bool any = Json.Has(op, "Active") || Json.Has(op, "Tag") || Json.Has(op, "Layer") || Json.Has(op, "Static");
            if (!any)
            {
                return true;
            }
            if (useUndo)
            {
                Undo.RecordObject(go, "Set GameObject");
            }
            if (Json.Has(op, "Active"))
            {
                go.SetActive(Json.Bool(op, "Active", true));
            }
            if (Json.Has(op, "Tag"))
            {
                string tag = Json.Str(op, "Tag");
                if (!InternalEditorUtility.tags.Contains(tag))
                {
                    Fail(label, "tag '" + tag + "' is not defined. Tags: " + string.Join(", ", InternalEditorUtility.tags));
                    return false;
                }
                go.tag = tag;
            }
            if (Json.Has(op, "Layer"))
            {
                JToken layer = Json.Get(op, "Layer");
                int index;
                if (layer.Type == JTokenType.Integer)
                {
                    index = (int)(long)layer;
                }
                else
                {
                    index = LayerMask.NameToLayer((string)layer);
                }
                if (index < 0 || index > 31)
                {
                    Fail(label, "layer '" + layer + "' is not defined");
                    return false;
                }
                go.layer = index;
            }
            if (Json.Has(op, "Static"))
            {
                StaticEditorFlags flags;
                string error;
                if (!ParseStatic(Json.Get(op, "Static"), out flags, out error))
                {
                    Fail(label, error);
                    return false;
                }
                GameObjectUtility.SetStaticEditorFlags(go, flags);
            }
            return true;
        }

        public static bool ParseStatic(JToken value, out StaticEditorFlags flags, out string error)
        {
            flags = 0;
            error = null;
            if (value.Type == JTokenType.Boolean)
            {
                if ((bool)value)
                {
                    foreach (StaticEditorFlags f in Enum.GetValues(typeof(StaticEditorFlags)))
                    {
                        flags |= f;
                    }
                }
                return true;
            }
            var names = value as JArray;
            if (names == null)
            {
                error = "\"Static\" is true, false or a list of StaticEditorFlags names";
                return false;
            }
            foreach (JToken n in names)
            {
                StaticEditorFlags f;
                if (!Enum.TryParse((string)n, true, out f))
                {
                    error = "'" + n + "' is not a StaticEditorFlags name. Names: " + string.Join(", ", Enum.GetNames(typeof(StaticEditorFlags)));
                    return false;
                }
                flags |= f;
            }
            return true;
        }

        void AddComponent(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            AddComponentTo(go, Json.Str(op, "Type"), Json.Get(op, "Properties") as JObject, label);
        }

        Component AddComponentTo(GameObject go, string typeName, JObject properties, string label)
        {
            string error;
            Type type = TypeResolver.Find(typeName, typeof(Component), out error);
            if (type == null)
            {
                Fail(label, error);
                return null;
            }
            if (type.IsAbstract)
            {
                Fail(label, type.Name + " is abstract");
                return null;
            }
            if (type == typeof(Transform))
            {
                Fail(label, "every GameObject already has a Transform");
                return null;
            }
            Component c = useUndo ? Undo.AddComponent(go, type) : go.AddComponent(type);
            if (c == null)
            {
                Fail(label, "Unity refused to add " + type.Name + " to '" + HierarchyPath.Of(go) + "' (DisallowMultipleComponent, a conflicting component, or a script that does not compile)");
                return null;
            }
            if (properties != null && !WriteProperties(c, properties, null, label))
            {
                return null;
            }
            log.Info("added " + TypeResolver.DisplayName(type) + " to " + HierarchyPath.Of(go));
            return c;
        }

        void RemoveComponent(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            string error;
            var c = HierarchyPath.ResolveComponent(go, Json.Str(op, "Type"), Json.Int(op, "Index", 0), out error) as Component;
            if (c == null)
            {
                Fail(label, error ?? "\"Type\" must name a component");
                return;
            }
            if (c is Transform)
            {
                Fail(label, "a Transform cannot be removed");
                return;
            }
            var dependents = go.GetComponents<Component>()
                .Where(other => other != null && other != c && RequiresType(other.GetType(), c.GetType()))
                .Select(other => TypeResolver.DisplayName(other.GetType()))
                .ToList();
            if (dependents.Count > 0)
            {
                Fail(label, TypeResolver.DisplayName(c.GetType()) + " is required by " + string.Join(", ", dependents) + ". Remove those first");
                return;
            }
            string what = TypeResolver.DisplayName(c.GetType());
            if (useUndo)
            {
                Undo.DestroyObjectImmediate(c);
            }
            else
            {
                Object.DestroyImmediate(c);
            }
            log.Info("removed " + what + " from " + HierarchyPath.Of(go));
        }

        static bool RequiresType(Type owner, Type required)
        {
            foreach (RequireComponent rc in owner.GetCustomAttributes(typeof(RequireComponent), true))
            {
                foreach (Type t in new[] { rc.m_Type0, rc.m_Type1, rc.m_Type2 })
                {
                    if (t != null && t.IsAssignableFrom(required))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        void Set(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            string error;
            Object target = HierarchyPath.ResolveComponent(go, Json.Str(op, "Component"), Json.Int(op, "Index", 0), out error);
            if (target == null)
            {
                Fail(label, error);
                return;
            }
            var properties = Json.Get(op, "Properties") as JObject;
            var arrayOps = Json.Get(op, "ArrayOps") as JArray;
            if (properties == null && arrayOps == null)
            {
                Fail(label, "needs \"Properties\" or \"ArrayOps\"");
                return;
            }
            if (WriteProperties(target, properties, arrayOps, label))
            {
                log.Info("set " + (properties != null ? string.Join(", ", properties.Properties().Select(p => p.Name)) : "array ops") + " on " + PropertyIO.Describe(target));
            }
        }

        bool WriteProperties(Object target, JObject properties, JArray arrayOps, string label)
        {
            var wc = new WriteContext { Scope = scope, UseUndo = useUndo };
            if (!PropertyIO.Write(target, properties, arrayOps, wc))
            {
                foreach (string e in wc.Errors)
                {
                    Fail(label, e);
                }
                return false;
            }
            return true;
        }

        void RevertOverrides(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                Fail(label, "'" + HierarchyPath.Of(go) + "' is not part of a prefab instance");
                return;
            }
            InteractionMode mode = useUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction;
            string component = Json.Str(op, "Component");
            if (component != null)
            {
                string error;
                Object target = HierarchyPath.ResolveComponent(go, component, Json.Int(op, "Index", 0), out error);
                if (target == null)
                {
                    Fail(label, error);
                    return;
                }
                PrefabUtility.RevertObjectOverride(target, mode);
                log.Info("reverted overrides on " + PropertyIO.Describe(target));
                return;
            }
            GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root != go)
            {
                Fail(label, "give the instance root '" + HierarchyPath.Of(root) + "', or a \"Component\" to revert one object");
                return;
            }
            PrefabUtility.RevertPrefabInstance(go, mode);
            log.Info("reverted every override of " + HierarchyPath.Of(go));
        }

        void UnpackPrefab(JToken op, string label)
        {
            GameObject go = Find(op, "Path", label);
            if (go == null)
            {
                return;
            }
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                Fail(label, "'" + HierarchyPath.Of(go) + "' is not an outermost prefab instance root");
                return;
            }
            PrefabUnpackMode unpack = Json.Bool(op, "Completely", false) ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(go, unpack, useUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction);
            log.Info("unpacked " + HierarchyPath.Of(go));
        }

        void RemoveMissingScripts(JToken op, string label)
        {
            var targets = new List<GameObject>();
            if (Json.Has(op, "Path"))
            {
                GameObject go = Find(op, "Path", label);
                if (go == null)
                {
                    return;
                }
                targets.Add(go);
            }
            else
            {
                targets.AddRange(scope.Roots);
            }
            bool recursive = Json.Bool(op, "Recursive", true);
            int removed = 0;
            foreach (GameObject top in targets)
            {
                IEnumerable<GameObject> all = recursive ? HierarchyPath.DepthFirst(top) : new[] { top };
                foreach (GameObject go in all.ToList())
                {
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) == 0)
                    {
                        continue;
                    }
                    if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
                    {
                        log.Warn(HierarchyPath.Of(go) + ": its missing scripts come from the source prefab, clean that prefab");
                        continue;
                    }
                    if (useUndo)
                    {
                        // The full hierarchy record also captures the component list, a plain object record does not.
                        Undo.RegisterFullObjectHierarchyUndo(go, "Remove missing scripts");
                    }
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                }
            }
            log.Info("removed " + removed + " missing script component(s)");
        }
    }
}
