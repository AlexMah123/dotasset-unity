using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Reads a whole object into {"<property>": value}, and writes such a map back.
    // Keys are SerializedProperty paths: script field names ("speed", "loadout.primary"), or the m_ names
    // of built-in components ("m_Mass", "m_LocalPosition"). Array elements: "items[2]" or "items.Array.data[2]".
    // A key without its m_ prefix is tried with it, so "mass" finds "m_Mass".
    public static class PropertyIO
    {
        // Bookkeeping fields every object carries, never interesting to read or write.
        public static readonly HashSet<string> Skip = new HashSet<string>
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_Script",
            "m_EditorHideFlags",
            "m_EditorClassIdentifier",
            "m_Name",
        };

        // Hierarchy fields expressed by the export's structure instead.
        static readonly HashSet<string> SkipOnHierarchy = new HashSet<string>
        {
            "m_Children",
            "m_Father",
            "m_Component",
            "m_RootOrder",
        };

        public static JObject Read(Object target, ReadOptions opt, JObject defaults = null, bool hierarchy = false)
        {
            var result = new JObject();
            var so = new SerializedObject(target);
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = false;
                if (Skip.Contains(it.name) || (hierarchy && SkipOnHierarchy.Contains(it.name)))
                {
                    continue;
                }
                JToken value = ValueCodec.Read(it, opt);
                if (defaults != null)
                {
                    JToken d = defaults[it.name];
                    if (d != null && JToken.DeepEquals(d, value))
                    {
                        continue;
                    }
                }
                result[it.name] = value;
            }
            return result;
        }

        static readonly Regex Bracket = new Regex(@"(?<!\.Array\.data)\[(\d+)\]", RegexOptions.Compiled);

        public static string NormalizePath(string path)
        {
            return Bracket.Replace(path, ".Array.data[$1]");
        }

        public static SerializedProperty Find(SerializedObject so, string key, WriteContext wc)
        {
            string path = NormalizePath(key);
            SerializedProperty prop = so.FindProperty(path);
            if (prop != null)
            {
                return prop;
            }
            // "mass" -> "m_Mass", "settings.range" -> "m_Settings.range"
            int dot = path.IndexOf('.');
            string head = dot < 0 ? path : path.Substring(0, dot);
            string rest = dot < 0 ? string.Empty : path.Substring(dot);
            if (!head.StartsWith("m_", StringComparison.Ordinal) && head.Length > 0)
            {
                string alt = "m_" + char.ToUpperInvariant(head[0]) + head.Substring(1) + rest;
                prop = so.FindProperty(alt);
                if (prop != null)
                {
                    return prop;
                }
            }
            wc.Fail(null, "'" + key + "' is not a property of " + Describe(so.targetObject) + ". Top-level properties: " + string.Join(", ", TopLevelNames(so).Take(40)));
            return null;
        }

        public static IEnumerable<string> TopLevelNames(SerializedObject so)
        {
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = false;
                if (!Skip.Contains(it.name))
                {
                    yield return it.name;
                }
            }
        }

        public static string Describe(Object o)
        {
            if (o == null)
            {
                return "(null)";
            }
            var c = o as Component;
            if (c != null)
            {
                return TypeResolver.DisplayName(o.GetType()) + " on '" + HierarchyPath.Of(c.gameObject) + "'";
            }
            return TypeResolver.DisplayName(o.GetType()) + " '" + o.name + "'";
        }

        public static void Commit(SerializedObject so, bool useUndo)
        {
            if (useUndo)
            {
                so.ApplyModifiedProperties();
            }
            else
            {
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // Writes {"path": value} and, when given, ArrayOps. Returns false with the reasons in wc.Errors.
        public static bool Write(Object target, JObject properties, JArray arrayOps, WriteContext wc)
        {
            int before = wc.Errors.Count;
            var so = new SerializedObject(target);
            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    SerializedProperty prop = Find(so, kv.Key, wc);
                    if (prop == null)
                    {
                        break;
                    }
                    if (!ValueCodec.Write(prop, kv.Value, wc))
                    {
                        break;
                    }
                }
            }
            if (wc.Errors.Count == before && arrayOps != null)
            {
                ApplyArrayOps(so, arrayOps, wc);
            }
            Commit(so, wc.UseUndo);
            return wc.Errors.Count == before;
        }

        // [{"Property": "items", "Insert": 2, "Value": {...}}, {"Property": "items", "Append": {...}}, {"Property": "items", "Remove": 3}]
        static void ApplyArrayOps(SerializedObject so, JArray ops, WriteContext wc)
        {
            foreach (JToken op in ops)
            {
                string key = Json.Str(op, "Property");
                if (key == null)
                {
                    wc.Fail(null, "an ArrayOps entry needs \"Property\"");
                    return;
                }
                SerializedProperty array = Find(so, key, wc);
                if (array == null)
                {
                    return;
                }
                if (!array.isArray || array.propertyType == SerializedPropertyType.String)
                {
                    wc.Fail(array, "is not an array");
                    return;
                }
                if (Json.Has(op, "Remove"))
                {
                    int index = Json.Int(op, "Remove", -1);
                    if (index < 0 || index >= array.arraySize)
                    {
                        wc.Fail(array, "Remove index " + index + " is out of range, size " + array.arraySize);
                        return;
                    }
                    // An object reference element first clears to null on delete, a second delete removes it.
                    int size = array.arraySize;
                    array.DeleteArrayElementAtIndex(index);
                    if (array.arraySize == size)
                    {
                        array.DeleteArrayElementAtIndex(index);
                    }
                    continue;
                }
                int at;
                JToken value;
                if (Json.Has(op, "Append"))
                {
                    at = array.arraySize;
                    value = Json.Get(op, "Append");
                }
                else if (Json.Has(op, "Insert"))
                {
                    at = Json.Int(op, "Insert", -1);
                    value = Json.Get(op, "Value");
                    if (at < 0 || at > array.arraySize)
                    {
                        wc.Fail(array, "Insert index " + at + " is out of range, size " + array.arraySize);
                        return;
                    }
                }
                else
                {
                    wc.Fail(array, "an ArrayOps entry needs Insert + Value, Append, or Remove");
                    return;
                }
                array.InsertArrayElementAtIndex(at);
                if (value != null && !ValueCodec.Write(array.GetArrayElementAtIndex(at), value, wc))
                {
                    return;
                }
            }
        }
    }

    // Default values of a type, read once per run from a fresh instance, so exports carry only what differs.
    public sealed class DefaultValues : IDisposable
    {
        readonly Dictionary<Type, JObject> cache = new Dictionary<Type, JObject>();

        public JObject For(Type type)
        {
            if (type == null)
            {
                return null;
            }
            JObject cached;
            if (cache.TryGetValue(type, out cached))
            {
                return cached;
            }
            JObject result = null;
            // A component may log while it is added to the scratch object, that is not the run's error.
            using (RunDispatcher.MuteEngineLog())
            {
                try
                {
                    result = Create(type);
                }
                catch (Exception)
                {
                    result = null;
                }
            }
            cache[type] = result;
            return result;
        }

        JObject Create(Type type)
        {
            if (typeof(ScriptableObject).IsAssignableFrom(type) && !type.IsAbstract)
            {
                var instance = ScriptableObject.CreateInstance(type);
                if (instance == null)
                {
                    return null;
                }
                instance.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    return PropertyIO.Read(instance, ReadOptions.Unbounded);
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
            if (typeof(Component).IsAssignableFrom(type) && !type.IsAbstract)
            {
                // Inactive, so Awake and OnEnable of the added component do not run.
                GameObject go = new GameObject("AssetOps.Defaults");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.SetActive(false);
                try
                {
                    Component c;
                    if (type == typeof(Transform))
                    {
                        c = go.transform;
                    }
                    else if (type == typeof(RectTransform))
                    {
                        c = go.AddComponent<RectTransform>();
                    }
                    else
                    {
                        c = go.AddComponent(type);
                    }
                    if (c == null)
                    {
                        return null;
                    }
                    return PropertyIO.Read(c, ReadOptions.Unbounded, null, true);
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
            return null;
        }

        public void Dispose()
        {
            cache.Clear();
        }
    }
}
