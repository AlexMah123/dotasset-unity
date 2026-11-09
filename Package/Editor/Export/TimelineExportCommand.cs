using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Timelines: duration and frame rate, every track with its type, the object it binds to, its clips and markers,
    // and the timeline's own markers. Timeline is read through its public API by reflection, so the package compiles
    // without com.unity.timeline and the run refuses where it is not installed.
    // Usage: dotasset.py TimelineExport Assets/Cutscenes/Intro.playable
    // Docs/Export.md
    public sealed class TimelineExportCommand : DotAssetCommand
    {
        const string TimelineAssembly = "Unity.Timeline";

        public override string Summary
        {
            get { return "Timeline tracks with their clips, markers and binding types (needs com.unity.timeline)"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new string[0]; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            Type timelineType = Type.GetType("UnityEngine.Timeline.TimelineAsset, " + TimelineAssembly);
            if (timelineType == null)
            {
                ctx.Log.Error("TimelineExport needs the Timeline package (com.unity.timeline), this project does not have it");
                return ExitCode.Refused;
            }
            var opt = ReadOptions.FromArgs(ctx.Args, RefScope.None);
            return ExportHelper.Run(ctx, path =>
            {
                Type t = AssetDatabase.GetMainAssetTypeAtPath(path);
                return t != null && timelineType.IsAssignableFrom(t);
            }, path =>
            {
                Object timeline = AssetDatabase.LoadMainAssetAtPath(path);
                if (timeline == null || !timelineType.IsInstanceOfType(timeline))
                {
                    ctx.Log.Error(path + " is not a timeline");
                    return false;
                }
                JObject json = ExportHelper.Header(ctx, path);
                Timeline(json, timeline, opt);
                ExportHelper.Save(ctx, path, json);
                return true;
            });
        }

        // Timeline counts time in ticks of 1e-12 s.
        const double TicksPerSecond = 1e12;

        static void Timeline(JObject json, Object timeline, ReadOptions opt)
        {
            json["Name"] = timeline.name;
            // Timeline's duration stops one tick short of the last clip's end, and its fixedDuration one tick short of
            // the fixed length, so a clip that ends there still plays. The export gives the length as the Timeline
            // window shows it: the tick goes back on, and in FixedLength mode duration is the stored fixed length.
            string mode = Convert.ToString(Get(timeline, "durationMode"));
            double duration = ((PlayableAsset)timeline).duration;
            if (mode == "BasedOnClips" && duration > 0)
            {
                duration = (Math.Round(duration * TicksPerSecond) + 1) / TicksPerSecond;
            }
            json["Duration"] = duration;
            json["DurationMode"] = mode;
            if (mode == "FixedLength")
            {
                json["FixedDuration"] = duration;
            }
            object settings = Get(timeline, "editorSettings");
            if (settings != null)
            {
                // frameRate since Timeline 1.6, fps before.
                json["FrameRate"] = Number(Get(settings, "frameRate") ?? Get(settings, "fps"));
            }
            // The root tracks start with the marker track, its markers are the timeline's own and listed apart.
            object markerTrack = Get(timeline, "markerTrack");
            var tracks = new JArray();
            foreach (object track in Call(timeline, "GetRootTracks"))
            {
                if (!ReferenceEquals(track, markerTrack))
                {
                    Track(tracks, track, null, opt);
                }
            }
            json["Tracks"] = tracks;
            json["Markers"] = markerTrack != null ? Markers(markerTrack, opt) : new JArray();
        }

        // Tracks are listed flat in timeline order, a child after its parent, named by their path through the groups.
        static void Track(JArray tracks, object track, string parent, ReadOptions opt)
        {
            var t = (Object)track;
            string path = parent == null ? t.name : parent + "/" + t.name;
            var obj = new JObject
            {
                ["Path"] = path,
                ["Type"] = TypeResolver.DisplayName(track.GetType()),
            };
            string binding = BindingType(track);
            if (binding != null)
            {
                obj["BindingType"] = binding;
            }
            if (Get(track, "muted") as bool? == true)
            {
                obj["Muted"] = true;
            }
            if (Get(track, "locked") as bool? == true)
            {
                obj["Locked"] = true;
            }
            // An animation track recorded without clips keeps its curves in an infinite clip.
            var infinite = Get(track, "infiniteClip") as AnimationClip;
            if (infinite != null && !infinite.empty)
            {
                obj["InfiniteClip"] = ObjectRefs.Write(infinite, RefScope.None);
            }
            var clips = new JArray();
            foreach (object clip in Call(track, "GetClips"))
            {
                clips.Add(Clip(clip, opt));
            }
            obj["Clips"] = clips;
            obj["Markers"] = Markers(track, opt);
            tracks.Add(obj);
            foreach (object child in Call(track, "GetChildTracks"))
            {
                Track(tracks, child, path, opt);
            }
        }

        // The type of object the track binds to in a PlayableDirector, e.g. Animator. The object itself is the
        // director's, in the scene or prefab that plays the timeline.
        static string BindingType(object track)
        {
            foreach (object o in Enumerate(Get(track, "outputs")))
            {
                if (o is PlayableBinding)
                {
                    Type target = ((PlayableBinding)o).outputTargetType;
                    if (target != null)
                    {
                        return TypeResolver.DisplayName(target);
                    }
                }
            }
            return null;
        }

        // Timing always, ease, blend and extrapolation only when set. The asset's clip (an animation or audio clip)
        // when it has one, the fields of a clip asset from outside the Timeline package as Properties.
        static JObject Clip(object clip, ReadOptions opt)
        {
            var obj = new JObject
            {
                ["Name"] = Convert.ToString(Get(clip, "displayName")),
                ["Start"] = Number(Get(clip, "start")),
                ["End"] = Number(Get(clip, "end")),
                ["Duration"] = Number(Get(clip, "duration")),
                ["ClipIn"] = Number(Get(clip, "clipIn")),
                ["TimeScale"] = Number(Get(clip, "timeScale")),
            };
            foreach (string name in new[] { "easeInDuration", "easeOutDuration", "blendInDuration", "blendOutDuration" })
            {
                double value = Number(Get(clip, name));
                if (value > 0)
                {
                    obj[char.ToUpperInvariant(name[0]) + name.Substring(1)] = value;
                }
            }
            foreach (string name in new[] { "preExtrapolationMode", "postExtrapolationMode" })
            {
                string mode = Convert.ToString(Get(clip, name));
                if (!string.IsNullOrEmpty(mode) && mode != "None")
                {
                    obj[char.ToUpperInvariant(name[0]) + name.Substring(1)] = mode;
                }
            }
            var asset = Get(clip, "asset") as Object;
            if (asset != null)
            {
                obj["AssetType"] = TypeResolver.DisplayName(asset.GetType());
                PropertyInfo source = Property(asset, "clip");
                if (source != null && typeof(Object).IsAssignableFrom(source.PropertyType))
                {
                    obj["Clip"] = ObjectRefs.Write(source.GetValue(asset, null) as Object, RefScope.None);
                }
                if (!FromTimeline(asset))
                {
                    obj["Properties"] = PropertyIO.Read(asset, opt);
                }
            }
            return obj;
        }

        // Time and type always. A signal emitter's signal and flags, a marker from outside the Timeline package
        // with its fields as Properties.
        static JArray Markers(object track, ReadOptions opt)
        {
            var markers = new JArray();
            foreach (object marker in Call(track, "GetMarkers"))
            {
                var obj = new JObject
                {
                    ["Time"] = Number(Get(marker, "time")),
                    ["Type"] = TypeResolver.DisplayName(marker.GetType()),
                };
                if (marker.GetType().Name == "SignalEmitter" && FromTimeline(marker))
                {
                    obj["Signal"] = ObjectRefs.Write(Get(marker, "asset") as Object, RefScope.None);
                    foreach (string name in new[] { "retroactive", "emitOnce" })
                    {
                        if (Get(marker, name) as bool? == true)
                        {
                            obj[char.ToUpperInvariant(name[0]) + name.Substring(1)] = true;
                        }
                    }
                }
                else if (!FromTimeline(marker) && marker is Object)
                {
                    obj["Properties"] = PropertyIO.Read((Object)marker, opt);
                }
                markers.Add(obj);
            }
            return markers;
        }

        static bool FromTimeline(object o)
        {
            return o.GetType().Assembly.GetName().Name == TimelineAssembly;
        }

        static PropertyInfo Property(object o, string name)
        {
            try
            {
                return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                // A derived class hides the property with its own, take the most derived one.
                for (Type t = o.GetType(); t != null; t = t.BaseType)
                {
                    PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (p != null)
                    {
                        return p;
                    }
                }
                return null;
            }
        }

        static object Get(object o, string name)
        {
            PropertyInfo p = Property(o, name);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
        }

        static IEnumerable<object> Call(object o, string method)
        {
            MethodInfo m = o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return Enumerate(m != null ? m.Invoke(o, null) : null);
        }

        static IEnumerable<object> Enumerate(object value)
        {
            var items = value as IEnumerable;
            if (items == null)
            {
                yield break;
            }
            foreach (object item in items)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        static double Number(object value)
        {
            return value == null ? 0 : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
