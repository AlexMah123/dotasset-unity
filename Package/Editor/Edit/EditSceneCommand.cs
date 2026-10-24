using System.Collections.Generic;

namespace AssetOps
{
    // Edits scenes with the same ops as EditPrefab. A scene open in the editor is edited in place on one undo
    // group: a failed or dry run reverts it, an applied run is one Ctrl+Z. A closed scene opens additively and
    // closes again. The scene is saved only when every op of the target succeeded.
    // Usage: assetops.py EditScene --args '-spec="C:/temp/scene.json" -apply'
    // Docs/Edit.md
    public sealed class EditSceneCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Scene GameObjects, components, properties and prefab instances, all or nothing per scene"; }
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
            return HierarchyEditor.RunSpec<SceneSession>(ctx, "scene",
                (string path, out string error) => SceneSession.Open(path, ctx.RunName, out error),
                session => new HierarchyEditor(session.Scene, session.UseUndo, ctx.Log));
        }
    }
}
