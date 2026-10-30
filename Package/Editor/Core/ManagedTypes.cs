using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotAsset
{
    // [SerializeReference] type entries as Unity writes them in YAML, type: {class: Outer/Inner, ns: My.Game, asm: Game},
    // and the "<assembly> <namespace>.<class>" form managedReferenceFullTypename and exports use for the same type.
    static class ManagedTypes
    {
        public static readonly Regex Entry = new Regex(@"type: \{class: ([^,}]*), ns: ([^,}]*), asm: ([^,}]*)\}", RegexOptions.Compiled);

        static readonly Dictionary<string, Type> resolved = new Dictionary<string, Type>();

        public static string Name(string asm, string ns, string cls)
        {
            return (asm.Length > 0 ? asm + " " : string.Empty) + (ns.Length > 0 ? ns + "." : string.Empty) + cls;
        }

        public static string YamlEntry(string asm, string ns, string cls)
        {
            return "type: {class: " + cls + ", ns: " + ns + ", asm: " + asm + "}";
        }

        // "Game My.Game.Outer/Inner" or "My.Game.Outer/Inner", asm is null when none is given.
        public static bool Parse(string name, out string asm, out string ns, out string cls)
        {
            asm = null;
            ns = string.Empty;
            cls = null;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string full = name.Trim();
            int space = full.IndexOf(' ');
            if (space >= 0)
            {
                asm = full.Substring(0, space);
                full = full.Substring(space + 1).Trim();
            }
            if (full.Length == 0)
            {
                return false;
            }
            int slash = full.IndexOf('/');
            int dot = full.LastIndexOf('.', slash >= 0 ? slash : full.Length - 1);
            ns = dot >= 0 ? full.Substring(0, dot) : string.Empty;
            cls = dot >= 0 ? full.Substring(dot + 1) : full;
            return cls.Length > 0 && full.IndexOfAny(new[] { ' ', ',', '{', '}' }) < 0;
        }

        public static void Split(Type type, out string asm, out string ns, out string cls)
        {
            asm = type.Assembly.GetName().Name;
            ns = type.Namespace ?? string.Empty;
            cls = (ns.Length > 0 ? type.FullName.Substring(ns.Length + 1) : type.FullName).Replace('+', '/');
        }

        // The compiled type the entry names, or null. Cached per domain, types only change with a reload.
        public static Type Resolve(string asm, string ns, string cls)
        {
            string key = Name(asm, ns, cls);
            Type type;
            if (resolved.TryGetValue(key, out type))
            {
                return type;
            }
            string full = (ns.Length > 0 ? ns + "." : string.Empty) + cls.Replace('/', '+');
            // As in TypeResolver: statics reset on every domain reload, so no unloaded assembly is ever asked.
#pragma warning disable UAC0005
            type = AppDomain.CurrentDomain.GetAssemblies()
#pragma warning restore UAC0005
                .Where(a => a.GetName().Name == asm)
                .Select(a => a.GetType(full, false))
                .FirstOrDefault(t => t != null);
            resolved[key] = type;
            return type;
        }

        // Compiled types with the same class name, for a renamed or moved type. At most a few.
        public static List<string> Candidates(string cls)
        {
            string simple = cls.Substring(cls.LastIndexOf('/') + 1);
            var found = new List<string>();
#pragma warning disable UAC0005
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
            {
                if (a.IsDynamic)
                {
                    continue;
                }
                Type[] types;
                try
                {
                    types = a.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                foreach (Type t in types)
                {
                    if (t.Name == simple && t.IsSerializable && !t.IsAbstract && !typeof(UnityEngine.Object).IsAssignableFrom(t))
                    {
                        string tAsm, tNs, tCls;
                        Split(t, out tAsm, out tNs, out tCls);
                        found.Add(Name(tAsm, tNs, tCls));
                        if (found.Count == 5)
                        {
                            return found;
                        }
                    }
                }
            }
            return found;
        }
    }
}
