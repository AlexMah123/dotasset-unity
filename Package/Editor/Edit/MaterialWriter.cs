using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AssetOps
{
    // Applies a material block, shared by EditMaterial and CreateAsset:
    //   "Shader": "Universal Render Pipeline/Lit" or a shader asset path
    //   "Parent": "Assets/Materials/Base.mat" makes it a variant, null detaches it
    //   "Properties": {"_BaseColor": [1, 0, 0, 1], "_Smoothness": 0.4, "_BaseMap": "Assets/T.png",
    //                  "_DetailMap": {"Texture": "Assets/D.png", "Scale": [4, 4], "Offset": [0, 0]}}
    //   "RevertProperties": ["_BaseColor"]       variant overrides to drop
    //   "EnableKeywords": [...], "DisableKeywords": [...]
    //   "RenderQueue": 2450 (-1 is the shader's queue), "EnableInstancing", "DoubleSidedGI", "GlobalIlluminationFlags"
    //   "RemoveStaleProperties": true            drops saved values the shader does not declare
    public static class MaterialWriter
    {
        public static readonly string[] Keys =
        {
            "Shader", "Parent", "Properties", "RevertProperties", "EnableKeywords", "DisableKeywords", "RenderQueue",
            "EnableInstancing", "DoubleSidedGI", "GlobalIlluminationFlags", "RemoveStaleProperties",
        };

        public static bool Apply(Material mat, JToken block, List<string> errors, RunLog log)
        {
            int before = errors.Count;

            if (Json.Has(block, "Shader"))
            {
                string shaderName = Json.Str(block, "Shader");
                Shader shader = shaderName.StartsWith("Assets/", StringComparison.Ordinal) || shaderName.StartsWith("Packages/", StringComparison.Ordinal)
                    ? AssetDatabase.LoadAssetAtPath<Shader>(shaderName)
                    : Shader.Find(shaderName);
                if (shader == null)
                {
                    errors.Add("Shader: no shader '" + shaderName + "'");
                    return false;
                }
                if (mat.shader != shader)
                {
                    mat.shader = shader;
                    log.Info("shader set to " + shader.name);
                }
            }

            if (Json.Get(block, "Parent") != null)
            {
                JToken parentToken = Json.Get(block, "Parent");
                if (parentToken.Type == JTokenType.Null)
                {
                    mat.parent = null;
                }
                else
                {
                    var parent = AssetDatabase.LoadAssetAtPath<Material>((string)parentToken);
                    if (parent == null)
                    {
                        errors.Add("Parent: no material at " + parentToken);
                        return false;
                    }
                    if (parent == mat)
                    {
                        errors.Add("Parent: a material cannot be its own parent");
                        return false;
                    }
                    mat.parent = parent;
                }
            }

            Shader s = mat.shader;
            var properties = Json.Get(block, "Properties") as JObject;
            if (properties != null)
            {
                if (s == null)
                {
                    errors.Add("Properties: the material has no shader");
                    return false;
                }
                foreach (var kv in properties)
                {
                    int index = s.FindPropertyIndex(kv.Key);
                    if (index < 0)
                    {
                        var names = Enumerable.Range(0, s.GetPropertyCount()).Select(s.GetPropertyName).ToList();
                        errors.Add("Properties: shader " + s.name + " has no property '" + kv.Key + "'. Properties: " + string.Join(", ", names));
                        return false;
                    }
                    string error = SetProperty(mat, kv.Key, s.GetPropertyType(index), kv.Value);
                    if (error != null)
                    {
                        errors.Add("Properties." + kv.Key + ": " + error);
                        return false;
                    }
                }
            }

            var revert = Json.Get(block, "RevertProperties") as JArray;
            if (revert != null)
            {
                if (mat.parent == null)
                {
                    errors.Add("RevertProperties: the material is not a variant");
                    return false;
                }
                foreach (JToken name in revert)
                {
                    mat.RevertPropertyOverride((string)name);
                }
            }

            foreach (string kw in Strings(block, "EnableKeywords"))
            {
                mat.EnableKeyword(kw);
            }
            var disable = Strings(block, "DisableKeywords").ToList();
            foreach (string kw in disable)
            {
                mat.DisableKeyword(kw);
            }
            if (disable.Count > 0)
            {
                // Keywords the shader does not declare live in their own list and survive DisableKeyword.
                DropKeywordStrings(mat, disable);
            }

            if (Json.Has(block, "RenderQueue"))
            {
                int queue = Json.Int(block, "RenderQueue", -1);
                if (queue < -1 || queue > 5000)
                {
                    errors.Add("RenderQueue: " + queue + " is outside -1..5000");
                    return false;
                }
                mat.renderQueue = queue;
            }
            if (Json.Has(block, "EnableInstancing"))
            {
                mat.enableInstancing = Json.Bool(block, "EnableInstancing", false);
            }
            if (Json.Has(block, "DoubleSidedGI"))
            {
                mat.doubleSidedGI = Json.Bool(block, "DoubleSidedGI", false);
            }
            if (Json.Has(block, "GlobalIlluminationFlags"))
            {
                MaterialGlobalIlluminationFlags gi;
                string text = Json.Str(block, "GlobalIlluminationFlags");
                if (!Enum.TryParse(text, true, out gi))
                {
                    errors.Add("GlobalIlluminationFlags: '" + text + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(MaterialGlobalIlluminationFlags))));
                    return false;
                }
                mat.globalIlluminationFlags = gi;
            }

            if (Json.Bool(block, "RemoveStaleProperties", false) && s != null)
            {
                int removed = RemoveStale(mat, s);
                log.Info("removed " + removed + " stale saved propert" + (removed == 1 ? "y" : "ies"));
            }
            return errors.Count == before;
        }

        static IEnumerable<string> Strings(JToken block, string key)
        {
            var array = Json.Get(block, key) as JArray;
            if (array == null)
            {
                return Enumerable.Empty<string>();
            }
            return array.Select(t => (string)t).Where(t => !string.IsNullOrEmpty(t));
        }

        static string SetProperty(Material mat, string name, ShaderPropertyType type, JToken value)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                {
                    Color c;
                    if (!ValueCodec.TryColor(value, out c))
                    {
                        return "expects [r, g, b, a] or \"#RRGGBB(AA)\", got " + ValueCodec.Show(value);
                    }
                    mat.SetColor(name, c);
                    return null;
                }
                case ShaderPropertyType.Vector:
                {
                    float[] f;
                    if (!ValueCodec.Floats(value, 4, out f))
                    {
                        return "expects [x, y, z, w], got " + ValueCodec.Show(value);
                    }
                    mat.SetVector(name, new Vector4(f[0], f[1], f[2], f[3]));
                    return null;
                }
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                {
                    double d;
                    if (!Json.TryDouble(value, out d))
                    {
                        return "expects a number, got " + ValueCodec.Show(value);
                    }
                    mat.SetFloat(name, (float)d);
                    return null;
                }
                case ShaderPropertyType.Texture:
                {
                    JToken texToken = value;
                    if (value is JObject && (Json.Has(value, "Texture") || Json.Has(value, "Scale") || Json.Has(value, "Offset")))
                    {
                        texToken = Json.Get(value, "Texture");
                        float[] f;
                        if (Json.Has(value, "Scale"))
                        {
                            if (!ValueCodec.Floats(Json.Get(value, "Scale"), 2, out f))
                            {
                                return "Scale expects [x, y]";
                            }
                            mat.SetTextureScale(name, new Vector2(f[0], f[1]));
                        }
                        if (Json.Has(value, "Offset"))
                        {
                            if (!ValueCodec.Floats(Json.Get(value, "Offset"), 2, out f))
                            {
                                return "Offset expects [x, y]";
                            }
                            mat.SetTextureOffset(name, new Vector2(f[0], f[1]));
                        }
                        if (!(value as JObject).ContainsKey("Texture"))
                        {
                            return null;
                        }
                    }
                    string error;
                    var texture = ObjectRefs.Resolve(texToken, typeof(Texture), RefScope.None, out error) as Texture;
                    if (error != null)
                    {
                        return error;
                    }
                    mat.SetTexture(name, texture);
                    return null;
                }
                default:
                {
                    double d;
                    if (!Json.TryDouble(value, out d) || Math.Floor(d) != d)
                    {
                        return "expects an integer, got " + ValueCodec.Show(value);
                    }
                    mat.SetInteger(name, (int)d);
                    return null;
                }
            }
        }

        static void DropKeywordStrings(Material mat, List<string> names)
        {
            var so = new SerializedObject(mat);
            bool changed = false;
            foreach (string field in new[] { "m_InvalidKeywords", "m_ValidKeywords" })
            {
                SerializedProperty array = so.FindProperty(field);
                if (array == null || !array.isArray)
                {
                    continue;
                }
                for (int i = array.arraySize - 1; i >= 0; i--)
                {
                    if (names.Contains(array.GetArrayElementAtIndex(i).stringValue))
                    {
                        array.DeleteArrayElementAtIndex(i);
                        changed = true;
                    }
                }
            }
            // Before 2021.2 keywords were one space-separated string.
            SerializedProperty legacy = so.FindProperty("m_ShaderKeywords");
            if (legacy != null && legacy.propertyType == SerializedPropertyType.String)
            {
                var kept = legacy.stringValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(k => !names.Contains(k)).ToArray();
                legacy.stringValue = string.Join(" ", kept);
                changed = true;
            }
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static int RemoveStale(Material mat, Shader shader)
        {
            var declared = new HashSet<string>(Enumerable.Range(0, shader.GetPropertyCount()).Select(shader.GetPropertyName));
            var so = new SerializedObject(mat);
            int removed = 0;
            foreach (string group in new[] { "m_SavedProperties.m_TexEnvs", "m_SavedProperties.m_Floats", "m_SavedProperties.m_Colors", "m_SavedProperties.m_Ints" })
            {
                SerializedProperty array = so.FindProperty(group);
                if (array == null || !array.isArray)
                {
                    continue;
                }
                for (int i = array.arraySize - 1; i >= 0; i--)
                {
                    SerializedProperty first = array.GetArrayElementAtIndex(i).FindPropertyRelative("first");
                    if (first != null && !declared.Contains(first.stringValue))
                    {
                        array.DeleteArrayElementAtIndex(i);
                        removed++;
                    }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return removed;
        }
    }
}
