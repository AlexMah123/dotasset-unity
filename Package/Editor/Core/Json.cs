using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AssetOps
{
    public static class Json
    {
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Indented, one property per line, LF line endings. Agents grep the file and read line ranges.
        public static string Format(JToken token)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                sw.NewLine = "\n";
                using (var writer = new JsonTextWriter(sw))
                {
                    writer.Formatting = Formatting.Indented;
                    writer.Indentation = 2;
                    writer.FloatFormatHandling = FloatFormatHandling.String;
                    token.WriteTo(writer);
                }
            }
            sb.Append('\n');
            return sb.ToString();
        }

        public static void WriteFile(string path, JToken token)
        {
            AssetOpsPaths.EnsureParent(path);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Format(token), Utf8NoBom);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        public static bool TryReadFile(string path, out JObject obj, out string error)
        {
            obj = null;
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    error = "file not found: " + path;
                    return false;
                }
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    var token = JToken.ReadFrom(reader);
                    obj = token as JObject;
                    if (obj == null)
                    {
                        error = "top level of " + path + " is not a JSON object";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = "cannot parse " + path + ": " + e.Message;
                return false;
            }
        }

        // float to the shortest double that prints the same, so 0.1f exports as 0.1 and not 0.100000001490116
        public static double F(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return value;
            }
            return double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        public static JArray Array(params float[] values)
        {
            var array = new JArray();
            foreach (float v in values)
            {
                array.Add(F(v));
            }
            return array;
        }

        public static string Str(JToken token, string key, string fallback = null)
        {
            var value = Get(token, key);
            if (value == null || value.Type == JTokenType.Null)
            {
                return fallback;
            }
            return value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
        }

        public static JToken Get(JToken token, string key)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                return null;
            }
            JToken value;
            return obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out value) ? value : null;
        }

        // Null when every key of the object is allowed (or a // comment), otherwise the message naming the strays.
        public static string UnknownKeys(JToken token, params string[] allowed)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                return null;
            }
            var unknown = new List<string>();
            foreach (JProperty p in obj.Properties())
            {
                if (p.Name.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                bool known = false;
                foreach (string a in allowed)
                {
                    if (string.Equals(a, p.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    unknown.Add(p.Name);
                }
            }
            if (unknown.Count == 0)
            {
                return null;
            }
            return "unknown key" + (unknown.Count > 1 ? "s " : " ") + string.Join(", ", unknown) + ". Allowed: " + string.Join(", ", allowed);
        }

        public static bool Has(JToken token, string key)
        {
            var value = Get(token, key);
            return value != null && value.Type != JTokenType.Null;
        }

        public static bool Bool(JToken token, string key, bool fallback)
        {
            var value = Get(token, key);
            if (value == null || value.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (value.Type == JTokenType.Boolean)
            {
                return (bool)value;
            }
            if (value.Type == JTokenType.Integer)
            {
                return (long)value != 0;
            }
            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        public static int Int(JToken token, string key, int fallback)
        {
            var value = Get(token, key);
            if (value == null || value.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                return (int)(double)value;
            }
            int parsed;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        public static bool TryDouble(JToken value, out double result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }
            switch (value.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    result = (double)value;
                    return true;
                case JTokenType.Boolean:
                    result = (bool)value ? 1 : 0;
                    return true;
                case JTokenType.String:
                    string s = (string)value;
                    if (string.Equals(s, "Infinity", StringComparison.OrdinalIgnoreCase))
                    {
                        result = double.PositiveInfinity;
                        return true;
                    }
                    if (string.Equals(s, "-Infinity", StringComparison.OrdinalIgnoreCase))
                    {
                        result = double.NegativeInfinity;
                        return true;
                    }
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }
    }
}
