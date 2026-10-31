using System;
using System.Collections.Generic;
using UnityEditor;

namespace DotAsset
{
    // Base of every run. The run name is the class name without the "Command" suffix, and the name decides
    // the group (see Groups.Resolve). Commands in your own editor assemblies are picked up the same way,
    // so a missing mechanism is one more class away.
    public abstract class DotAssetCommand
    {
        public virtual string Name
        {
            get
            {
                string n = GetType().Name;
                return n.EndsWith("Command", StringComparison.Ordinal) ? n.Substring(0, n.Length - "Command".Length) : n;
            }
        }

        // The group the command means to be in. Override it to state it: the registry refuses a command whose
        // name resolves to another group, so a name like CreateBackup cannot quietly make a run write without -apply.
        public virtual CommandGroup Group
        {
            get { return Groups.Resolve(Name); }
        }

        // True only for Import runs, which write without -apply. An Import command must say so by overriding this,
        // and any other command must not, or the registry refuses it.
        public virtual bool WritesWithoutApply
        {
            get { return false; }
        }

        // One line, shown by the wrapper's --list and the DotAsset window.
        public abstract string Summary { get; }

        // Extra-argument keys this command reads. Anything else on the command line is refused, so a typo
        // like -aply cannot silently turn a write into a dry run.
        public virtual IEnumerable<string> Arguments
        {
            get { return Array.Empty<string>(); }
        }

        // False for runs whose targets come from a spec or arguments. An AssetList given to them is refused
        // rather than silently ignored.
        public virtual bool TakesAssetList
        {
            get { return true; }
        }

        public abstract ExitCode Execute(RunContext ctx);
    }

    public static class CommandRegistry
    {
        static Dictionary<string, DotAssetCommand> commands;
        static Dictionary<string, string> rejected;

        // Commands left out of the registry, run name to reason. The unknown-run error and CommandCatalogExport
        // report them. Not logged to the console, the registry is built inside the first run and would add to its log.
        public static IReadOnlyDictionary<string, string> Rejected
        {
            get
            {
                if (commands == null)
                {
                    Build();
                }
                return rejected;
            }
        }

        public static IReadOnlyDictionary<string, DotAssetCommand> All
        {
            get
            {
                if (commands == null)
                {
                    Build();
                }
                return commands;
            }
        }

        public static DotAssetCommand Find(string runName)
        {
            DotAssetCommand cmd;
            return runName != null && All.TryGetValue(runName, out cmd) ? cmd : null;
        }

        static void Build()
        {
            commands = new Dictionary<string, DotAssetCommand>(StringComparer.OrdinalIgnoreCase);
            rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Type type in TypeCache.GetTypesDerivedFrom<DotAssetCommand>())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }
                DotAssetCommand cmd;
                try
                {
                    cmd = (DotAssetCommand)Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    continue;
                }
                if (commands.ContainsKey(cmd.Name))
                {
                    UnityEngine.Debug.LogWarning("[DotAsset] two commands are named " + cmd.Name + ", keeping " + commands[cmd.Name].GetType().FullName);
                    continue;
                }
                string refusal = Refusal(cmd);
                if (refusal != null)
                {
                    rejected[cmd.Name] = refusal;
                    continue;
                }
                commands.Add(cmd.Name, cmd);
            }
        }

        // Why a command cannot be registered, or null. Its declarations must agree with the name rule.
        internal static string Refusal(DotAssetCommand cmd)
        {
            CommandGroup byName = Groups.Resolve(cmd.Name);
            if (cmd.Group != byName)
            {
                return cmd.GetType().FullName + " says it is " + cmd.Group + " but its name makes it " + byName
                    + ", rename it (see Groups.Resolve)";
            }
            bool import = cmd.Group == CommandGroup.Import;
            if (cmd.WritesWithoutApply != import)
            {
                return import
                    ? cmd.GetType().FullName + " is an Import run, which writes without -apply. Override WritesWithoutApply to return true, or rename it"
                    : cmd.GetType().FullName + " says it writes without -apply, but only Import runs (*Import, Create*) do";
            }
            return null;
        }

        // Arguments every command of a group accepts on top of its own.
        public static IEnumerable<string> CommonArguments(CommandGroup group)
        {
            switch (group)
            {
                case CommandGroup.Export:
                    return new[] { "outdir", "full", "maxarray", "maxstring" };
                case CommandGroup.Edit:
                case CommandGroup.Migrate:
                    return new[] { "apply", "atomic" };
                case CommandGroup.Audit:
                    // -apply is accepted and ignored: audits never write, so a habitual -apply must not refuse the run.
                    return new[] { "report", "apply" };
                default:
                    // Import writes without -apply, and accepts it for the same reason.
                    return new[] { "apply", "atomic" };
            }
        }
    }
}
