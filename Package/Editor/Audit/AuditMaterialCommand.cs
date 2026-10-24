using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DotAsset
{
    // Checks materials for what renders pink or carries dead weight.
    //   M1  no shader, or the error shader
    //   M2  the shader has compile errors
    //   M3  the shader is not supported here, e.g. a Built-in shader under URP or HDRP
    //   M4  saved values for properties the shader no longer declares
    //   M5  enabled keywords the shader does not declare
    // Fix spec: EditMaterial targets for M4 (RemoveStaleProperties) and M5 (DisableKeywords).
    // M1 to M3 need a shader choice, give one per material with EditMaterial's "Shader".
    // Usage: dotasset.py AuditMaterial [Assets/Materials] --args "-scandir=Assets"
    // Docs/Audit.md
    public sealed class AuditMaterialCommand : DotAssetCommand
    {
        static readonly string[] BuiltinOnly = { "Standard", "Standard (Specular setup)", "Legacy Shaders/", "Mobile/", "Nature/", "Particles/Standard", "Autodesk Interactive" };

        public override string Summary
        {
            get { return "Materials with missing, broken or unsupported shaders, stale properties and stray keywords"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "rules" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("M1", "no shader, or the error shader");
            report.Rule("M2", "shader has compile errors");
            report.Rule("M3", "shader not supported by the current render pipeline or GPU");
            report.Rule("M4", "saved values for properties the shader no longer declares");
            report.Rule("M5", "enabled keywords the shader does not declare");

            var rules = new HashSet<string>(ctx.Args.GetList("rules"), StringComparer.OrdinalIgnoreCase);
            var knownRules = new[] { "M1", "M2", "M3", "M4", "M5" };
            var badRules = rules.Where(r => !knownRules.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (badRules.Count > 0)
            {
                ctx.Log.Error("-rules: unknown " + string.Join(", ", badRules) + ". Rules: " + string.Join(", ", knownRules));
                return ExitCode.Refused;
            }
            Func<string, bool> on = id => rules.Count == 0 || rules.Contains(id);
            Func<string, bool> isMaterial = p => AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(Material);
            List<string> paths = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, isMaterial, ctx.Log).Where(isMaterial).ToList() : AssetScan.All(AssetScan.Roots(ctx.Args), isMaterial);
            report.Checked = paths.Count;
            bool srp = GraphicsSettings.currentRenderPipeline != null;
            // Headless with -nographics there is no GPU to ask, every shader would read as unsupported.
            bool canAskGpu = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!canAskGpu)
            {
                ctx.Log.Info("no graphics device (-nographics), M3 checks Built-in shaders under a render pipeline only");
            }
            report.Extra["RenderPipeline"] = srp ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in";
            var targets = new JArray();

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 50 == 0)
                {
                    ctx.Progress(i, paths.Count, path);
                }
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    continue;
                }
                Shader shader = mat.shader;
                var fix = new JObject { ["AssetPath"] = path };

                if (shader == null || shader.name == "Hidden/InternalErrorShader")
                {
                    if (on("M1"))
                    {
                        report.Add("M1", "Error", path, shader == null ? "no shader" : "uses the error shader, its shader is missing");
                    }
                    continue;
                }
                if (on("M2") && ShaderUtil.ShaderHasError(shader))
                {
                    report.Add("M2", "Error", path, "shader " + shader.name + " has compile errors");
                }
                if (on("M3"))
                {
                    bool builtinUnderSrp = srp && BuiltinOnly.Any(b => b.EndsWith("/") ? shader.name.StartsWith(b, StringComparison.Ordinal) : shader.name == b);
                    if (builtinUnderSrp || (canAskGpu && !shader.isSupported))
                    {
                        JObject issue = report.Add("M3", "Error", path, "shader " + shader.name + (builtinUnderSrp ? " is a Built-in Render Pipeline shader, it renders pink under " + report.Extra["RenderPipeline"] : " is not supported here"));
                        issue["Shader"] = shader.name;
                    }
                }
                var declared = new HashSet<string>(Enumerable.Range(0, shader.GetPropertyCount()).Select(shader.GetPropertyName));
                List<string> stale = MaterialExportCommand.StaleProperties(mat, declared);
                if (on("M4") && stale.Count > 0)
                {
                    JObject issue = report.Add("M4", "Info", path, stale.Count + " stale saved propert" + (stale.Count == 1 ? "y" : "ies") + ": " + string.Join(", ", stale.Take(10)));
                    issue["Properties"] = new JArray(stale.Select(s => (object)s).ToArray());
                    fix["RemoveStaleProperties"] = true;
                }
                var valid = new HashSet<string>(shader.keywordSpace.keywordNames);
                var stray = mat.shaderKeywords.Where(k => !valid.Contains(k)).OrderBy(k => k).ToList();
                if (on("M5") && stray.Count > 0)
                {
                    JObject issue = report.Add("M5", "Warning", path, "keywords the shader does not declare: " + string.Join(", ", stray));
                    issue["Keywords"] = new JArray(stray.Select(s => (object)s).ToArray());
                    fix["DisableKeywords"] = new JArray(stray.Select(s => (object)s).ToArray());
                }
                if (fix.Count > 1)
                {
                    targets.Add(fix);
                }
            }
            report.FixSpec = new JObject { ["Targets"] = targets };
            report.FixRun = "EditMaterial";
            return report.Finish();
        }
    }
}
