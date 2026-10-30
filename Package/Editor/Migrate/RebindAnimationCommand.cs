using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Rewrites the hierarchy paths in .anim clips after a GameObject or bone was renamed or moved, so the curves find
    // their target again instead of showing "(Missing!)". -from and -to are paths relative to the Animator, the way
    // the Animation window shows them. Curves on children move along: Body/Arm -> Body/ArmL also rewrites Body/Arm/Hand.
    // Clips inside model files are read-only and are skipped, extract them to .anim first.
    // Usage: dotasset.py RebindAnimation --args "-from=Body/Arm -to=Body/ArmL -scandir=Assets/Animations -apply"
    // Docs/Migrate.md
    public sealed class RebindAnimationCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Rewrites animation curve paths after a GameObject or bone was renamed or moved"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "scandir" }; }
        }

        // The new path when path is from or lies beneath it, otherwise null.
        static string Rebased(string path, string from, string to)
        {
            if (path == from)
            {
                return to;
            }
            if (path.StartsWith(from + "/", StringComparison.Ordinal))
            {
                return to + path.Substring(from.Length);
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
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                ctx.Log.Error("RebindAnimation needs -from=<old path> and -to=<new path>, both relative to the Animator, e.g. -from=Body/Arm -to=Body/ArmL");
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

            Func<string, bool> isClip = p => AssetScan.HasExtension(p, ".anim");
            List<string> clips = ctx.Assets.Count > 0
                ? AssetScan.Expand(ctx.Assets, isClip, ctx.Log)
                : AssetScan.All(AssetScan.Roots(ctx.Args), isClip);
            int changedClips = 0;
            int changedCurves = 0;
            for (int i = 0; i < clips.Count; i++)
            {
                string path = clips[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, clips.Count, path);
                }
                if (!isClip(path))
                {
                    ctx.Log.Warn("skipped " + path + ", only .anim files are writable clips");
                    continue;
                }
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    ctx.FailTarget(path, "not an AnimationClip");
                    continue;
                }
                EditorCurveBinding[] floats = AnimationUtility.GetCurveBindings(clip);
                EditorCurveBinding[] refs = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                var moved = new List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>>();
                var movedRefs = new List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>>();
                var staying = new HashSet<string>(StringComparer.Ordinal);
                foreach (EditorCurveBinding b in floats.Concat(refs))
                {
                    string newPath = Rebased(b.path, from, to);
                    if (newPath == null)
                    {
                        staying.Add(Key(b));
                        continue;
                    }
                    EditorCurveBinding nb = b;
                    nb.path = newPath;
                    (b.isPPtrCurve ? movedRefs : moved).Add(new KeyValuePair<EditorCurveBinding, EditorCurveBinding>(b, nb));
                }
                if (moved.Count + movedRefs.Count == 0)
                {
                    continue;
                }
                // A curve already animating the same property at the new path would be overwritten.
                string clash = moved.Concat(movedRefs).Select(m => m.Value).Where(nb => staying.Contains(Key(nb)))
                    .Select(nb => nb.path + " " + nb.propertyName).FirstOrDefault();
                if (clash != null)
                {
                    ctx.FailTarget(path, "the clip already animates " + clash + ", merge the curves by hand");
                    continue;
                }

                int count = moved.Count + movedRefs.Count;
                var byPath = moved.Concat(movedRefs).GroupBy(m => m.Key.path).OrderBy(g => g.Key, StringComparer.Ordinal);
                ctx.Log.Info((ctx.DryRun ? "would rebind " : "rebound ") + count + " curve(s) in " + path);
                if (ctx.DryRun)
                {
                    var changes = new List<JObject>();
                    foreach (var g in byPath)
                    {
                        changes.Add(new JObject
                        {
                            ["Property"] = "path",
                            ["Before"] = g.Key,
                            ["After"] = g.First().Value.path,
                            ["Curves"] = g.Count(),
                        });
                    }
                    ctx.PlanChanges(path, changes);
                    changedClips++;
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
                using (var snapshot = new AssetSnapshot(clip))
                {
                    // Read every curve before removing any, a removal re-syncs the clip.
                    var curves = moved.Select(m => AnimationUtility.GetEditorCurve(clip, m.Key)).ToList();
                    var keys = movedRefs.Select(m => AnimationUtility.GetObjectReferenceCurve(clip, m.Key)).ToList();
                    foreach (var m in moved)
                    {
                        AnimationUtility.SetEditorCurve(clip, m.Key, null);
                    }
                    foreach (var m in movedRefs)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, m.Key, null);
                    }
                    for (int c = 0; c < moved.Count; c++)
                    {
                        AnimationUtility.SetEditorCurve(clip, moved[c].Value, curves[c]);
                    }
                    for (int c = 0; c < movedRefs.Count; c++)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, movedRefs[c].Value, keys[c]);
                    }
                    string error;
                    if (!ctx.ReadyToSave(out error))
                    {
                        ctx.FailTarget(path, error);
                        continue;
                    }
                    EditorUtility.SetDirty(clip);
                    AssetDatabase.SaveAssetIfDirty(clip);
                    snapshot.Commit();
                }
                ctx.WarnIfSavedWithErrors(path);
                changedClips++;
                changedCurves += count;
            }
            ctx.Log.Info((ctx.DryRun ? "would rebind " : "rebound ") + changedCurves + " curve(s) in " + changedClips + " clip(s) from " + from + " to " + to);
            if (changedClips == 0 && ctx.FailedTargets.Count == 0)
            {
                ctx.Log.Warn("no clip under the scanned folders animates " + from);
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}
