using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // The hierarchy that local references resolve against: the open prefab contents, or a scene.
    public sealed class RefScope
    {
        public readonly List<GameObject> Roots = new List<GameObject>();

        public static RefScope ForScene(Scene scene)
        {
            var scope = new RefScope();
            if (scene.IsValid() && scene.isLoaded)
            {
                scope.Roots.AddRange(scene.GetRootGameObjects());
            }
            return scope;
        }

        public static RefScope ForRoot(GameObject root)
        {
            var scope = new RefScope();
            if (root != null)
            {
                scope.Roots.Add(root);
            }
            return scope;
        }

        public static readonly RefScope None = new RefScope();

        public bool Contains(Object o)
        {
            GameObject go = o as GameObject;
            if (go == null)
            {
                var c = o as Component;
                go = c != null ? c.gameObject : null;
            }
            if (go == null || Roots.Count == 0)
            {
                return false;
            }
            Transform top = go.transform.root;
            return Roots.Any(r => r != null && r.transform == top);
        }
    }

    // How a UnityEngine.Object reference reads in JSON, and back:
    //   null                                                    no reference
    //   "Assets/Materials/Red.mat"                              the main asset at that path
    //   {"Asset": "Assets/Models/Hero.fbx", "Sub": "Hero", "Type": "Mesh"}     a sub-asset by name and type
    //   {"Asset": "Assets/Prefabs/Gun.prefab", "Object": "Gun/Barrel", "Component": "Transform"}  inside a prefab asset
    //   {"Object": "Player/Model", "Component": "Animator", "Index": 0}       in the same prefab or scene
    //   {"Guid": "0123...", "Sub": ...}                         an asset by GUID instead of path
    //   {"Missing": true}                                       the reference points at something that no longer exists (export only)
    // Built-in resources read as {"Asset": "Library/unity default resources", "Sub": "Cube", "Type": "Mesh"}.
    public static class ObjectRefs
    {
        // A reference that is set but resolves to nothing: the target was deleted, or its script is gone.
        // The same test Unity's ObjectField uses. Unity 6.4 replaced the instance ID accessor with an EntityId one,
        // and newer versions refuse the old one at compile time.
        public static bool IsMissing(SerializedProperty prop)
        {
            if (prop.propertyType != SerializedPropertyType.ObjectReference || prop.objectReferenceValue != null)
            {
                return false;
            }
#if UNITY_6000_4_OR_NEWER
            return prop.objectReferenceEntityIdValue != EntityId.None;
#else
            return prop.objectReferenceInstanceIDValue != 0;
#endif
        }

        public static JToken Write(Object o, RefScope scope)
        {
            if (o == null)
            {
                return JValue.CreateNull();
            }
            scope = scope ?? RefScope.None;

            if (!EditorUtility.IsPersistent(o))
            {
                if (scope.Contains(o))
                {
                    return Local(o, null);
                }
                return new JObject { ["Unresolved"] = o.name, ["Type"] = TypeResolver.DisplayName(o.GetType()) };
            }

            string path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path))
            {
                return new JObject { ["Unresolved"] = o.name, ["Type"] = TypeResolver.DisplayName(o.GetType()) };
            }

            if (AssetDatabase.IsMainAsset(o))
            {
                return path;
            }

            // A GameObject or component inside a prefab asset.
            if (o is GameObject || o is Component)
            {
                JObject local = Local(o, path);
                local.AddFirst(new JProperty("Asset", path));
                return local;
            }

            return new JObject
            {
                ["Asset"] = path,
                ["Sub"] = o.name,
                ["Type"] = TypeResolver.DisplayName(o.GetType()),
            };
        }

        static JObject Local(Object o, string assetPath)
        {
            var go = o as GameObject;
            var c = o as Component;
            if (go == null && c != null)
            {
                go = c.gameObject;
            }
            var obj = new JObject { ["Object"] = HierarchyPath.Of(go) };
            if (c != null)
            {
                obj["Component"] = TypeResolver.DisplayName(c.GetType());
                int index = HierarchyPath.IndexAmongSameType(c);
                if (index > 0)
                {
                    obj["Index"] = index;
                }
            }
            return obj;
        }

        // expected: the field's type, used to pick a component off a GameObject or a sub-asset off a main asset.
        public static Object Resolve(JToken token, Type expected, RefScope scope, out string error)
        {
            error = null;
            expected = expected ?? typeof(Object);
            scope = scope ?? RefScope.None;
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            Object result;
            if (token.Type == JTokenType.String)
            {
                string path = (string)token;
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal)
                    && !path.StartsWith("Library/", StringComparison.Ordinal) && !path.StartsWith("Resources/", StringComparison.Ordinal))
                {
                    error = "\"" + path + "\" is not an asset path. For an object in this prefab or scene write {\"Object\": \"" + path + "\"}";
                    return null;
                }
                result = ResolveAsset(path, null, null, expected, out error);
                return Fit(result, expected, token, ref error);
            }

            var obj = token as JObject;
            if (obj == null)
            {
                error = "an object reference is a path string, an object or null, got " + token.Type;
                return null;
            }

            string strays = Json.UnknownKeys(obj, "Asset", "Guid", "Sub", "Type", "Object", "Component", "Index", "Missing", "Unresolved");
            if (strays != null)
            {
                // {"Object": "Player", "Componet": "Collider"} must not quietly resolve to the Transform.
                error = "reference " + strays;
                return null;
            }
            if (Json.Has(obj, "Unresolved"))
            {
                error = "cannot write an {\"Unresolved\"} reference, it points outside this prefab or scene. Give an asset path or a local \"Object\"";
                return null;
            }
            if (Json.Has(obj, "Index") && !Json.Has(obj, "Component"))
            {
                error = "\"Index\" picks among components of one type, give \"Component\" with it";
                return null;
            }
            if (Json.Bool(obj, "Missing", false))
            {
                error = "cannot write a {\"Missing\": true} reference, give a real target or null";
                return null;
            }

            string asset = Json.Str(obj, "Asset");
            string guid = Json.Str(obj, "Guid");
            if (asset == null && guid != null)
            {
                asset = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(asset))
                {
                    error = "no asset has GUID " + guid;
                    return null;
                }
            }
            string objectPath = Json.Str(obj, "Object");
            string component = Json.Str(obj, "Component");
            int index = Json.Int(obj, "Index", 0);

            if (asset == null)
            {
                if (objectPath == null)
                {
                    error = "a reference object needs \"Asset\", \"Guid\" or \"Object\"";
                    return null;
                }
                if (scope.Roots.Count == 0)
                {
                    error = "\"Object\" references need a prefab or scene to resolve against, this target has none";
                    return null;
                }
                GameObject go = HierarchyPath.Resolve(scope.Roots, objectPath, out error);
                if (go == null)
                {
                    return null;
                }
                result = PickOnGameObject(go, component, index, expected, out error);
                return Fit(result, expected, token, ref error);
            }

            if (objectPath != null || component != null)
            {
                var root = AssetDatabase.LoadMainAssetAtPath(asset) as GameObject;
                if (root == null)
                {
                    error = asset + " is not a prefab, \"Object\" and \"Component\" need one";
                    return null;
                }
                GameObject go = objectPath == null ? root : HierarchyPath.Resolve(new List<GameObject> { root }, objectPath, out error);
                if (go == null)
                {
                    return null;
                }
                result = PickOnGameObject(go, component, index, expected, out error);
                return Fit(result, expected, token, ref error);
            }

            result = ResolveAsset(asset, Json.Str(obj, "Sub"), Json.Str(obj, "Type"), expected, out error);
            return Fit(result, expected, token, ref error);
        }

        static Object PickOnGameObject(GameObject go, string component, int index, Type expected, out string error)
        {
            error = null;
            if (component != null)
            {
                return HierarchyPath.ResolveComponent(go, component, index, out error);
            }
            if (typeof(Component).IsAssignableFrom(expected))
            {
                var c = go.GetComponent(expected);
                if (c == null)
                {
                    error = "'" + HierarchyPath.Of(go) + "' has no " + expected.Name;
                }
                return c;
            }
            return go;
        }

        static Object ResolveAsset(string path, string sub, string typeName, Type expected, out string error)
        {
            error = null;
            bool builtin = path == "Library/unity default resources" || path == "Resources/unity_builtin_extra";
            if (!builtin && AssetDatabase.GetMainAssetTypeAtPath(path) == null)
            {
                error = "no asset at " + path;
                return null;
            }

            Type subType = null;
            if (typeName != null)
            {
                subType = TypeResolver.Find(typeName, typeof(Object), out error);
                if (subType == null)
                {
                    return null;
                }
            }

            if (sub == null && !builtin)
            {
                Object main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null)
                {
                    error = "cannot load " + path;
                    return null;
                }
                if (expected.IsInstanceOfType(main))
                {
                    return main;
                }
                var go = main as GameObject;
                if (go != null && typeof(Component).IsAssignableFrom(expected))
                {
                    var c = go.GetComponent(expected);
                    if (c != null)
                    {
                        return c;
                    }
                }
                // e.g. a texture path given for a Sprite field: take its only sprite.
                var fitting = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).Where(expected.IsInstanceOfType).ToList();
                if (fitting.Count == 1)
                {
                    return fitting[0];
                }
                if (fitting.Count > 1)
                {
                    error = path + " holds " + fitting.Count + " " + expected.Name + " sub-assets, name one with {\"Asset\", \"Sub\"}: " + string.Join(", ", fitting.Take(8).Select(f => f.name));
                    return null;
                }
                return main;
            }

            Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            var named = all.Where(a => a != null && (sub == null || a.name == sub) && (subType == null || subType.IsInstanceOfType(a))).ToList();
            var fit = named.Where(expected.IsInstanceOfType).ToList();
            if (fit.Count == 1)
            {
                return fit[0];
            }
            if (fit.Count > 1)
            {
                error = path + " has " + fit.Count + " objects named '" + sub + "', add \"Type\"";
                return null;
            }
            if (named.Count > 0)
            {
                return named[0];
            }
            error = "no sub-asset '" + sub + "'" + (typeName != null ? " of type " + typeName : "") + " in " + path
                + ". There: " + string.Join(", ", all.Where(a => a != null).Take(12).Select(a => a.name + " (" + a.GetType().Name + ")"));
            return null;
        }

        static Object Fit(Object result, Type expected, JToken token, ref string error)
        {
            if (result == null || error != null)
            {
                if (result == null && error == null)
                {
                    error = "reference " + token.ToString(Newtonsoft.Json.Formatting.None) + " resolved to nothing";
                }
                return null;
            }
            if (!expected.IsInstanceOfType(result))
            {
                error = "reference " + token.ToString(Newtonsoft.Json.Formatting.None) + " is a " + result.GetType().Name + ", the field takes " + expected.Name;
                return null;
            }
            return result;
        }
    }
}
