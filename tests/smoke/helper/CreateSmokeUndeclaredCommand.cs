namespace DotAsset.Smoke
{
    // Smoke test only, installed into the test project as an embedded package by tests/smoke/run_smoke.py.
    // Named like an Import run but does not declare WritesWithoutApply, so the registry must refuse it.
    // Usage: dotasset.py CreateSmokeUndeclared      (exit 2, "not registered")
    public sealed class CreateSmokeUndeclaredCommand : DotAssetCommand
    {
        public override string Summary
        {
            get { return "Smoke test only: an Import run that does not declare WritesWithoutApply"; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            ctx.Log.Error("CreateSmokeUndeclared ran, the registry should have refused it");
            return ExitCode.Failed;
        }
    }
}
