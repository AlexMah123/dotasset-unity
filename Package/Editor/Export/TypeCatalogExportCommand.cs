using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // The property paths a component or ScriptableObject type serializes, with their kind, default value and
    // enum options. Answers "what do I write in Properties" before editing anything. -filter is required.
    // Usage: assetops.py TypeCatalogExport --args "-filter=Light"   (matches type names containing the text)
    // Docs/Export.md
    public sealed class TypeCatalogExportCommand : AssetOpsCommand
    {
        const int MaxTypes = 40;

        public override string Summary
        {
            get { return "Serialized property paths, kinds, defaults and enum options of component and ScriptableObject types"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "filter", "kind" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            string filter = ctx.Args.Get("filter");
            if (string.IsNullOrEmpty(filter))
            {
                ctx.Log.Error("TypeCatalogExport needs -filter=<part of a type name>, the full catalog would flood the context");
                return ExitCode.Refused;
            }
            string kind = ctx.Args.Get("kind", "all").ToLowerInvariant();
            var types = new List<Type>();
            if (kind == "all" || kind == "component")
            {
                types.AddRange(TypeResolver.Search(filter, typeof(Component)));
            }
            if (kind == "all" || kind == "scriptableobject")
            {
                types.AddRange(TypeResolver.Search(filter, typeof(ScriptableObject)).Where(t => !typeof(EditorWindow).IsAssignableFrom(t) && !typeof(Editor).IsAssignableFrom(t)));
            }
            // Exact name matches first, then shorter names.
            types = types.Distinct()
                .OrderBy(t => string.Equals(t.Name, filter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.Name.Length)
                .ThenBy(t => t.FullName)
                .ToList();

            JObject json = ExportHelper.Header(ctx, null);
            json["Filter"] = filter;
            json["Matches"] = types.Count;
            if (types.Count > MaxTypes)
            {
                ctx.Log.Warn(types.Count + " types match, listing the first " + MaxTypes + ". Narrow -filter");
                types = types.Take(MaxTypes).ToList();
            }
            var list = new JArray();
            using (RunDispatcher.MuteEngineLog())
            {
                foreach (Type t in types)
                {
                    list.Add(Describe(t));
                }
            }
            json["Types"] = list;
            ExportHelper.Save(ctx, null, json);
            return ExitCode.Success;
        }

        static JObject Describe(Type t)
        {
            var entry = new JObject
            {
                ["Name"] = TypeResolver.DisplayName(t),
                ["FullName"] = t.FullName,
                ["Assembly"] = t.Assembly.GetName().Name,
                ["Base"] = t.BaseType != null ? t.BaseType.Name : null,
                ["Kind"] = typeof(Component).IsAssignableFrom(t) ? "Component" : "ScriptableObject",
            };
            Object instance = null;
            GameObject host = null;
            try
            {
                if (typeof(ScriptableObject).IsAssignableFrom(t))
                {
                    instance = ScriptableObject.CreateInstance(t);
                }
                else
                {
                    host = EditorUtility.CreateGameObjectWithHideFlags("AssetOps.Catalog", HideFlags.HideAndDontSave);
                    host.SetActive(false);
                    instance = t == typeof(Transform) ? host.transform : host.AddComponent(t);
                }
                if (instance == null)
                {
                    entry["Error"] = "could not create an instance to inspect";
                    return entry;
                }
                var so = new SerializedObject(instance);
                var props = new JArray();
                SerializedProperty it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = false;
                    if (PropertyIO.Skip.Contains(it.name))
                    {
                        continue;
                    }
                    props.Add(Property(it, 0));
                }
                entry["Properties"] = props;
            }
            catch (Exception e)
            {
                entry["Error"] = e.Message;
            }
            finally
            {
                if (host != null)
                {
                    Object.DestroyImmediate(host);
                }
                else if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            return entry;
        }

        static JObject Property(SerializedProperty p, int depth)
        {
            var obj = new JObject
            {
                ["Path"] = p.propertyPath,
                ["Kind"] = p.propertyType.ToString(),
                ["Type"] = p.type,
            };
            if (!string.IsNullOrEmpty(p.tooltip))
            {
                obj["Tooltip"] = p.tooltip;
            }
            if (p.propertyType == SerializedPropertyType.Enum)
            {
                obj["Options"] = new JArray(p.enumNames.Select(n => (object)n).ToArray());
            }
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                obj["Array"] = true;
                obj["Default"] = ValueCodec.Read(p, ReadOptions.Unbounded);
                return obj;
            }
            obj["Default"] = ValueCodec.Read(p, ReadOptions.Unbounded);
            if (depth < 3 && p.hasChildren && (p.propertyType == SerializedPropertyType.Generic || p.propertyType == SerializedPropertyType.ManagedReference))
            {
                var children = new JArray();
                SerializedProperty child = p.Copy();
                SerializedProperty end = p.GetEndProperty();
                if (child.Next(true))
                {
                    while (!SerializedProperty.EqualContents(child, end) && child.depth == p.depth + 1)
                    {
                        children.Add(Property(child, depth + 1));
                        if (!child.Next(false))
                        {
                            break;
                        }
                    }
                }
                obj["Fields"] = children;
            }
            return obj;
        }
    }
}
