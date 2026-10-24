using System.Collections.Generic;

namespace AssetOps
{
    // Edits prefabs in an isolated preview scene: add, delete, rename, move GameObjects, add and remove components,
    // set properties and references, add nested prefab instances, revert or unpack instances.
    // The prefab is saved only when every op of the target succeeded. Op reference: HierarchyEditor, Docs/Edit.md.
    // Usage: assetops.py EditPrefab --args '-spec="C:/temp/prefab.json" -apply'
    // Docs/Edit.md
    public sealed class EditPrefabCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Prefab GameObjects, components, properties and nested instances, all or nothing per prefab"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            return HierarchyEditor.RunSpec<PrefabSession>(ctx, "prefab", PrefabSession.Open,
                session => new HierarchyEditor(session.Root, session.Path, ctx.Log));
        }
    }
}
