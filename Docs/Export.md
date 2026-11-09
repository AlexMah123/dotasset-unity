# Export

Reads assets through Unity's serializer and writes grep-friendly JSON. Nothing is modified.

## Runs

| RunName | Exports | Entry assets |
| --- | --- | --- |
| `PrefabExport` | Prefab hierarchy, components, properties; variants and nested instances as overrides | `.prefab` (folders expand), models accepted by explicit path |
| `SceneExport` | Scene hierarchy in the same node format, prefab instances as overrides | `.unity` |
| `AssetExport` | ScriptableObjects, and the main object plus sub-assets of any other asset file | anything but scenes and prefabs or models. Folders skip scripts, a script given by path exports as its `MonoScript` |
| `MaterialExport` | Shader properties with values, keywords, render queue, variant parent, stale properties | `.mat` |
| `AnimatorControllerExport` | Parameters, layers, state machines, states, transitions, conditions, blend trees | `.controller` |
| `AnimationClipExport` | Clip settings, float curves with their keys, object reference curves, events | `.anim`, the clips inside `.controller` and `.playable` files, models by explicit path |
| `TimelineExport` | Timeline duration and frame rate, tracks with their binding types, clips and markers. Needs `com.unity.timeline`, refused (exit 2) without it | `.playable` files that are timelines |
| `ImporterExport` | Import settings (the `.meta`), with a typed summary for textures and models | textures, models, audio, any asset with an importer |
| `AssetReferenceExport` | Direct (and with `-recursive` transitive) dependencies, and what references the asset | any |
| `TypeCatalogExport` | Property paths, kinds, defaults, enum options of component and ScriptableObject types | none, `-filter=` |
| `CommandCatalogExport` | Every registered run with group, summary, arguments and `WritesWithoutApply` (true only for Import runs), plus `Rejected`: commands left out because their declared group or `WritesWithoutApply` disagrees with their name | none |

```bash
python dotasset.py PrefabExport "Assets/Prefabs/Player.prefab,Assets/Prefabs/Enemies"
```

Several assets per run, comma separated. A folder expands to the assets beneath it that the run handles.

## Arguments

Every export takes:

| Argument | Effect |
| --- | --- |
| `-full` | No delta: every property, every GameObject field, every material property, hidden sub-objects |
| `-maxarray=N` | Arrays longer than N (default 100) export their first N entries and `{"$truncated": <total>}` |
| `-maxstring=N` | Strings longer than N characters (default 4000) are cut and marked |
| `-outdir=<dir>` | Write under this folder instead of `Library/DotAsset/Export` |

N is a whole number of 0 or more. Anything else, such as `-maxarray=lots` or `-maxarray=1e3`, is refused (exit 2) rather than replaced by the default.

Per run:

| Run | Argument | Effect |
| --- | --- | --- |
| `PrefabExport`, `SceneExport` | `-expand` | Walk into prefab instances instead of exporting them as overrides |
| `PrefabExport`, `SceneExport` | `-subtree=<path>` | Export only that GameObject and its children |
| `AssetExport` | `-type=<type>` | When a folder is given, only assets whose main object is this type |
| `AssetExport` | `-nosub` | Main object only |
| `AnimationClipExport` | `-nokeys` | Every curve with its `KeyCount` instead of its keys, for a long clip |
| `AssetReferenceExport` | `-scandir=<dirs>` | Where to look for referencers, default `Assets` |
| `AssetReferenceExport` | `-recursive` | Add `DependenciesRecursive` and `ReferencedByRecursive` |
| `TypeCatalogExport` | `-filter=<text>` | Required. Types whose full name contains the text |
| `TypeCatalogExport` | `-kind=component\|scriptableobject\|all` | Default all |

## Output

`Library/DotAsset/Export/<AssetPath>_<YYYYMMDD-HHMMSS>.json`, the time in UTC, for example `Library/DotAsset/Export/Assets/Prefabs/Player.prefab_20261023-201500.json`. Exports not tied to one asset: `Export/<RunName>_<timestamp>.json`. Repeated exports never overwrite each other; read the newest. Before 0.12.0 the name carried local time, so while older files are still there, tell the newest by its write time or its `ExportedUtc`, not by its name. The newest 10 per asset are kept and older ones deleted, except under a folder given with `-outdir`, which is left alone.

The files are indented one property per line. Grep for what you need, then read that line range.

Every export starts with:

