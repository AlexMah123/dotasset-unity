using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Finds a type from what an agent writes: "Rigidbody", "UnityEngine.Rigidbody", "MyGame.Inventory",
    // "PPtr<$Material>", "Assets/Scripts/Inventory.cs" (the class a MonoScript defines),
    // or Unity's managed reference form "Assembly-CSharp MyGame.Weapon".
    public static class TypeResolver
    {
        static Dictionary<string, Type> byFullName;
        static Dictionary<string, List<Type>> byShortName;

        static void Build()
        {
            byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
            byShortName = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (Type t in types)
                {
                    if (t.FullName == null || t.IsGenericTypeDefinition || t.FullName.IndexOf('<') >= 0)
                    {
                        continue;
                    }
                    if (!byFullName.ContainsKey(t.FullName))
                    {
                        byFullName.Add(t.FullName, t);
                    }
                    List<Type> list;
                    if (!byShortName.TryGetValue(t.Name, out list))
                    {
                        list = new List<Type>();
                        byShortName.Add(t.Name, list);
                    }
                    list.Add(t);
                }
            }
        }

        // baseType narrows the candidates, e.g. typeof(Component) for AddComponent.
        public static Type Find(string name, Type baseType, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no type given";
                return null;
            }
            if (byFullName == null)
            {
                Build();
            }
            name = name.Trim();

            if (name.StartsWith("PPtr<", StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal))
            {
                name = name.Substring(5, name.Length - 6).TrimStart('$');
            }

            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && (name.StartsWith("Assets/", StringComparison.Ordinal) || name.StartsWith("Packages/", StringComparison.Ordinal)))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(name);
                Type cls = script != null ? script.GetClass() : null;
                if (cls == null)
                {
                    error = "no class found in script " + name + " (the class name must match the file name, and the project must compile)";
                    return null;
                }
                return Check(cls, baseType, name, out error);
            }

            // Unity's managed reference typename: "<assembly> <namespace.type>"
            int space = name.IndexOf(' ');
            if (space > 0)
            {
                string assemblyName = name.Substring(0, space);
                string typeName = name.Substring(space + 1).Replace('/', '+');
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == assemblyName)
                    {
                        Type t = assembly.GetType(typeName, false);
                        if (t != null)
                        {
                            return Check(t, baseType, name, out error);
                        }
                    }
                }
                name = typeName;
            }

            Type exact;
            if (byFullName.TryGetValue(name, out exact) || byFullName.TryGetValue(name.Replace('/', '+'), out exact))
            {
                return Check(exact, baseType, name, out error);
            }

            List<Type> candidates;
            string shortName = name.IndexOf('.') >= 0 ? name.Substring(name.LastIndexOf('.') + 1) : name;
            if (!byShortName.TryGetValue(shortName, out candidates))
            {
                error = "type '" + name + "' not found";
                return null;
            }
            var matching = candidates.Where(t => baseType == null || baseType.IsAssignableFrom(t)).ToList();
            if (name.IndexOf('.') >= 0)
            {
                matching = matching.Where(t => t.FullName.EndsWith(name, StringComparison.Ordinal)).ToList();
            }
            if (matching.Count == 1)
            {
                return matching[0];
            }
            if (matching.Count == 0)
            {
                error = "type '" + name + "' exists but is not a " + (baseType != null ? baseType.Name : "usable type");
                return null;
            }
            // Prefer UnityEngine over UnityEditor and internal namespaces when a short name is shared.
            var engine = matching.Where(t => t.Namespace == "UnityEngine").ToList();
            if (engine.Count == 1)
            {
                return engine[0];
            }
            var global = matching.Where(t => string.IsNullOrEmpty(t.Namespace)).ToList();
            if (global.Count == 1)
            {
                return global[0];
            }
            error = "type '" + name + "' is ambiguous, use the full name: " + string.Join(", ", matching.Select(t => t.FullName).Take(8));
            return null;
        }

        static Type Check(Type t, Type baseType, string name, out string error)
        {
            error = null;
            if (baseType != null && !baseType.IsAssignableFrom(t))
            {
                error = "'" + name + "' is a " + t.FullName + ", not a " + baseType.Name;
                return null;
            }
            return t;
        }

        // Short name when no other UnityEngine.Object type shares it, otherwise the full name.
        public static string DisplayName(Type t)
        {
            if (t == null)
            {
                return null;
            }
            if (byFullName == null)
            {
                Build();
            }
            List<Type> candidates;
            if (byShortName.TryGetValue(t.Name, out candidates))
            {
                int objectTypes = candidates.Count(c => typeof(Object).IsAssignableFrom(c));
                if (objectTypes > 1 && typeof(Object).IsAssignableFrom(t))
                {
                    return t.FullName;
                }
            }
            return t.Name;
        }

        // All Component or ScriptableObject types whose name contains the filter, for the catalog.
        public static List<Type> Search(string filter, Type baseType)
        {
            if (byFullName == null)
            {
                Build();
            }
            return TypeCache.GetTypesDerivedFrom(baseType)
                .Where(t => !t.IsAbstract && !t.IsGenericType && t.FullName != null
                    && (string.IsNullOrEmpty(filter) || t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.FullName)
                .ToList();
        }
    }
}
