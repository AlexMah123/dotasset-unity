using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AssetWorkbench
{
    // Extra arguments of a run, in commandlet style:
    //   -apply                     flag
    //   -spec=C:/temp/edit.json    value
    //   -scandir="Assets/My Art"   quoted value, quotes are stripped
    // Keys are case-insensitive. A key given twice keeps the last value.
    public sealed class RunArgs
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();

        public string Raw { get; private set; }

        public IReadOnlyList<string> Keys => order;

        public static RunArgs Parse(string raw)
        {
            var args = new RunArgs { Raw = raw ?? string.Empty };
            foreach (string token in Tokenize(args.Raw))
            {
                string body = token.TrimStart('-');
                if (body.Length == 0)
                {
                    continue;
                }
                int eq = body.IndexOf('=');
                string key = eq < 0 ? body : body.Substring(0, eq);
                string value = eq < 0 ? null : body.Substring(eq + 1);
                if (!args.values.ContainsKey(key))
                {
                    args.order.Add(key);
                }
                args.values[key] = value;
            }
            return args;
        }

        // Splits on whitespace outside double quotes. Quotes are removed, a backslash before a quote keeps it.
        public static List<string> Tokenize(string raw)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            bool any = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '"')
                {
                    current.Append('"');
                    any = true;
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    any = true;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (any)
                    {
                        tokens.Add(current.ToString());
                        current.Length = 0;
                        any = false;
                    }
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any)
            {
                tokens.Add(current.ToString());
            }
            return tokens;
        }

        public bool Has(string key)
        {
            return values.ContainsKey(key);
        }

        public string Get(string key, string fallback = null)
        {
            string value;
            if (values.TryGetValue(key, out value) && value != null)
            {
                return value;
            }
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int result;
            string value = Get(key);
            if (value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return fallback;
        }

        public float GetFloat(string key, float fallback)
        {
            float result;
            string value = Get(key);
            if (value != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return fallback;
        }

        // Comma-separated list, entries trimmed, empties dropped.
        public List<string> GetList(string key)
        {
            var list = new List<string>();
            string value = Get(key);
            if (string.IsNullOrEmpty(value))
            {
                return list;
            }
            foreach (string part in value.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    list.Add(trimmed);
                }
            }
            return list;
        }

        // A value-taking key given without a value, e.g. "-spec" instead of "-spec=path".
        public bool IsBareFlag(string key)
        {
            string value;
            return values.TryGetValue(key, out value) && value == null;
        }
    }
}