| Field | Meaning |
| --- | --- |
| `ExporterVersion` | DotAsset version |
| `FormatVersion` | The JSON format, 1. Goes up only on a breaking change, see [AI-Guide.md](AI-Guide.md#stability) |
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
| AnimationCurve | `{"Keys": [{"Time", "Value", "In", "Out"}], "PreWrap", "PostWrap"}`; weighted keys add `WeightedMode`, `InWeight`, `OutWeight`. On a write every key given must parse (numbers for the tangents and weights, member names for the modes), a bad one refuses the value |
| Gradient | `{"Mode": "Blend", "Colors": [[t, r, g, b]], "Alphas": [[t, a]]}` |
| Hash128 | 32-digit hex string |
| struct, `[Serializable]` class | an object of its fields. A write patches only the fields given |
| array, `List<T>` | a JSON array. A write replaces the whole array; `ArrayOps` edits single entries |
| `[SerializeReference]` | `{"$type": "<Assembly> <Namespace.Type>", ...fields}`, `null` for none. A write with a different `$type` creates that type |
| object reference | see below |
| Unity 6.3 `EntityId` | not supported yet, exports as `{"$unsupported": "EntityId"}` |

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
  "ExporterVersion": "0.2.1",
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

`Properties` leaves out values at the shader default (all with `-full`) and is exactly what `EditMaterial` takes. A texture with tiling reads `{"Texture": ..., "Scale": [x, y], "Offset": [x, y]}`. Properties marked `[HideInInspector]` in the shader are left out unless `-full`. For a material variant, `Properties` holds only the variant's own overrides and `Inherited` the values it takes from its parent; `OverridesParent: true` in `PropertyInfo` marks the overrides. `StaleProperties` are saved values the shader no longer declares, `UndeclaredKeywords` are enabled keywords it does not declare; `AuditMaterial` flags both.

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

State `Path`s, transition `To`s and blend tree blocks are exactly what `EditAnimatorController` takes. Two things are export only: a transition's `ToStateMachine` (a transition into a sub-state machine), and a sub-state machine's `ExitTransitions`. `AddTransition` targets states and `Exit` only. A synced layer reports `SyncedLayer` instead of a state machine.

## AnimationClipExport

```json
{
  "Clips": [
    {
      "Name": "Wave", "Length": 1, "FrameRate": 30, "Legacy": false,
      "Settings": { "LoopTime": true, "LoopBlend": false, "CycleOffset": 0, "StartTime": 0, "StopTime": 1, "Mirror": false, "...": "..." },
      "Curves": [ { "Path": "Body/Arm", "Type": "Transform", "Property": "m_LocalPosition.x",
                    "Keys": [ { "Time": 0, "Value": 0, "InTangent": 2, "OutTangent": 2 },
                              { "Time": 1, "Value": 2, "InTangent": 2, "OutTangent": 2 } ] } ],
      "ObjectCurves": [ { "Path": "Body", "Type": "MeshRenderer", "Property": "m_Materials.Array.data[0]",
                          "Keys": [ { "Time": 0.5, "Value": "Assets/Materials/Red.mat" } ] } ],
      "Events": [ { "Time": 0.25, "Function": "Step", "Int": 3 } ]
    }
  ]
}
```

`Clips` has one entry per clip: the clip of a `.anim`, the clips stored inside a `.controller` or a `.playable` (Timeline recordings), or the clips of a model named by path. A folder expands to `.anim` files and to the controllers and timelines that hold clips, and skips models. A model's export carries `"Imported": true`: its clips are read-only, extract one to `.anim` to change it.

`Settings` holds the fields of `AnimationClipSettings` (`LoopBlend` is the inspector's Loop Pose), plus `AdditiveReferencePoseClip` and `AdditiveReferencePoseTime` when the clip has an additive reference pose. A curve is named by `Path` (the GameObject path below the animated root, empty for the root), `Type` and `Property`, the same names `RebindAnimation` and `EditAnimationClip` take. A key carries `InMode` and `OutMode` (`Free`, `Auto`, `Linear`, `Constant`, `ClampedAuto`) only when one side is not `Free`, `Broken` when its tangents are split, and `WeightedMode`, `InWeight` and `OutWeight` only on a weighted key. A constant tangent reads `"Infinity"`. An event lists `Float`, `Int`, `String` and `Object` only when they are set. Keys are never cut by `-maxarray`, so for a long clip (motion capture, hundreds of curves) export with `-nokeys` first.

## TimelineExport

```json
{
  "Name": "Intro", "Duration": 5, "DurationMode": "BasedOnClips", "FrameRate": 60,
  "Tracks": [
    { "Path": "Group", "Type": "GroupTrack", "Clips": [], "Markers": [] },
    { "Path": "Group/Body", "Type": "AnimationTrack", "BindingType": "Animator",
      "Clips": [ { "Name": "Wave", "Start": 1, "End": 2, "Duration": 1, "ClipIn": 0, "TimeScale": 1,
                   "AssetType": "AnimationPlayableAsset", "Clip": "Assets/Animation/Wave.anim" } ],
      "Markers": [ { "Time": 0.5, "Type": "SignalEmitter", "Signal": "Assets/Cutscenes/Hit.signal" } ] },
    { "Path": "Lights", "Type": "ActivationTrack", "BindingType": "GameObject", "Muted": true,
      "Clips": [ { "Name": "Active", "Start": 0, "End": 5, "Duration": 5, "ClipIn": 0, "TimeScale": 1,
                   "AssetType": "ActivationPlayableAsset" } ], "Markers": [] }
  ],
  "Markers": [ { "Time": 2, "Type": "SignalEmitter", "Signal": "Assets/Cutscenes/Hit.signal" } ]
}
```

Times are in seconds. `FixedDuration` is added when `DurationMode` is `FixedLength`. `Tracks` is flat in timeline order, a child right after its parent, and `Path` joins the track names through their groups. Two tracks can share a name, so read them in order. `BindingType` is the type of object the track binds to. The bound object itself is stored in the `PlayableDirector` that plays the timeline, so read it from that scene or prefab with `SceneExport` or `PrefabExport` (its `m_SceneBindings`). `Muted` and `Locked` appear only when set, and so does `InfiniteClip`, the clip an animation track records into when it has no clips.

A clip always has its timing. `EaseInDuration`, `EaseOutDuration`, `BlendInDuration`, `BlendOutDuration`, `PreExtrapolationMode` and `PostExtrapolationMode` appear only when set. `Clip` is the animation or audio clip the clip asset plays. A clip asset whose type comes from outside the Timeline package (your own `PlayableAsset`) adds its fields as `Properties`, in the [value format](#value-format). The same goes for a marker. A signal emitter adds `Signal`, plus `Retroactive` and `EmitOnce` when they are set. `Markers` at the top level are the timeline's own markers, the ones on its marker track. The curves a timeline recorded are clips inside the `.playable` that `AnimationClipExport` reads, and `AssetExport` lists every object in the file through the serializer.

## ImporterExport

`Importer` names the importer type. `Properties` is every importer field through the serializer (the `.meta` contents), writable with `EditImporter`'s `Properties`. Textures add a `Summary` in `EditImporter`'s `Texture` keys:

```json
"Summary": { "SourceSize": [2048, 2048], "TextureType": "Default", "sRGB": true, "Mipmaps": true, "MaxSize": 2048,
             "Compression": "Compressed", "ReadWrite": false, "Platforms": { "Android": { "MaxSize": 1024, "Format": "ASTC_6x6" } } }
```

Models add one with scale, animation, mesh and material import settings, the clip list, and `Meshes`, the meshes the import made:

```json
"Meshes": [ { "Name": "Hero_Body", "Vertices": 8124, "Triangles": 12006, "SubMeshes": 2, "BlendShapes": 14, "Bones": 52 } ]
```

Triangles come from the index counts (a quad is two), so they need no Read/Write. `Bones` is the number of bind poses, 0 on a mesh that is not skinned.

## AssetReferenceExport

`Dependencies` (direct), `ReferencedBy` (direct, assets under `-scandir`), and with `-recursive` `DependenciesRecursive` and `ReferencedByRecursive`. Unity keeps only the forward direction, so the run indexes every asset under `-scandir` once; on a large project give a narrower `-scandir` when you can.

## TypeCatalogExport

```json
{ "Types": [ { "Name": "Rigidbody", "FullName": "UnityEngine.Rigidbody", "Kind": "Component",
    "Properties": [ { "Path": "m_Mass", "Kind": "Float", "Type": "float", "Default": 1 },
                    { "Path": "m_Interpolate", "Kind": "Enum", "Type": "Enum", "Options": ["None", "Interpolate", "Extrapolate"], "Default": "None" } ] } ] }
```

Up to 40 types, exact name matches first. Struct fields are listed under `Fields`, three levels deep.
