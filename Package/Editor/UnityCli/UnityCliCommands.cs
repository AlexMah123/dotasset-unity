using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace DotAsset
{
    // DotAsset runs as Unity CLI commands, in projects with com.unity.pipeline 0.9 or later (this assembly compiles only then).
    // Usage: unity command dotasset run --name EditMaterial --args "-spec=Assets/Specs/red.json" --confirm true
    // Docs/AI-Guide.md
    static class UnityCliCommands
    {
        const string Group = "dotasset";

        [CliCommand(Group, "run", "Run a DotAsset run in the open editor. An Edit or Migrate run writes only with confirm=true or -apply in args, otherwise it reports its plan. Import runs write either way, Export and Audit runs never. The result is the run's JSON, a run that does not exit 0 fails with its lines as the message")]
        public static async Task<JObject> Run(
            [CliArg("name", "The run, e.g. EditMaterial or MoveAsset. dotasset list shows them all", Required = true)] string name,
            [CliArg("args", "The run's arguments as on the dotasset.py command line, e.g. \"-spec=Assets/Specs/red.json\"")] string args = "",
            [CliArg("assets", "Comma-separated asset paths or folders for runs that take entry assets, as the AssetList of dotasset.py")] string assets = "",
            [CliArg("confirm", "true adds -apply, so an Edit or Migrate run writes. -apply in args writes too, so a dry run leaves both out. Import runs write either way")] bool confirm = false,
            [CliArg("timeout", "Seconds the caller waits. A run that outlasts it still finishes and writes, read its log before rerunning")] double timeout = 1800)
        {
            // A string, not string[]: the CLI takes an array only as JSON, and refuses a repeated --assets.
            RunResult result = await EditorRun.RunAsync(name, RunArgs.SplitList(assets), args, confirm);
            if (result.ExitCode != (int)ExitCode.Success)
            {
                throw new InvalidOperationException(EditorRun.Describe(result));
            }
            return result.ToJson();
        }

        [CliCommand(Group, "list", "Every DotAsset run with its group, summary and arguments")]
        public static JArray List()
        {
            return CommandCatalogExportCommand.Catalog();
        }
    }
}
