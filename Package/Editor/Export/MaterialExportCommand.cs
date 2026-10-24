using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DotAsset
{
    // Material as the shader sees it: every shader property with its value, keywords, render queue, variant parent,
    // plus saved values the current shader no longer declares (stale properties).
    // The Properties block reads back as EditMaterial's Properties.
    // Usage: dotasset.py MaterialExport Assets/Materials[,Assets/Art/Rock.mat] [--args "-full"]
    // Docs/Export.md
    public sealed class MaterialExportCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Material shader properties, keywords, render queue and variant overrides"; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            return ExportHelper.Run(ctx, p => AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(Material), path =>
            {
                var mat = AssetDatabase.LoadMainAssetAtPath(path) as Material;
                if (mat == null)
                {
                    ctx.Log.Error(path + " is not a material");
                    return false;
                }
                JObject json = ExportHelper.Header(ctx, path);
                Describe(mat, json, ctx.Args.Has("full"));
                ExportHelper.Save(ctx, path, json);
                return true;
            });
        }

        public static void Describe(Material mat, JObject json, bool full)
        {
            Shader shader = mat.shader;
            json["Shader"] = shader != null ? shader.name : null;
            string shaderPath = shader != null ? AssetDatabase.GetAssetPath(shader) : null;
            if (!string.IsNullOrEmpty(shaderPath))
            {
                json["ShaderAsset"] = shaderPath;
            }
            if (shader != null)
            {
                // Without a graphics device (-nographics) Unity cannot tell, leave it out rather than say false.
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                {
                    json["ShaderSupported"] = shader.isSupported;
                }
                if (ShaderUtil.ShaderHasError(shader))
                {
                    json["ShaderHasErrors"] = true;
                }
            }
            if (mat.parent != null)
            {
                json["Parent"] = AssetDatabase.GetAssetPath(mat.parent);
            }
            // -1 means "the shader's queue". Export what is stored, so writing it back does not pin the queue.
            var serialized = new SerializedObject(mat);
            SerializedProperty custom = serialized.FindProperty("m_CustomRenderQueue");
            json["RenderQueue"] = custom != null ? custom.intValue : mat.renderQueue;
            if (custom != null && custom.intValue == -1)
            {
                json["ResolvedRenderQueue"] = mat.renderQueue;
            }
            if (shader != null && (full || mat.renderQueue != shader.renderQueue))
            {
                json["ShaderRenderQueue"] = shader.renderQueue;
            }
            json["EnableInstancing"] = mat.enableInstancing;
            json["DoubleSidedGI"] = mat.doubleSidedGI;
            json["GlobalIlluminationFlags"] = mat.globalIlluminationFlags.ToString();
            json["Keywords"] = new JArray(mat.shaderKeywords.OrderBy(k => k).Select(k => (object)k).ToArray());

            var properties = new JObject();
            var inherited = new JObject();
            var declared = new HashSet<string>();
            var info = new JObject();
            if (shader != null)
            {
                // Values are read from a copy. The first property read lets Unity validate the material through its
                // shader GUI, which on Standard and URP Lit resets a custom render queue and marks the asset dirty,
                // a change Unity then saves on quit. An export must leave the asset as it found it.
                var reader = new Material(mat) { hideFlags = HideFlags.HideAndDontSave };
                int count = shader.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    string name = shader.GetPropertyName(p);
                    declared.Add(name);
                    ShaderPropertyType type = shader.GetPropertyType(p);
                    ShaderPropertyFlags flags = shader.GetPropertyFlags(p);
                    if ((flags & ShaderPropertyFlags.HideInInspector) != 0 && !full)
                    {
                        continue;
                    }
                    JToken value = ReadProperty(reader, shader, p, name, type);
                    JToken defaultValue = DefaultOf(shader, p, type);
                    bool isDefault = defaultValue != null && JToken.DeepEquals(value, defaultValue);
                    bool overridden = mat.parent != null && mat.IsPropertyOverriden(name);
                    if (!full && isDefault && !overridden)
                    {
                        continue;
                    }
                    if (mat.parent != null && !overridden)
                    {
                        // Inherited from the parent. Kept apart: writing it back through EditMaterial would turn it into an override.
                        inherited[name] = value;
                    }
                    else
                    {
                        properties[name] = value;
                    }
                    var meta = new JObject { ["Type"] = type.ToString(), ["Description"] = shader.GetPropertyDescription(p) };
                    if (type == ShaderPropertyType.Range)
                    {
                        Vector2 limits = shader.GetPropertyRangeLimits(p);
                        meta["Range"] = Json.Array(limits.x, limits.y);
                    }
                    if (overridden)
                    {
                        meta["OverridesParent"] = true;
                    }
                    if (full && defaultValue != null)
                    {
                        meta["Default"] = defaultValue;
                    }
                    info[name] = meta;
                }
                Object.DestroyImmediate(reader);
            }
            json["Properties"] = properties;
            if (mat.parent != null)
            {
                json["Inherited"] = inherited;
            }
            json["PropertyInfo"] = info;
            if (!full)
            {
                json["PropertiesOmitted"] = "properties at the shader default are left out, -full lists all";
            }

            var stale = StaleProperties(mat, declared);
            if (stale.Count > 0)
            {
                json["StaleProperties"] = new JArray(stale.Select(s => (object)s).ToArray());
            }

            if (shader != null)
            {
                var valid = new HashSet<string>(shader.keywordSpace.keywordNames);
                var invalid = mat.shaderKeywords.Where(k => !valid.Contains(k)).ToList();
                if (invalid.Count > 0)
                {
                    json["UndeclaredKeywords"] = new JArray(invalid.Select(k => (object)k).ToArray());
                }
            }
        }

        static JToken ReadProperty(Material mat, Shader shader, int index, string name, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    Color c = mat.GetColor(name);
                    return Json.Array(c.r, c.g, c.b, c.a);
                case ShaderPropertyType.Vector:
                    Vector4 v = mat.GetVector(name);
                    return Json.Array(v.x, v.y, v.z, v.w);
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return Json.F(mat.GetFloat(name));
                case ShaderPropertyType.Texture:
                {
                    Texture t = mat.GetTexture(name);
                    Vector2 scale = mat.GetTextureScale(name);
                    Vector2 offset = mat.GetTextureOffset(name);
                    JToken texture = ObjectRefs.Write(t, RefScope.None);
                    if (scale == Vector2.one && offset == Vector2.zero)
                    {
                        return texture;
                    }
                    return new JObject { ["Texture"] = texture, ["Scale"] = Json.Array(scale.x, scale.y), ["Offset"] = Json.Array(offset.x, offset.y) };
                }
                default:
                    // Int, added in 2021.1
                    return mat.GetInteger(name);
            }
        }

        static JToken DefaultOf(Shader shader, int index, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector:
                    Vector4 v = shader.GetPropertyDefaultVectorValue(index);
                    return Json.Array(v.x, v.y, v.z, v.w);
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return Json.F(shader.GetPropertyDefaultFloatValue(index));
                case ShaderPropertyType.Texture:
                    // The default is a built-in texture by name ("white", "bump"), assigned none.
                    return JValue.CreateNull();
                default:
                    return shader.GetPropertyDefaultIntValue(index);
            }
        }

        // Values saved in m_SavedProperties whose name the shader no longer declares.
        public static List<string> StaleProperties(Material mat, HashSet<string> declared)
        {
            var stale = new List<string>();
            var so = new SerializedObject(mat);
            foreach (string group in new[] { "m_SavedProperties.m_TexEnvs", "m_SavedProperties.m_Floats", "m_SavedProperties.m_Colors", "m_SavedProperties.m_Ints" })
            {
                SerializedProperty array = so.FindProperty(group);
                if (array == null || !array.isArray)
                {
                    continue;
                }
                for (int i = 0; i < array.arraySize; i++)
                {
                    SerializedProperty first = array.GetArrayElementAtIndex(i).FindPropertyRelative("first");
                    if (first != null && first.propertyType == SerializedPropertyType.String && !declared.Contains(first.stringValue))
                    {
                        stale.Add(first.stringValue);
                    }
                }
            }
            return stale.Distinct().OrderBy(s => s).ToList();
        }
    }
}
