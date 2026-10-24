using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Points every reference to one asset at another, across prefabs, scenes, materials, ScriptableObjects and
    // controllers under -scandir. Sub-asset references map by name and type (Hero.fbx's mesh "Body" to the new
    // model's "Body"). Prefab instances keep inheriting: only overridden values are rewritten there, the source
    // prefab is fixed as its own asset. Each referencing asset is all or nothing.
    // Usage: assetops.py RemapReference --args '-from=Assets/Mat/Old.mat -to=Assets/Mat/New.mat -apply'
    //        assetops.py RemapReference --args '-spec="C:/temp/remap.json" -apply'    {"Pairs": [{"From", "To"}]}
    // Docs/Migrate.md
    public sealed class RemapReferenceCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Repoints every reference from one asset to another across the project"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
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
            public readonly HashSet<string> Referencers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            foreach (Mapping m in mappings)
            {
                m.Referencers.UnionWith(AssetScan.ReferencersOf(index, m.From));
                // The new asset, and the prefabs and models it nests or is a variant of, keep pointing at the old one:
                // repointing them would nest the new prefab inside itself. Plain field references may form a cycle.
                m.Protected.Add(m.To);
                foreach (string dep in AssetDatabase.GetDependencies(m.To, true))
                {
                    if (dep != m.To && AssetDatabase.GetMainAssetTypeAtPath(dep) == typeof(GameObject))
                    {
                        m.Protected.Add(dep);
                    }
                }
            }
            if (candidates.Count == 0)
            {
                ctx.Log.Info("nothing under " + string.Join(", ", roots) + " references " + string.Join(", ", mappings.Select(m => m.From)));
                return ExitCode.Success;
            }

            // Prefab to prefab: instances are references too, and swap through Unity's Replace Prefab Asset,
            // which carries overrides across by name. -keepinstances leaves them alone.
            var allSwaps = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            if (!ctx.Args.Has("keepinstances"))
            {
                foreach (Mapping m in mappings)
                {
                    var toRoot = AssetDatabase.LoadMainAssetAtPath(m.To) as GameObject;
                    if (toRoot != null && AssetDatabase.LoadMainAssetAtPath(m.From) is GameObject)
                    {
                        allSwaps[m.From] = toRoot;
                    }
                }
            }

            int changedAssets = 0;
            int changedRefs = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i];
                ctx.Progress(i, candidates.Count, path);
                // Only the pairs this file references, minus those it must keep (see Protected).
                var active = new List<Mapping>();
                foreach (Mapping m in mappings.Where(x => x.Referencers.Contains(path)))
                {
                    if (m.Protected.Contains(path))
                    {
                        ctx.Log.Warn(path + " is " + (path == m.To ? "the new asset" : "a prefab " + m.To + " is built from") + ", left pointing at " + m.From);
                        continue;
                    }
                    active.Add(m);
                }
                if (active.Count == 0)
                {
                    continue;
                }
                var all = new Dictionary<Object, Object>();
                var unmapped = new HashSet<Object>();
                var swaps = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                foreach (Mapping m in active)
                {
                    foreach (var kv in m.Objects)
                    {
                        all[kv.Key] = kv.Value;
                    }
                    unmapped.UnionWith(m.Unmapped);
                    GameObject toRoot;
                    if (allSwaps.TryGetValue(m.From, out toRoot))
                    {
                        swaps[m.From] = toRoot;
                    }
                }

                ctx.BeginTarget();
                int count;
                string error;
                bool ok;
                try
                {
                    ok = Remap(ctx, path, all, unmapped, swaps, out count, out error);
                }
                catch (Exception e)
                {
                    ok = false;
                    count = 0;
                    error = e.GetType().Name + ": " + e.Message;
                }
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
                    List<KeyValuePair<string, int>> steps = RelativeSteps(sourceGo, (GameObject)fromMain);
                    GameObject targetGo = steps == null ? null : Follow((GameObject)toMain, steps);
                    if (targetGo != null)
                    {
                        match = c == null ? (Object)targetGo : SameComponent(targetGo, c);
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

        // The path from root to go as (name, which of the same-named siblings), so "Slot", "Slot" map one to one.
        static List<KeyValuePair<string, int>> RelativeSteps(GameObject go, GameObject root)
        {
            var steps = new List<KeyValuePair<string, int>>();
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (t == root.transform)
                {
                    steps.Reverse();
                    return steps;
                }
                int occurrence = 0;
                for (int i = 0; i < t.GetSiblingIndex(); i++)
                {
                    if (t.parent.GetChild(i).name == t.name)
                    {
                        occurrence++;
                    }
                }
                steps.Add(new KeyValuePair<string, int>(t.name, occurrence));
            }
            return null;
        }

        static GameObject Follow(GameObject root, List<KeyValuePair<string, int>> steps)
        {
            Transform cur = root.transform;
            foreach (var step in steps)
            {
                Transform next = null;
                int seen = 0;
                for (int i = 0; i < cur.childCount && next == null; i++)
                {
                    Transform child = cur.GetChild(i);
                    if (child.name == step.Key && seen++ == step.Value)
                    {
                        next = child;
                    }
                }
                if (next == null)
                {
                    return null;
                }
                cur = next;
            }
            return cur.gameObject;
        }

        // The component of the same exact type and the same position among that type, e.g. the second BoxCollider.
        static Component SameComponent(GameObject go, Component source)
        {
            Type type = source.GetType();
            int index = HierarchyPath.IndexAmongSameType(source);
            return go.GetComponents(type).Where(x => x != null && x.GetType() == type).ElementAtOrDefault(index);
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
                    int swapped = SwapInstances(ctx, path, new[] { session.Root }, swaps, false, session.Root, out error);
                    if (swapped < 0)
                    {
                        return false;
                    }
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
                    int swapped = SwapInstances(ctx, path, session.Scene.GetRootGameObjects(), swaps, session.UseUndo, null, out error);
                    if (swapped < 0)
                    {
                        return false;
                    }
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
                using (var snapshot = new AssetSnapshot(importer))
                {
                    if (!RemapObjects(new Object[] { importer }, map, unmapped, false, !ctx.DryRun, out count, out error))
                    {
                        return false;
                    }
                    if (count == 0 || ctx.DryRun)
                    {
                        return true;
                    }
                    // A failed check restores the importer from the snapshot, nothing half-applied stays in memory.
                    if (!ctx.ReadyToSave(out error))
                    {
                        return false;
                    }
                    snapshot.Commit();
                }
                importer.SaveAndReimport();
                return true;
            }

            // Nothing is applied unless every reference in the file maps, so a failure leaves the asset untouched.
            Object[] assetObjects = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o != null).ToArray();
            // Each object is snapshotted before the commit, and restored unless the whole file passes its checks.
            var snapshots = new List<AssetSnapshot>();
            try
            {
                if (!ctx.DryRun)
                {
                    foreach (Object o in assetObjects)
                    {
                        snapshots.Add(new AssetSnapshot(o));
                    }
                }
                if (!RemapObjects(assetObjects, map, unmapped, false, !ctx.DryRun, out count, out error))
                {
                    return false;
                }
                if (count == 0 || ctx.DryRun)
                {
                    return true;
                }
                if (!ctx.ReadyToSave(out error))
                {
                    return false;
                }
                foreach (AssetSnapshot snapshot in snapshots)
                {
                    snapshot.Commit();
                }
            }
            finally
            {
                foreach (AssetSnapshot snapshot in snapshots)
                {
                    snapshot.Dispose();
                }
            }
            foreach (Object o in assetObjects)
            {
                EditorUtility.SetDirty(o);
            }
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(path));
            return true;
        }

        // Returns the number of instances swapped, or -1 with error set.
        static int SwapInstances(RunContext ctx, string path, IEnumerable<GameObject> roots, Dictionary<string, GameObject> swaps, bool useUndo, GameObject contentsRoot, out string error)
        {
            error = null;
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
                if (go == contentsRoot)
                {
                    // The root of a variant is its base: changing that is a different operation than a reference swap.
                    ctx.Log.Warn(path + " is a variant of " + source + ", its base is left as it is");
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
                    try
                    {
                        PrefabUtility.ReplacePrefabAssetOfPrefabInstance(go, target, useUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction);
                    }
                    catch (Exception e)
                    {
                        error = "swapping " + HierarchyPath.Of(go) + " failed: " + e.Message;
                        return -1;
                    }
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
                    bool mapped = map.ContainsKey(value);
                    if (!mapped && !unmapped.Contains(value))
                    {
                        continue;
                    }
                    if (inInstance && !it.prefabOverride)
                    {
                        // Inherited from the source prefab, which is remapped as its own asset.
                        continue;
                    }
                    if (!mapped)
                    {
                        error = PropertyIO.Describe(o) + "." + it.propertyPath + " uses " + value.name + " (" + value.GetType().Name + "), which has no counterpart in the new asset";
                        return false;
                    }
                    Object replacement = map[value];
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
