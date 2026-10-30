using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Renames a saved shader property in .mat files after the shader renamed it, so the value follows instead of
    // falling back to the shader default. The entry in m_SavedProperties keeps its value and type, only the name changes.
    // Scripts and animation clips that name the old property are listed, not rewritten.
    // Usage: dotasset.py RenameShaderProperty --args "-from=_Color -to=_BaseColor -shader=Custom/Toon -apply"
    // Docs/Migrate.md
    public sealed class RenameShaderPropertyCommand : DotAssetCommand
    {
        static readonly string[] Groups = { "m_TexEnvs", "m_Floats", "m_Ints", "m_Colors" };

        public override string Summary
        {
            get { return "Renames a shader property in every material that saved it, values kept"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "shader", "overwrite", "scandir" }; }
        }

        // The array element named name in one of the saved property groups, or null.
        static SerializedProperty Find(SerializedObject so, string name, out string group)
        {
            foreach (string g in Groups)
            {
                SerializedProperty array = so.FindProperty("m_SavedProperties." + g);
                if (array == null || !array.isArray)
                {
                    continue;
                }
                for (int i = 0; i < array.arraySize; i++)
                {
                    SerializedProperty element = array.GetArrayElementAtIndex(i);
                    SerializedProperty first = element.FindPropertyRelative("first");
                    if (first != null && first.stringValue == name)
                    {
                        group = g;
                        return element;
                    }
                }
            }
            group = null;
            return null;
        }

        public override ExitCode Execute(RunContext ctx)
        {
            string from = ctx.Args.Get("from");
            string to = ctx.Args.Get("to");
            string shaderName = ctx.Args.Get("shader");
            bool overwrite = ctx.Args.Has("overwrite");
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                ctx.Log.Error("RenameShaderProperty needs -from=<old property> and -to=<new property>, e.g. -from=_Color -to=_BaseColor");
                return ExitCode.Refused;
            }
            if (from == to)
            {
                ctx.Log.Error("-from and -to are the same property");
                return ExitCode.Refused;
            }

            Func<string, bool> isMaterial = p => AssetScan.HasExtension(p, ".mat");
            List<string> materials = ctx.Assets.Count > 0
                ? AssetScan.Expand(ctx.Assets, isMaterial, ctx.Log)
                : AssetScan.All(AssetScan.Roots(ctx.Args), isMaterial);
            int changed = 0;
            for (int i = 0; i < materials.Count; i++)
            {
                string path = materials[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, materials.Count, path);
                }
                if (!isMaterial(path))
                {
                    ctx.Log.Warn("skipped " + path + ", only .mat files are writable materials");
                    continue;
                }
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    ctx.FailTarget(path, "not a Material");
                    continue;
                }
                if (!string.IsNullOrEmpty(shaderName) && (mat.shader == null || mat.shader.name != shaderName))
                {
                    continue;
                }
                var so = new SerializedObject(mat);
                string fromGroup;
                string toGroup;
                SerializedProperty old = Find(so, from, out fromGroup);
                if (old == null)
                {
                    continue;
                }
                SerializedProperty existing = Find(so, to, out toGroup);
                if (existing != null && toGroup != fromGroup)
                {
                    ctx.FailTarget(path, "the material saves " + from + " under " + fromGroup + " but " + to + " under " + toGroup + ", the types differ");
                    continue;
                }
                if (existing != null && !overwrite)
                {
                    ctx.FailTarget(path, "the material already saves " + to + ", pass -overwrite to replace it with the value of " + from);
                    continue;
                }
                if (mat.shader != null && mat.shader.FindPropertyIndex(to) < 0)
                {
                    ctx.Log.Warn(path + ": shader " + mat.shader.name + " does not declare " + to);
                }

                ctx.Log.Info((ctx.DryRun ? "would rename " : "renamed ") + from + " to " + to + " in " + path);
                if (ctx.DryRun)
                {
                    ctx.PlanChanges(path, new List<JObject>
                    {
                        new JObject
                        {
                            ["Property"] = "m_SavedProperties." + fromGroup,
                            ["Before"] = from,
                            ["After"] = to,
                            ["Overwrites"] = existing != null,
                        },
                    });
                    changed++;
                    continue;
                }

                string unsaved = OpenWork.UnsavedIn(path);
                if (unsaved != null)
                {
                    ctx.FailTarget(path, unsaved);
                    continue;
                }
                ctx.BeginTarget();
                using (var snapshot = new AssetSnapshot(mat))
                {
                    if (existing != null)
                    {
                        SerializedProperty array = so.FindProperty("m_SavedProperties." + toGroup);
                        for (int e = 0; e < array.arraySize; e++)
                        {
                            if (array.GetArrayElementAtIndex(e).FindPropertyRelative("first").stringValue == to)
                            {
                                array.DeleteArrayElementAtIndex(e);
                                break;
                            }
                        }
                        // Indices shifted, look the old entry up again.
                        old = Find(so, from, out fromGroup);
                    }
                    old.FindPropertyRelative("first").stringValue = to;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    string error;
                    if (!ctx.ReadyToSave(out error))
                    {
                        ctx.FailTarget(path, error);
                        continue;
                    }
                    EditorUtility.SetDirty(mat);
                    AssetDatabase.SaveAssetIfDirty(mat);
                    snapshot.Commit();
                }
                ctx.WarnIfSavedWithErrors(path);
                changed++;
            }
            ctx.Log.Info((ctx.DryRun ? "would rename " : "renamed ") + from + " to " + to + " in " + changed + " material(s)");
            if (changed == 0 && ctx.FailedTargets.Count == 0)
            {
                ctx.Log.Warn("no material under the scanned folders saves " + from);
            }
            WarnMentions(ctx, from, to);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // Material.SetColor("_Color") in code and material._Color curves in clips keep the old name.
        static void WarnMentions(RunContext ctx, string from, string to)
        {
            string quoted = "\"" + from + "\"";
            string binding = "material." + from;
            foreach (string file in AssetScan.All(new[] { "Assets" }, p => AssetScan.HasExtension(p, ".cs") || AssetScan.HasExtension(p, ".anim")))
            {
                string text;
                try
                {
                    text = File.ReadAllText(DotAssetPaths.Absolute(file));
                }
                catch (Exception)
                {
                    continue;
                }
                bool script = AssetScan.HasExtension(file, ".cs");
                int at = text.IndexOf(script ? quoted : binding, StringComparison.Ordinal);
                if (script && at >= 0)
                {
                    ctx.Log.Warn(file + " mentions " + quoted + " as a string, change it to \"" + to + "\" by hand");
                    continue;
                }
                // material._Color, material._Color.r, but not material._ColorTint.
                while (!script && at >= 0)
                {
                    int end = at + binding.Length;
                    if (end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                    {
                        ctx.Log.Warn(file + " animates " + binding + ", rebind it to material." + to + " in the Animation window");
                        break;
                    }
                    at = text.IndexOf(binding, end, StringComparison.Ordinal);
                }
            }
        }
    }
}
