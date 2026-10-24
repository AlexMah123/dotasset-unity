using System;

namespace DotAsset
{
    // Process exit code of a run. The wrapper passes it straight through.
    public enum ExitCode
    {
        Success = 0,
        // The run started and something failed. Nothing of a failed target was saved.
        Failed = 1,
        // The run refused to start: unknown run, bad argument, unreadable spec,
        // or the editor holds the project and the run would fight it.
        Refused = 2,
        // Audit only: the run itself succeeded and the report lists something to fix.
        IssuesFound = 3,
    }

    public enum CommandGroup
    {
        Export,
        Import,
        Edit,
        Migrate,
        Audit,
    }

    public static class Groups
    {
        // Same rule as resolve_group() in Scripts~/dotasset.py. Change both together.
        //   *Export          Export
        //   *Import, Create* Import
        //   Edit*            Edit
        //   Audit*           Audit
        //   anything else    Migrate
        public static CommandGroup Resolve(string runName)
        {
            if (string.IsNullOrEmpty(runName))
            {
                return CommandGroup.Migrate;
            }
            if (runName.EndsWith("Export", StringComparison.Ordinal))
            {
                return CommandGroup.Export;
            }
            if (runName.EndsWith("Import", StringComparison.Ordinal) || runName.StartsWith("Create", StringComparison.Ordinal))
            {
                return CommandGroup.Import;
            }
            if (runName.StartsWith("Edit", StringComparison.Ordinal))
            {
                return CommandGroup.Edit;
            }
            if (runName.StartsWith("Audit", StringComparison.Ordinal))
            {
                return CommandGroup.Audit;
            }
            return CommandGroup.Migrate;
        }

        // Edit and Migrate never write without -apply.
        public static bool IsDryRunByDefault(CommandGroup group)
        {
            return group == CommandGroup.Edit || group == CommandGroup.Migrate;
        }
    }
}
