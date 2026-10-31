using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DotAsset
{
    // Extra arguments of a run, in commandlet style:
    //   -apply                     flag
    //   -spec=C:/temp/edit.json    value
    //   -scandir="Assets/My Art"   quoted value, quotes are stripped
    //   -name="say \"hi\""        \" is a literal quote inside a value, except right before a space or the end,
    //                              where the quote closes it, so -scandir="Assets\Art\" works
    //   -apply=false               switches also take =true/1/yes and =false/0/no
    // Keys are case-insensitive. A key given twice keeps the last value.
    public sealed class RunArgs
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();

        public string Raw { get; private set; }

        public IReadOnlyList<string> Keys => order;

        // Tokens that do not start with -, refused by the dispatcher: a bare "apply" must not act as -apply.
        public readonly List<string> Stray = new List<string>();

        // A quote that never closed. It would swallow every argument after it (an -apply among them), so it is refused.
        public bool UnclosedQuote { get; private set; }

        // Arguments that are switches. "-apply=false" must not switch anything on.
        public static readonly string[] Switches = { "apply", "atomic", "full", "expand", "nosub", "recursive", "deep", "force", "permanent", "keepinstances", "metaonly" };

        // Switches given as =false, so a switch that is on by default (-atomic for Migrate) can be turned off.
        readonly HashSet<string> off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Normalizes switch values: "-x=true" or "-x=1" is "-x", "-x=false" or "-x=0" drops it and marks it off.
        // Returns the switches whose value is neither.
        public List<string> NormalizeSwitches()
        {
            var bad = new List<string>();
            foreach (string key in Switches)
            {
                string value;
                if (!values.TryGetValue(key, out value) || value == null)
                {
                    continue;
                }
                string v = value.Trim().ToLowerInvariant();
                if (v == "true" || v == "1" || v == "yes")
                {
                    values[key] = null;
                }
                else if (v == "false" || v == "0" || v == "no")
                {
                    values.Remove(key);
                    order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    off.Add(key);
                }
                else
                {
                    bad.Add(key);
                }
            }
            return bad;
        }

        // Arguments that take a whole number. A typo there must not quietly fall back to the default.
        public static readonly string[] Integers = { "maxarray", "maxstring", "maxsize" };

        // Returns the integer arguments given without a value, or with one that is not a whole number of 0 or more.
        public List<string> BadIntegers()
        {
            var bad = new List<string>();
            foreach (string key in Integers)
            {
                string value;
                int parsed;
                if (values.TryGetValue(key, out value)
                    && (value == null || !int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed)))
                {
                    bad.Add(key);
                }
            }
            return bad;
        }

        public static RunArgs Parse(string raw)
        {
            var args = new RunArgs { Raw = raw ?? string.Empty };
            bool unclosed;
            List<string> tokens = Tokenize(args.Raw, out unclosed);
            args.UnclosedQuote = unclosed;
            foreach (string token in tokens)
            {
                if (!token.StartsWith("-", StringComparison.Ordinal))
                {
                    args.Stray.Add(token);
                    continue;
                }
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

        // Splits on whitespace outside double quotes. Quotes are removed. A backslash before a quote keeps the quote,
        // except where that quote ends a value ("Assets\Art\" followed by a space or the end): Windows folders end in \.
        public static List<string> Tokenize(string raw, out bool unclosed)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            bool any = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool quoteEndsValue = i + 2 >= raw.Length || char.IsWhiteSpace(raw[i + 2]);
                if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '"' && !(inQuotes && quoteEndsValue))
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
            unclosed = inQuotes;
            return tokens;
        }

        public bool Has(string key)
        {
            return values.ContainsKey(key);
        }

        // True when a switch was given as =false, after NormalizeSwitches.
        public bool Off(string key)
        {
            return off.Contains(key);
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
            return SplitList(Get(key));
        }

        public static List<string> SplitList(string csv)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(csv))
            {
                return list;
            }
            foreach (string part in csv.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    list.Add(trimmed);
                }
            }
            return list;
        }
    }
}
