using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AssetWorkbench
{
    // Every registered run with its group, summary and arguments, including commands from your own assemblies.
    // The wrapper's --list prints it.
    // Usage: uaw.py CommandCatalogExport      (or: uaw.py --list)
    // Docs/AI-Guide.md
    public sealed class CommandCatalogExportCommand : WorkbenchCommand
    {
        public override string Summary
        {
            get { return "Every registered run with its group, summary and arguments"; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JObject json = ExportHelper.Header(ctx, null);
            var list = new JArray();
            foreach (WorkbenchCommand cmd in CommandRegistry.All.Values.OrderBy(c => c.Group).ThenBy(c => c.Name))
            {
                list.Add(new JObject
                {
                    ["RunName"] = cmd.Name,
                    ["Group"] = cmd.Group.ToString(),
                    ["Summary"] = cmd.Summary,
                    ["Arguments"] = new JArray(cmd.Arguments.Concat(CommandRegistry.CommonArguments(cmd.Group)).Distinct().Select(a => (object)("-" + a)).ToArray()),
                    ["Class"] = cmd.GetType().FullName,
                });
                ctx.Log.Info(cmd.Group + "\t" + cmd.Name + "\t" + cmd.Summary);
            }
            json["Commands"] = list;
            ExportHelper.Save(ctx, null, json);
            return ExitCode.Success;
        }
    }
}
