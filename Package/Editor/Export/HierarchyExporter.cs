using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetOps
{
    // Flattens a GameObject hierarchy into one JSON node per GameObject, depth first.
    // Token-aware by default:
    //   - component properties are the delta from a fresh component of the same type (-full for everything)
    //   - GameObject fields at their defaults (active, Untagged, Default layer, not static) are left out
    //   - a prefab instance is its source path plus overrides, added and removed objects; the inside of the
    //     instance is the source prefab's own export (-expand to walk into instances anyway)
    public sealed class HierarchyExporter
    {
        readonly ReadOptions opt;
        readonly DefaultValues defaults;
        readonly bool full;
        readonly bool expand;

        public HierarchyExporter(ReadOptions opt, DefaultValues defaults, bool full, bool expand)
        {
            this.opt = opt;
            this.defaults = defaults;
            this.full = full;
            this.expand = expand;
        }

        public JArray Export(IEnumerable<GameObject> roots)
        {
            var nodes = new JArray();
            foreach (GameObject root in roots)
            {
                Visit(root, nodes);
            }
            return nodes;
        }

        void Visit(GameObject go, JArray nodes)
        {
            var node = new JObject { ["Path"] = HierarchyPath.Of(go) };
            AddGameObjectFields(go, node);

            bool instanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(go);
            if (instanceRoot && !expand)
            {
                node["PrefabInstance"] = InstanceInfo(go);
                nodes.Add(node);
                if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                {
                    foreach (var added in PrefabUtility.GetAddedGameObjects(go))
                    {
                        if (added.instanceGameObject != null)
                        {
                            Visit(added.instanceGameObject, nodes);
                        }
                    }
                }
                return;
            }

            node["Components"] = Components(go);
            nodes.Add(node);
            for (int i = 0; i < go.transform.childCount; i++)
            {
                Visit(go.transform.GetChild(i).gameObject, nodes);
            }
        }

        void AddGameObjectFields(GameObject go, JObject node)
        {
            if (full || !go.activeSelf)
            {
                node["Active"] = go.activeSelf;
            }
            if (full || go.tag != "Untagged")
            {
                node["Tag"] = go.tag;
            }
            if (full || go.layer != 0)
            {
                string layerName = LayerMask.LayerToName(go.layer);
                node["Layer"] = string.IsNullOrEmpty(layerName) ? (JToken)go.layer : layerName;
            }
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
            if (full || flags != 0)
            {
                node["Static"] = StaticNames(flags);
            }
        }

        public static JArray StaticNames(StaticEditorFlags flags)
        {
            var names = new JArray();
            foreach (StaticEditorFlags f in System.Enum.GetValues(typeof(StaticEditorFlags)).Cast<StaticEditorFlags>().Distinct())
            {
                int bits = (int)f;
                if (bits != 0 && (bits & (bits - 1)) == 0 && (flags & f) == f)
                {
                    // Value 1 has two names, ContributeGI and the obsolete LightmapStatic.
                    names.Add(bits == 1 ? "ContributeGI" : f.ToString());
                }
            }
            return names;
        }

        JArray Components(GameObject go)
        {
            var list = new JArray();
            foreach (Component c in go.GetComponents<Component>())
            {
                list.Add(Component(c));
            }
            return list;
        }

        public JObject Component(Component c)
        {
            if (c == null)
            {
                return new JObject { ["Type"] = "(Missing Script)", ["Missing"] = true };
            }
            var entry = new JObject { ["Type"] = TypeResolver.DisplayName(c.GetType()) };
            int index = HierarchyPath.IndexAmongSameType(c);
            if (index > 0)
            {
                entry["Index"] = index;
            }
            var mb = c as MonoBehaviour;
            if (mb != null)
            {
                MonoScript script = MonoScript.FromMonoBehaviour(mb);
                string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    entry["Script"] = scriptPath;
                }
            }
            entry["Properties"] = PropertyIO.Read(c, opt, full ? null : defaults.For(c.GetType()), true);
            return entry;
        }

        JObject InstanceInfo(GameObject root)
        {
            var info = new JObject();
            Object source = PrefabUtility.GetCorrespondingObjectFromSource(root);
            info["Source"] = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (PrefabUtility.IsPrefabAssetMissing(root))
            {
                info["Missing"] = true;
                return info;
            }
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(root))
            {
                // Overrides of nested instances are recorded on the outermost instance.
                info["Nested"] = true;
                return info;
            }

            var sourceToInstance = new Dictionary<Object, Object>();
            foreach (GameObject go in HierarchyPath.DepthFirst(root))
            {
                Object s = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (s != null && !sourceToInstance.ContainsKey(s))
                {
                    sourceToInstance.Add(s, go);
                }
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null)
                    {
                        continue;
                    }
                    Object sc = PrefabUtility.GetCorrespondingObjectFromSource(c);
                    if (sc != null && !sourceToInstance.ContainsKey(sc))
                    {
                        sourceToInstance.Add(sc, c);
                    }
                }
            }

            var overrides = new JArray();
            PropertyModification[] mods = PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0];
            foreach (PropertyModification mod in mods)
            {
                if (mod == null || mod.target == null)
                {
                    continue;
                }
                var entry = new JObject();
                Object instanceObject;
                if (sourceToInstance.TryGetValue(mod.target, out instanceObject))
                {
                    var c = instanceObject as Component;
                    var go = c != null ? c.gameObject : instanceObject as GameObject;
                    entry["Object"] = HierarchyPath.Of(go);
                    if (c != null)
                    {
                        entry["Component"] = TypeResolver.DisplayName(c.GetType());
                        int index = HierarchyPath.IndexAmongSameType(c);
                        if (index > 0)
                        {
                            entry["Index"] = index;
                        }
                    }
                }
                else
                {
                    entry["Target"] = mod.target.name + " (" + mod.target.GetType().Name + ")";
                }
                entry["Property"] = mod.propertyPath;
                if (mod.objectReference != null)
                {
                    entry["Reference"] = ObjectRefs.Write(mod.objectReference, opt.Scope);
                }
                else
                {
                    entry["Value"] = mod.value;
                }
                overrides.Add(entry);
            }
            info["Overrides"] = overrides;

            var addedComponents = new JArray();
            foreach (var added in PrefabUtility.GetAddedComponents(root))
            {
                if (added.instanceComponent == null)
                {
                    continue;
                }
                JObject c = Component(added.instanceComponent);
                c.AddFirst(new JProperty("Object", HierarchyPath.Of(added.instanceComponent.gameObject)));
                addedComponents.Add(c);
            }
            if (addedComponents.Count > 0)
            {
                info["AddedComponents"] = addedComponents;
            }

            var removedComponents = new JArray();
            foreach (var removed in PrefabUtility.GetRemovedComponents(root))
            {
                removedComponents.Add(new JObject
                {
                    ["Object"] = removed.containingInstanceGameObject != null ? HierarchyPath.Of(removed.containingInstanceGameObject) : null,
                    ["Type"] = removed.assetComponent != null ? TypeResolver.DisplayName(removed.assetComponent.GetType()) : null,
                });
            }
            if (removedComponents.Count > 0)
            {
                info["RemovedComponents"] = removedComponents;
            }

            var removedObjects = new JArray();
            foreach (var removed in PrefabUtility.GetRemovedGameObjects(root))
            {
                removedObjects.Add(new JObject
                {
                    ["Parent"] = removed.parentOfRemovedGameObjectInInstance != null ? HierarchyPath.Of(removed.parentOfRemovedGameObjectInInstance) : null,
                    ["Name"] = removed.assetGameObject != null ? removed.assetGameObject.name : null,
                });
            }
            if (removedObjects.Count > 0)
            {
                info["RemovedGameObjects"] = removedObjects;
            }

            var addedObjects = PrefabUtility.GetAddedGameObjects(root).Where(a => a.instanceGameObject != null).Select(a => (JToken)HierarchyPath.Of(a.instanceGameObject)).ToList();
            if (addedObjects.Count > 0)
            {
                info["AddedGameObjects"] = new JArray(addedObjects);
            }
            return info;
        }
    }
}
