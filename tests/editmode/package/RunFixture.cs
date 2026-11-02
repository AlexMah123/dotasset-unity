using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DotAsset.Tests
{
    // Runs a command in this process the way the queue does, on a folder CreateAsset fills before every test with
    // the smoke test's assets: Red.mat, Crate.prefab using it, the HeavyCrate variant, Door.controller and a scene
    // holding a Crate. Specs are written with ' for " to keep them readable.
    public abstract class RunFixture
    {
        protected const string Folder = "Assets/DotAssetRunTest";
        protected const string Red = Folder + "/Red.mat";
        protected const string Crate = Folder + "/Crate.prefab";
        protected const string Heavy = Folder + "/HeavyCrate.prefab";
        protected const string Door = Folder + "/Door.controller";
        protected const string Scene = Folder + "/Smoke.unity";
        readonly List<string> specs = new List<string>();

        [SetUp]
        public void CreateAssets()
        {
            Ok("CreateAsset", Spec(@"{'Assets': [
                {'Kind': 'Folder', 'Path': '" + Folder + @"'},
                {'Kind': 'Material', 'Path': '" + Red + @"', 'Properties': {}},
                {'Kind': 'Prefab', 'Path': '" + Crate + @"', 'Root': 'Crate',
                 'Components': ['BoxCollider', {'Type': 'Rigidbody', 'Properties': {'m_Mass': 5}}],
                 'Ops': [{'Op': 'AddGameObject', 'Parent': 'Crate', 'Name': 'Mesh', 'Components': ['MeshFilter', 'MeshRenderer']},
                         {'Op': 'Set', 'Path': 'Crate/Mesh', 'Component': 'MeshRenderer', 'Properties': {'m_Materials': ['" + Red + @"']}}]},
                {'Kind': 'PrefabVariant', 'Path': '" + Heavy + @"', 'Base': '" + Crate + @"',
                 'Ops': [{'Op': 'Set', 'Path': 'HeavyCrate', 'Component': 'Rigidbody', 'Properties': {'m_Mass': 50}}]},
                {'Kind': 'AnimatorController', 'Path': '" + Door + @"',
                 'Ops': [{'Op': 'AddParameter', 'Name': 'Open', 'Type': 'Bool'},
                         {'Op': 'AddState', 'Path': 'Closed', 'Default': true},
                         {'Op': 'AddState', 'Path': 'Opened'},
                         {'Op': 'AddTransition', 'From': 'Closed', 'To': 'Opened', 'Conditions': [{'Parameter': 'Open', 'Mode': 'If'}]}]},
                {'Kind': 'Scene', 'Path': '" + Scene + @"',
                 'Ops': [{'Op': 'AddPrefabInstance', 'Prefab': '" + Crate + @"', 'Name': 'Crate'}]}]}"));
        }

        [TearDown]
        public void DeleteAssets()
        {
            foreach (string s in specs)
            {
                File.Delete(s);
            }
            specs.Clear();
            AssetDatabase.DeleteAsset(Folder);
        }

        protected string Spec(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "DotAssetRunTest" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json.Replace('\'', '"'));
            specs.Add(path);
            return "-spec=\"" + path.Replace('\\', '/') + "\"";
        }

        protected static RunResult Run(string run, string args, params string[] assets)
        {
            // A run logs its errors to the Console too, which would fail the test on its own. Set per run, the
            // setting made in SetUp does not reach the test body.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            return RunDispatcher.Run(run, assets, args, false);
        }

        protected static RunResult Ok(string run, string args, params string[] assets)
        {
            RunResult r = Run(run, args, assets);
            Assert.AreEqual(0, r.ExitCode, run + " " + args + "\n" + string.Join("\n", r.Lines.ToArray()));
            return r;
        }

        protected static string Absolute(string path)
        {
            return DotAssetPaths.Absolute(path);
        }

        // The export file a run wrote, checked to name the run and this package's version.
        protected static JObject Export(string run, string asset, string args = "")
        {
            RunResult r = asset == null ? Ok(run, args) : Ok(run, args, asset);
            Assert.IsNotEmpty(r.Outputs, run + " wrote no output");
            JObject export = JObject.Parse(File.ReadAllText(Absolute(r.Outputs.Last())));
            Assert.AreEqual(run, (string)export["ExportType"]);
            Assert.AreEqual(DotAssetVersion.Version, (string)export["ExporterVersion"]);
            return export;
        }

        // An audit's report, after checking the exit code: 3 with issues, 0 without, AnyIssues for either.
        protected const int AnyIssues = -1;

        protected static JObject Report(string run, int code, string args, params string[] assets)
        {
            RunResult r = Run(run, args, assets);
            string lines = run + " " + args + "\n" + string.Join("\n", r.Lines.ToArray());
            if (code == AnyIssues)
            {
                Assert.IsTrue(r.ExitCode == 0 || r.ExitCode == 3, lines);
            }
            else
            {
                Assert.AreEqual(code, r.ExitCode, lines);
            }
            string report = r.Outputs.LastOrDefault(o => !o.EndsWith(".fix.json"));
            Assert.IsNotNull(report, run + " wrote no report");
            return JObject.Parse(File.ReadAllText(Absolute(report)));
        }

        protected static bool HasIssue(JObject report, string rule, string asset)
        {
            return (report["Issues"] ?? new JArray()).Any(i => (string)i["Rule"] == rule && (string)i["Asset"] == asset);
        }

        protected static string FixSpec(JObject report)
        {
            Assert.IsNotNull((string)report["FixSpec"], "the report has no fix spec");
            return "-spec=\"" + Absolute((string)report["FixSpec"]).Replace('\\', '/') + "\"";
        }

        protected static bool Planned(RunResult r, string key, string value)
        {
            return r.Plan.Any(p => (string)p[key] == value);
        }

        protected static JToken Node(JObject export, string path)
        {
            return (export["Hierarchy"] ?? new JArray()).FirstOrDefault(n => (string)n["Path"] == path);
        }

        protected static JToken Component(JObject export, string path, string type)
        {
            JToken node = Node(export, path);
            return node == null ? null : (node["Components"] ?? new JArray()).FirstOrDefault(c => (string)c["Type"] == type);
        }

        protected static string Text(string asset)
        {
            return File.ReadAllText(DotAssetPaths.Absolute(asset));
        }

        // Rewrites an asset's YAML on disk and imports it, as if it was saved that way.
        protected static void Rewrite(string asset, string from, string to)
        {
            string text = Text(asset);
            StringAssert.Contains(from, text);
            File.WriteAllText(DotAssetPaths.Absolute(asset), text.Replace(from, to));
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate);
        }

        protected static string MaterialOf(string prefab)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            return AssetDatabase.GetAssetPath(root.transform.Find("Mesh").GetComponent<MeshRenderer>().sharedMaterial);
        }

        protected static void WritePng(string asset)
        {
            var texture = new Texture2D(4, 4);
            File.WriteAllBytes(DotAssetPaths.Absolute(asset), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(asset);
        }

        // A component's script, in Runtime/ because an editor-only assembly's MonoBehaviour cannot be added.
        protected static string ScriptPath<T>()
        {
            return "Packages/com.alexmah.dotasset.tests/Runtime/" + typeof(T).Name + ".cs";
        }
    }
}
