# Export

Reads assets through Unity's serializer and writes grep-friendly JSON. Nothing is modified.

## Runs

| RunName | Exports | Entry assets |
| --- | --- | --- |
| `PrefabExport` | Prefab hierarchy, components, properties; variants and nested instances as overrides | `.prefab` (folders expand), models accepted by explicit path |
| `SceneExport` | Scene hierarchy in the same node format, prefab instances as overrides | `.unity` |
| `AssetExport` | ScriptableObjects, and the main object plus sub-assets of any other asset file | anything but scenes, prefabs, scripts |
| `MaterialExport` | Shader properties with values, keywords, render queue, variant parent, stale properties | `.mat` |
| `AnimatorControllerExport` | Parameters, layers, state machines, states, transitions, conditions, blend trees | `.controller` |
| `ImporterExport` | Import settings (the `.meta`), with a typed summary for textures and models | textures, models, audio, any asset with an importer |
| `AssetReferenceExport` | Direct (and with `-recursive` transitive) dependencies, and what references the asset | any |
| `TypeCatalogExport` | Property paths, kinds, defaults, enum options of component and ScriptableObject types | none, `-filter=` |
| `CommandCatalogExport` | Every registered run with group, summary and arguments | none |

```bash
python assetops.py PrefabExport "Assets/Prefabs/Player.prefab,Assets/Prefabs/Enemies"
```

Several assets per run, comma separated. A folder expands to the assets beneath it that the run handles.

## Arguments

Every export takes:

| Argument | Effect |
| --- | --- |
| `-full` | No delta: every property, every GameObject field, every material property, hidden sub-objects |
| `-maxarray=N` | Arrays longer than N (default 100) export their first N entries and `{"$truncated": <total>}` |
| `-maxstring=N` | Strings longer than N characters (default 4000) are cut and marked |
| `-outdir=<dir>` | Write under this folder instead of `Library/AssetOps/Export` |

Per run:

| Run | Argument | Effect |
| --- | --- | --- |
| `PrefabExport`, `SceneExport` | `-expand` | Walk into prefab instances instead of exporting them as overrides |
| `PrefabExport`, `SceneExport` | `-subtree=<path>` | Export only that GameObject and its children |
| `AssetExport` | `-type=<type>` | When a folder is given, only assets whose main object is this type |
| `AssetExport` | `-nosub` | Main object only |
| `AssetReferenceExport` | `-scandir=<dirs>` | Where to look for referencers, default `Assets` |
| `AssetReferenceExport` | `-recursive` | Add `DependenciesRecursive` and `ReferencedByRecursive` |
| `TypeCatalogExport` | `-filter=<text>` | Required. Types whose full name contains the text |
| `TypeCatalogExport` | `-kind=component\|scriptableobject\|all` | Default all |

## Output

`Library/AssetOps/Export/<AssetPath>_<YYYYMMDD-HHMMSS>.json`, for example `Library/AssetOps/Export/Assets/Prefabs/Player.prefab_20261023-201500.json`. Exports not tied to one asset: `Export/<RunName>_<timestamp>.json`. Repeated exports never overwrite each other; read the newest.

The files are indented one property per line. Grep for what you need, then read that line range.

Every export starts with:

| Field | Meaning |
| --- | --- |
| `ExporterVersion` | AssetOps version |
| `ExportType` | The run name |
| `UnityVersion`, `ExportedUtc` | |
| `AssetPath`, `Guid` | |
| `DependencyHash` | Changes whenever the asset or anything it depends on changes. Compare it to tell a stale export |
| `Options` | The arguments the export ran with |

## Value format

The same format in every export and every edit, so an exported value can be written back as is.

| Kind | JSON |
| --- | --- |
| int, float, bool, string | as JSON. NaN and infinities as `"NaN"`, `"Infinity"`, `"-Infinity"` |
| enum | the name (`"Interpolate"`); the integer when no single name matches (flags). Writes take a name, a display name, an integer, or `"A|B"` for flags |
| Vector2/3/4, Rect | `[x, y]`, `[x, y, z]`, `[x, y, z, w]`, `[x, y, width, height]` |
| Vector2Int/3Int, RectInt | integer arrays |
| Color | `[r, g, b, a]`. Writes also take `"#RRGGBB"` and `"#RRGGBBAA"` |
| Quaternion | `[x, y, z, w]`. Writes also take `[x, y, z]` as Euler degrees |
| Bounds | `{"Center": [x, y, z], "Extents": [x, y, z]}` |
| BoundsInt | `{"Position": [..], "Size": [..]}` |
| LayerMask | the mask integer. Writes also take `["Default", "Water"]`, `"Everything"`, `"Nothing"` |
| AnimationCurve | `{"Keys": [{"Time", "Value", "In", "Out"}], "PreWrap", "PostWrap"}`; weighted keys add `WeightedMode`, `InWeight`, `OutWeight` |
| Gradient | `{"Mode": "Blend", "Colors": [[t, r, g, b]], "Alphas": [[t, a]]}` |
| Hash128 | 32-digit hex string |
| struct, `[Serializable]` class | an object of its fields. A write patches only the fields given |
| array, `List<T>` | a JSON array. A write replaces the whole array; `ArrayOps` edits single entries |
| `[SerializeReference]` | `{"$type": "<Assembly> <Namespace.Type>", ...fields}`, `null` for none. A write with a different `$type` creates that type |
| object reference | see below |

