using System.Text.RegularExpressions;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // RenameField's YAML passes on fixture text: which keys and prefab overrides it renames, and which it leaves.
    public sealed class RenameFieldTests
    {
        const string Script = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string Prefab = "cccccccccccccccccccccccccccccccc";
        const string Variant = "dddddddddddddddddddddddddddddddd";
        const string Scene = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string Head = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        static string Component(long id, string script, string fields)
        {
            return "--- !u!114 &" + id + "\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_Enabled: 1\n"
                + "  m_Script: {fileID: 11500000, guid: " + script + ", type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n" + fields;
        }

        static string Instance(long id, string source, params string[] overrides)
        {
            string text = "--- !u!1001 &" + id + "\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n"
                + "    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications:\n";
            foreach (string o in overrides)
            {
                text += o;
            }
            return text + "    m_RemovedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: " + source + ", type: 3}\n";
        }

        static string Override(long target, string guid, string path)
        {
            return "    - target: {fileID: " + target + ", guid: " + guid + ", type: 3}\n      propertyPath: " + path
                + "\n      value: 7\n      objectReference: {fileID: 0}\n";
        }

        static Regex Ref
        {
            get { return RenameFieldCommand.ScriptRef(new[] { Script }); }
        }

        static string Rewrite(string text, RenameFieldCommand.Targets targets, out int hits)
        {
            string conflict;
            string result = RenameFieldCommand.Rewrite(text, "hp", "health", Ref, targets, out hits, out conflict);
            Assert.IsNull(conflict);
            return result;
        }

        [Test]
        public void RenamesTopLevelKeyOfTheScriptOnly()
        {
            string text = Head + Component(11400000, Script, "  hp: 5\n  stats:\n    hp: 1\n  hpMax: 9\n")
                + Component(11400002, Other, "  hp: 3\n");
            int hits;
            string result = Rewrite(text, new RenameFieldCommand.Targets(), out hits);
            Assert.AreEqual(1, hits);
            Assert.AreEqual(text.Replace("  hp: 5\n", "  health: 5\n"), result);
        }

        [Test]
        public void KeepsLineEndingsAndTheTextBeforeTheFirstDocument()
        {
            string text = (Head + Component(11400000, Script, "  hp: 5\n")).Replace("\n", "\r\n");
            int hits;
            string result = Rewrite(text, new RenameFieldCommand.Targets(), out hits);
            Assert.AreEqual(1, hits);
            Assert.AreEqual(text.Replace("  hp: 5\r\n", "  health: 5\r\n"), result);
        }

        [Test]
        public void RefusesAnObjectThatAlreadyHasTheNewKey()
        {
            string text = Head + Component(11400000, Script, "  hp: 5\n  health: 6\n");
            int hits;
            string conflict;
            string result = RenameFieldCommand.Rewrite(text, "hp", "health", Ref, new RenameFieldCommand.Targets(), out hits, out conflict);
            Assert.IsNull(result);
            Assert.AreEqual(0, hits);
            StringAssert.Contains("&11400000", conflict);
        }

        [Test]
        public void TextWithoutDocumentsIsLeftAlone()
        {
            int hits;
            Assert.AreEqual("hp: 5\n", Rewrite("hp: 5\n", new RenameFieldCommand.Targets(), out hits));
            Assert.AreEqual(0, hits);
        }

        [Test]
        public void RenamesOverridesThatTargetTheScriptsObjects()
        {
            var targets = new RenameFieldCommand.Targets();
            targets.Scan(Prefab, Head + Component(1234, Script, "  hp: 5\n") + Component(1235, Other, "  hp: 5\n"), Ref);
            targets.Follow();
            string text = Head + Instance(5000, Prefab,
                Override(1234, Prefab, "hp"),
                Override(1234, Prefab, "hp.x"),
                Override(1234, Prefab, "hpMax"),
                Override(1235, Prefab, "hp"));
            int hits;
            string result = Rewrite(text, targets, out hits);
            Assert.AreEqual(2, hits);
            StringAssert.Contains("{fileID: 1234, guid: " + Prefab + ", type: 3}\n      propertyPath: health\n", result);
            StringAssert.Contains("propertyPath: health.x\n", result);
            StringAssert.Contains("propertyPath: hpMax\n", result);
            StringAssert.Contains("{fileID: 1235, guid: " + Prefab + ", type: 3}\n      propertyPath: hp\n", result);
        }

        [Test]
        public void FollowsVariantsAndNestedPrefabsInAnyOrder()
        {
            // The variant holds the prefab as instance &5000, so the component is (1234 ^ 5000) there. The scene
            // overrides it through its instance of the variant. The scene is scanned first.
            long inVariant = (1234L ^ 5000L) & long.MaxValue;
            var targets = new RenameFieldCommand.Targets();
            targets.Scan(Scene, Head + Instance(7000, Variant, Override(inVariant, Variant, "hp")), Ref);
            targets.Scan(Variant, Head + Instance(5000, Prefab), Ref);
            targets.Scan(Prefab, Head + Component(1234, Script, "  hp: 5\n"), Ref);
            targets.Follow();
            Assert.IsTrue(targets.Contains(Variant, inVariant));
            Assert.IsTrue(targets.Contains(Scene, (inVariant ^ 7000L) & long.MaxValue));
            int hits;
            string result = Rewrite(Head + Instance(7000, Variant, Override(inVariant, Variant, "hp")), targets, out hits);
            Assert.AreEqual(1, hits);
            StringAssert.Contains("propertyPath: health\n", result);
        }

        [Test]
        public void FollowsStrippedObjects()
        {
            // A stripped document stands for the source object it names, under its own fileID.
            var targets = new RenameFieldCommand.Targets();
            targets.Scan(Prefab, Head + Component(1234, Script, "  hp: 5\n"), Ref);
            targets.Scan(Variant, Head + "--- !u!114 &888 stripped\nMonoBehaviour:\n  m_CorrespondingSourceObject: {fileID: 1234, guid: "
                + Prefab + ", type: 3}\n  m_PrefabInstance: {fileID: 5000}\n", Ref);
            targets.Follow();
            Assert.IsTrue(targets.Contains(Variant, 888));
            Assert.IsFalse(targets.Contains(Variant, 1234));
        }

        [Test]
        public void LeavesAStrippedComponentsKeysAlone()
        {
            string text = Head + "--- !u!114 &888 stripped\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: " + Script + ", type: 3}\n  hp: 5\n";
            int hits;
            Assert.AreEqual(text, Rewrite(text, new RenameFieldCommand.Targets(), out hits));
            Assert.AreEqual(0, hits);
        }
    }
}
