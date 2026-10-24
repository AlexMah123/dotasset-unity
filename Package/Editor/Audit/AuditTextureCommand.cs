using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AssetOps
{
    // Checks texture import settings against what the texture is for.
    //   T1  named like a normal map (_n, _normal, _nrm, _nm) but not imported as one
    //   T2  named like a data map (_mask, _orm, _ao, _rough, _metal, _height ...) but sRGB is on
    //   T3  uncompressed while 256 px or larger
    //   T4  Read/Write enabled, the texture is kept in CPU memory as well
    //   T5  mipmaps on a sprite or GUI texture
    //   T6  mipmaps off on a 3D texture of 256 px or larger
    //   T7  not a power of two, NPOT scale off, a side not a multiple of 4: block compression cannot apply
    //   T8  imported larger than the -maxsize budget (default 2048)
    // Fix spec: EditImporter targets for T1 T2 T3 T4 T5 T6 T8. T7 needs the source resized.
    // Usage: assetops.py AuditTexture [Assets/Art] --args "-scandir=Assets -maxsize=2048 -rules=T1,T2"
    // Docs/Audit.md
    public sealed class AuditTextureCommand : AssetOpsCommand
    {
        static readonly string[] DefaultNormal = { "_n", "_normal", "_nrm", "_nm", "_norm", "_normalmap" };
        static readonly string[] DefaultData = { "_mask", "_orm", "_mra", "_arm", "_ao", "_rough", "_roughness", "_metal", "_metallic", "_height", "_disp", "_displacement", "_ms", "_mt", "_smoothness", "_spec", "_specular" };

        public override string Summary
        {
            get { return "Texture import settings against usage: normal maps, sRGB, compression, mipmaps, size budget"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "maxsize", "rules", "normalsuffix", "datasuffix" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("T1", "named like a normal map but not imported as one");
            report.Rule("T2", "named like a data map but sRGB is on");
            report.Rule("T3", "uncompressed while 256 px or larger");
            report.Rule("T4", "Read/Write enabled");
            report.Rule("T5", "mipmaps on a sprite or GUI texture");
            report.Rule("T6", "mipmaps off on a 3D texture of 256 px or larger");
            report.Rule("T7", "NPOT with a side not a multiple of 4, block compression cannot apply");
            report.Rule("T8", "imported larger than the size budget");

            int budget = ctx.Args.GetInt("maxsize", 2048);
            if (budget < 32 || budget > 16384 || (budget & (budget - 1)) != 0)
            {
                ctx.Log.Error("-maxsize must be a power of two between 32 and 16384, the fix spec writes it as a max size");
                return ExitCode.Refused;
            }
            var rules = new HashSet<string>(ctx.Args.GetList("rules"), StringComparer.OrdinalIgnoreCase);
            var knownRules = new[] { "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8" };
            var badRules = rules.Where(r => !knownRules.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (badRules.Count > 0)
            {
                ctx.Log.Error("-rules: unknown " + string.Join(", ", badRules) + ". Rules: " + string.Join(", ", knownRules));
                return ExitCode.Refused;
            }
            string[] normal = ctx.Args.Has("normalsuffix") ? ctx.Args.GetList("normalsuffix").ToArray() : DefaultNormal;
            string[] data = ctx.Args.Has("datasuffix") ? ctx.Args.GetList("datasuffix").ToArray() : DefaultData;
            Func<string, bool> on = id => rules.Count == 0 || rules.Contains(id);

            Func<string, bool> isTexture = p => AssetImporter.GetAtPath(p) is TextureImporter;
            List<string> paths = ctx.Assets.Count > 0 ? AssetScan.Expand(ctx.Assets, isTexture, ctx.Log).Where(isTexture).ToList() : AssetScan.All(AssetScan.Roots(ctx.Args), isTexture);
            report.Checked = paths.Count;
            var targets = new JArray();

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (i % 50 == 0)
                {
                    ctx.Progress(i, paths.Count, path);
                }
                var t = (TextureImporter)AssetImporter.GetAtPath(path);
                int w, h;
                t.GetSourceTextureWidthAndHeight(out w, out h);
                int longest = Math.Max(w, h);
                string stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                bool isSpriteOrGui = t.textureType == TextureImporterType.Sprite || t.textureType == TextureImporterType.GUI;
                bool is3D = t.textureType == TextureImporterType.Default || t.textureType == TextureImporterType.NormalMap;
                var fix = new JObject();

                if (on("T1") && t.textureType != TextureImporterType.NormalMap && normal.Any(s => stem.EndsWith(s, StringComparison.Ordinal)))
                {
                    report.Add("T1", "Warning", path, "named like a normal map, imported as " + t.textureType);
                    fix["TextureType"] = "NormalMap";
                }
                if (on("T2") && t.sRGBTexture && t.textureType == TextureImporterType.Default && data.Any(s => stem.EndsWith(s, StringComparison.Ordinal)))
                {
                    report.Add("T2", "Warning", path, "named like a data map, sRGB is on, values will be gamma-converted");
                    fix["sRGB"] = false;
                }
                if (on("T3") && t.textureCompression == TextureImporterCompression.Uncompressed && longest >= 256)
                {
                    report.Add("T3", "Warning", path, "uncompressed at " + w + "x" + h);
                    fix["Compression"] = "Compressed";
                }
                if (on("T4") && t.isReadable)
                {
                    report.Add("T4", "Info", path, "Read/Write enabled, keep it only if scripts read the pixels");
                    fix["ReadWrite"] = false;
                }
                if (on("T5") && isSpriteOrGui && t.mipmapEnabled)
                {
                    report.Add("T5", "Warning", path, "mipmaps on a " + t.textureType + " texture");
                    fix["Mipmaps"] = false;
                }
                if (on("T6") && is3D && !t.mipmapEnabled && longest >= 256)
                {
                    report.Add("T6", "Warning", path, "mipmaps off on a " + w + "x" + h + " " + t.textureType + " texture");
                    fix["Mipmaps"] = true;
                }
                bool pot = IsPow2(w) && IsPow2(h);
                if (on("T7") && !pot && t.npotScale == TextureImporterNPOTScale.None && (w % 4 != 0 || h % 4 != 0) && t.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    report.Add("T7", "Warning", path, w + "x" + h + " is not a multiple of 4, block compression falls back to uncompressed on many platforms");
                }
                int imported = Math.Min(t.maxTextureSize, longest);
                if (on("T8") && imported > budget)
                {
                    report.Add("T8", "Warning", path, "imported at " + imported + " px, budget " + budget);
                    fix["MaxSize"] = budget;
                }

                if (fix.Count > 0)
                {
                    targets.Add(new JObject { ["AssetPath"] = path, ["Texture"] = fix });
                }
            }
            report.Extra["Budget"] = budget;
            report.FixSpec = new JObject { ["Targets"] = targets };
            report.FixRun = "EditImporter";
            return report.Finish();
        }

        static bool IsPow2(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }
    }
}
