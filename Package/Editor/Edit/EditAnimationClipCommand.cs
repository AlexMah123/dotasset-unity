using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Edits animation clips: float and object reference curves, their keys, events, clip settings and frame rate.
    // Curves are named by Path, Type and Property, keys and events by their time, as AnimationClipExport prints them.
    // Usage: dotasset.py EditAnimationClip --args '-spec="C:/temp/clip.json" -apply'
    // Docs/Edit.md
    public sealed class EditAnimationClipCommand : DotAssetCommand
    {
        static readonly string[] OpNames = { "SetCurve", "SetKeys", "RemoveKeys", "RemoveCurve", "SetObjectCurve", "SetEvent", "RemoveEvent" };

        // Keys each op reads. A misspelt key refuses the spec instead of being dropped.
        static readonly Dictionary<string, string[]> OpKeys = new Dictionary<string, string[]>
        {
            { "SetCurve", new[] { "Op", "Path", "Type", "Property", "Keys", "Check" } },
            { "SetKeys", new[] { "Op", "Path", "Type", "Property", "Keys", "Check" } },
            { "RemoveKeys", new[] { "Op", "Path", "Type", "Property", "Times" } },
            { "RemoveCurve", new[] { "Op", "Path", "Type", "Property" } },
            { "SetObjectCurve", new[] { "Op", "Path", "Type", "Property", "Keys", "Check" } },
            { "SetEvent", new[] { "Op", "Time", "Function", "Float", "Int", "String", "Object" } },
            { "RemoveEvent", new[] { "Op", "Time", "Function" } },
        };

        static readonly string[] KeyFields =
        {
            "Time", "Value", "InTangent", "OutTangent", "InMode", "OutMode", "Broken", "WeightedMode", "InWeight", "OutWeight",
        };

        static readonly string[] BoolSettings =
        {
            "LoopTime", "LoopBlend", "Mirror", "HeightFromFeet", "KeepOriginalOrientation", "KeepOriginalPositionY",
            "KeepOriginalPositionXZ", "LoopBlendOrientation", "LoopBlendPositionY", "LoopBlendPositionXZ",
        };

        static readonly string[] FloatSettings = { "CycleOffset", "StartTime", "StopTime", "Level", "OrientationOffsetY", "AdditiveReferencePoseTime" };

        static readonly string[] SettingNames = BoolSettings.Concat(FloatSettings).Concat(new[] { "AdditiveReferencePoseClip" }).ToArray();

        public override string Summary
        {
            get { return "Animation clip curves, keys, events, clip settings and frame rate"; }
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++)
            {
                string where = "Targets[" + i + "]";
                if (!Spec.CheckKeys(ctx, targets[i], where, "AssetPath", "Clip", "FrameRate", "Settings", "Ops"))
                {
                    return ExitCode.Refused;
                }
                string invalid = Validate(targets[i], where);
                if (invalid != null)
                {
                    ctx.Log.Error(invalid);
                    return ExitCode.Refused;
                }
                // One target per clip: a second target would carry or undo the first one's changes.
                string id = Json.Str(targets[i], "AssetPath") + " :: " + Json.Str(targets[i], "Clip");
                if (!seen.Add(id))
                {
                    ctx.Log.Error(Json.Str(targets[i], "AssetPath") + " appears in more than one target for the same clip, merge them");
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            foreach (JToken target in targets)
            {
                string path = Json.Str(target, "AssetPath") ?? "(no AssetPath)";
                string clipName = Json.Str(target, "Clip");
                string label = clipName != null ? path + " :: " + clipName : path;
                string error;
                AnimationClip clip = Find(path, clipName, out error);
                if (clip == null)
                {
                    ctx.FailTarget(label, error);
                    continue;
                }
                string refused = ReadOnly.Reason(path) ?? OpenWork.UnsavedIn(path);
                if (refused != null)
                {
                    ctx.FailTarget(label, refused);
                    continue;
                }
                if (AssetImporter.GetAtPath(path) is ModelImporter)
                {
                    // A model's clips come out of its importer, a write would be lost on the next import.
                    ctx.FailTarget(label, "the clips of a model are imported, extract the clip to an .anim file first");
                    continue;
                }
                if (!Json.Has(target, "Ops") && !Json.Has(target, "Settings") && !Json.Has(target, "FrameRate"))
                {
                    ctx.FailTarget(label, "needs \"Ops\", \"Settings\" or \"FrameRate\"");
                    continue;
                }

                ctx.BeginTarget();
                using (var snapshot = new AssetSnapshot(clip))
                {
                    JObject before = AnimationClipExportCommand.Clip(clip, true);
                    var editor = new ClipEditor(clip);
                    if (!editor.Apply(target) || ctx.TargetFailed())
                    {
                        ctx.FailTarget(label, editor.Errors.Count > 0 ? editor.Errors[0] : ctx.TargetError);
                        continue;
                    }
                    List<JObject> changes = Diff(clip.name, before, AnimationClipExportCommand.Clip(clip, true));
                    if (changes.Count == 0)
                    {
                        ctx.Log.Info(label + ": the values are already there" + (ctx.DryRun ? string.Empty : ", not saved"));
                        continue;
                    }
                    if (ctx.DryRun)
                    {
                        ctx.Log.Info(label + ": " + changes.Count + " change(s) would apply");
                        ctx.PlanChanges(label, changes);
                        continue;
                    }
                    EditorUtility.SetDirty(clip);
                    RunJournal.Keep(path);
                    ctx.BeforeSave(path);
                    // Saves the whole file, a clip inside a controller or timeline included.
                    AssetDatabase.SaveAssetIfDirty(clip);
                    if (ctx.SaveFailed(label))
                    {
                        continue;
                    }
                    snapshot.Commit();
                    written++;
                    ctx.Log.Info("wrote " + label + " (" + changes.Count + " change(s))");
                }
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("wrote " + written + " clip(s)");
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // Op names, op keys, key fields and setting names, before anything is changed. Also CreateAsset's AnimationClip entries.
        internal static string Validate(JToken target, string where)
        {
            JToken opsToken = Json.Get(target, "Ops");
            if (opsToken != null && opsToken.Type != JTokenType.Null && !(opsToken is JArray))
            {
                return where + " Ops: needs a list";
            }
            var ops = opsToken as JArray;
            for (int i = 0; ops != null && i < ops.Count; i++)
            {
                string name = Json.Str(ops[i], "Op");
                string[] keys;
                if (name == null || !OpKeys.TryGetValue(name, out keys))
                {
                    return where + " Ops[" + i + "]: unknown op '" + (name ?? "(none)") + "'. Ops: " + string.Join(", ", OpNames);
                }
                if (!(ops[i] is JObject))
                {
                    return where + " Ops[" + i + "]: is not an object";
                }
                string strays = Json.UnknownKeys(ops[i], keys);
                if (strays != null)
                {
                    return where + " Ops[" + i + "] " + name + ": " + strays;
                }
                var list = Json.Get(ops[i], "Keys") as JArray;
                for (int k = 0; list != null && k < list.Count; k++)
                {
                    strays = Json.UnknownKeys(list[k], name == "SetObjectCurve" ? new[] { "Time", "Value" } : KeyFields);
                    if (strays != null)
                    {
                        return where + " Ops[" + i + "] " + name + " Keys[" + k + "]: " + strays;
                    }
                }
            }
            JToken settings = Json.Get(target, "Settings");
            if (settings != null && settings.Type != JTokenType.Null)
            {
                if (!(settings is JObject))
                {
                    return where + " Settings: needs an object";
                }
                string strays = Json.UnknownKeys(settings, SettingNames);
                if (strays != null)
                {
                    return where + " Settings: " + strays;
                }
            }
            return null;
        }

        // Applies a spec's Ops, Settings and FrameRate to a clip, for CreateAsset. Null when it worked, else the first error.
        internal static string Fill(AnimationClip clip, JToken spec)
        {
            var editor = new ClipEditor(clip);
            return editor.Apply(spec) ? null : editor.Errors[0];
        }

        // The clip a target names: the only clip in the file, or the one called "Clip".
        static AnimationClip Find(string path, string name, out string error)
        {
            error = null;
            if (AssetDatabase.GetMainAssetTypeAtPath(path) == null)
            {
                error = "no asset at that path";
                return null;
            }
            List<AnimationClip> clips = AnimationClipExportCommand.ClipsIn(path);
            string names = string.Join(", ", clips.Select(c => c.name).ToArray());
            if (clips.Count == 0)
            {
                error = "no animation clip in that file";
                return null;
            }
            if (name == null)
            {
                if (clips.Count == 1)
                {
                    return clips[0];
                }
                error = "the file holds " + clips.Count + " clips (" + names + "), name one with \"Clip\"";
                return null;
            }
            List<AnimationClip> matches = clips.Where(c => c.name == name).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
            error = matches.Count == 0 ? "no clip named '" + name + "', the clips are " + names : matches.Count + " clips are named '" + name + "'";
            return null;
        }

        // What changed, in AnimationClipExport's names: FrameRate, Settings.<field>, a curve's keys, Events.
        static List<JObject> Diff(string clip, JObject before, JObject after)
        {
            var changes = new List<JObject>();
            Compare(changes, clip, "FrameRate", before["FrameRate"], after["FrameRate"]);
            var a = (JObject)before["Settings"];
            var b = (JObject)after["Settings"];
            foreach (string key in a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)))
            {
                Compare(changes, clip, "Settings." + key, a[key], b[key]);
            }
            Curves(changes, clip, "Curves", (JArray)before["Curves"], (JArray)after["Curves"]);
            Curves(changes, clip, "ObjectCurves", (JArray)before["ObjectCurves"], (JArray)after["ObjectCurves"]);
            Compare(changes, clip, "Events", before["Events"], after["Events"]);
            return changes;
        }

        static void Compare(List<JObject> changes, string clip, string property, JToken before, JToken after)
        {
            if (JToken.DeepEquals(before, after))
            {
                return;
            }
            changes.Add(new JObject
            {
                ["Object"] = clip,
                ["Property"] = property,
                ["Before"] = before ?? JValue.CreateNull(),
                ["After"] = after ?? JValue.CreateNull(),
            });
        }

        // One change per curve whose keys differ, named Curves[Path|Type|Property].
        static void Curves(List<JObject> changes, string clip, string kind, JArray before, JArray after)
        {
            var a = new Dictionary<string, JToken>();
            var b = new Dictionary<string, JToken>();
            foreach (JToken c in before)
            {
                a[CurveId(c)] = c["Keys"];
            }
            foreach (JToken c in after)
            {
                b[CurveId(c)] = c["Keys"];
            }
            foreach (string id in a.Keys.Union(b.Keys))
            {
                JToken ka;
                JToken kb;
                a.TryGetValue(id, out ka);
                b.TryGetValue(id, out kb);
                if (JToken.DeepEquals(ka, kb))
                {
                    continue;
                }
                JToken briefA = Brief(ka);
                JToken briefB = Brief(kb);
                if (JToken.DeepEquals(briefA, briefB))
                {
                    // Only tangents or weights changed, the full keys show it.
                    briefA = ka;
                    briefB = kb;
                }
                changes.Add(new JObject
                {
                    ["Object"] = clip,
                    ["Property"] = kind + "[" + id + "]",
                    ["Before"] = briefA ?? JValue.CreateNull(),
                    ["After"] = briefB ?? JValue.CreateNull(),
                });
            }
        }

        static string CurveId(JToken curve)
        {
            return (string)curve["Path"] + "|" + (string)curve["Type"] + "|" + (string)curve["Property"];
        }

        // [time, value] pairs, or a count and the time range for a long curve.
        static JToken Brief(JToken keys)
        {
            var list = keys as JArray;
            if (list == null)
            {
                return null;
            }
            if (list.Count > 8)
            {
                return list.Count + " keys, " + Text(list.First["Time"]) + " to " + Text(list.Last["Time"]) + " s";
            }
            return new JArray(list.Select(k => (JToken)new JArray(k["Time"], k["Value"])).ToArray());
        }

        static string Text(JToken token)
        {
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        static string Time(float t)
        {
            return Json.F(t).ToString("R", CultureInfo.InvariantCulture);
        }

        // A key as the spec gives it. A side's mode is the one given, Free when its tangent is given, ClampedAuto otherwise.
        sealed class KeySpec
        {
            public Keyframe Frame;
            public AnimationUtility.TangentMode In;
            public AnimationUtility.TangentMode Out;
            public bool Broken;

            public static KeySpec From(AnimationCurve curve, int index)
            {
                return new KeySpec
                {
                    Frame = curve[index],
                    In = AnimationUtility.GetKeyLeftTangentMode(curve, index),
                    Out = AnimationUtility.GetKeyRightTangentMode(curve, index),
                    Broken = AnimationUtility.GetKeyBroken(curve, index),
                };
            }
        }

        sealed class ClipEditor
        {
            readonly AnimationClip clip;
            public readonly List<string> Errors = new List<string>();
            string label;

            public ClipEditor(AnimationClip clip)
            {
                this.clip = clip;
            }

            void Fail(string message)
            {
                Errors.Add(label + ": " + message);
            }

            // Ops first, then Settings and FrameRate, so a StopTime given here is not moved by a curve change.
            public bool Apply(JToken target)
            {
                var ops = Json.Get(target, "Ops") as JArray;
                for (int i = 0; ops != null && i < ops.Count; i++)
                {
                    string name = Json.Str(ops[i], "Op");
                    label = "Ops[" + i + "] " + name;
                    try
                    {
                        One(name, ops[i]);
                    }
                    catch (Exception e)
                    {
                        Fail(e.Message);
                    }
                    if (Errors.Count > 0)
                    {
                        return false;
                    }
                }
                var settings = Json.Get(target, "Settings") as JObject;
                try
                {
                    if (settings != null && !Settings(settings))
                    {
                        return false;
                    }
                }
                catch (Exception e)
                {
                    // N7-1: a wrong-typed value in a Settings reference, e.g. "Missing": "yes", fails this clip only.
                    label = label ?? "Settings";
                    Fail(e.Message);
                    return false;
                }
                if (Json.Has(target, "FrameRate"))
                {
                    label = "FrameRate";
                    double rate;
                    JToken value = Json.Get(target, "FrameRate");
                    if (value.Type == JTokenType.Boolean || !Json.TryDouble(value, out rate) || !(rate > 0) || double.IsInfinity(rate))
                    {
                        Fail("needs a number above 0");
                        return false;
                    }
                    clip.frameRate = (float)rate;
                }
                return true;
            }

            void One(string name, JToken op)
            {
                switch (name)
                {
                    case "SetCurve":
                        SetCurve(op, false);
                        break;
                    case "SetKeys":
                        SetCurve(op, true);
                        break;
                    case "RemoveKeys":
                        RemoveKeys(op);
                        break;
                    case "RemoveCurve":
                        RemoveCurve(op);
                        break;
                    case "SetObjectCurve":
                        SetObjectCurve(op);
                        break;
                    case "SetEvent":
                        SetEvent(op);
                        break;
                    case "RemoveEvent":
                        RemoveEvent(op);
                        break;
                    default:
                        Fail("unknown op. Ops: " + string.Join(", ", OpNames));
                        break;
                }
            }

            bool Bind(JToken op, bool objectCurve, out EditorCurveBinding binding)
            {
                binding = default(EditorCurveBinding);
                string typeName = Json.Str(op, "Type");
                string property = Json.Str(op, "Property");
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(property))
                {
                    Fail("needs \"Type\" and \"Property\"");
                    return false;
                }
                string error;
                Type type = TypeResolver.Find(typeName, typeof(Object), out error);
                if (type == null)
                {
                    Fail(error);
                    return false;
                }
                if (type != typeof(GameObject) && !typeof(Component).IsAssignableFrom(type))
                {
                    Fail(TypeResolver.DisplayName(type) + " is not a component, a clip animates GameObjects and components");
                    return false;
                }
                // The root object's curves have the path "".
                string path = Json.Str(op, "Path") ?? string.Empty;
                binding = objectCurve ? EditorCurveBinding.PPtrCurve(path, type, property) : EditorCurveBinding.FloatCurve(path, type, property);
                return true;
            }

            static bool Same(EditorCurveBinding a, EditorCurveBinding b)
            {
                return a.path == b.path && a.type == b.type && a.propertyName == b.propertyName;
            }

            bool InClip(EditorCurveBinding b, bool objectCurve)
            {
                EditorCurveBinding[] bindings = objectCurve
                    ? AnimationUtility.GetObjectReferenceCurveBindings(clip)
                    : AnimationUtility.GetCurveBindings(clip);
                return bindings.Any(x => Same(x, b));
            }

            static string Describe(EditorCurveBinding b)
            {
                return (b.path.Length == 0 ? "(root)" : b.path) + " " + TypeResolver.DisplayName(b.type) + " " + b.propertyName;
            }

            // The type of value Unity binds the curve to on an object holding the component, null when it binds to nothing.
            // A curve with a Path is checked on a child, the root's own on the root: Unity binds a few properties
            // (the root's m_IsActive) only below the root. The probe stays inactive, so no script's Awake or OnEnable runs.
            static Type ValueType(EditorCurveBinding b)
            {
                if (b.type.IsAbstract)
                {
                    return null;
                }
                GameObject probe = EditorUtility.CreateGameObjectWithHideFlags("DotAsset binding check", HideFlags.HideAndDontSave);
                try
                {
                    probe.SetActive(false);
                    GameObject holder = probe;
                    EditorCurveBinding local = b;
                    if (b.path.Length > 0)
                    {
                        holder = EditorUtility.CreateGameObjectWithHideFlags("Child", HideFlags.HideAndDontSave);
                        holder.transform.SetParent(probe.transform, false);
                        local.path = holder.name;
                    }
                    if (b.type != typeof(GameObject) && holder.GetComponent(b.type) == null && holder.AddComponent(b.type) == null)
                    {
                        return null;
                    }
                    return AnimationUtility.GetEditorCurveValueType(probe, local);
                }
                finally
                {
                    Object.DestroyImmediate(probe);
                }
            }

            // A curve the clip does not have yet must bind to something, unless "Check" is false.
            bool Checked(JToken op, EditorCurveBinding b, bool objectCurve, Type valueType)
            {
                if (!Json.Bool(op, "Check", true))
                {
                    return true;
                }
                if (valueType == null)
                {
                    Fail(TypeResolver.DisplayName(b.type) + " has no animatable property '" + b.propertyName + "'"
                        + (b.type.IsAbstract ? ", name the component's own type" : string.Empty)
                        + (b.type == typeof(GameObject) && b.path.Length == 0 ? " on the root, Unity animates it only below the root" : string.Empty)
                        + ". Export a clip that animates it for the exact name, or add \"Check\": false");
                    return false;
                }
                bool isObject = typeof(Object).IsAssignableFrom(valueType);
                if (isObject != objectCurve)
                {
                    Fail("'" + b.propertyName + "' is " + (isObject ? "an object reference, use SetObjectCurve" : "a number, use SetCurve"));
                    return false;
                }
                return true;
            }

            // The other kind of curve under the same name, which this op cannot change.
            bool OtherKind(EditorCurveBinding b, bool objectCurve)
            {
                EditorCurveBinding other = objectCurve
                    ? EditorCurveBinding.FloatCurve(b.path, b.type, b.propertyName)
                    : EditorCurveBinding.PPtrCurve(b.path, b.type, b.propertyName);
                if (InClip(other, !objectCurve))
                {
                    Fail(Describe(b) + (objectCurve ? " is a float curve, use SetCurve" : " is an object reference curve, use SetObjectCurve"));
                    return true;
                }
                return false;
            }

            bool Number(JToken value, string what, bool allowInfinity, out float result)
            {
                result = 0;
                double d;
                if (value == null || value.Type == JTokenType.Boolean || !Json.TryDouble(value, out d) || double.IsNaN(d)
                    || (!allowInfinity && double.IsInfinity(d)))
                {
                    Fail(what + " needs a number");
                    return false;
                }
                result = (float)d;
                return true;
            }

            bool Mode(JToken key, string field, out AnimationUtility.TangentMode mode, out bool given)
            {
                mode = AnimationUtility.TangentMode.ClampedAuto;
                string text = Json.Str(key, field);
                given = text != null;
                if (text == null)
                {
                    return true;
                }
                foreach (string n in Enum.GetNames(typeof(AnimationUtility.TangentMode)))
                {
                    if (string.Equals(n, text, StringComparison.OrdinalIgnoreCase))
                    {
                        mode = (AnimationUtility.TangentMode)Enum.Parse(typeof(AnimationUtility.TangentMode), n);
                        return true;
                    }
                }
                Fail(field + " '" + text + "' is not a tangent mode. Modes: " + string.Join(", ", Enum.GetNames(typeof(AnimationUtility.TangentMode))));
                return false;
            }

            KeySpec ParseKey(JToken key)
            {
                if (!(key is JObject))
                {
                    Fail("a key is an object with \"Time\" and \"Value\"");
                    return null;
                }
                float time;
                float value;
                if (!Number(Json.Get(key, "Time"), "Time", false, out time))
                {
                    return null;
                }
                JToken v = Json.Get(key, "Value");
                if (v != null && v.Type == JTokenType.Boolean)
                {
                    // m_IsActive and m_Enabled curves hold 0 and 1.
                    value = (bool)v ? 1 : 0;
                }
                else if (!Number(v, "Value", true, out value))
                {
                    return null;
                }
                var spec = new KeySpec { Frame = new Keyframe(time, value) };
                AnimationUtility.TangentMode mode;
                bool given;
                if (!Mode(key, "InMode", out mode, out given))
                {
                    return null;
                }
                spec.In = given ? mode : Json.Has(key, "InTangent") ? AnimationUtility.TangentMode.Free : AnimationUtility.TangentMode.ClampedAuto;
                if (!Mode(key, "OutMode", out mode, out given))
                {
                    return null;
                }
                spec.Out = given ? mode : Json.Has(key, "OutTangent") ? AnimationUtility.TangentMode.Free : AnimationUtility.TangentMode.ClampedAuto;
                float tangent;
                if (Json.Has(key, "InTangent"))
                {
                    if (!Number(Json.Get(key, "InTangent"), "InTangent", true, out tangent))
                    {
                        return null;
                    }
                    spec.Frame.inTangent = tangent;
                }
                if (Json.Has(key, "OutTangent"))
                {
                    if (!Number(Json.Get(key, "OutTangent"), "OutTangent", true, out tangent))
                    {
                        return null;
                    }
                    spec.Frame.outTangent = tangent;
                }
                spec.Broken = Json.Bool(key, "Broken", false);
                string weighted = Json.Str(key, "WeightedMode");
                if (weighted != null)
                {
                    string match = Enum.GetNames(typeof(WeightedMode)).FirstOrDefault(n => string.Equals(n, weighted, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Fail("WeightedMode '" + weighted + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(WeightedMode))));
                        return null;
                    }
                    spec.Frame.weightedMode = (WeightedMode)Enum.Parse(typeof(WeightedMode), match);
                    float weight = 1f / 3f;
                    if (Json.Has(key, "InWeight") && !Number(Json.Get(key, "InWeight"), "InWeight", false, out weight))
                    {
                        return null;
                    }
                    spec.Frame.inWeight = weight;
                    weight = 1f / 3f;
                    if (Json.Has(key, "OutWeight") && !Number(Json.Get(key, "OutWeight"), "OutWeight", false, out weight))
                    {
                        return null;
                    }
                    spec.Frame.outWeight = weight;
                }
                if (spec.Frame.weightedMode == WeightedMode.None && (Json.Has(key, "InWeight") || Json.Has(key, "OutWeight")))
                {
                    Fail("InWeight and OutWeight need \"WeightedMode\"");
                    return null;
                }
                return spec;
            }

            // Builds the curve from keys sorted by time, then sets each key's modes, which recomputes the auto tangents.
            static AnimationCurve Build(List<KeySpec> keys, AnimationCurve old)
            {
                keys.Sort((x, y) => x.Frame.time.CompareTo(y.Frame.time));
                var curve = new AnimationCurve(keys.Select(k => k.Frame).ToArray());
                if (old != null)
                {
                    curve.preWrapMode = old.preWrapMode;
                    curve.postWrapMode = old.postWrapMode;
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    AnimationUtility.SetKeyBroken(curve, i, keys[i].Broken);
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, keys[i].In);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, keys[i].Out);
                }
                return curve;
            }

            // Reads the curve back: Unity must hold every key at its time and value.
            void Store(EditorCurveBinding b, List<KeySpec> keys, AnimationCurve old)
            {
                AnimationUtility.SetEditorCurve(clip, b, Build(keys, old));
                AnimationCurve back = AnimationUtility.GetEditorCurve(clip, b);
                if (back == null || back.length != keys.Count)
                {
                    Fail("Unity kept " + (back == null ? 0 : back.length) + " of " + keys.Count + " keys on " + Describe(b));
                    return;
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    if (back[i].time != keys[i].Frame.time || back[i].value != keys[i].Frame.value)
                    {
                        Fail("Unity changed the key at " + Time(keys[i].Frame.time) + " on " + Describe(b));
                        return;
                    }
                }
            }

            List<KeySpec> Keys(JToken op)
            {
                var list = Json.Get(op, "Keys") as JArray;
                if (list == null || list.Count == 0)
                {
                    Fail("needs \"Keys\", a list of {\"Time\", \"Value\"}");
                    return null;
                }
                var keys = new List<KeySpec>();
                foreach (JToken k in list)
                {
                    KeySpec spec = ParseKey(k);
                    if (spec == null)
                    {
                        return null;
                    }
                    if (keys.Any(x => x.Frame.time == spec.Frame.time))
                    {
                        Fail("two keys at " + Time(spec.Frame.time));
                        return null;
                    }
                    keys.Add(spec);
                }
                return keys;
            }

            // SetCurve replaces the curve's keys, SetKeys replaces the keys at the given times and keeps the rest.
            void SetCurve(JToken op, bool merge)
            {
                EditorCurveBinding b;
                if (!Bind(op, false, out b) || OtherKind(b, false))
                {
                    return;
                }
                bool inClip = InClip(b, false);
                if (!inClip && !Checked(op, b, false, Json.Bool(op, "Check", true) ? ValueType(b) : null))
                {
                    return;
                }
                List<KeySpec> keys = Keys(op);
                if (keys == null)
                {
                    return;
                }
                AnimationCurve old = inClip ? AnimationUtility.GetEditorCurve(clip, b) : null;
                if (merge && old != null)
                {
                    for (int i = 0; i < old.length; i++)
                    {
                        float t = old[i].time;
                        if (!keys.Any(k => k.Frame.time == t))
                        {
                            keys.Add(KeySpec.From(old, i));
                        }
                    }
                }
                Store(b, keys, old);
            }

            void RemoveKeys(JToken op)
            {
                EditorCurveBinding b;
                if (!Bind(op, false, out b))
                {
                    return;
                }
                if (!InClip(b, false))
                {
                    Fail("the clip has no float curve " + Describe(b));
                    return;
                }
                var times = Json.Get(op, "Times") as JArray;
                if (times == null || times.Count == 0)
                {
                    Fail("needs \"Times\", a list of key times");
                    return;
                }
                AnimationCurve old = AnimationUtility.GetEditorCurve(clip, b);
                var gone = new List<float>();
                foreach (JToken t in times)
                {
                    float time;
                    if (!Number(t, "Times", false, out time))
                    {
                        return;
                    }
                    if (!old.keys.Any(k => k.time == time))
                    {
                        Fail("no key at " + Time(time) + " on " + Describe(b) + ", the keys are at " + string.Join(", ", old.keys.Select(k => Time(k.time)).ToArray()));
                        return;
                    }
                    gone.Add(time);
                }
                var keys = new List<KeySpec>();
                for (int i = 0; i < old.length; i++)
                {
                    if (!gone.Contains(old[i].time))
                    {
                        keys.Add(KeySpec.From(old, i));
                    }
                }
                if (keys.Count == 0)
                {
                    Fail("that removes every key, use RemoveCurve");
                    return;
                }
                Store(b, keys, old);
            }

            void RemoveCurve(JToken op)
            {
                EditorCurveBinding b;
                if (!Bind(op, false, out b))
                {
                    return;
                }
                if (InClip(b, false))
                {
                    AnimationUtility.SetEditorCurve(clip, b, null);
                    return;
                }
                EditorCurveBinding pptr = EditorCurveBinding.PPtrCurve(b.path, b.type, b.propertyName);
                if (InClip(pptr, true))
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, pptr, null);
                    return;
                }
                Fail("the clip has no curve " + Describe(b));
            }

            // Replaces the object reference curve's keys. A key's Value is a reference as the export prints it, or null.
            void SetObjectCurve(JToken op)
            {
                EditorCurveBinding b;
                if (!Bind(op, true, out b) || OtherKind(b, true))
                {
                    return;
                }
                Type valueType = ValueType(b);
                if (!InClip(b, true) && !Checked(op, b, true, valueType))
                {
                    return;
                }
                Type expected = valueType != null && typeof(Object).IsAssignableFrom(valueType) ? valueType : typeof(Object);
                var list = Json.Get(op, "Keys") as JArray;
                if (list == null || list.Count == 0)
                {
                    Fail("needs \"Keys\", a list of {\"Time\", \"Value\"}");
                    return;
                }
                var keys = new List<ObjectReferenceKeyframe>();
                foreach (JToken k in list)
                {
                    float time;
                    if (!(k is JObject) || !Number(Json.Get(k, "Time"), "Time", false, out time))
                    {
                        if (Errors.Count == 0)
                        {
                            Fail("a key is an object with \"Time\" and \"Value\"");
                        }
                        return;
                    }
                    if (keys.Any(x => x.time == time))
                    {
                        Fail("two keys at " + Time(time));
                        return;
                    }
                    string error;
                    Object value = ObjectRefs.Resolve(Json.Get(k, "Value"), expected, RefScope.None, out error);
                    if (error != null)
                    {
                        Fail("Value at " + Time(time) + ": " + error);
                        return;
                    }
                    keys.Add(new ObjectReferenceKeyframe { time = time, value = value });
                }
                keys.Sort((x, y) => x.time.CompareTo(y.time));
                AnimationUtility.SetObjectReferenceCurve(clip, b, keys.ToArray());
                ObjectReferenceKeyframe[] back = AnimationUtility.GetObjectReferenceCurve(clip, b) ?? new ObjectReferenceKeyframe[0];
                if (back.Length != keys.Count || back.Where((x, i) => x.time != keys[i].time || x.value != keys[i].value).Any())
                {
                    Fail("Unity did not keep the keys of " + Describe(b));
                }
            }

            bool EventKey(JToken op, out float time, out string function)
            {
                function = Json.Str(op, "Function");
                if (!Number(Json.Get(op, "Time"), "Time", false, out time))
                {
                    return false;
                }
                if (string.IsNullOrEmpty(function))
                {
                    Fail("needs \"Function\"");
                    return false;
                }
                return true;
            }

            // Adds the event, or sets the parameters of the one at that Time with that Function. A parameter left out is cleared.
            void SetEvent(JToken op)
            {
                float time;
                string function;
                if (!EventKey(op, out time, out function))
                {
                    return;
                }
                var e = new AnimationEvent { time = time, functionName = function };
                if (Json.Has(op, "Float"))
                {
                    float f;
                    if (!Number(Json.Get(op, "Float"), "Float", false, out f))
                    {
                        return;
                    }
                    e.floatParameter = f;
                }
                if (Json.Has(op, "Int"))
                {
                    JToken i = Json.Get(op, "Int");
                    if (i.Type != JTokenType.Integer)
                    {
                        Fail("Int needs a whole number");
                        return;
                    }
                    e.intParameter = (int)i;
                }
                if (Json.Has(op, "String"))
                {
                    JToken s = Json.Get(op, "String");
                    if (s.Type != JTokenType.String)
                    {
                        Fail("String needs a string");
                        return;
                    }
                    e.stringParameter = (string)s;
                }
                if (Json.Has(op, "Object"))
                {
                    string error;
                    e.objectReferenceParameter = ObjectRefs.Resolve(Json.Get(op, "Object"), typeof(Object), RefScope.None, out error);
                    if (error != null)
                    {
                        Fail("Object: " + error);
                        return;
                    }
                }
                List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
                int index = events.FindIndex(x => x.time == time && x.functionName == function);
                if (index >= 0)
                {
                    events[index] = e;
                }
                else
                {
                    events.Add(e);
                }
                AnimationUtility.SetAnimationEvents(clip, events.OrderBy(x => x.time).ToArray());
            }

            void RemoveEvent(JToken op)
            {
                float time;
                string function;
                if (!EventKey(op, out time, out function))
                {
                    return;
                }
                List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
                if (events.RemoveAll(x => x.time == time && x.functionName == function) == 0)
                {
                    Fail("no event " + function + " at " + Time(time) + (events.Count == 0 ? ", the clip has no events"
                        : ", the events are " + string.Join(", ", events.Select(x => x.functionName + " at " + Time(x.time)).ToArray())));
                    return;
                }
                AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            }

            bool Settings(JObject spec)
            {
                AnimationClipSettings s = AnimationUtility.GetAnimationClipSettings(clip);
                foreach (JProperty p in spec.Properties())
                {
                    if (p.Name.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string name = SettingNames.First(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                    label = "Settings." + name;
                    if (BoolSettings.Contains(name))
                    {
                        if (p.Value.Type != JTokenType.Boolean)
                        {
                            Fail("needs true or false");
                            return false;
                        }
                        SetBool(s, name, (bool)p.Value);
                    }
                    else if (name == "AdditiveReferencePoseClip")
                    {
                        string error;
                        Object pose = ObjectRefs.Resolve(p.Value, typeof(AnimationClip), RefScope.None, out error);
                        if (error != null)
                        {
                            Fail(error);
                            return false;
                        }
                        s.additiveReferencePoseClip = pose as AnimationClip;
                        s.hasAdditiveReferencePose = pose != null;
                    }
                    else
                    {
                        float f;
                        if (!Number(p.Value, name, false, out f))
                        {
                            return false;
                        }
                        SetFloat(s, name, f);
                    }
                }
                AnimationUtility.SetAnimationClipSettings(clip, s);
                return true;
            }

            static void SetBool(AnimationClipSettings s, string name, bool v)
            {
                switch (name)
                {
                    case "LoopTime": s.loopTime = v; break;
                    case "LoopBlend": s.loopBlend = v; break;
                    case "Mirror": s.mirror = v; break;
                    case "HeightFromFeet": s.heightFromFeet = v; break;
                    case "KeepOriginalOrientation": s.keepOriginalOrientation = v; break;
                    case "KeepOriginalPositionY": s.keepOriginalPositionY = v; break;
                    case "KeepOriginalPositionXZ": s.keepOriginalPositionXZ = v; break;
                    case "LoopBlendOrientation": s.loopBlendOrientation = v; break;
                    case "LoopBlendPositionY": s.loopBlendPositionY = v; break;
                    case "LoopBlendPositionXZ": s.loopBlendPositionXZ = v; break;
                }
            }

            static void SetFloat(AnimationClipSettings s, string name, float v)
            {
                switch (name)
                {
                    case "CycleOffset": s.cycleOffset = v; break;
                    case "StartTime": s.startTime = v; break;
                    case "StopTime": s.stopTime = v; break;
                    case "Level": s.level = v; break;
                    case "OrientationOffsetY": s.orientationOffsetY = v; break;
                    case "AdditiveReferencePoseTime": s.additiveReferencePoseTime = v; break;
                }
            }
        }
    }
}
