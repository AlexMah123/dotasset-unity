using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DotAsset
{
    // A spec value of the wrong type, thrown by Json.Bool and Json.Int. The op or target that read it fails with this message.
    public sealed class SpecValueException : FormatException
    {
        public SpecValueException(string message) : base(message)
        {
        }
    }

    public static class Json
    {
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

        // Through a temp file and File.Replace, so a reader or a crash sees the old file or the new one, never none.
        public static void WriteFile(string path, JToken token)
        {
            DotAssetPaths.ReplaceText(path, Format(token));
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

        // A spec value, read with fallback when the key is absent or null. A value of the wrong type throws
        // SpecValueException naming the key, so "Active": "0" fails the op instead of quietly becoming the default.
        public static bool Bool(JToken token, string key, bool fallback)
        {
            bool result;
            if (TryBool(Get(token, key), fallback, out result))
            {
                return result;
            }
            throw new SpecValueException(key + ": " + ValueCodec.Show(Get(token, key)) + " is not true or false");
        }

        // As Bool: a whole number, also "3" or 3.0. "last", 2.5 or true throws SpecValueException.
        public static int Int(JToken token, string key, int fallback)
        {
            int result;
            if (TryInt(Get(token, key), fallback, out result))
            {
                return result;
            }
            throw new SpecValueException(key + ": " + ValueCodec.Show(Get(token, key)) + " is not a whole number");
        }

        // For files DotAsset writes itself (queue requests, results), where a bad value falls back instead of throwing.
        public static bool LooseBool(JToken token, string key, bool fallback)
        {
            bool result;
            return TryBool(Get(token, key), fallback, out result) ? result : fallback;
        }

        public static int LooseInt(JToken token, string key, int fallback)
        {
            int result;
            return TryInt(Get(token, key), fallback, out result) ? result : fallback;
        }

        static bool TryBool(JToken value, bool fallback, out bool result)
        {
            result = fallback;
            if (value == null || value.Type == JTokenType.Null)
            {
                return true;
            }
            if (value.Type == JTokenType.Boolean)
            {
                result = (bool)value;
                return true;
            }
            if (value.Type == JTokenType.Integer)
            {
                result = (long)value != 0;
                return true;
            }
            return value.Type == JTokenType.String && bool.TryParse((string)value, out result);
        }

        static bool TryInt(JToken value, int fallback, out int result)
        {
            result = fallback;
            if (value == null || value.Type == JTokenType.Null)
            {
                return true;
            }
            double d;
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                d = (double)value;
            }
            else if (value.Type != JTokenType.String || !double.TryParse((string)value, NumberStyles.Integer, CultureInfo.InvariantCulture, out d))
            {
                return false;
            }
            if (Math.Floor(d) != d || d < int.MinValue || d > int.MaxValue)
            {
                return false;
            }
            result = (int)d;
            return true;
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
