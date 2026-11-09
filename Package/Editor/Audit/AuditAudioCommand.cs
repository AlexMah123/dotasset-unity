using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DotAsset
{
    // Checks how prefabs and scenes route their audio.
    //   A1  an AudioSource with no output mixer group while the project has an AudioMixer: it plays straight to the
    //       listener, past the mixer's volume, snapshots and effects
    // A project without an AudioMixer has nothing to route to, so A1 does not apply there and nothing is checked. The
    // report's Mixers lists the mixers found. Audio clip import settings are not checked, Unity's Project Auditor does
    // that. The scan and the prefab instance rule are ComponentAudit's. No fix spec: which group a source plays to is a
    // choice, set as m_OutputAudioMixerGroup with EditPrefab or EditScene on the Object the issue names.
    // Usage: dotasset.py AuditAudio [Assets/Prefabs] --args "-scandir=Assets"
    // Docs/Audit.md
    public sealed class AuditAudioCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "AudioSources that play past the project's AudioMixer"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "scandir", "rules" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            var report = new AuditReport(ctx);
            report.Rule("A1", "AudioSource with no output mixer group while the project has an AudioMixer");

            Func<string, bool> on = report.RuleFilter();
            if (on == null)
            {
                return ExitCode.Refused;
            }
            string[] mixers = AssetDatabase.FindAssets("t:AudioMixer").Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".mixer", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            report.Extra["Mixers"] = new JArray(mixers.Cast<object>().ToArray());
            if (mixers.Length == 0)
            {
                ctx.Log.Info("the project has no AudioMixer, so A1 does not apply");
                return report.Finish();
            }

            var audit = new ComponentAudit(ctx, report, on);
            audit.Run(root =>
            {
                foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
                {
                    audit.Add("A1", "Warning", source, Unrouted);
                }
            });
            return report.Finish();
        }

        // A group whose mixer was deleted reads as none too.
        static string Unrouted(AudioSource source)
        {
            return source.outputAudioMixerGroup == null
                ? "AudioSource has no output mixer group, it plays past the project's AudioMixer (volume, snapshots, effects)"
                : null;
        }
    }
}