## Object references

| JSON | Means |
| --- | --- |
| `null` | no reference |
| `"Assets/Materials/Red.mat"` | the main asset at that path |
| `{"Asset": "Assets/Models/Hero.fbx", "Sub": "Body", "Type": "Mesh"}` | a sub-asset by name, `Type` when names repeat |
| `{"Asset": "Assets/Prefabs/Gun.prefab", "Object": "Gun/Barrel", "Component": "Transform"}` | a GameObject or component inside a prefab asset |
| `{"Object": "Player/Model", "Component": "Animator", "Index": 0}` | a GameObject or component in the same prefab or scene |
| `{"Guid": "6e1f...", "Sub": ...}` | an asset by GUID instead of path (write only) |
| `{"Missing": true}` | the reference points at something that no longer exists (export only) |
| `{"Unresolved": "<name>", "Type": ...}` | an object outside this prefab or scene, e.g. in another scene (export only) |
| `{"Asset": "Library/unity default resources", "Sub": "Cube", "Type": "Mesh"}` | a built-in resource |

Writes are forgiving where the field type makes the intent clear: a prefab path for a `Rigidbody` field takes the prefab root's `Rigidbody`, a texture path for a `Sprite` field takes the texture's only sprite.

## Paths

`Player/Model/Hand.R` names GameObjects from the root down, the root's own name included, in prefabs and scenes alike. When siblings share a name, `[i]` picks the i-th from 0: `Enemies/Grunt[2]`. A `/`, `[`, `]` or `\` inside a name is escaped with `\`. Components are addressed by type, plus `Index` among same-type components on that GameObject.

## PrefabExport and SceneExport

```json
{
  "ExporterVersion": "0.1.0",
  "ExportType": "PrefabExport",
  "AssetPath": "Assets/Prefabs/Player.prefab",
  "PrefabAssetType": "Regular",
  "GameObjectCount": 3,
  "Hierarchy": [
    {
      "Path": "Player",
      "Tag": "Player",
      "Components": [
        { "Type": "Transform", "Properties": {} },
        { "Type": "Rigidbody", "Properties": { "m_Mass": 2, "m_Interpolate": "Interpolate" } },
        { "Type": "PlayerController", "Script": "Assets/Scripts/PlayerController.cs",
          "Properties": { "speed": 6.5, "weapon": { "Object": "Player/Hand/Gun" } } }
      ]
    },
    { "Path": "Player/Hand", "Components": [ { "Type": "Transform", "Properties": { "m_LocalPosition": [0.4, 1.2, 0] } } ] },
    {
      "Path": "Player/Hand/Gun",
      "PrefabInstance": {
        "Source": "Assets/Prefabs/Gun.prefab",
        "Overrides": [
          { "Object": "Player/Hand/Gun", "Component": "Transform", "Property": "m_LocalPosition.x", "Value": "0" },
          { "Object": "Player/Hand/Gun/Barrel", "Component": "MeshRenderer", "Property": "m_Materials.Array.data[0]",
            "Reference": "Assets/Materials/Chrome.mat" }
        ],
        "AddedComponents": [ { "Object": "Player/Hand/Gun", "Type": "AudioSource", "Properties": {} } ]
      }
    }
  ]
}
```

| Node field | Meaning |
| --- | --- |
| `Path` | GameObject path, see Paths |
| `Active`, `Tag`, `Layer`, `Static` | only when not the default (inactive, a tag, a layer other than Default, any static flag), all with `-full` |
| `Components[]` | `Type`, `Index` when the type repeats, `Script` for scripts, `Properties` as the delta from a fresh component; `"(Missing Script)"` with `Missing: true` for a missing script |
| `PrefabInstance` | on an instance root: `Source`, `Overrides[]` (`Object`, `Component`, `Property`, `Value` or `Reference`), `AddedComponents[]`, `RemovedComponents[]`, `AddedGameObjects[]`, `RemovedGameObjects[]`. `Missing: true` when the source prefab is gone, `Nested: true` on an instance inside another instance (its overrides live on the outer one) |

Added GameObjects inside an instance export as nodes of their own after the instance root. A variant's root is an instance of its base, so a variant exports as `VariantOf` plus its overrides. `SceneExport` adds `BuildIndex`, `RootCount`, and `UnsavedChanges: true` when the scene is open in the editor with unsaved changes.

