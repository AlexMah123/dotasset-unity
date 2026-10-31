using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DotAsset
{
    // Every registered run with its group, summary and arguments, including commands from your own assemblies.
    // The wrapper's --list prints it.
    // Usage: dotasset.py CommandCatalogExport      (or: dotasset.py --list)
    // Docs/AI-Guide.md
    public sealed class CommandCatalogExportCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Every registered run with its group, summary and arguments"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JObject json = ExportHelper.Header(ctx, null);
            var list = new JArray();
            foreach (DotAssetCommand cmd in CommandRegistry.All.Values.OrderBy(c => c.Group).ThenBy(c => c.Name))
            {
                list.Add(new JObject
                {
                    ["RunName"] = cmd.Name,
                    ["Group"] = cmd.Group.ToString(),
                    ["Summary"] = cmd.Summary,
                    ["WritesWithoutApply"] = cmd.WritesWithoutApply,
                    ["Arguments"] = new JArray(cmd.Arguments.Concat(CommandRegistry.CommonArguments(cmd.Group)).Distinct().Select(a => (object)("-" + a)).ToArray()),
                    ["Class"] = cmd.GetType().FullName,
                });
                ctx.Log.Info(cmd.Group + "\t" + cmd.Name + "\t" + cmd.Summary);
            }
            json["Commands"] = list;
            // Commands whose declarations disagree with their name, left out so they cannot run.
            var rejected = new JArray();
            foreach (KeyValuePair<string, string> r in CommandRegistry.Rejected.OrderBy(r => r.Key))
            {
                rejected.Add(new JObject { ["RunName"] = r.Key, ["Reason"] = r.Value });
                ctx.Log.Warn("not registered: " + r.Value);
            }
            json["Rejected"] = rejected;
            ExportHelper.Save(ctx, null, json);
            return ExitCode.Success;
        }
    }
}
