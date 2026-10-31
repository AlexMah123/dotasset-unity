using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace DotAsset
{
    // Points [SerializeReference] data saved under one type at another, by rewriting type: {class, ns, asm} in the
    // YAML. The fix after a class behind a [SerializeReference] field was renamed or moved to another namespace or
    // assembly: Unity reads such data as null until the entry names a compiled type. AuditMissingReference R5 lists them.
    // Needs Asset Serialization Mode = Force Text. Field data is kept as long as the new class has the same fields.
    // Usage: dotasset.py RenameSerializeReferenceType --args '-from="Assembly-CSharp Game.OldEffect" -to=Game.Effects.Burn -apply'
    // Docs/Migrate.md
    public sealed class RenameSerializeReferenceTypeCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Repoints [SerializeReference] data from one type name to a compiled type"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "scandir" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            if (!AssetScan.IsForceText())
            {
                ctx.Log.Error("RenameSerializeReferenceType rewrites YAML, set Project Settings > Editor > Asset Serialization Mode to Force Text first");
                return ExitCode.Refused;
            }
            string fromArg = ctx.Args.Get("from");
            string toArg = ctx.Args.Get("to");
            string fromAsm, fromNs, fromCls;
            if (fromArg == null || toArg == null || !ManagedTypes.Parse(fromArg, out fromAsm, out fromNs, out fromCls))
            {
                ctx.Log.Error("RenameSerializeReferenceType needs -from=\"<assembly> <namespace>.<class>\" (the assembly may be left out) and -to=<compiled type>");
                return ExitCode.Refused;
            }
            string error;
            Type to = TypeResolver.Find(toArg, null, out error);
            if (to == null)
            {
                ctx.Log.Error("-to: " + error);
                return ExitCode.Refused;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(to) || to.IsAbstract || to.IsValueType || to.IsGenericTypeDefinition || !to.IsSerializable)
            {
                ctx.Log.Error("-to: " + to.FullName + " cannot be stored in a [SerializeReference] field, it must be a [Serializable] class, not abstract, generic or a UnityEngine.Object");
                return ExitCode.Refused;
            }
            string toAsm, toNs, toCls;
            ManagedTypes.Split(to, out toAsm, out toNs, out toCls);
            if (fromCls == toCls && fromNs == toNs && (fromAsm == null || fromAsm == toAsm))
            {
                ctx.Log.Error("-from and -to name the same type");
                return ExitCode.Refused;
            }
            if (ManagedTypes.Resolve(fromAsm ?? toAsm, fromNs, fromCls) != null)
            {
                ctx.Log.Warn(ManagedTypes.Name(fromAsm ?? toAsm, fromNs, fromCls) + " still compiles, its data moves to " + to.FullName + " anyway");
            }

            Regex pattern = Pattern(fromAsm, fromNs, fromCls);
            string replacement = ManagedTypes.YamlEntry(toAsm, toNs, toCls);
            string toName = ManagedTypes.Name(toAsm, toNs, toCls);

            var files = AssetScan.All(AssetScan.Roots(ctx.Args), p => AssetScan.HasExtension(p, AssetScan.YamlExtensions));
            int changedFiles = 0;
            int changedRefs = 0;
            var toImport = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                if (i % 100 == 0)
                {
                    ctx.Progress(i, files.Count, path);
                }
                string abs = DotAssetPaths.Absolute(path);
                string text;
                try
                {
                    text = File.ReadAllText(abs);
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                    continue;
                }
                if (text.IndexOf("class: " + fromCls + ",", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                int hits = pattern.Matches(text).Count;
                if (hits == 0)
                {
                    continue;
                }
                string busy = AssetScan.OpenInEditor(path);
                if (busy != null)
                {
                    // The file is rewritten on disk. A loaded copy would keep the old entry and write it back on its next save.
                    ctx.FailTarget(path, busy);
                    continue;
                }
                changedFiles++;
                changedRefs += hits;
                ctx.Log.Info((ctx.DryRun ? "would repoint " : "repointed ") + hits + " [SerializeReference] entr" + (hits == 1 ? "y" : "ies") + " in " + path);
                if (ctx.DryRun)
                {
                    ctx.PlanFile("rewrite", path, hits + " entr" + (hits == 1 ? "y" : "ies") + " to " + toName);
                    continue;
                }
                try
                {
                    RunJournal.Keep(path);
                    DotAssetPaths.ReplaceText(abs, pattern.Replace(text, replacement));
                    toImport.Add(path);
                }
                catch (Exception e)
                {
                    ctx.FailTarget(path, e.Message);
                }
            }
            foreach (string path in toImport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            ctx.Log.Info((ctx.DryRun ? "would change " : "changed ") + changedRefs + " entr" + (changedRefs == 1 ? "y" : "ies") + " in " + changedFiles + " file(s) to " + toName);
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // type: {class: <class>, ns: <namespace>, asm: <assembly>}, any assembly when it is left out.
        internal static Regex Pattern(string fromAsm, string fromNs, string fromCls)
        {
            return new Regex(@"type: \{class: " + Regex.Escape(fromCls) + ", ns: " + Regex.Escape(fromNs) + ", asm: "
                + (fromAsm != null ? Regex.Escape(fromAsm) : "[^,}]*") + @"\}");
        }
    }
}
