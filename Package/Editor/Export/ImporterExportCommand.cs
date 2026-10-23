using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetOps
{
    // Import settings from the .meta file, read through the importer: textures, models, audio, anything with one.
    // Textures and models also get a Summary block with the settings agents ask about most, in EditImporter's terms.
    // Usage: assetops.py ImporterExport Assets/Art/Textures[,Assets/Art/Hero.fbx] [--args "-full"]
    // Docs/Export.md
    public sealed class ImporterExportCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Importer settings of textures, models, audio and other source assets"; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<string> entries;
            if (!ExportHelper.Entries(ctx, HasSourceImporter, out entries))
            {
                return ExitCode.Refused;
            }
            bool failed = false;
            for (int i = 0; i < entries.Count; i++)
            {
                string path = entries[i];
                ctx.Progress(i, entries.Count, path);
                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                {
                    ctx.Log.Error(path + " has no importer");
                    failed = true;
                    continue;
                }
                JObject json = ExportHelper.Header(ctx, path);
                json["Importer"] = TypeResolver.DisplayName(importer.GetType());
                var texture = importer as TextureImporter;
                if (texture != null)
                {
                    json["Summary"] = TextureSummary(texture);
                }
                var model = importer as ModelImporter;
                if (model != null)
                {
                    json["Summary"] = ModelSummary(model);
                }
                json["Properties"] = PropertyIO.Read(importer, ReadOptions.FromArgs(ctx.Args, RefScope.None));
                ExportHelper.Save(ctx, path, json);
            }
            return failed ? ExitCode.Failed : ExitCode.Success;
        }

        // Importers of Unity's own formats (.mat, .prefab, .asset, scripts) hold nothing worth reading, a folder skips them.
        static readonly HashSet<string> NativeImporters = new HashSet<string>
        {
            "NativeFormatImporter", "PrefabImporter", "DefaultImporter", "MonoImporter", "AssemblyDefinitionImporter",
            "AssemblyDefinitionReferenceImporter", "PackageManifestImporter", "TextScriptImporter",
        };

        static bool HasSourceImporter(string path)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            return importer != null && !NativeImporters.Contains(importer.GetType().Name);
        }

        // Keys match EditImporter's "Texture" block, so a summary edits and feeds back as is.
        public static JObject TextureSummary(TextureImporter t)
        {
            int width, height;
            t.GetSourceTextureWidthAndHeight(out width, out height);
            var summary = new JObject
            {
                ["SourceSize"] = new JArray(width, height),
                ["TextureType"] = t.textureType.ToString(),
                ["TextureShape"] = t.textureShape.ToString(),
                ["sRGB"] = t.sRGBTexture,
                ["AlphaSource"] = t.alphaSource.ToString(),
                ["AlphaIsTransparency"] = t.alphaIsTransparency,
                ["ReadWrite"] = t.isReadable,
                ["Mipmaps"] = t.mipmapEnabled,
                ["MaxSize"] = t.maxTextureSize,
                ["Compression"] = t.textureCompression.ToString(),
                ["CrunchedCompression"] = t.crunchedCompression,
                ["CompressionQuality"] = t.compressionQuality,
                ["NPOTScale"] = t.npotScale.ToString(),
                ["FilterMode"] = t.filterMode.ToString(),
                ["WrapMode"] = t.wrapMode.ToString(),
                ["AnisoLevel"] = t.anisoLevel,
            };
            if (t.textureType == TextureImporterType.Sprite)
            {
                summary["SpriteMode"] = t.spriteImportMode.ToString();
                summary["PixelsPerUnit"] = Json.F(t.spritePixelsPerUnit);
            }
            var platforms = new JObject();
            foreach (string platform in new[] { "Standalone", "Android", "iPhone", "WebGL", "PS4", "PS5", "XboxOne", "GameCoreXboxSeries", "Switch", "Server" })
            {
                TextureImporterPlatformSettings ps = t.GetPlatformTextureSettings(platform);
                if (ps != null && ps.overridden)
                {
                    platforms[platform] = new JObject
                    {
                        ["MaxSize"] = ps.maxTextureSize,
                        ["Format"] = ps.format.ToString(),
                        ["Compression"] = ps.textureCompression.ToString(),
                        ["CompressionQuality"] = ps.compressionQuality,
                        ["CrunchedCompression"] = ps.crunchedCompression,
                    };
                }
            }
            if (platforms.Count > 0)
            {
                summary["Platforms"] = platforms;
            }
            return summary;
        }

        static JObject ModelSummary(ModelImporter m)
        {
            return new JObject
            {
                ["GlobalScale"] = Json.F(m.globalScale),
                ["UseFileScale"] = m.useFileScale,
                ["ImportAnimation"] = m.importAnimation,
                ["AnimationType"] = m.animationType.ToString(),
                ["ImportBlendShapes"] = m.importBlendShapes,
                ["ImportCameras"] = m.importCameras,
                ["ImportLights"] = m.importLights,
                ["MeshCompression"] = m.meshCompression.ToString(),
                ["ReadWrite"] = m.isReadable,
                ["OptimizeMeshes"] = m.optimizeMeshPolygons && m.optimizeMeshVertices,
                ["GenerateColliders"] = m.addCollider,
                ["Normals"] = m.importNormals.ToString(),
                ["Tangents"] = m.importTangents.ToString(),
                ["MaterialImportMode"] = m.materialImportMode.ToString(),
                ["Clips"] = new JArray(m.clipAnimations.Select(c => (JToken)new JObject
                {
                    ["Name"] = c.name,
                    ["Take"] = c.takeName,
                    ["FirstFrame"] = Json.F(c.firstFrame),
                    ["LastFrame"] = Json.F(c.lastFrame),
                    ["Loop"] = c.loopTime,
                }).ToArray()),
            };
        }
    }
}
