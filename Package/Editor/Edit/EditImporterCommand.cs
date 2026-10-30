using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Changes import settings and reimports. "Texture" takes the keys ImporterExport's Summary prints, "Properties"
    // takes raw importer property paths for anything else (models, audio, custom importers).
    // A target names one asset in "AssetPath", or many in "AssetPaths" where folders expand to the assets beneath.
    // Usage: dotasset.py EditImporter --args '-spec="C:/temp/textures.json" -apply'
    // Docs/Edit.md
    public sealed class EditImporterCommand : DotAssetCommand
    {
        static readonly string[] TextureKeys =
        {
            "SourceSize", "TextureType", "TextureShape", "sRGB", "AlphaSource", "AlphaIsTransparency", "ReadWrite", "Mipmaps",
            "MaxSize", "Compression", "CrunchedCompression", "CompressionQuality", "NPOTScale", "FilterMode", "WrapMode",
            "AnisoLevel", "SpriteMode", "PixelsPerUnit", "Platforms",
        };

        public override string Summary
        {
            get { return "Import settings of textures (typed) and any importer (raw properties), then reimport"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JArray targets = Spec.LoadTargets(ctx);
            if (targets == null)
            {
                return ExitCode.Refused;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                if (!Spec.CheckKeys(ctx, targets[i], "Targets[" + i + "]", "AssetPath", "AssetPaths", "Texture", "Properties"))
                {
                    return ExitCode.Refused;
                }
                JToken tex = Json.Get(targets[i], "Texture");
                if (tex != null && !Spec.CheckKeys(ctx, tex, "Targets[" + i + "].Texture", TextureKeys))
                {
                    return ExitCode.Refused;
                }
                string platformError = CheckPlatforms(Json.Get(tex, "Platforms"));
                if (platformError != null)
                {
                    ctx.Log.Error("Targets[" + i + "].Texture.Platforms: " + platformError);
                    return ExitCode.Refused;
                }
                var many = Json.Get(targets[i], "AssetPaths");
                if (many != null && (!(many is JArray) || ((JArray)many).Any(t => t.Type != JTokenType.String)))
                {
                    ctx.Log.Error("Targets[" + i + "].AssetPaths is an array of path strings");
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                JToken target = targets[i];
                if (!Json.Has(target, "Texture") && !Json.Has(target, "Properties"))
                {
                    ctx.FailTarget("Targets[" + i + "]", "needs \"Texture\" or \"Properties\", there is nothing to change");
                    continue;
                }
                List<string> paths = Paths(ctx, target);
                if (paths.Count == 0)
                {
                    ctx.FailTarget("Targets[" + i + "]", Json.Has(target, "AssetPath") || Json.Has(target, "AssetPaths")
                        ? "its paths hold no asset this block applies to" : "needs \"AssetPath\" or \"AssetPaths\"");
                    continue;
                }
                foreach (string path in paths)
                {
                    ctx.Progress(written, targets.Count, path);
                    if (WriteOne(ctx, path, target))
                    {
                        written++;
                    }
                }
            }
            ctx.Log.Info((ctx.DryRun ? "checked " : "reimported ") + written + " asset(s)");
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static List<string> Paths(RunContext ctx, JToken target)
        {
            var list = new List<string>();
            string one = Json.Str(target, "AssetPath");
            if (one != null)
            {
                list.Add(one);
            }
            var many = Json.Get(target, "AssetPaths") as JArray;
            if (many != null)
            {
                list.AddRange(many.Select(t => (string)t));
            }
            bool textureOnly = Json.Has(target, "Texture");
            return AssetScan.Expand(list, p => textureOnly ? AssetImporter.GetAtPath(p) is TextureImporter : AssetScan.HasSourceImporter(p), ctx.Log);
        }

        static bool WriteOne(RunContext ctx, string path, JToken target)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                ctx.FailTarget(path, "has no importer");
                return false;
            }
            string refused = ReadOnly.Reason(path) ?? OpenWork.UnsavedIn(path);
            if (refused != null)
            {
                ctx.FailTarget(path, refused);
                return false;
            }
            ctx.BeginTarget();
            var errors = new List<string>();
            List<JObject> changes = null;
            using (var snapshot = new AssetSnapshot(importer))
            {
                JToken tex = Json.Get(target, "Texture");
                if (tex != null)
                {
                    var ti = importer as TextureImporter;
                    if (ti == null)
                    {
                        ctx.FailTarget(path, "\"Texture\" needs a texture, this importer is a " + importer.GetType().Name);
                        return false;
                    }
                    ApplyTexture(ti, tex, errors);
                }
                var props = Json.Get(target, "Properties") as JObject;
                if (errors.Count == 0 && props != null)
                {
                    var wc = new WriteContext();
                    if (ctx.DryRun)
                    {
                        wc.Changes = new List<JObject>();
                    }
                    if (!PropertyIO.Write(importer, props, null, wc))
                    {
                        errors.AddRange(wc.Errors);
                    }
                    changes = wc.Changes;
                }
                if (errors.Count > 0 || ctx.TargetFailed())
                {
                    ctx.FailTarget(path, errors.Count > 0 ? errors[0] : ctx.TargetError);
                    return false;
                }
                if (ctx.DryRun)
                {
                    ctx.Log.Info("would reimport " + path);
                    ctx.PlanChanges(path, changes);
                    ctx.PlanFile("reimport", path, tex != null ? "Texture settings" : null);
                    return true;
                }
                snapshot.Commit();
                EditorUtility.SetDirty(importer);
                RunJournal.Keep(importer.assetPath);
                importer.SaveAndReimport();
                ctx.WarnIfSavedWithErrors(path);
                ctx.Log.Info("reimported " + path);
                return true;
            }
        }

        static void ApplyTexture(TextureImporter t, JToken tex, List<string> errors)
        {
            foreach (JProperty p in ((JObject)tex).Properties())
            {
                string error = null;
                JToken v = p.Value;
                try
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "sourcesize":
                            // Read-only, there so an exported summary feeds back unchanged.
                            break;
                        case "texturetype":
                            error = SetEnum<TextureImporterType>(v, x => t.textureType = x);
                            break;
                        case "textureshape":
                            error = SetEnum<TextureImporterShape>(v, x => t.textureShape = x);
                            break;
                        case "srgb":
                            t.sRGBTexture = (bool)v;
                            break;
                        case "alphasource":
                            error = SetEnum<TextureImporterAlphaSource>(v, x => t.alphaSource = x);
                            break;
                        case "alphaistransparency":
                            t.alphaIsTransparency = (bool)v;
                            break;
                        case "readwrite":
                            t.isReadable = (bool)v;
                            break;
                        case "mipmaps":
                            t.mipmapEnabled = (bool)v;
                            break;
                        case "maxsize":
                            error = CheckSize((int)v);
                            if (error == null)
                            {
                                t.maxTextureSize = (int)v;
                            }
                            break;
                        case "compression":
                            error = SetEnum<TextureImporterCompression>(v, x => t.textureCompression = x);
                            break;
                        case "crunchedcompression":
                            t.crunchedCompression = (bool)v;
                            break;
                        case "compressionquality":
                            t.compressionQuality = (int)v;
                            break;
                        case "npotscale":
                            error = SetEnum<TextureImporterNPOTScale>(v, x => t.npotScale = x);
                            break;
                        case "filtermode":
                            error = SetEnum<FilterMode>(v, x => t.filterMode = x);
                            break;
                        case "wrapmode":
                            error = SetEnum<TextureWrapMode>(v, x => t.wrapMode = x);
                            break;
                        case "anisolevel":
                            t.anisoLevel = (int)v;
                            break;
                        case "spritemode":
                            error = SetEnum<SpriteImportMode>(v, x => t.spriteImportMode = x);
                            break;
                        case "pixelsperunit":
                            t.spritePixelsPerUnit = (float)(double)v;
                            break;
                        case "platforms":
                            error = ApplyPlatforms(t, v as JObject);
                            break;
                    }
                }
                catch (Exception)
                {
                    error = "bad value " + ValueCodec.Show(v);
                }
                if (error != null)
                {
                    errors.Add("Texture." + p.Name + ": " + error);
                    return;
                }
            }
        }

        // Platform names and block keys, checked for every target before the first one is reimported.
        static string CheckPlatforms(JToken platforms)
        {
            if (platforms == null)
            {
                return null;
            }
            var obj = platforms as JObject;
            if (obj == null)
            {
                return "expects {\"<platform>\": {\"Override\", \"MaxSize\", \"Format\", ...}}";
            }
            foreach (var kv in obj)
            {
                if (Array.IndexOf(PlatformNames, kv.Key) < 0)
                {
                    return "unknown platform '" + kv.Key + "'. Platforms: " + string.Join(", ", PlatformNames);
                }
                if (!(kv.Value is JObject))
                {
                    return kv.Key + " is an object of settings";
                }
                string strays = Json.UnknownKeys(kv.Value, "Override", "MaxSize", "Format", "Compression", "CompressionQuality", "CrunchedCompression");
                if (strays != null)
                {
                    return kv.Key + ": " + strays;
                }
            }
            return null;
        }

        static string ApplyPlatforms(TextureImporter t, JObject platforms)
        {
            if (platforms == null)
            {
                return "expects {\"<platform>\": {\"Override\", \"MaxSize\", \"Format\", ...}}";
            }
            foreach (var kv in platforms)
            {
                // Unity hands back fresh settings for any name, a typo would write an override nobody reads.
                if (Array.IndexOf(PlatformNames, kv.Key) < 0)
                {
                    return "unknown platform '" + kv.Key + "'. Platforms: " + string.Join(", ", PlatformNames);
                }
                TextureImporterPlatformSettings ps = t.GetPlatformTextureSettings(kv.Key);
                JToken block = kv.Value;
                string strays = Json.UnknownKeys(block, "Override", "MaxSize", "Format", "Compression", "CompressionQuality", "CrunchedCompression");
                if (strays != null)
                {
                    return kv.Key + ": " + strays;
                }
                ps.overridden = Json.Bool(block, "Override", true);
                if (Json.Has(block, "MaxSize"))
                {
                    int size = Json.Int(block, "MaxSize", 2048);
                    string error = CheckSize(size);
                    if (error != null)
                    {
                        return kv.Key + ".MaxSize: " + error;
                    }
                    ps.maxTextureSize = size;
                }
                if (Json.Has(block, "Format"))
                {
                    TextureImporterFormat format;
                    if (!Enum.TryParse(Json.Str(block, "Format"), true, out format))
                    {
                        return kv.Key + ".Format: '" + Json.Str(block, "Format") + "' is not a TextureImporterFormat";
                    }
                    ps.format = format;
                }
                if (Json.Has(block, "Compression"))
                {
                    TextureImporterCompression c;
                    if (!Enum.TryParse(Json.Str(block, "Compression"), true, out c))
                    {
                        return kv.Key + ".Compression: '" + Json.Str(block, "Compression") + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(TextureImporterCompression)));
                    }
                    ps.textureCompression = c;
                }
                if (Json.Has(block, "CompressionQuality"))
                {
                    ps.compressionQuality = Json.Int(block, "CompressionQuality", 50);
                }
                if (Json.Has(block, "CrunchedCompression"))
                {
                    ps.crunchedCompression = Json.Bool(block, "CrunchedCompression", false);
                }
                t.SetPlatformTextureSettings(ps);
            }
            return null;
        }

        public static readonly string[] PlatformNames =
        {
            "Standalone", "Android", "iPhone", "tvOS", "WebGL", "Windows Store Apps", "PS4", "PS5", "XboxOne",
            "GameCoreXboxOne", "GameCoreXboxSeries", "Nintendo Switch", "Switch", "LinuxHeadlessSimulation", "Server", "VisionOS", "Lumin", "Stadia",
        };

        static string CheckSize(int size)
        {
            if (size < 32 || size > 16384 || (size & (size - 1)) != 0)
            {
                return size + " is not a power of two between 32 and 16384";
            }
            return null;
        }

        static string SetEnum<T>(JToken v, Action<T> set) where T : struct
        {
            T value;
            string text = v.Type == JTokenType.String ? (string)v : null;
            if (text == null || !Enum.TryParse(text, true, out value))
            {
                return "'" + v + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(T)));
            }
            set(value);
            return null;
        }
    }
}
