using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DotAsset
{
    // GameObject addressing shared by every export and edit:
    //   "Player/Model/Hand.R"   names from the root down, the root's own name included
    //   "Enemies/Grunt[2]"      [i] picks the i-th (from 0) of same-named siblings, only written when names repeat
    //   "My\/Odd\[Name"         a slash, bracket or backslash inside a name is escaped with a backslash
    // Components are addressed by type plus an index among same-type components on that GameObject.
    public static class HierarchyPath
    {
        public static string Of(Transform t)
        {
            var segments = new List<string>();
            for (Transform cur = t; cur != null; cur = cur.parent)
            {
                segments.Add(Segment(cur));
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        public static string Of(GameObject go)
        {
            return go == null ? null : Of(go.transform);
        }

        static string Segment(Transform t)
        {
            string escaped = Escape(t.name);
            IEnumerable<Transform> siblings = Siblings(t);
            int same = 0;
            int index = 0;
            foreach (Transform s in siblings)
            {
                if (s.name == t.name)
                {
                    if (s == t)
                    {
                        index = same;
                    }
                    same++;
                }
            }
            return same > 1 ? escaped + "[" + index + "]" : escaped;
        }

        static IEnumerable<Transform> Siblings(Transform t)
        {
            if (t.parent != null)
            {
                for (int i = 0; i < t.parent.childCount; i++)
                {
                    yield return t.parent.GetChild(i);
                }
                yield break;
            }
            if (t.gameObject.scene.IsValid())
            {
                foreach (GameObject root in t.gameObject.scene.GetRootGameObjects())
                {
                    yield return root.transform;
                }
                yield break;
            }
            yield return t;
        }

        public static string Escape(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '/' || c == '[' || c == ']' || c == '\\')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        struct ParsedSegment
        {
            public string Name;
            public int Index;
        }

        static bool TryParse(string path, List<ParsedSegment> segments, out string error)
        {
            error = null;
            var name = new StringBuilder();
            int index = -1;
            int i = 0;
            while (i <= path.Length)
            {
                if (i == path.Length || path[i] == '/')
                {
                    if (name.Length == 0)
                    {
                        error = "empty segment in path '" + path + "'";
                        return false;
                    }
                    segments.Add(new ParsedSegment { Name = name.ToString(), Index = index });
                    name.Length = 0;
                    index = -1;
                    i++;
                    continue;
                }
                char c = path[i];
                if (c == '\\' && i + 1 < path.Length)
                {
                    name.Append(path[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '[')
                {
                    int close = path.IndexOf(']', i);
                    int parsed;
                    if (close < 0 || !int.TryParse(path.Substring(i + 1, close - i - 1), out parsed) || parsed < 0)
                    {
                        error = "bad index in path '" + path + "'";
                        return false;
                    }
                    index = parsed;
                    i = close + 1;
                    if (i < path.Length && path[i] != '/')
                    {
                        error = "text after an index in path '" + path + "'";
                        return false;
                    }
                    continue;
                }
                name.Append(c);
                i++;
            }
            return true;
        }

        // Resolves a path against a set of roots (scene roots, or the single root of a prefab).
        public static GameObject Resolve(IList<GameObject> roots, string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "empty GameObject path";
                return null;
            }
            var segments = new List<ParsedSegment>();
            if (!TryParse(path, segments, out error))
            {
                return null;
            }
            IList<Transform> level = roots.Where(r => r != null).Select(r => r.transform).ToList();
            Transform found = null;
            for (int s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                var named = level.Where(t => t.name == seg.Name).ToList();
                if (named.Count == 0)
                {
                    string where = found == null ? "at the root" : "under '" + Of(found) + "'";
                    var names = level.Select(t => t.name).Distinct().Take(12).ToList();
                    error = "no GameObject '" + seg.Name + "' " + where + (names.Count > 0 ? ". There: " + string.Join(", ", names) : ", it has no children");
                    return null;
                }
                if (seg.Index >= 0)
                {
                    if (seg.Index >= named.Count)
                    {
                        error = "'" + seg.Name + "[" + seg.Index + "]' is out of range, there are " + named.Count;
                        return null;
                    }
                    found = named[seg.Index];
                }
                else if (named.Count > 1)
                {
                    error = named.Count + " GameObjects are named '" + seg.Name + "' there, add [0] to [" + (named.Count - 1) + "]";
                    return null;
                }
                else
                {
                    found = named[0];
                }
                var children = new List<Transform>();
                for (int c = 0; c < found.childCount; c++)
                {
                    children.Add(found.GetChild(c));
                }
                level = children;
            }
            return found == null ? null : found.gameObject;
        }

        public static IEnumerable<GameObject> DepthFirst(GameObject root)
        {
            var stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                yield return t.gameObject;
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    stack.Push(t.GetChild(i));
                }
            }
        }

        public static int IndexAmongSameType(Component c)
        {
            if (c == null)
            {
                return 0;
            }
            int index = 0;
            foreach (Component other in c.gameObject.GetComponents(c.GetType()))
            {
                if (other == c)
                {
                    return index;
                }
                if (other.GetType() == c.GetType())
                {
                    index++;
                }
            }
            return 0;
        }

        // "GameObject" addresses the GameObject itself.
        public static UnityEngine.Object ResolveComponent(GameObject go, string typeName, int index, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(typeName) || typeName == "GameObject")
            {
                return go;
            }
            Type type = TypeResolver.Find(typeName, typeof(Component), out error);
            if (type == null)
            {
                return null;
            }
            var all = go.GetComponents(type).Where(c => c != null && c.GetType() == type).ToList();
            if (all.Count == 0)
            {
                // Allow a base type, e.g. "Collider" for the one collider on the object.
                all = go.GetComponents(type).Where(c => c != null).ToList();
            }
            if (all.Count == 0)
            {
                error = "'" + Of(go) + "' has no " + typeName + ". It has: " + string.Join(", ", go.GetComponents<Component>().Select(c => c == null ? "(Missing Script)" : TypeResolver.DisplayName(c.GetType())));
                return null;
            }
            if (index < 0)
            {
                error = "Index " + index + " is negative";
                return null;
            }
            if (index >= all.Count)
            {
                error = "'" + Of(go) + "' has " + all.Count + " " + typeName + ", index " + index + " is out of range";
                return null;
            }
            return all[index];
        }
    }
}
