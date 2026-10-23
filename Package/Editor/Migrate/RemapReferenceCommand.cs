using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Assetops
{
    // Points every reference to one asset at another, across prefabs, scenes, materials, ScriptableObjects and
    // controllers under -scandir. Sub-asset references map by name and type (Hero.fbx's mesh "Body" to the new
    // model's "Body"). Prefab instances keep inheriting: only overridden values are rewritten there, the source
    // prefab is fixed as its own asset. Each referencing asset is all or nothing.
    // Usage: assetops.py RemapReference --args '-from=Assets/Mat/Old.mat -to=Assets/Mat/New.mat -apply'
    //        assetops.py RemapReference --args '-spec="C:/temp/remap.json" -apply'    {"Pairs": [{"From", "To"}]}
    // Docs/Migrate.md
    public sealed class RemapReferenceCommand : AssetopsCommand
    {
        public override string Summary
        {
            get { return "Repoints every reference from one asset to another across the project"; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "from", "to", "spec", "scandir", "keepinstances" }; }
        }

        sealed class Mapping
        {
            public string From;
            public string To;
            public readonly Dictionary<Object, Object> Objects = new Dictionary<Object, Object>();
            public readonly HashSet<Object> Unmapped = new HashSet<Object>();
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<KeyValuePair<string, string>> pairs;
            if (!MoveAssetCommand.Pairs(ctx, "Pairs", out pairs))
            {
                return ExitCode.Refused;
            }
            var mappings = new List<Mapping>();
            foreach (var pair in pairs)
            {
                Mapping m;
                string error;
                if (!BuildMapping(pair.Key, pair.Value, out m, out error))
                {
                    ctx.Log.Error(error);
                    return ExitCode.Refused;
                }
                mappings.Add(m);
            }

            var roots = AssetScan.Roots(ctx.Args);
            ctx.Log.Info("indexing references under " + string.Join(", ", roots));
            var index = AssetScan.Referencers(roots, ctx);
            var candidates = mappings.SelectMany(m => AssetScan.ReferencersOf(index, m.From)).Distinct().OrderBy(p => p).ToList();
            if (candidates.Count == 0)
            {
                ctx.Log.Info("nothing under " + string.Join(", ", roots) + " references " + string.Join(", ", mappings.Select(m => m.From)));
                return ExitCode.Success;
            }

            var all = new Dictionary<Object, Object>();
            var unmapped = new HashSet<Object>();
            foreach (Mapping m in mappings)
            {
                foreach (var kv in m.Objects)
                {
                    all[kv.Key] = kv.Value;
                }
                unmapped.UnionWith(m.Unmapped);
            }

            // Prefab to prefab: instances are references too, and swap through Unity's Replace Prefab Asset,
            // which carries overrides across by name. -keepinstances leaves them alone.
            var swaps = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            if (!ctx.Args.Has("keepinstances"))
            {
                foreach (Mapping m in mappings)
                {
                    var toRoot = AssetDatabase.LoadMainAssetAtPath(m.To) as GameObject;
                    if (toRoot != null && AssetDatabase.LoadMainAssetAtPath(m.From) is GameObject)
                    {
                        swaps[m.From] = toRoot;
                    }
                }
            }

            int changedAssets = 0;
            int changedRefs = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i];
                ctx.Progress(i, candidates.Count, path);
                ctx.BeginTarget();
                int count;
                string error;
                bool ok = Remap(ctx, path, all, unmapped, swaps, out count, out error);
                if (!ok)
                {
                    ctx.FailTarget(path, error ?? ctx.TargetError);
                    continue;
                }
                if (ctx.DryRun && ctx.TargetFailed())
                {
                    ctx.FailTarget(path, ctx.TargetError);
                    continue;
                }
                if (!ctx.DryRun && count > 0)
                {
                    ctx.WarnIfSavedWithErrors(path);
                }
                if (count > 0)
                {
                    changedAssets++;
                    changedRefs += count;
                    ctx.Log.Info((ctx.DryRun ? "would repoint " : "repointed ") + count + " reference(s) in " + path);
                }
            }
            ctx.Log.Info((ctx.DryRun ? "would change " : "changed ") + changedRefs + " reference(s) in " + changedAssets + " asset(s)");
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        static bool BuildMapping(string from, string to, out Mapping m, out string error)
        {
            m = new Mapping { From = from, To = to };
            error = null;
            if (from == to)
            {
                error = "-from and -to are the same asset";
                return false;
            }
            Object[] fromObjects = AssetDatabase.LoadAllAssetsAtPath(from).Where(o => o != null).ToArray();
            Object[] toObjects = AssetDatabase.LoadAllAssetsAtPath(to).Where(o => o != null).ToArray();
            if (fromObjects.Length == 0)
            {
                error = "nothing at " + from;
                return false;
            }
            if (toObjects.Length == 0)
            {
                error = "nothing at " + to;
                return false;
            }
            Object fromMain = AssetDatabase.LoadMainAssetAtPath(from);
            Object toMain = AssetDatabase.LoadMainAssetAtPath(to);
            if (fromMain.GetType() != toMain.GetType() && !(fromMain is GameObject && toMain is GameObject))
            {
                error = from + " is a " + fromMain.GetType().Name + " and " + to + " a " + toMain.GetType().Name + ", references would not fit";
                return false;
            }
            m.Objects[fromMain] = toMain;
            foreach (Object o in fromObjects)
            {
                if (o == fromMain)
                {
                    continue;
                }
                // Components and child GameObjects of a prefab map by hierarchy path, sub-assets by name and type.
                Object match = null;
                var c = o as Component;
                var go = o as GameObject;
                if ((c != null || go != null) && toMain is GameObject)
                {
                    GameObject sourceGo = c != null ? c.gameObject : go;
                    string rel = RelativePath(sourceGo, (GameObject)fromMain);
                    GameObject targetGo = rel == null ? null : Find((GameObject)toMain, rel);
                    if (targetGo != null)
                    {
                        match = c == null ? (Object)targetGo : targetGo.GetComponent(c.GetType());
                    }
                }
                else
                {
                    match = toObjects.FirstOrDefault(t => t.name == o.name && t.GetType() == o.GetType() && t != toMain);
                }
                if (match != null)
                {
                    m.Objects[o] = match;
                }
                else
                {
                    m.Unmapped.Add(o);
                }
            }
            return true;
        }

        static string RelativePath(GameObject go, GameObject root)
        {
            var names = new List<string>();
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (t == root.transform)
                {
                    names.Reverse();
                    return string.Join("/", names);
                }
                names.Add(t.name);
            }
            return null;
        }

        static GameObject Find(GameObject root, string rel)
        {
            if (rel.Length == 0)
            {
                return root;
            }
            Transform t = root.transform.Find(rel);
            return t != null ? t.gameObject : null;
        }

        static bool Remap(RunContext ctx, string path, Dictionary<Object, Object> map, HashSet<Object> unmapped, Dictionary<string, GameObject> swaps, out int count, out string error)
        {
            count = 0;
            error = null;
            Type main = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                using (PrefabSession session = PrefabSession.Open(path, out error))
                {
                    if (session == null)
                    {
                        return false;
                    }
                    int swapped = SwapInstances(ctx, path, new[] { session.Root }, swaps, false);
                    if (!RemapObjects(HierarchyObjects(session.Root), map, unmapped, false, !ctx.DryRun, out count, out error))
                    {
                        return false;
                    }
                    count += swapped;
                    if (count == 0 || ctx.DryRun)
                    {
                        return true;
                    }
                    return ctx.ReadyToSave(out error) && session.Save(out error);
                }
            }
            if (main == typeof(SceneAsset))
            {
                using (SceneSession session = SceneSession.Open(path, ctx.RunName, out error))
                {
                    if (session == null)
                    {
                        return false;
                    }
                    int swapped = SwapInstances(ctx, path, session.Scene.GetRootGameObjects(), swaps, session.UseUndo);
                    var objects = session.Scene.GetRootGameObjects().SelectMany(HierarchyObjects).ToList();
                    if (!RemapObjects(objects, map, unmapped, session.UseUndo, !ctx.DryRun, out count, out error))
                    {
                        return false;
                    }
                    count += swapped;
                    if (count == 0 || ctx.DryRun)
                    {
                        return true;
                    }
                    return ctx.ReadyToSave(out error) && session.Save(out error);
                }
            }
            if (main == typeof(GameObject))
            {
                // A model: its references live in the importer, e.g. remapped materials.
                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                {
                    return true;
                }
                if (!RemapObjects(new Object[] { importer }, map, unmapped, false, !ctx.DryRun, out count, out error))
                {
                    return false;
                }
                if (count > 0 && !ctx.DryRun)
                {
                    if (!ctx.ReadyToSave(out error))
                    {
                        return false;
                    }
                    importer.SaveAndReimport();
                }
                return true;
            }

            // Nothing is applied unless every reference in the file maps, so a failure leaves the asset untouched.
            Object[] assetObjects = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o != null).ToArray();
            if (!RemapObjects(assetObjects, map, unmapped, false, !ctx.DryRun, out count, out error))
            {
                return false;
            }
            if (count > 0 && !ctx.DryRun)
            {
                if (!ctx.ReadyToSave(out error))
                {
                    return false;
                }
                foreach (Object o in assetObjects)
                {
                    EditorUtility.SetDirty(o);
                }
                AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(path));
            }
            return true;
        }

        static int SwapInstances(RunContext ctx, string path, IEnumerable<GameObject> roots, Dictionary<string, GameObject> swaps, bool useUndo)
        {
            if (swaps.Count == 0)
            {
                return 0;
            }
            int swapped = 0;
            var instanceRoots = roots.SelectMany(HierarchyPath.DepthFirst)
                .Where(go => PrefabUtility.IsAnyPrefabInstanceRoot(go))
                .ToList();
            foreach (GameObject go in instanceRoots)
            {
                if (go == null)
                {
                    continue;
                }
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                GameObject target;
                if (source == null || !swaps.TryGetValue(source, out target))
                {
                    continue;
                }
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                {
                    ctx.Log.Warn(path + ": " + HierarchyPath.Of(go) + " is an instance of " + source + " nested inside another instance, swap it in that prefab");
                    continue;
                }
                ctx.Log.Info(path + ": " + (ctx.DryRun ? "would swap " : "swapping ") + "instance " + HierarchyPath.Of(go) + " to " + AssetDatabase.GetAssetPath(target));
                if (!ctx.DryRun)
                {
                    PrefabUtility.ReplacePrefabAssetOfPrefabInstance(go, target, useUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction);
                }
                swapped++;
            }
            return swapped;
        }

        static IEnumerable<Object> HierarchyObjects(GameObject root)
        {
            foreach (GameObject go in HierarchyPath.DepthFirst(root))
            {
                yield return go;
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c != null)
                    {
                        yield return c;
                    }
                }
            }
        }

        // Rewrites matching references on every object, collecting all changes before applying any.
        // With apply false it only counts.
        static bool RemapObjects(IEnumerable<Object> objects, Dictionary<Object, Object> map, HashSet<Object> unmapped, bool useUndo, bool apply, out int count, out string error)
        {
            count = 0;
            error = null;
            var pending = new List<SerializedObject>();
            foreach (Object o in objects)
            {
                bool inInstance = PrefabUtility.IsPartOfPrefabInstance(o);
                var so = new SerializedObject(o);
                SerializedProperty it = so.GetIterator();
                bool touched = false;
                while (it.Next(true))
                {
                    // Prefab bookkeeping (m_CorrespondingSourceObject, m_PrefabInstance) and m_Script are not references to repoint.
                    if (it.propertyType != SerializedPropertyType.ObjectReference || (it.depth == 0 && PropertyIO.Skip.Contains(it.name)))
                    {
                        continue;
                    }
                    Object value = it.objectReferenceValue;
                    if (value == null)
                    {
                        continue;
                    }
                    if (unmapped.Contains(value))
                    {
                        error = PropertyIO.Describe(o) + "." + it.propertyPath + " uses " + value.name + " (" + value.GetType().Name + "), which has no counterpart in the new asset";
                        return false;
                    }
                    Object replacement;
                    if (!map.TryGetValue(value, out replacement))
                    {
                        continue;
                    }
                    if (inInstance && !it.prefabOverride)
                    {
                        // Inherited from the source prefab, which is remapped as its own asset.
                        continue;
                    }
                    it.objectReferenceValue = replacement;
                    if (it.objectReferenceValue != replacement)
                    {
                        error = PropertyIO.Describe(o) + "." + it.propertyPath + " does not accept " + replacement.GetType().Name;
                        return false;
                    }
                    count++;
                    touched = true;
                }
                if (touched)
                {
                    pending.Add(so);
                }
            }
            if (apply)
            {
                foreach (SerializedObject so in pending)
                {
                    PropertyIO.Commit(so, useUndo);
                }
            }
            return true;
        }
    }
}