The Properties of a component are what a `Set` op takes. Override values are Unity's raw strings; write them back with `Set` and a typed value.

## AssetExport

```json
{
  "MainType": "ItemDefinition",
  "Objects": [
    { "Name": "Sword", "Type": "ItemDefinition", "Main": true, "LocalId": 11400000,
      "Script": "Assets/Scripts/ItemDefinition.cs",
      "Properties": { "displayName": "Sword", "damage": 12, "icon": { "Asset": "Assets/UI/Icons.png", "Sub": "sword", "Type": "Sprite" } } }
  ]
}
```

ScriptableObject properties are the delta from a fresh instance. Other objects export every property. Hidden sub-objects (the states inside a controller) are skipped unless `-full`.

## MaterialExport

```json
{
  "Shader": "Universal Render Pipeline/Lit",
  "ShaderSupported": true,
  "Parent": "Assets/Materials/Base.mat",
  "RenderQueue": 2000,
  "EnableInstancing": false,
  "Keywords": ["_NORMALMAP"],
  "Properties": { "_BaseColor": [1, 0, 0, 1], "_BaseMap": "Assets/Textures/Rust.png", "_Smoothness": 0.3 },
  "PropertyInfo": { "_Smoothness": { "Type": "Range", "Description": "Smoothness", "Range": [0, 1] } },
  "StaleProperties": ["_OldMask"],
  "UndeclaredKeywords": ["_LEGACY"]
}
```

`Properties` leaves out values at the shader default (all with `-full`) and is exactly what `EditMaterial` takes. A texture with tiling reads `{"Texture": ..., "Scale": [x, y], "Offset": [x, y]}`. `OverridesParent: true` in `PropertyInfo` marks a variant's own values. `StaleProperties` are saved values the shader no longer declares, `UndeclaredKeywords` are enabled keywords it does not declare; `AuditMaterial` flags both.

## AnimatorControllerExport

```json
{
  "Parameters": [ { "Name": "Speed", "Type": "Float", "Default": 0 }, { "Name": "Jump", "Type": "Trigger" } ],
  "Layers": [
    {
      "Name": "Base Layer", "Index": 0, "Weight": 1, "Blending": "Override",
      "StateMachine": {
        "DefaultState": "Idle",
        "States": [
          { "Path": "Idle", "Motion": "Assets/Anim/Idle.anim", "Speed": 1,
            "Transitions": [ { "To": "Locomotion/Run", "HasExitTime": false, "Duration": 0.1, "FixedDuration": true,
                               "Conditions": [ { "Parameter": "Speed", "Mode": "Greater", "Threshold": 0.1 } ] } ] }
        ],
        "AnyStateTransitions": [ { "To": "Jump", "Conditions": [ { "Parameter": "Jump", "Mode": "If", "Threshold": 0 } ] } ],
        "StateMachines": [ { "Name": "Locomotion", "Path": "Locomotion", "States": [ { "Path": "Locomotion/Run",
          "Motion": { "BlendTree": "Run", "BlendType": "Simple1D", "Parameter": "Speed",
                      "Children": [ { "Motion": "Assets/Anim/Walk.anim", "Threshold": 0.5, "TimeScale": 1 } ] } } ] } ]
      }
    }
  ]
}
```

State `Path`s, transition `To`s and blend tree blocks are exactly what `EditAnimatorController` takes. A synced layer reports `SyncedLayer` instead of a state machine.

## ImporterExport

`Importer` names the importer type. `Properties` is every importer field through the serializer (the `.meta` contents), writable with `EditImporter`'s `Properties`. Textures add a `Summary` in `EditImporter`'s `Texture` keys:

```json
"Summary": { "SourceSize": [2048, 2048], "TextureType": "Default", "sRGB": true, "Mipmaps": true, "MaxSize": 2048,
             "Compression": "Compressed", "ReadWrite": false, "Platforms": { "Android": { "MaxSize": 1024, "Format": "ASTC_6x6" } } }
```

Models add one with scale, animation, mesh and material import settings and the clip list.

## AssetReferenceExport

`Dependencies` (direct), `ReferencedBy` (direct, assets under `-scandir`), and with `-recursive` `DependenciesRecursive` and `ReferencedByRecursive`. Unity keeps only the forward direction, so the run indexes every asset under `-scandir` once; on a large project give a narrower `-scandir` when you can.

## TypeCatalogExport

```json
{ "Types": [ { "Name": "Rigidbody", "FullName": "UnityEngine.Rigidbody", "Kind": "Component",
    "Properties": [ { "Path": "m_Mass", "Kind": "Float", "Type": "float", "Default": 1 },
                    { "Path": "m_Interpolate", "Kind": "Enum", "Type": "Enum", "Options": ["None", "Interpolate", "Extrapolate"], "Default": "None" } ] } ] }
```

Up to 40 types, exact name matches first. Struct fields are listed under `Fields`, three levels deep.
