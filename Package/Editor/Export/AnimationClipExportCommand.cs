using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Animation clips: clip settings, float curves with their keys, object reference curves, events. Reads .anim files,
    // the clips inside .controller and .playable files, and a model's clips when the model is named.
    // Path, Type and Property name a curve the way RebindAnimation takes it.
    // Usage: dotasset.py AnimationClipExport Assets/Animation/Wave.anim
    // Docs/Export.md
    public sealed class AnimationClipExportCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Animation clip settings, curves with their keys, object reference curves and events"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "nokeys" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            bool keys = !ctx.Args.Has("nokeys");
            return ExportHelper.Run(ctx, HoldsClips, path =>
            {
                List<AnimationClip> clips = ClipsIn(path);
                if (clips.Count == 0)
                {
                    ctx.Log.Error(path + " holds no animation clip");
                    return false;
                }
                JObject json = ExportHelper.Header(ctx, path);
                if (AssetImporter.GetAtPath(path) is ModelImporter)
                {
                    // A model's clips are read-only, extract one to .anim to change it.
                    json["Imported"] = true;
                }
                json["Clips"] = new JArray(clips.Select(c => (JToken)Clip(c, keys)).ToArray());
                ExportHelper.Save(ctx, path, json);
                return true;
            });
        }

        // A folder expands to its .anim files and the controllers and timelines that hold clips. A model only when named.
        static bool HoldsClips(string path)
        {
            if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(AnimationClip))
            {
                return true;
            }
            return AssetScan.HasExtension(path, ".controller", ".playable") && ClipsIn(path).Count > 0;
        }

        // The clips in a file, the main asset first, then by name. A model's hidden preview clips are left out.
        public static List<AnimationClip> ClipsIn(string path)
        {
            Object main = AssetDatabase.LoadMainAssetAtPath(path);
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                .OrderBy(c => c == main ? 0 : 1)
                .ThenBy(c => c.name, StringComparer.Ordinal)
                .ToList();
        }

        // Also EditAnimationClip's before and after, so its plan names what this export prints.
        internal static JObject Clip(AnimationClip clip, bool keys)
        {
            var obj = new JObject
            {
                ["Name"] = clip.name,
                ["Length"] = Json.F(clip.length),
                ["FrameRate"] = Json.F(clip.frameRate),
                ["Legacy"] = clip.legacy,
            };
            if (clip.legacy)
            {
                obj["WrapMode"] = clip.wrapMode.ToString();
            }
            if (clip.humanMotion)
            {
                obj["HumanMotion"] = true;
            }
            obj["Settings"] = Settings(AnimationUtility.GetAnimationClipSettings(clip));

            var curves = new JArray();
            foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, b);
                Keyframe[] frames = curve != null ? curve.keys : new Keyframe[0];
                JObject c = Binding(b);
                if (keys)
                {
                    c["Keys"] = new JArray(frames.Select((k, i) => (JToken)Key(curve, k, i)).ToArray());
                }
                else
                {
                    c["KeyCount"] = frames.Length;
                }
                curves.Add(c);
            }
            obj["Curves"] = curves;

            var objectCurves = new JArray();
            foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                ObjectReferenceKeyframe[] frames = AnimationUtility.GetObjectReferenceCurve(clip, b) ?? new ObjectReferenceKeyframe[0];
                JObject c = Binding(b);
                if (keys)
                {
                    c["Keys"] = new JArray(frames.Select(k => (JToken)new JObject
                    {
                        ["Time"] = Json.F(k.time),
                        ["Value"] = ObjectRefs.Write(k.value, RefScope.None),
                    }).ToArray());
                }
                else
                {
                    c["KeyCount"] = frames.Length;
                }
                objectCurves.Add(c);
            }
            obj["ObjectCurves"] = objectCurves;

            obj["Events"] = new JArray(AnimationUtility.GetAnimationEvents(clip).Select(e => (JToken)Event(e)).ToArray());
            return obj;
        }

        static JObject Binding(EditorCurveBinding b)
        {
            return new JObject
            {
                ["Path"] = b.path,
                ["Type"] = TypeResolver.DisplayName(b.type),
                ["Property"] = b.propertyName,
            };
        }

        // Tangent modes only when a side is not Free or the tangents are broken, weights only on a weighted key.
        static JObject Key(AnimationCurve curve, Keyframe k, int index)
        {
            var obj = new JObject
            {
                ["Time"] = Json.F(k.time),
                ["Value"] = Json.F(k.value),
                ["InTangent"] = Json.F(k.inTangent),
                ["OutTangent"] = Json.F(k.outTangent),
            };
            AnimationUtility.TangentMode left = AnimationUtility.GetKeyLeftTangentMode(curve, index);
            AnimationUtility.TangentMode right = AnimationUtility.GetKeyRightTangentMode(curve, index);
            bool broken = AnimationUtility.GetKeyBroken(curve, index);
            if (left != AnimationUtility.TangentMode.Free || right != AnimationUtility.TangentMode.Free || broken)
            {
                obj["InMode"] = left.ToString();
                obj["OutMode"] = right.ToString();
                if (broken)
                {
                    obj["Broken"] = true;
                }
            }
            if (k.weightedMode != WeightedMode.None)
            {
                obj["WeightedMode"] = k.weightedMode.ToString();
                obj["InWeight"] = Json.F(k.inWeight);
                obj["OutWeight"] = Json.F(k.outWeight);
            }
            return obj;
        }

        // The field names of AnimationClipSettings. LoopBlend is the inspector's Loop Pose.
        static JObject Settings(AnimationClipSettings s)
        {
            var obj = new JObject
            {
                ["LoopTime"] = s.loopTime,
                ["LoopBlend"] = s.loopBlend,
                ["CycleOffset"] = Json.F(s.cycleOffset),
                ["StartTime"] = Json.F(s.startTime),
                ["StopTime"] = Json.F(s.stopTime),
                ["Mirror"] = s.mirror,
                ["HeightFromFeet"] = s.heightFromFeet,
                ["Level"] = Json.F(s.level),
                ["OrientationOffsetY"] = Json.F(s.orientationOffsetY),
                ["KeepOriginalOrientation"] = s.keepOriginalOrientation,
                ["KeepOriginalPositionY"] = s.keepOriginalPositionY,
                ["KeepOriginalPositionXZ"] = s.keepOriginalPositionXZ,
                ["LoopBlendOrientation"] = s.loopBlendOrientation,
                ["LoopBlendPositionY"] = s.loopBlendPositionY,
                ["LoopBlendPositionXZ"] = s.loopBlendPositionXZ,
            };
            if (s.hasAdditiveReferencePose)
            {
                obj["AdditiveReferencePoseClip"] = ObjectRefs.Write(s.additiveReferencePoseClip, RefScope.None);
                obj["AdditiveReferencePoseTime"] = Json.F(s.additiveReferencePoseTime);
            }
            return obj;
        }

        // Time and Function always, a parameter only when it is set.
        static JObject Event(AnimationEvent e)
        {
            var obj = new JObject { ["Time"] = Json.F(e.time), ["Function"] = e.functionName };
            if (e.floatParameter != 0)
            {
                obj["Float"] = Json.F(e.floatParameter);
            }
            if (e.intParameter != 0)
            {
                obj["Int"] = e.intParameter;
            }
            if (!string.IsNullOrEmpty(e.stringParameter))
            {
                obj["String"] = e.stringParameter;
            }
            if (e.objectReferenceParameter != null)
            {
                obj["Object"] = ObjectRefs.Write(e.objectReferenceParameter, RefScope.None);
            }
            return obj;
        }
    }
}
