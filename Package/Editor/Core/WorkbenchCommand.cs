using System;
using System.Collections.Generic;
using UnityEditor;

namespace AssetWorkbench
{
    // Base of every run. The run name is the class name without the "Command" suffix, and the name decides
    // the group (see Groups.Resolve). Commands in your own editor assemblies are picked up the same way,
    // so a missing mechanism is one more class away.
    public abstract class WorkbenchCommand
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

        // One line, shown by the wrapper's --list and the Asset Workbench window.
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
        static Dictionary<string, WorkbenchCommand> commands;

        public static IReadOnlyDictionary<string, WorkbenchCommand> All
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

        public static WorkbenchCommand Find(string runName)
        {
            WorkbenchCommand cmd;
            return runName != null && All.TryGetValue(runName, out cmd) ? cmd : null;
        }

        static void Build()
        {
            commands = new Dictionary<string, WorkbenchCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (Type type in TypeCache.GetTypesDerivedFrom<WorkbenchCommand>())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }
                WorkbenchCommand cmd;
                try
                {
                    cmd = (WorkbenchCommand)Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    continue;
                }
                if (commands.ContainsKey(cmd.Name))
                {
                    UnityEngine.Debug.LogWarning("[AssetWorkbench] two commands are named " + cmd.Name + ", keeping " + commands[cmd.Name].GetType().FullName);
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
                    return new[] { "report" };
                default:
                    return Array.Empty<string>();
            }
        }
    }
}
