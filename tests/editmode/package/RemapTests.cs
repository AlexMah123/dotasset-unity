using NUnit.Framework;

namespace DotAsset.Tests
{
    // The patterns RemapScript and RenameSerializeReferenceType rewrite with, on fixture text.
    public sealed class RemapTests
    {
        const string Old = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string New = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        static string Remap(string text, string fromFileId, long newFileId)
        {
            return RemapScriptCommand.Pattern(fromFileId, Old).Replace(text, RemapScriptCommand.Replacement(newFileId, New));
        }

        [Test]
        public void RepointsScriptReferencesOnly()
        {
            string text = "  m_Script: {fileID: 11500000, guid: " + Old + ", type: 3}\n"
                + "  m_Material: {fileID: 11500000, guid: " + Old + ", type: 3}\n"
                + "  m_Script: {fileID: 11500000, guid: " + Old + ", type: 2}\n"
                + "  m_Script: {fileID: 11500001, guid: " + Old + ", type: 3}\n";
            string result = Remap(text, "11500000", 11500000);
            Assert.AreEqual(text.Replace("m_Script: {fileID: 11500000, guid: " + Old + ", type: 3}", "m_Script: {fileID: 11500000, guid: " + New + ", type: 3}"), result);
            Assert.AreNotEqual(text, result);
        }

        [Test]
        public void RepointsAClassInsideADllByItsFileId()
        {
            string text = "  m_Script: {fileID: -765806418, guid: " + Old + ", type: 3}\n  m_Script: {fileID: 11500000, guid: " + Old + ", type: 3}\n";
            string result = Remap(text, "-765806418", 11500000);
            Assert.AreEqual("  m_Script: {fileID: 11500000, guid: " + New + ", type: 3}\n  m_Script: {fileID: 11500000, guid: " + Old + ", type: 3}\n", result);
        }

        [Test]
        public void ManagedEntryMatchesClassAndNamespaceExactly()
        {
            var pattern = RenameSerializeReferenceTypeCommand.Pattern(null, "Game", "Old");
            string to = ManagedTypes.YamlEntry("Game.Runtime", "Game", "New");
            Assert.AreEqual("      type: {class: New, ns: Game, asm: Game.Runtime}\n",
                pattern.Replace("      type: {class: Old, ns: Game, asm: Assembly-CSharp}\n", to));
            Assert.IsFalse(pattern.IsMatch("type: {class: OldThing, ns: Game, asm: Assembly-CSharp}"));
            Assert.IsFalse(pattern.IsMatch("type: {class: Old, ns: Game.Sub, asm: Assembly-CSharp}"));
            Assert.IsFalse(pattern.IsMatch("type: {class: Old, ns: , asm: Assembly-CSharp}"));
        }

        [Test]
        public void ManagedEntryKeepsToTheAssemblyWhenOneIsGiven()
        {
            var pattern = RenameSerializeReferenceTypeCommand.Pattern("Game.Runtime", "", "Old");
            Assert.IsTrue(pattern.IsMatch("type: {class: Old, ns: , asm: Game.Runtime}"));
            Assert.IsFalse(pattern.IsMatch("type: {class: Old, ns: , asm: Assembly-CSharp}"));
        }

        [Test]
        public void ParsesTypeNames()
        {
            string asm, ns, cls;
            Assert.IsTrue(ManagedTypes.Parse("Game.Runtime My.Game.Outer/Inner", out asm, out ns, out cls));
            Assert.AreEqual(new[] { "Game.Runtime", "My.Game", "Outer/Inner" }, new[] { asm, ns, cls });
            Assert.IsTrue(ManagedTypes.Parse("Plain", out asm, out ns, out cls));
            Assert.AreEqual(new[] { null, "", "Plain" }, new[] { asm, ns, cls });
            Assert.IsFalse(ManagedTypes.Parse("Game Bad,Name", out asm, out ns, out cls));
            Assert.IsFalse(ManagedTypes.Parse("", out asm, out ns, out cls));
        }
    }
}
