using System.Linq;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // A failed run's Error, the error: line of --agent and of an MCP tool result, names the run and its failed
    // targets once, followed by the first error. That first error is often FailTarget's own line for the target.
    public sealed class RunErrorTests : RunFixture
    {
        static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, System.StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        [Test]
        public void TheFailTargetLineIsNotRepeated()
        {
            string from = Folder + "/Missing.txt";
            string to = Folder + "/Moved.txt";
            RunResult r = Run("MoveAsset", "-from=" + from + " -to=" + to);
            Assert.AreEqual(1, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            Assert.AreEqual("MoveAsset failed for " + from + " -> " + to + ". Reason: nothing at " + from, r.Error);
        }

        [Test]
        public void AnOpErrorFollowsTheFailedTarget()
        {
            RunResult r = Run("EditPrefab", Spec(@"{'Targets': [{'AssetPath': '" + Crate + @"', 'Ops': [
                {'Op': 'Rename', 'Path': 'Crate/DoesNotExist', 'NewName': 'X'}]}]}") + " -apply");
            Assert.AreEqual(1, r.ExitCode, string.Join("\n", r.Lines.ToArray()));
            StringAssert.StartsWith("EditPrefab failed for " + Crate + ". ", r.Error);
            Assert.AreEqual(1, Count(r.Error, "failed for"), r.Error);
        }
    }
}
