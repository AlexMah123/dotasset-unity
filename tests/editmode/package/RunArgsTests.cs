using System.Collections.Generic;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // How the --args string of a run is split and read.
    public sealed class RunArgsTests
    {
        static List<string> Tokens(string raw, bool unclosed = false)
        {
            bool open;
            List<string> tokens = RunArgs.Tokenize(raw, out open);
            Assert.AreEqual(unclosed, open);
            return tokens;
        }

        [Test]
        public void SplitsOnWhitespaceOutsideQuotes()
        {
            Assert.AreEqual(new[] { "-apply", "-scandir=Assets/My Art", "-x=1" }, Tokens("  -apply   -scandir=\"Assets/My Art\"\t-x=1 "));
        }

        [Test]
        public void EscapedQuoteIsLiteralExceptWhereItEndsTheValue()
        {
            Assert.AreEqual(new[] { "-name=say \"hi\"" }, Tokens("-name=\"say \\\"hi\\\"\""));
            Assert.AreEqual(new[] { "-scandir=Assets\\Art\\", "-apply" }, Tokens("-scandir=\"Assets\\Art\\\" -apply"));
            Assert.AreEqual(new[] { "-scandir=Assets\\Art\\" }, Tokens("-scandir=\"Assets\\Art\\\""));
        }

        [Test]
        public void EmptyQuotesAreAnEmptyToken()
        {
            Assert.AreEqual(new[] { "-name=", "" }, Tokens("-name=\"\" \"\""));
        }

        [Test]
        public void UnclosedQuoteIsReported()
        {
            Assert.AreEqual(new[] { "-scandir=Assets -apply" }, Tokens("-scandir=\"Assets -apply", true));
            Assert.IsTrue(RunArgs.Parse("-scandir=\"Assets -apply").UnclosedQuote);
        }

        [Test]
        public void AtomicFalseTurnsTheSwitchOff()
        {
            RunArgs args = RunArgs.Parse("-atomic=false -apply");
            CollectionAssert.IsEmpty(args.NormalizeSwitches());
            Assert.IsFalse(args.Has("atomic"));
            Assert.IsTrue(args.Off("atomic"));
            Assert.IsFalse(args.Off("apply"));
            RunArgs on = RunArgs.Parse("-atomic=yes");
            on.NormalizeSwitches();
            Assert.IsTrue(on.Has("atomic"));
            Assert.IsFalse(on.Off("atomic"));
        }

        [Test]
        public void ParsesKeysCaseInsensitivelyLastValueWins()
        {
            RunArgs args = RunArgs.Parse("-Spec=a.json -apply -spec=b.json --deep -");
            Assert.AreEqual("b.json", args.Get("SPEC"));
            Assert.IsTrue(args.Has("apply"));
            Assert.IsNull(args.Get("apply"));
            Assert.AreEqual("x", args.Get("apply", "x"));
            Assert.IsTrue(args.Has("deep"));
            Assert.AreEqual(new[] { "Spec", "apply", "deep" }, args.Keys);
            Assert.AreEqual("a=b", RunArgs.Parse("-name=a=b").Get("name"));
        }

        [Test]
        public void TokensWithoutADashAreStray()
        {
            RunArgs args = RunArgs.Parse("apply -force");
            Assert.AreEqual(new[] { "apply" }, args.Stray);
            Assert.IsFalse(args.Has("apply"));
        }

        [Test]
        public void SwitchValuesAreNormalized()
        {
            RunArgs args = RunArgs.Parse("-apply=false -force=Yes -deep=1 -recursive=maybe -spec=0");
            Assert.AreEqual(new[] { "recursive" }, args.NormalizeSwitches());
            Assert.IsFalse(args.Has("apply"));
            Assert.IsTrue(args.Has("force"));
            Assert.IsNull(args.Get("force"));
            Assert.IsTrue(args.Has("deep"));
            Assert.AreEqual("0", args.Get("spec"));
            CollectionAssert.DoesNotContain(args.Keys, "apply");
        }

        [Test]
        public void IntegerArgumentsMustBeWholeNumbers()
        {
            Assert.AreEqual(new[] { "maxarray", "maxstring" }, RunArgs.Parse("-maxarray=-1 -maxstring -maxsize=20").BadIntegers());
            Assert.AreEqual(new string[0], RunArgs.Parse("-maxarray=0 -maxstring=5").BadIntegers());
            Assert.AreEqual(new[] { "maxstring" }, RunArgs.Parse("-maxstring=").BadIntegers());
            Assert.AreEqual(7, RunArgs.Parse("-n=x").GetInt("n", 7));
            Assert.AreEqual(1.5f, RunArgs.Parse("-f=1.5").GetFloat("f", 0f));
        }

        [Test]
        public void ListsAreTrimmedWithoutEmpties()
        {
            Assert.AreEqual(new[] { "a", "b c" }, RunArgs.Parse("-list=\" a, ,b c,\"").GetList("list"));
            Assert.AreEqual(new string[0], RunArgs.SplitList(null));
        }
    }
}
