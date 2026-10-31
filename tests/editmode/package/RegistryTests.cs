using NUnit.Framework;

namespace DotAsset.Tests
{
    // A command's declared group and WritesWithoutApply must agree with its name, or the registry leaves it out.
    public sealed class RegistryTests
    {
        // The registry builds only commands with a public parameterless constructor, the probes have internal ones.
        public abstract class Probe : DotAssetCommand
        {
            public override string Summary
            {
                get { return "test only"; }
            }

            public override ExitCode Execute(RunContext ctx)
            {
                return ExitCode.Failed;
            }
        }

        sealed class Declared : Probe
        {
            internal Declared()
            {
            }

            public override string Name
            {
                get { return "CreateDeclaredProbe"; }
            }

            public override bool WritesWithoutApply
            {
                get { return true; }
            }
        }

        sealed class Undeclared : Probe
        {
            internal Undeclared()
            {
            }

            public override string Name
            {
                get { return "CreateUndeclaredProbe"; }
            }
        }

        sealed class MigrateThatWrites : Probe
        {
            internal MigrateThatWrites()
            {
            }

            public override string Name
            {
                get { return "MoveProbe"; }
            }

            public override bool WritesWithoutApply
            {
                get { return true; }
            }
        }

        sealed class WrongGroup : Probe
        {
            internal WrongGroup()
            {
            }

            public override string Name
            {
                get { return "MoveProbe"; }
            }

            public override CommandGroup Group
            {
                get { return CommandGroup.Edit; }
            }
        }

        sealed class RightGroup : Probe
        {
            internal RightGroup()
            {
            }

            public override string Name
            {
                get { return "EditProbe"; }
            }

            public override CommandGroup Group
            {
                get { return CommandGroup.Edit; }
            }
        }

        [Test]
        public void DeclarationsThatMatchTheNameAreAccepted()
        {
            Assert.IsNull(CommandRegistry.Refusal(new Declared()));
            Assert.IsNull(CommandRegistry.Refusal(new RightGroup()));
        }

        [Test]
        public void ImportRunMustDeclareItWritesWithoutApply()
        {
            StringAssert.Contains("WritesWithoutApply", CommandRegistry.Refusal(new Undeclared()));
        }

        [Test]
        public void OnlyImportRunsMayWriteWithoutApply()
        {
            StringAssert.Contains("only Import runs", CommandRegistry.Refusal(new MigrateThatWrites()));
        }

        [Test]
        public void GroupMustMatchTheName()
        {
            StringAssert.Contains("its name makes it Migrate", CommandRegistry.Refusal(new WrongGroup()));
        }

        [Test]
        public void EveryRegisteredRunAgrees()
        {
            foreach (DotAssetCommand cmd in CommandRegistry.All.Values)
            {
                Assert.AreEqual(cmd.Group == CommandGroup.Import, cmd.WritesWithoutApply, cmd.Name);
            }
            Assert.IsTrue(CommandRegistry.Find("CreateAsset").WritesWithoutApply);
        }
    }
}
