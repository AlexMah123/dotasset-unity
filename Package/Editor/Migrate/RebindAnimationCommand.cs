using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Rewrites animation curves after a GameObject or bone was renamed or moved, or after an animated field was
    // renamed, so the curves find their target again instead of showing "(Missing!)". -from and -to are paths relative
    // to the Animator, the way the Animation window shows them. Curves on children move along: Body/Arm -> Body/ArmL
    // also rewrites Body/Arm/Hand. -property and -toproperty rename a property of the components -type names, its parts
    // along: m_Color -> m_Tint also rewrites m_Color.r. Both pairs in one run do both. Clips are read from .anim files
    // and from inside .controller and .playable (Timeline) files. Clips inside model files are read-only and are
    // skipped, extract them to .anim first.
    // Usage: dotasset.py RebindAnimation --args "-from=Body/Arm -to=Body/ArmL -scandir=Assets/Animations -apply"
    // Docs/Migrate.md
    public sealed class RebindAnimationCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Rewrites animation curve paths or property names after a GameObject, bone or field was renamed or moved"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "property", "toproperty", "type", "scandir" }; }
        }

        // The curves of one clip that change, each with its binding before and after.
        sealed class ClipChange
        {
            public AnimationClip Clip;
            public readonly List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>> Floats = new List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>>();
            public readonly List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>> Refs = new List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>>();

            public int Count
            {
                get { return Floats.Count + Refs.Count; }
            }
        }

        // The new name when name is from or lies beneath it (from + separator + rest), otherwise null.
        static string Rebased(string name, string from, string to, char separator)
        {
            if (name == from)
            {
                return to;
            }
            if (name.StartsWith(from + separator, StringComparison.Ordinal))
            {
                return to + name.Substring(from.Length);
            }
            return null;
        }

        static string Key(EditorCurveBinding b)
        {
            return b.path + "|" + (b.type != null ? b.type.FullName : "") + "|" + b.propertyName;
        }

        public override ExitCode Execute(RunContext ctx)
        {
            string from = ctx.Args.Get("from");
            string to = ctx.Args.Get("to");
            string property = ctx.Args.Get("property");
            string toProperty = ctx.Args.Get("toproperty");
            List<string> types = ctx.Args.GetList("type");
            bool byPath = !string.IsNullOrEmpty(from) || !string.IsNullOrEmpty(to);
            bool byProperty = !string.IsNullOrEmpty(property) || !string.IsNullOrEmpty(toProperty) || types.Count > 0;
            if (!byPath && !byProperty)
            {
                ctx.Log.Error("RebindAnimation needs -from=<old path> and -to=<new path>, both relative to the Animator, e.g. -from=Body/Arm -to=Body/ArmL, "
                    + "or -property=<old name> -toproperty=<new name> -type=<component>, e.g. -property=speed -toproperty=moveSpeed -type=Mover");
                return ExitCode.Refused;
            }
            if (byPath)
            {
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                {
                    ctx.Log.Error("-from and -to go together, both relative to the Animator, e.g. -from=Body/Arm -to=Body/ArmL");
                    return ExitCode.Refused;
                }
                from = from.Trim('/');
                to = to.Trim('/');
                if (from == to)
                {
                    ctx.Log.Error("-from and -to are the same path");
                    return ExitCode.Refused;
                }
                if (to.StartsWith(from + "/", StringComparison.Ordinal))
                {
                    // Fine as a move (Arm -> Arm/Pivot/Arm), but every run would push the curves one level deeper again.
                    ctx.Log.Warn("-to lies beneath -from, run this once only");
                }
            }
            if (byProperty)
            {
                if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(toProperty) || types.Count == 0)
                {
                    // A property name alone can belong to several components, a Light's m_Color is not a SpriteRenderer's.
                    ctx.Log.Error("-property, -toproperty and -type go together. -type names the component whose property was renamed, "
                        + "e.g. -property=speed -toproperty=moveSpeed -type=Mover, a list for several (-type=MeshRenderer,SkinnedMeshRenderer)");
                    return ExitCode.Refused;
                }
                property = property.Trim('.');
                toProperty = toProperty.Trim('.');
                if (property == toProperty)
                {
                    ctx.Log.Error("-property and -toproperty are the same name");
                    return ExitCode.Refused;
                }
                if (toProperty.StartsWith(property + ".", StringComparison.Ordinal))
                {
                    ctx.Log.Warn("-toproperty lies beneath -property, run this once only");
                }
            }

            Func<EditorCurveBinding, EditorCurveBinding?> rebind = b =>
            {
                string newPath = byPath ? Rebased(b.path, from, to, '/') : null;
                bool ofType = b.type != null && types.Any(t => t == b.type.Name || t == b.type.FullName);
                string newProperty = byProperty && ofType ? Rebased(b.propertyName, property, toProperty, '.') : null;
                if (newPath == null && newProperty == null)
                {
                    return null;
                }
                EditorCurveBinding nb = b;
                nb.path = newPath ?? b.path;
                nb.propertyName = newProperty ?? b.propertyName;
                return nb;
            };
            string what = (byPath ? from : "") + (byPath && byProperty ? " or " : "") + (byProperty ? property : "");

            Func<string, bool> holdsClips = p => AssetScan.HasExtension(p, ".anim", ".controller", ".playable");
            List<string> files = ctx.Assets.Count > 0
                ? AssetScan.Expand(ctx.Assets, holdsClips, ctx.Log)
                : AssetScan.All(AssetScan.Roots(ctx.Args), holdsClips);
            int changedFiles = 0;
            int changedCurves = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, files.Count, path);
                }
                if (!holdsClips(path))
                {
                    ctx.Log.Warn("skipped " + path + ", only clips in .anim, .controller and .playable files are writable");
                    continue;
                }
                bool single = AssetScan.HasExtension(path, ".anim");
                List<AnimationClip> clips;
                if (single)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip == null)
                    {
                        ctx.FailTarget(path, "not an AnimationClip");
                        continue;
                    }
                    clips = new List<AnimationClip> { clip };
                }
                else
                {
                    // A controller or timeline holds its clips as sub-assets, many have none.
                    clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToList();
                    if (clips.Count == 0)
                    {
                        continue;
                    }
                }
                string readOnly = ReadOnly.Reason(path);
                if (readOnly != null)
                {
                    ctx.FailTarget(path, readOnly);
                    continue;
                }

                var changes = new List<ClipChange>();
                string clash = null;
                foreach (AnimationClip clip in clips)
                {
                    var change = new ClipChange { Clip = clip };
                    var staying = new HashSet<string>(StringComparer.Ordinal);
                    foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    {
                        EditorCurveBinding? nb = rebind(b);
                        if (nb == null)
                        {
                            staying.Add(Key(b));
                            continue;
                        }
                        (b.isPPtrCurve ? change.Refs : change.Floats).Add(new KeyValuePair<EditorCurveBinding, EditorCurveBinding>(b, nb.Value));
                    }
                    if (change.Count == 0)
                    {
                        continue;
                    }
                    // A curve already animating the same property at the new path would be overwritten.
                    clash = change.Floats.Concat(change.Refs).Select(m => m.Value).Where(nb => staying.Contains(Key(nb)))
                        .Select(nb => (single ? "" : clip.name + " ") + "already animates " + nb.path + " " + nb.propertyName).FirstOrDefault();
                    if (clash != null)
                    {
                        break;
                    }
                    changes.Add(change);
                }
                if (clash != null)
                {
                    ctx.FailTarget(path, "the clip " + clash + ", merge the curves by hand");
                    continue;
                }
                if (changes.Count == 0)
                {
                    continue;
                }

                int count = changes.Sum(c => c.Count);
                ctx.Log.Info((ctx.DryRun ? "would rebind " : "rebound ") + count + " curve(s) in " + path);
                if (ctx.DryRun)
                {
                    ctx.PlanChanges(path, Plan(changes, single, property, toProperty));
                    changedFiles++;
                    changedCurves += count;
                    continue;
                }

                string unsaved = OpenWork.UnsavedIn(path);
                if (unsaved != null)
                {
                    ctx.FailTarget(path, unsaved);
                    continue;
                }
                ctx.BeginTarget();
                var snapshots = changes.Select(c => new AssetSnapshot(c.Clip)).ToList();
                try
                {
                    foreach (ClipChange c in changes)
                    {
                        // Read every curve before removing any, a removal re-syncs the clip.
                        var curves = c.Floats.Select(m => AnimationUtility.GetEditorCurve(c.Clip, m.Key)).ToList();
                        var keys = c.Refs.Select(m => AnimationUtility.GetObjectReferenceCurve(c.Clip, m.Key)).ToList();
                        foreach (var m in c.Floats)
                        {
                            AnimationUtility.SetEditorCurve(c.Clip, m.Key, null);
                        }
                        foreach (var m in c.Refs)
                        {
                            AnimationUtility.SetObjectReferenceCurve(c.Clip, m.Key, null);
                        }
                        for (int k = 0; k < c.Floats.Count; k++)
                        {
                            AnimationUtility.SetEditorCurve(c.Clip, c.Floats[k].Value, curves[k]);
                        }
                        for (int k = 0; k < c.Refs.Count; k++)
                        {
                            AnimationUtility.SetObjectReferenceCurve(c.Clip, c.Refs[k].Value, keys[k]);
                        }
                    }
                    string error;
                    if (!ctx.ReadyToSave(out error))
                    {
                        ctx.FailTarget(path, error);
                        continue;
                    }
                    foreach (ClipChange c in changes)
                    {
                        EditorUtility.SetDirty(c.Clip);
                    }
                    RunJournal.Keep(path);
                    ctx.BeforeSave(path);
                    // Saves the whole file, the sub-asset clips of a controller or timeline with it.
                    AssetDatabase.SaveAssetIfDirty(changes[0].Clip);
                    if (ctx.SaveFailed(path))
                    {
                        continue;
                    }
                    foreach (AssetSnapshot s in snapshots)
                    {
                        s.Commit();
                    }
                }
                finally
                {
                    foreach (AssetSnapshot s in snapshots)
                    {
                        s.Dispose();
                    }
                }
                changedFiles++;
                changedCurves += count;
            }
            ctx.Log.Info((ctx.DryRun ? "would rebind " : "rebound ") + changedCurves + " curve(s) in " + changedFiles + " file(s)");
            if (changedFiles == 0 && ctx.FailedTargets.Count == 0)
            {
                ctx.Log.Warn("no clip under the scanned folders animates " + what);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // One plan entry per old path and per renamed property of each type, with the clip's name when the file holds
        // several.
        static List<JObject> Plan(List<ClipChange> changes, bool single, string property, string toProperty)
        {
            var plan = new List<JObject>();
            foreach (ClipChange c in changes)
            {
                var all = c.Floats.Concat(c.Refs).ToList();
                foreach (var g in all.Where(m => m.Key.path != m.Value.path).GroupBy(m => m.Key.path).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    plan.Add(Entry(c, single, "path", null, g.Key, g.First().Value.path, g.Count()));
                }
                foreach (var g in all.Where(m => m.Key.propertyName != m.Value.propertyName).GroupBy(m => m.Key.type.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    plan.Add(Entry(c, single, "property", g.Key, property, toProperty, g.Count()));
                }
            }
            return plan;
        }

        static JObject Entry(ClipChange c, bool single, string what, string type, string before, string after, int curves)
        {
            var entry = new JObject();
            if (!single)
            {
                entry["Clip"] = c.Clip.name;
            }
            entry["Property"] = what;
            if (type != null)
            {
                entry["Type"] = type;
            }
            entry["Before"] = before;
            entry["After"] = after;
            entry["Curves"] = curves;
            return entry;
        }
    }
}
