using System;
using System.Collections.Generic;
using UnityEditor;

namespace AssetOps
{
    // Base of every run. The run name is the class name without the "Command" suffix, and the name decides
    // the group (see Groups.Resolve). Commands in your own editor assemblies are picked up the same way,
    // so a missing mechanism is one more class away.
    public abstract class AssetOpsCommand
    {
        public virtual string Name
        {
            get
            {
                string n = GetType().Name;
                return n.EndsWith("Command", StringComparison.Ordinal) ? n.Substring(0, n.Length - "Command".Length) : n;
            }
        }

        public CommandGroup Group
        {
            get { return Groups.Resolve(Name); }
        }

        // One line, shown by the wrapper's --list and the AssetOps window.
        public abstract string Summary { get; }

        // Extra-argument keys this command reads. Anything else on the command line is refused, so a typo
        // like -aply cannot silently turn a write into a dry run.
        public virtual IEnumerable<string> Arguments
        {
            get { return Array.Empty<string>(); }
        }

        public abstract ExitCode Execute(RunContext ctx);
    }

    public static class CommandRegistry
    {
        static Dictionary<string, AssetOpsCommand> commands;

        public static IReadOnlyDictionary<string, AssetOpsCommand> All
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

        public static AssetOpsCommand Find(string runName)
        {
            AssetOpsCommand cmd;
            return runName != null && All.TryGetValue(runName, out cmd) ? cmd : null;
        }

        static void Build()
        {
            commands = new Dictionary<string, AssetOpsCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (Type type in TypeCache.GetTypesDerivedFrom<AssetOpsCommand>())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }
                AssetOpsCommand cmd;
                try
                {
                    cmd = (AssetOpsCommand)Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    continue;
                }
                if (commands.ContainsKey(cmd.Name))
                {
                    UnityEngine.Debug.LogWarning("[AssetOps] two commands are named " + cmd.Name + ", keeping " + commands[cmd.Name].GetType().FullName);
                    continue;
                }
                commands.Add(cmd.Name, cmd);
            }
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
                    return new[] { "apply" };
                case CommandGroup.Audit:
                    // -apply is accepted and ignored: audits never write, so a habitual -apply must not refuse the run.
                    return new[] { "report", "apply" };
                default:
                    // Import writes without -apply, and accepts it for the same reason.
                    return new[] { "apply" };
            }
        }
    }
}
