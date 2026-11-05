using System;
using System.Collections.Generic;

namespace DotAsset
{
    // Enum values given in a spec as names, in any case. Enum.TryParse alone also takes a number ("7") and a comma
    // list ("In, Out") for any enum, which name no member: those are refused, a comma list only goes with [Flags].
    public static class EnumNames
    {
        public static bool TryParse<T>(string text, out T value) where T : struct
        {
            value = default(T);
            object parsed;
            if (!TryParse(typeof(T), text, out parsed))
            {
                return false;
            }
            value = (T)parsed;
            return true;
        }

        public static bool TryParse(Type enumType, string text, out object value)
        {
            value = null;
            if (text == null || !enumType.IsEnum)
            {
                return false;
            }
            string[] parts = text.Split(',');
            if (parts.Length > 1 && !enumType.IsDefined(typeof(FlagsAttribute), false))
            {
                return false;
            }
            string[] names = Enum.GetNames(enumType);
            var matched = new List<string>();
            foreach (string part in parts)
            {
                string name = Array.Find(names, n => string.Equals(n, part.Trim(), StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    return false;
                }
                matched.Add(name);
            }
            value = Enum.Parse(enumType, string.Join(", ", matched.ToArray()));
            return true;
        }

        // "None, In, Out, Both", for an error message.
        public static string List(Type enumType)
        {
            return string.Join(", ", Enum.GetNames(enumType));
        }
    }
}
