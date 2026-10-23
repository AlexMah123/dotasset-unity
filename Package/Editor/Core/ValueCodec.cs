using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    public sealed class ReadOptions
    {
        public RefScope Scope = RefScope.None;
        public int MaxArray = 100;
        public int MaxString = 4000;
        public int MaxDepth = 12;

        public static ReadOptions FromArgs(RunArgs args, RefScope scope)
        {
            return new ReadOptions
            {
                Scope = scope ?? RefScope.None,
                MaxArray = args.GetInt("maxarray", 100),
                MaxString = args.GetInt("maxstring", 4000),
            };
        }

        public static readonly ReadOptions Unbounded = new ReadOptions { MaxArray = int.MaxValue, MaxString = int.MaxValue };
    }

    public sealed class WriteContext
    {
        public RefScope Scope = RefScope.None;
        // Record undo for scene edits so a failed or dry run can be reverted, and an applied one undone by hand.
        public bool UseUndo;
        public readonly List<string> Errors = new List<string>();

        public void Fail(SerializedProperty prop, string message)
        {
            Errors.Add((prop != null ? prop.propertyPath + ": " : string.Empty) + message);
        }
    }

    // SerializedProperty <-> JSON. One format for every export and every edit, so an export reads back as a spec.
    //   numbers, bools, strings      as JSON
    //   enums                        the name, or the integer value when no single name matches (flags)
    //   Vector2/3/4, Rect, Color     [x, y, ...]; colors also take "#RRGGBB" or "#RRGGBBAA"
    //   Quaternion                   [x, y, z, w] on export; a write takes 4 numbers, or 3 as Euler degrees
    //   Bounds                       {"Center": [..], "Extents": [..]}
    //   object references            see ObjectRefs
    //   arrays and lists             JSON arrays, longer than MaxArray end with {"$truncated": <total>}
    //   structs and plain classes    objects of their fields; a write patches only the fields given
    //   [SerializeReference]         {"$type": "<assembly> <type>", fields...}, null for none
    //   AnimationCurve               {"Keys": [{"Time","Value","In","Out"}], "PreWrap", "PostWrap"}
    //   Gradient                     {"Mode", "Colors": [[t, r, g, b]], "Alphas": [[t, a]]}
    public static class ValueCodec
    {
        // ------------------------------------------------------------------ read

        public static JToken Read(SerializedProperty prop, ReadOptions opt, int depth = 0)
        {
            if (depth > opt.MaxDepth)
            {
                return new JObject { ["$truncated"] = "depth" };
            }
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (prop.numericType == SerializedPropertyNumericType.UInt64)
                    {
                        return new JValue(prop.ulongValue);
                    }
                    return new JValue(prop.longValue);
                case SerializedPropertyType.Boolean:
                    return new JValue(prop.boolValue);
                case SerializedPropertyType.Float:
                    return prop.numericType == SerializedPropertyNumericType.Double ? new JValue(prop.doubleValue) : new JValue(Json.F(prop.floatValue));
                case SerializedPropertyType.String:
                    string s = prop.stringValue ?? string.Empty;
                    if (s.Length > opt.MaxString)
                    {
                        return s.Substring(0, opt.MaxString) + "...[truncated, " + s.Length + " chars]";
                    }
                    return s;
                case SerializedPropertyType.Color:
                    Color c = prop.colorValue;
                    return Json.Array(c.r, c.g, c.b, c.a);
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue == null && prop.objectReferenceInstanceIDValue != 0)
                    {
                        return new JObject { ["Missing"] = true };
                    }
                    return ObjectRefs.Write(prop.objectReferenceValue, opt.Scope);
                case SerializedPropertyType.LayerMask:
                    return new JValue(prop.intValue);
                case SerializedPropertyType.Enum:
                    return ReadEnum(prop);
                case SerializedPropertyType.Vector2:
                    Vector2 v2 = prop.vector2Value;
                    return Json.Array(v2.x, v2.y);
                case SerializedPropertyType.Vector3:
                    Vector3 v3 = prop.vector3Value;
                    return Json.Array(v3.x, v3.y, v3.z);
                case SerializedPropertyType.Vector4:
                    Vector4 v4 = prop.vector4Value;
                    return Json.Array(v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Rect:
                    Rect r = prop.rectValue;
                    return Json.Array(r.x, r.y, r.width, r.height);
                case SerializedPropertyType.ArraySize:
                    return new JValue(prop.intValue);
                case SerializedPropertyType.Character:
                    return ((char)prop.intValue).ToString();
                case SerializedPropertyType.AnimationCurve:
                    return ReadCurve(prop.animationCurveValue);
                case SerializedPropertyType.Bounds:
                    Bounds b = prop.boundsValue;
                    return new JObject { ["Center"] = Json.Array(b.center.x, b.center.y, b.center.z), ["Extents"] = Json.Array(b.extents.x, b.extents.y, b.extents.z) };
                case SerializedPropertyType.Gradient:
                    return ReadGradient(prop.gradientValue);
                case SerializedPropertyType.Quaternion:
                    Quaternion q = prop.quaternionValue;
                    return Json.Array(q.x, q.y, q.z, q.w);
                case SerializedPropertyType.ExposedReference:
                    return ObjectRefs.Write(prop.exposedReferenceValue, opt.Scope);
                case SerializedPropertyType.FixedBufferSize:
                    return new JValue(prop.intValue);
                case SerializedPropertyType.Vector2Int:
                    Vector2Int v2i = prop.vector2IntValue;
                    return new JArray(v2i.x, v2i.y);
                case SerializedPropertyType.Vector3Int:
                    Vector3Int v3i = prop.vector3IntValue;
                    return new JArray(v3i.x, v3i.y, v3i.z);
                case SerializedPropertyType.RectInt:
                    RectInt ri = prop.rectIntValue;
                    return new JArray(ri.x, ri.y, ri.width, ri.height);
                case SerializedPropertyType.BoundsInt:
                    BoundsInt bi = prop.boundsIntValue;
                    return new JObject { ["Position"] = new JArray(bi.position.x, bi.position.y, bi.position.z), ["Size"] = new JArray(bi.size.x, bi.size.y, bi.size.z) };
                case SerializedPropertyType.Hash128:
                    return prop.hash128Value.ToString();
                case SerializedPropertyType.ManagedReference:
                    return ReadManaged(prop, opt, depth);
                case SerializedPropertyType.Generic:
                    return ReadGeneric(prop, opt, depth);
                default:
                    // Newer property kinds, e.g. RenderingLayerMask in Unity 6, are unsigned bit masks.
                    if ((int)prop.propertyType == 26)
                    {
                        return new JValue(prop.uintValue);
                    }
                    if (prop.hasChildren)
                    {
                        return ReadChildren(prop, opt, depth);
                    }
                    return new JObject { ["$unsupported"] = prop.propertyType.ToString() };
            }
        }

        static JToken ReadEnum(SerializedProperty prop)
        {
            int index = prop.enumValueIndex;
            string[] names = prop.enumNames;
            if (index >= 0 && index < names.Length)
            {
                return names[index];
            }
            return new JValue(prop.intValue);
        }

        static JToken ReadGeneric(SerializedProperty prop, ReadOptions opt, int depth)
        {
            if (prop.isArray)
            {
                int size = prop.arraySize;
                var array = new JArray();
                int n = Math.Min(size, opt.MaxArray);
                for (int i = 0; i < n; i++)
                {
                    array.Add(Read(prop.GetArrayElementAtIndex(i), opt, depth + 1));
                }
                if (size > n)
                {
                    array.Add(new JObject { ["$truncated"] = size });
                }
                return array;
            }
            if (prop.isFixedBuffer)
            {
                var array = new JArray();
                for (int i = 0; i < prop.fixedBufferSize; i++)
                {
                    array.Add(Read(prop.GetFixedBufferElementAtIndex(i), opt, depth + 1));
                }
                return array;
            }
            return ReadChildren(prop, opt, depth);
        }

        static JObject ReadChildren(SerializedProperty prop, ReadOptions opt, int depth)
        {
            var obj = new JObject();
            if (!prop.hasChildren)
            {
                return obj;
            }
            SerializedProperty child = prop.Copy();
            SerializedProperty end = prop.GetEndProperty();
            if (!child.Next(true))
            {
                return obj;
            }
            while (!SerializedProperty.EqualContents(child, end))
            {
                if (child.depth != prop.depth + 1)
                {
                    break;
                }
                obj[child.name] = Read(child, opt, depth + 1);
                if (!child.Next(false))
                {
                    break;
                }
            }
            return obj;
        }

        static JToken ReadManaged(SerializedProperty prop, ReadOptions opt, int depth)
        {
            string typename = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(typename))
            {
                return JValue.CreateNull();
            }
            JObject fields = ReadChildren(prop, opt, depth);
            fields.AddFirst(new JProperty("$type", typename));
            return fields;
        }

        static JObject ReadCurve(AnimationCurve curve)
        {
            var keys = new JArray();
            if (curve != null)
            {
                foreach (Keyframe k in curve.keys)
                {
                    var key = new JObject { ["Time"] = Json.F(k.time), ["Value"] = Json.F(k.value), ["In"] = Json.F(k.inTangent), ["Out"] = Json.F(k.outTangent) };
                    if (k.weightedMode != WeightedMode.None)
                    {
                        key["WeightedMode"] = k.weightedMode.ToString();
                        key["InWeight"] = Json.F(k.inWeight);
                        key["OutWeight"] = Json.F(k.outWeight);
                    }
                    keys.Add(key);
                }
            }
            return new JObject
            {
                ["Keys"] = keys,
                ["PreWrap"] = curve != null ? curve.preWrapMode.ToString() : "ClampForever",
                ["PostWrap"] = curve != null ? curve.postWrapMode.ToString() : "ClampForever",
            };
        }

        static JObject ReadGradient(Gradient g)
        {
            var colors = new JArray();
            var alphas = new JArray();
            if (g != null)
            {
                foreach (GradientColorKey k in g.colorKeys)
                {
                    colors.Add(Json.Array(k.time, k.color.r, k.color.g, k.color.b));
                }
                foreach (GradientAlphaKey k in g.alphaKeys)
                {
                    alphas.Add(Json.Array(k.time, k.alpha));
                }
            }
            return new JObject { ["Mode"] = g != null ? g.mode.ToString() : "Blend", ["Colors"] = colors, ["Alphas"] = alphas };
        }

        // ------------------------------------------------------------------ write

        // Writes a JSON value into the property. Returns false and records why in wc.Errors when it cannot.
        public static bool Write(SerializedProperty prop, JToken value, WriteContext wc)
        {
            int before = wc.Errors.Count;
            try
            {
                WriteInner(prop, value, wc);
            }
            catch (Exception e)
            {
                wc.Fail(prop, e.Message);
            }
            return wc.Errors.Count == before;
        }

        static void WriteInner(SerializedProperty prop, JToken value, WriteContext wc)
        {
            if (value == null)
            {
                wc.Fail(prop, "no value");
                return;
            }
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                {
                    double d;
                    if (!Json.TryDouble(value, out d) || Math.Floor(d) != d)
                    {
                        wc.Fail(prop, "expects an integer, got " + Show(value));
                        return;
                    }
                    string range = value.Type == JTokenType.Integer ? OutOfRangeExact(prop.numericType, value) : null;
                    range = range ?? (value.Type == JTokenType.Integer && Is64(prop.numericType) ? null : OutOfRange(prop.numericType, value, d));
                    if (range != null)
                    {
                        wc.Fail(prop, range);
                        return;
                    }
                    string digits = value.Type == JTokenType.Integer ? value.ToString(Newtonsoft.Json.Formatting.None) : null;
                    if (prop.numericType == SerializedPropertyNumericType.UInt64)
                    {
                        prop.ulongValue = digits != null ? ulong.Parse(digits, CultureInfo.InvariantCulture) : (ulong)d;
                    }
                    else
                    {
                        prop.longValue = digits != null ? long.Parse(digits, CultureInfo.InvariantCulture) : (long)d;
                    }
                    return;
                }
                case SerializedPropertyType.Boolean:
                    if (value.Type == JTokenType.Boolean)
                    {
                        prop.boolValue = (bool)value;
                        return;
                    }
                    if (value.Type == JTokenType.Integer)
                    {
                        prop.boolValue = (long)value != 0;
                        return;
                    }
                    wc.Fail(prop, "expects true or false, got " + Show(value));
                    return;
                case SerializedPropertyType.Float:
                {
                    double d;
                    if (!Json.TryDouble(value, out d))
                    {
                        wc.Fail(prop, "expects a number, got " + Show(value));
                        return;
                    }
                    if (prop.numericType == SerializedPropertyNumericType.Double)
                    {
                        prop.doubleValue = d;
                    }
                    else
                    {
                        prop.floatValue = (float)d;
                    }
                    return;
                }
                case SerializedPropertyType.String:
                    if (value.Type == JTokenType.Null)
                    {
                        prop.stringValue = string.Empty;
                        return;
                    }
                    if (value.Type == JTokenType.Object || value.Type == JTokenType.Array)
                    {
                        wc.Fail(prop, "expects a string, got " + value.Type);
                        return;
                    }
                    prop.stringValue = value.Type == JTokenType.String ? (string)value : value.ToString(Newtonsoft.Json.Formatting.None);
                    return;
                case SerializedPropertyType.Color:
                {
                    Color c;
                    if (!TryColor(value, out c))
                    {
                        wc.Fail(prop, "expects [r, g, b, a] or \"#RRGGBB(AA)\", got " + Show(value));
                        return;
                    }
                    prop.colorValue = c;
                    return;
                }
                case SerializedPropertyType.ObjectReference:
                    WriteObjectReference(prop, value, wc);
                    return;
                case SerializedPropertyType.LayerMask:
                    WriteLayerMask(prop, value, wc);
                    return;
                case SerializedPropertyType.Enum:
                    WriteEnum(prop, value, wc);
                    return;
                case SerializedPropertyType.Vector2:
                {
                    float[] f;
                    if (!Floats(value, 2, out f))
                    {
                        wc.Fail(prop, "expects [x, y], got " + Show(value));
                        return;
                    }
                    prop.vector2Value = new Vector2(f[0], f[1]);
                    return;
                }
                case SerializedPropertyType.Vector3:
                {
                    float[] f;
                    if (!Floats(value, 3, out f))
                    {
                        wc.Fail(prop, "expects [x, y, z], got " + Show(value));
                        return;
                    }
                    prop.vector3Value = new Vector3(f[0], f[1], f[2]);
                    return;
                }
                case SerializedPropertyType.Vector4:
                {
                    float[] f;
                    if (!Floats(value, 4, out f))
                    {
                        wc.Fail(prop, "expects [x, y, z, w], got " + Show(value));
                        return;
                    }
                    prop.vector4Value = new Vector4(f[0], f[1], f[2], f[3]);
                    return;
                }
                case SerializedPropertyType.Rect:
                {
                    float[] f;
                    if (!Floats(value, 4, out f))
                    {
                        wc.Fail(prop, "expects [x, y, width, height], got " + Show(value));
                        return;
                    }
                    prop.rectValue = new Rect(f[0], f[1], f[2], f[3]);
                    return;
                }
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.FixedBufferSize:
                {
                    double d;
                    if (!Json.TryDouble(value, out d) || d < 0)
                    {
                        wc.Fail(prop, "expects a size, got " + Show(value));
                        return;
                    }
                    prop.intValue = (int)d;
                    return;
                }
                case SerializedPropertyType.Character:
                {
                    string s = value.Type == JTokenType.String ? (string)value : null;
                    if (s != null && s.Length == 1)
                    {
                        prop.intValue = s[0];
                        return;
                    }
                    double d;
                    if (Json.TryDouble(value, out d))
                    {
                        prop.intValue = (int)d;
                        return;
                    }
                    wc.Fail(prop, "expects a one-character string, got " + Show(value));
                    return;
                }
                case SerializedPropertyType.AnimationCurve:
                {
                    AnimationCurve curve;
                    string error;
                    if (!TryCurve(value, out curve, out error))
                    {
                        wc.Fail(prop, error);
                        return;
                    }
                    prop.animationCurveValue = curve;
                    return;
                }
                case SerializedPropertyType.Bounds:
                {
                    float[] center, extents;
                    if (!Floats(Json.Get(value, "Center"), 3, out center) || !Floats(Json.Get(value, "Extents"), 3, out extents))
                    {
                        wc.Fail(prop, "expects {\"Center\": [x, y, z], \"Extents\": [x, y, z]}, got " + Show(value));
                        return;
                    }
                    prop.boundsValue = new Bounds(new Vector3(center[0], center[1], center[2]), new Vector3(extents[0], extents[1], extents[2]) * 2f);
                    return;
                }
                case SerializedPropertyType.Gradient:
                {
                    Gradient g;
                    string error;
                    if (!TryGradient(value, out g, out error))
                    {
                        wc.Fail(prop, error);
                        return;
                    }
                    prop.gradientValue = g;
                    return;
                }
                case SerializedPropertyType.Quaternion:
                {
                    var arr = value as JArray;
                    float[] f;
                    if (arr != null && arr.Count == 3 && Floats(value, 3, out f))
                    {
                        prop.quaternionValue = Quaternion.Euler(f[0], f[1], f[2]);
                        return;
                    }
                    if (Floats(value, 4, out f))
                    {
                        prop.quaternionValue = new Quaternion(f[0], f[1], f[2], f[3]);
                        return;
                    }
                    wc.Fail(prop, "expects [x, y, z, w], or [x, y, z] Euler degrees, got " + Show(value));
                    return;
                }
                case SerializedPropertyType.ExposedReference:
                {
                    string error;
                    Object target = ObjectRefs.Resolve(value, typeof(Object), wc.Scope, out error);
                    if (error != null)
                    {
                        wc.Fail(prop, error);
                        return;
                    }
                    prop.exposedReferenceValue = target;
                    return;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    int[] n;
                    if (!Ints(value, 2, out n))
                    {
                        wc.Fail(prop, "expects [x, y] integers, got " + Show(value));
                        return;
                    }
                    prop.vector2IntValue = new Vector2Int(n[0], n[1]);
                    return;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    int[] n;
                    if (!Ints(value, 3, out n))
                    {
                        wc.Fail(prop, "expects [x, y, z] integers, got " + Show(value));
                        return;
                    }
                    prop.vector3IntValue = new Vector3Int(n[0], n[1], n[2]);
                    return;
                }
                case SerializedPropertyType.RectInt:
                {
                    int[] n;
                    if (!Ints(value, 4, out n))
                    {
                        wc.Fail(prop, "expects [x, y, width, height] integers, got " + Show(value));
                        return;
                    }
                    prop.rectIntValue = new RectInt(n[0], n[1], n[2], n[3]);
                    return;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    int[] p, s;
                    if (!Ints(Json.Get(value, "Position"), 3, out p) || !Ints(Json.Get(value, "Size"), 3, out s))
                    {
                        wc.Fail(prop, "expects {\"Position\": [x, y, z], \"Size\": [x, y, z]}, got " + Show(value));
                        return;
                    }
                    prop.boundsIntValue = new BoundsInt(new Vector3Int(p[0], p[1], p[2]), new Vector3Int(s[0], s[1], s[2]));
                    return;
                }
                case SerializedPropertyType.Hash128:
                    if (value.Type != JTokenType.String || !System.Text.RegularExpressions.Regex.IsMatch((string)value, "^[0-9a-fA-F]{32}$"))
                    {
                        wc.Fail(prop, "expects a 32-digit hex string, got " + Show(value));
                        return;
                    }
                    prop.hash128Value = Hash128.Parse((string)value);
                    return;
                case SerializedPropertyType.ManagedReference:
                    WriteManaged(prop, value, wc);
                    return;
                case SerializedPropertyType.Generic:
                    WriteGeneric(prop, value, wc);
                    return;
                default:
                    if ((int)prop.propertyType == 26)
                    {
                        double d;
                        if (!Json.TryDouble(value, out d) || d < 0)
                        {
                            wc.Fail(prop, "expects an unsigned mask, got " + Show(value));
                            return;
                        }
                        prop.uintValue = (uint)d;
                        return;
                    }
                    if (prop.hasChildren && value.Type == JTokenType.Object)
                    {
                        WriteChildren(prop, (JObject)value, wc);
                        return;
                    }
                    wc.Fail(prop, "cannot write a " + prop.propertyType + " property");
                    return;
            }
        }

        static void WriteObjectReference(SerializedProperty prop, JToken value, WriteContext wc)
        {
            Type expected = FieldTypes.ObjectReferenceType(prop);
            string error;
            Object target = ObjectRefs.Resolve(value, expected, wc.Scope, out error);
            if (error != null)
            {
                wc.Fail(prop, error);
                return;
            }
            prop.objectReferenceValue = target;
            if (prop.objectReferenceValue != target)
            {
                wc.Fail(prop, "Unity refused " + (target != null ? target.GetType().Name + " '" + target.name + "'" : "null") + " for this " + prop.type + " field");
            }
        }

        static void WriteLayerMask(SerializedProperty prop, JToken value, WriteContext wc)
        {
            if (value.Type == JTokenType.Integer)
            {
                prop.intValue = (int)(long)value;
                return;
            }
            var names = value as JArray;
            if (value.Type == JTokenType.String)
            {
                names = new JArray(((string)value).Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            }
            if (names == null)
            {
                wc.Fail(prop, "expects a mask integer or a list of layer names, got " + Show(value));
                return;
            }
            int mask = 0;
            foreach (JToken n in names)
            {
                string layerName = (string)n;
                if (layerName == "Everything")
                {
                    mask = ~0;
                    continue;
                }
                if (layerName == "Nothing")
                {
                    continue;
                }
                int layer = LayerMask.NameToLayer(layerName);
                if (layer < 0)
                {
                    wc.Fail(prop, "no layer named '" + layerName + "'");
                    return;
                }
                mask |= 1 << layer;
            }
            prop.intValue = mask;
        }

        static void WriteEnum(SerializedProperty prop, JToken value, WriteContext wc)
        {
            string[] names = prop.enumNames;
            string[] display = prop.enumDisplayNames;
            if (value.Type == JTokenType.Integer)
            {
                prop.intValue = (int)(long)value;
                return;
            }
            if (value.Type != JTokenType.String)
            {
                wc.Fail(prop, "expects an enum name or integer value, got " + Show(value));
                return;
            }
            string text = ((string)value).Trim();
            int index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = Array.FindIndex(display, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            }
            if (index >= 0)
            {
                prop.enumValueIndex = index;
                return;
            }

            // Flags: "A|B" or "A, B", resolved through the C# enum when the field has one.
            Type enumType = FieldTypes.Of(prop);
            if (enumType != null && enumType.IsEnum && (text.IndexOf('|') >= 0 || text.IndexOf(',') >= 0))
            {
                long combined = 0;
                foreach (string part in text.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = part.Trim();
                    string match = Enum.GetNames(enumType).FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        wc.Fail(prop, "'" + p + "' is not a " + enumType.Name + ". Options: " + string.Join(", ", names));
                        return;
                    }
                    combined |= Convert.ToInt64(Enum.Parse(enumType, match), CultureInfo.InvariantCulture);
                }
                prop.longValue = combined;
                return;
            }
            int asInt;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out asInt))
            {
                prop.intValue = asInt;
                return;
            }
            wc.Fail(prop, "'" + text + "' is not an option. Options: " + string.Join(", ", names));
        }

        static void WriteGeneric(SerializedProperty prop, JToken value, WriteContext wc)
        {
            if (prop.isArray)
            {
                var array = value as JArray;
                if (array == null)
                {
                    wc.Fail(prop, "expects an array, got " + Show(value));
                    return;
                }
                if (array.Any(e => e is JObject && Json.Has(e, "$truncated")))
                {
                    wc.Fail(prop, "this array was truncated on export, write the full array or use ArrayOps");
                    return;
                }
                int oldSize = prop.arraySize;
                prop.arraySize = array.Count;
                for (int i = 0; i < array.Count; i++)
                {
                    SerializedProperty element = prop.GetArrayElementAtIndex(i);
                    if (i >= oldSize && element.propertyType == SerializedPropertyType.ManagedReference)
                    {
                        // A grown [SerializeReference] array repeats the last reference. A new slot must not share it.
                        element.managedReferenceValue = null;
                    }
                    if (!Write(element, array[i], wc))
                    {
                        return;
                    }
                }
                return;
            }
            if (prop.isFixedBuffer)
            {
                var array = value as JArray;
                if (array == null || array.Count != prop.fixedBufferSize)
                {
                    wc.Fail(prop, "expects an array of exactly " + prop.fixedBufferSize + " values");
                    return;
                }
                for (int i = 0; i < array.Count; i++)
                {
                    if (!Write(prop.GetFixedBufferElementAtIndex(i), array[i], wc))
                    {
                        return;
                    }
                }
                return;
            }
            var obj = value as JObject;
            if (obj == null)
            {
                wc.Fail(prop, "expects an object of fields (" + string.Join(", ", ChildNames(prop).Take(12)) + "), got " + Show(value));
                return;
            }
            WriteChildren(prop, obj, wc);
        }

        static void WriteChildren(SerializedProperty prop, JObject obj, WriteContext wc)
        {
            foreach (var kv in obj)
            {
                if (kv.Key.StartsWith("$", StringComparison.Ordinal))
                {
                    continue;
                }
                SerializedProperty child = prop.FindPropertyRelative(kv.Key);
                if (child == null)
                {
                    wc.Fail(prop, "has no field '" + kv.Key + "'. Fields: " + string.Join(", ", ChildNames(prop).Take(20)));
                    return;
                }
                if (!Write(child, kv.Value, wc))
                {
                    return;
                }
            }
        }

        static void WriteManaged(SerializedProperty prop, JToken value, WriteContext wc)
        {
            if (value.Type == JTokenType.Null)
            {
                prop.managedReferenceValue = null;
                return;
            }
            var obj = value as JObject;
            if (obj == null)
            {
                wc.Fail(prop, "a [SerializeReference] field takes null or {\"$type\": ..., fields}, got " + Show(value));
                return;
            }
            string typeName = Json.Str(obj, "$type");
            string current = prop.managedReferenceFullTypename;
            if (typeName != null && !SameManagedType(typeName, current))
            {
                string error;
                Type t = TypeResolver.Find(typeName, null, out error);
                if (t == null)
                {
                    wc.Fail(prop, error);
                    return;
                }
                if (typeof(Object).IsAssignableFrom(t) || t.IsAbstract || t.IsValueType || t.IsGenericTypeDefinition)
                {
                    wc.Fail(prop, t.FullName + " cannot be stored in a [SerializeReference] field");
                    return;
                }
                object instance = t.GetConstructor(Type.EmptyTypes) != null
                    ? Activator.CreateInstance(t)
                    : System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
                prop.managedReferenceValue = instance;
                // The new instance's fields only show up as child properties after a round trip through the object.
                // This applies the instance early. Callers hold the target in a session, a snapshot or an undo group,
                // so a later failure still leaves nothing changed.
                string path = prop.propertyPath;
                SerializedObject so = prop.serializedObject;
                PropertyIO.Commit(so, wc.UseUndo);
                so.Update();
                prop = so.FindProperty(path);
                if (prop == null)
                {
                    wc.Fail(null, path + ": lost after assigning the new instance");
                    return;
                }
            }
            else if (typeName == null && string.IsNullOrEmpty(current))
            {
                wc.Fail(prop, "the field is null, give \"$type\" to create an instance");
                return;
            }
            WriteChildren(prop, obj, wc);
        }

        static bool SameManagedType(string given, string current)
        {
            if (string.IsNullOrEmpty(current))
            {
                return false;
            }
            if (given == current)
            {
                return true;
            }
            // "Assembly-CSharp MyGame.Weapon" against "MyGame.Weapon" or "Weapon"
            string currentType = current.IndexOf(' ') >= 0 ? current.Substring(current.IndexOf(' ') + 1) : current;
            string givenType = given.IndexOf(' ') >= 0 ? given.Substring(given.IndexOf(' ') + 1) : given;
            return currentType == givenType || currentType.EndsWith("." + givenType, StringComparison.Ordinal);
        }

        public static IEnumerable<string> ChildNames(SerializedProperty prop)
        {
            if (!prop.hasChildren)
            {
                yield break;
            }
            SerializedProperty child = prop.Copy();
            SerializedProperty end = prop.GetEndProperty();
            if (!child.Next(true))
            {
                yield break;
            }
            while (!SerializedProperty.EqualContents(child, end) && child.depth == prop.depth + 1)
            {
                yield return child.name;
                if (!child.Next(false))
                {
                    yield break;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        static bool Is64(SerializedPropertyNumericType type)
        {
            return type == SerializedPropertyNumericType.Int64 || type == SerializedPropertyNumericType.UInt64
                || type == SerializedPropertyNumericType.Unknown;
        }

        // JSON integers can exceed what a double holds exactly, so 64-bit fields compare the digits themselves.
        static string OutOfRangeExact(SerializedPropertyNumericType type, JToken value)
        {
            if (!Is64(type))
            {
                return null;
            }
            string digits = value.ToString(Newtonsoft.Json.Formatting.None);
            if (type == SerializedPropertyNumericType.UInt64)
            {
                ulong u;
                return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out u) ? null : digits + " is outside the UInt64 range";
            }
            long l;
            return long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l) ? null : digits + " is outside the Int64 range";
        }

        // An int field must not silently wrap: 3000000000 into an int, or -1 into a uint, is refused.
        static string OutOfRange(SerializedPropertyNumericType type, JToken value, double d)
        {
            double min, max;
            switch (type)
            {
                case SerializedPropertyNumericType.Int8:
                    min = sbyte.MinValue;
                    max = sbyte.MaxValue;
                    break;
                case SerializedPropertyNumericType.UInt8:
                    min = byte.MinValue;
                    max = byte.MaxValue;
                    break;
                case SerializedPropertyNumericType.Int16:
                    min = short.MinValue;
                    max = short.MaxValue;
                    break;
                case SerializedPropertyNumericType.UInt16:
                    min = ushort.MinValue;
                    max = ushort.MaxValue;
                    break;
                case SerializedPropertyNumericType.Int32:
                    min = int.MinValue;
                    max = int.MaxValue;
                    break;
                case SerializedPropertyNumericType.UInt32:
                    min = uint.MinValue;
                    max = uint.MaxValue;
                    break;
                case SerializedPropertyNumericType.UInt64:
                    // 2^64 itself does not fit, and is the nearest double to ulong.MaxValue.
                    if (d < 0 || d >= 18446744073709551616.0)
                    {
                        return Show(value) + " is outside the UInt64 range";
                    }
                    return null;
                default:
                    // Int64, and integer fields Unity reports no size for.
                    if (d < -9223372036854775808.0 || d >= 9223372036854775808.0)
                    {
                        return Show(value) + " is outside the Int64 range";
                    }
                    return null;
            }
            if (d < min || d > max)
            {
                return Show(value) + " is outside the " + type + " range " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }

        public static string Show(JToken value)
        {
            if (value == null)
            {
                return "nothing";
            }
            string s = value.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
        }

        public static bool Floats(JToken value, int count, out float[] result)
        {
            result = null;
            var array = value as JArray;
            if (array == null || array.Count != count)
            {
                return false;
            }
            result = new float[count];
            for (int i = 0; i < count; i++)
            {
                double d;
                if (!Json.TryDouble(array[i], out d))
                {
                    return false;
                }
                result[i] = (float)d;
            }
            return true;
        }

        static bool Ints(JToken value, int count, out int[] result)
        {
            result = null;
            var array = value as JArray;
            if (array == null || array.Count != count)
            {
                return false;
            }
            result = new int[count];
            for (int i = 0; i < count; i++)
            {
                double d;
                if (!Json.TryDouble(array[i], out d) || Math.Floor(d) != d)
                {
                    return false;
                }
                result[i] = (int)d;
            }
            return true;
        }

        public static bool TryColor(JToken value, out Color color)
        {
            color = Color.white;
            if (value == null)
            {
                return false;
            }
            if (value.Type == JTokenType.String)
            {
                string s = (string)value;
                return ColorUtility.TryParseHtmlString(s.StartsWith("#", StringComparison.Ordinal) ? s : "#" + s, out color);
            }
            var array = value as JArray;
            if (array == null || (array.Count != 3 && array.Count != 4))
            {
                return false;
            }
            float[] f;
            if (!Floats(value, array.Count, out f))
            {
                return false;
            }
            color = new Color(f[0], f[1], f[2], f.Length == 4 ? f[3] : 1f);
            return true;
        }

        public static bool TryCurve(JToken value, out AnimationCurve curve, out string error)
        {
            curve = null;
            error = null;
            var keys = Json.Get(value, "Keys") as JArray;
            if (keys == null)
            {
                error = "expects {\"Keys\": [{\"Time\", \"Value\", \"In\", \"Out\"}]}, got " + Show(value);
                return false;
            }
            var frames = new List<Keyframe>();
            foreach (JToken k in keys)
            {
                double t, v;
                if (!Json.TryDouble(Json.Get(k, "Time"), out t) || !Json.TryDouble(Json.Get(k, "Value"), out v))
                {
                    error = "every curve key needs Time and Value, got " + Show(k);
                    return false;
                }
                double inT, outT;
                Json.TryDouble(Json.Get(k, "In"), out inT);
                Json.TryDouble(Json.Get(k, "Out"), out outT);
                var frame = new Keyframe((float)t, (float)v, (float)inT, (float)outT);
                string weighted = Json.Str(k, "WeightedMode");
                if (weighted != null)
                {
                    WeightedMode mode;
                    if (!Enum.TryParse(weighted, true, out mode))
                    {
                        error = "WeightedMode is None, In, Out or Both, got " + weighted;
                        return false;
                    }
                    frame.weightedMode = mode;
                    double iw, ow;
                    if (Json.TryDouble(Json.Get(k, "InWeight"), out iw))
                    {
                        frame.inWeight = (float)iw;
                    }
                    if (Json.TryDouble(Json.Get(k, "OutWeight"), out ow))
                    {
                        frame.outWeight = (float)ow;
                    }
                }
                frames.Add(frame);
            }
            curve = new AnimationCurve(frames.ToArray());
            WrapMode wrap;
            string pre = Json.Str(value, "PreWrap");
            string post = Json.Str(value, "PostWrap");
            if (pre != null && Enum.TryParse(pre, true, out wrap))
            {
                curve.preWrapMode = wrap;
            }
            if (post != null && Enum.TryParse(post, true, out wrap))
            {
                curve.postWrapMode = wrap;
            }
            return true;
        }

        static bool TryGradient(JToken value, out Gradient gradient, out string error)
        {
            gradient = null;
            error = null;
            var colors = Json.Get(value, "Colors") as JArray;
            var alphas = Json.Get(value, "Alphas") as JArray;
            if (colors == null || alphas == null)
            {
                error = "expects {\"Colors\": [[t, r, g, b]], \"Alphas\": [[t, a]]}, got " + Show(value);
                return false;
            }
            var ck = new List<GradientColorKey>();
            foreach (JToken c in colors)
            {
                float[] f;
                if (!Floats(c, 4, out f))
                {
                    error = "a gradient color key is [t, r, g, b], got " + Show(c);
                    return false;
                }
                ck.Add(new GradientColorKey(new Color(f[1], f[2], f[3]), f[0]));
            }
            var ak = new List<GradientAlphaKey>();
            foreach (JToken a in alphas)
            {
                float[] f;
                if (!Floats(a, 2, out f))
                {
                    error = "a gradient alpha key is [t, a], got " + Show(a);
                    return false;
                }
                ak.Add(new GradientAlphaKey(f[1], f[0]));
            }
            gradient = new Gradient();
            gradient.SetKeys(ck.ToArray(), ak.ToArray());
            string modeText = Json.Str(value, "Mode");
            GradientMode mode;
            if (modeText != null && Enum.TryParse(modeText, true, out mode))
            {
                gradient.mode = mode;
            }
            return true;
        }
    }
}
