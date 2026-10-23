# Edit

Changes existing assets to match an intent. Creating is [Import](Import.md), moves and project-wide reference changes are [Migrate](Migrate.md).

## Runs

| RunName | Edits | All or nothing per |
| --- | --- | --- |
| `EditPrefab` | GameObjects, components, properties, nested instances of prefabs | prefab |
| `EditScene` | the same in scenes | scene |
| `EditAsset` | serialized properties of ScriptableObjects and other asset objects | object |
| `EditMaterial` | shader properties, textures, keywords, render queue, shader, variant parent | material |
| `EditImporter` | texture import settings (typed), any importer field (raw), then reimport | asset |
| `EditAnimatorController` | parameters, layers, sub-state machines, states, blend trees, transitions | controller |

```bash
python assetops.py EditPrefab --args '-spec="C:/temp/player.json"'           # dry run: every op runs, nothing is saved
python assetops.py EditPrefab --args '-spec="C:/temp/player.json" -apply'    # write
```

Every Edit run takes `-spec=<path>` and `-apply`, nothing else. The spec is `{"Targets": [ ... ]}`. Unknown keys in a target are refused (exit 2) before anything runs.

A dry run executes everything a real run does and checks it just as hard; it only skips the save and then drops the changes. It works on both routes, including a scene open in the editor (the changes are reverted through undo).

Failure: `<RunName> failed for <target>. Reason: <first error>`. Nothing of that target is saved, the other targets still run. Errors Unity itself logs while a target is written (a component that refuses to be removed, a script exception in `OnValidate`) also fail the target.

## EditPrefab

```json
{
  "Targets": [
    {
      "AssetPath": "Assets/Prefabs/Player.prefab",
      "Ops": [
        { "Op": "AddGameObject", "Parent": "Player", "Name": "Hand.R", "Components": ["BoxCollider"] },
        { "Op": "Set", "Path": "Player/Hand.R", "Component": "Transform", "Properties": { "m_LocalPosition": [0.4, 1.2, 0] } },
        { "Op": "AddPrefabInstance", "Parent": "Player/Hand.R", "Prefab": "Assets/Prefabs/Gun.prefab", "Name": "Gun" },
        { "Op": "Set", "Path": "Player", "Component": "PlayerController",
          "Properties": { "speed": 7, "weapon": { "Object": "Player/Hand.R/Gun", "Component": "Gun" } } },
        { "Op": "RemoveComponent", "Path": "Player", "Type": "LegacyInput" }
      ]
    }
  ]
}
```

The prefab loads into an isolated preview scene (`PrefabUtility.LoadPrefabContents`), the ops run in order, and `SaveAsPrefabAsset` writes it only if every op succeeded. Refused: models (`.fbx`, use `EditImporter` or a variant) and a prefab open in Prefab Mode with unsaved changes.

## EditScene

Same spec and ops, `AssetPath` a `.unity`.

- Not loaded: opens next to the user's scenes (headless: on its own) and closes again without saving unless the run writes.
- Loaded in the editor: edited in place. Every change records undo on one group, so a failed or dry run reverts it, and an applied run is a single Ctrl+Z named `Assetops: EditScene`.
- Loaded with unsaved changes: refused, save or discard them first.

A missing `Parent` in `AddGameObject` / `AddPrefabInstance` / `Reparent` means a scene root.

## Hierarchy ops

Shared by `EditPrefab`, `EditScene` and `CreateAsset`. `Path` and `Parent` are [GameObject paths](Export.md#paths). The first failing op stops the target. Every op, `Components` entry, condition and blend tree child is checked against the keys listed here, and an unknown key fails the op.

| Op | Keys | Does |
| --- | --- | --- |
| `AddGameObject` | `Parent`, `Name`, `Components`, `RectTransform`, `SiblingIndex`, `Active`, `Tag`, `Layer`, `Static` | new GameObject. `Components` lists type names or `{Type, Properties}`. Under a RectTransform parent it gets a RectTransform |
| `AddPrefabInstance` | `Parent`, `Prefab`, `Name`, `SiblingIndex`, `Active`, `Tag`, `Layer`, `Static` | nested prefab instance, linked to its source |
| `Delete` | `Path` | removes the GameObject and its children. Refused for the prefab root and for objects inside a prefab instance |
| `Rename` | `Path`, `NewName` | |
| `Reparent` | `Path`, `Parent`, `SiblingIndex`, `WorldPositionStays` (default false) | `SiblingIndex` on a scene root is refused in a scene open in the editor, undo cannot restore root order |
| `SetGameObject` | `Path`, `Active`, `Tag`, `Layer` (name or 0-31), `Static` (`true`, `false`, or `["BatchingStatic", ...]`) | tags and layers must exist |
| `AddComponent` | `Path`, `Type`, `Properties` | refused when Unity refuses (DisallowMultipleComponent, a script that does not compile) |
| `RemoveComponent` | `Path`, `Type`, `Index` | refused while another component `[RequireComponent]`s it |
| `Set` | `Path`, `Component` (omit for the GameObject itself), `Index`, `Properties`, `ArrayOps` | writes properties. On a prefab instance this creates overrides |
| `RevertOverrides` | `Path`, `Component`, `Index` | reverts one object's overrides, or with no `Component` every override of the outermost instance at `Path` |
| `UnpackPrefab` | `Path`, `Completely` (default false) | unpacks an outermost instance root |
| `RemoveMissingScripts` | `Path` (default every root), `Recursive` (default true) | |

`Type` and `Component` take a short type name (`Rigidbody`), a full name (`UnityEngine.Rigidbody`), a base type when only one component fits (`Collider`), or a script path (`Assets/Scripts/Gun.cs`).

### Properties

`{"<property path>": <value>}` in the [value format](Export.md#value-format). The keys are SerializedProperty paths, exactly as exports print them:

- script fields by field name: `speed`, `loadout.primary`, `waypoints[2]` (also `waypoints.Array.data[2]`)
- built-in components by their serialized names: `m_Mass`, `m_LocalPosition`, `m_Materials`. A key without `m_` is retried with it, so `mass` finds `m_Mass`
- the GameObject itself (no `Component`): `m_IsActive`, `m_Layer`, `m_TagString`, `m_StaticEditorFlags`, though `SetGameObject` is simpler

An unknown key fails with the list of the object's real property names. `TypeCatalogExport -filter=<type>` lists them up front.

### ArrayOps

Single-entry changes without rewriting the array:

```json
"ArrayOps": [
  { "Property": "waypoints", "Append": [0, 0, 5] },
  { "Property": "waypoints", "Insert": 0, "Value": [0, 0, 0] },
  { "Property": "loot", "Remove": 3 }
]
```

Applied after `Properties`, in order. A new entry starts as a copy of its neighbour, as in the Inspector, so give every field that should differ, or the whole entry.

## EditAsset

```json
{
  "Targets": [
    { "AssetPath": "Assets/Data/Items/Sword.asset", "Properties": { "damage": 15, "tags": ["melee", "blade"] } },
    { "AssetPath": "Assets/Data/Loot.asset", "ArrayOps": [ { "Property": "entries", "Append": { "item": "Assets/Data/Items/Sword.asset", "weight": 2 } } ] },
    { "AssetPath": "Assets/Data/Bundle.asset", "Sub": "Config", "SubType": "BundleConfig", "Properties": { "enabled": true } }
  ]
}
```

| Key | Meaning |
| --- | --- |
| `AssetPath` | the asset file. Prefabs and scenes are refused, use `EditPrefab` / `EditScene`. So are imported assets (textures, models, audio): their objects are rebuilt from the source file on every import, change them with `EditImporter` |
| `Sub`, `SubType` | a named sub-asset instead of the main object; `SubType` when names repeat |
| `Properties`, `ArrayOps` | as above |

A copy of the object is taken first and copied back when the target fails or the run is dry.

## EditMaterial

```json
{
  "Targets": [
    {
      "AssetPath": "Assets/Materials/Rust.mat",
      "Properties": { "_BaseColor": [0.6, 0.3, 0.2, 1], "_Smoothness": 0.25,
                      "_BaseMap": { "Texture": "Assets/Textures/Rust.png", "Scale": [2, 2], "Offset": [0, 0] } },
      "EnableKeywords": ["_NORMALMAP"],
      "DisableKeywords": ["_EMISSION"],
      "RenderQueue": -1
    }
  ]
}
```

| Key | Meaning |
| --- | --- |
| `Shader` | shader name (`Universal Render Pipeline/Lit`) or shader asset path |
| `Parent` | a material path makes this a variant of it, `null` detaches |
| `Properties` | shader property names. Colors `[r,g,b,a]` or `"#hex"`, vectors `[x,y,z,w]`, floats and ranges numbers, ints integers, textures a reference or `{Texture, Scale, Offset}` (each optional) |
| `RevertProperties` | variant only: property names whose override is dropped |
| `EnableKeywords`, `DisableKeywords` | keyword names. Disabling also removes undeclared keywords from the saved lists |
| `RenderQueue` | -1 for the shader's queue, up to 5000 |
| `EnableInstancing`, `DoubleSidedGI` | bool |
| `GlobalIlluminationFlags` | `None`, `RealtimeEmissive`, `BakedEmissive`, `EmissiveIsBlack`, `AnyEmissive` |
| `RemoveStaleProperties` | `true` drops saved values the shader does not declare |

`Shader` is applied first, so `Properties` can name the new shader's properties in the same target. A property the shader does not declare fails with the shader's property list.

## EditImporter

```json
{
  "Targets": [
    { "AssetPath": "Assets/Art/Rock_n.png", "Texture": { "TextureType": "NormalMap" } },
    { "AssetPaths": ["Assets/UI"], "Texture": { "Mipmaps": false, "Compression": "Compressed",
      "Platforms": { "Android": { "MaxSize": 1024, "Format": "ASTC_6x6" } } } },
    { "AssetPath": "Assets/Models/Hero.fbx", "Properties": { "meshes.globalScale": 0.01, "importAnimation": false } }
  ]
}
```

`AssetPath` names one asset, `AssetPaths` many; folders expand to the textures beneath them when there is a `Texture` block, to every asset with an importer otherwise. Each asset is reimported once after all its changes.

`Texture` keys (the same keys `ImporterExport` prints in `Summary`, so a summary edits and feeds back as is):

| Key | Values |
| --- | --- |
| `TextureType` | `Default`, `NormalMap`, `GUI`, `Sprite`, `Cursor`, `Cookie`, `Lightmap`, `SingleChannel`, ... |
| `TextureShape` | `Texture2D`, `TextureCube`, `Texture2DArray`, `Texture3D` |
| `sRGB`, `AlphaIsTransparency`, `ReadWrite`, `Mipmaps`, `CrunchedCompression` | bool |
| `AlphaSource` | `None`, `FromInput`, `FromGrayScale` |
| `MaxSize` | power of two, 32 to 16384 |
| `Compression` | `Uncompressed`, `Compressed`, `CompressedHQ`, `CompressedLQ` |
| `CompressionQuality`, `AnisoLevel` | int |
| `NPOTScale` | `None`, `ToNearest`, `ToLarger`, `ToSmaller` |
| `FilterMode` | `Point`, `Bilinear`, `Trilinear` |
| `WrapMode` | `Repeat`, `Clamp`, `Mirror`, `MirrorOnce` |
| `SpriteMode` | `None`, `Single`, `Multiple`, `Polygon` |
| `PixelsPerUnit` | number |
| `Platforms` | `{"<platform>": {"Override" (default true), "MaxSize", "Format" (a TextureImporterFormat name), "Compression", "CompressionQuality", "CrunchedCompression"}}`. Platform names: `Standalone`, `Android`, `iPhone`, `WebGL`, ... |
| `SourceSize` | read-only, ignored |

`Properties` writes importer fields by serialized path, as `ImporterExport` lists them, for models, audio and anything else. A dry run restores the importer from a snapshot and does not reimport.

## EditAnimatorController

```json
{
  "Targets": [
    {
      "AssetPath": "Assets/Anim/Player.controller",
      "Ops": [
        { "Op": "AddParameter", "Name": "Grounded", "Type": "Bool", "Default": true },
        { "Op": "AddStateMachine", "Path": "Air" },
        { "Op": "AddState", "Path": "Air/Fall", "Motion": "Assets/Anim/Fall.anim", "Position": [300, 120] },
        { "Op": "AddTransition", "From": "AnyState", "To": "Air/Fall", "Duration": 0.1,
          "Conditions": [ { "Parameter": "Grounded", "Mode": "IfNot" } ] },
        { "Op": "SetState", "State": "Locomotion/Run", "Speed": 1.2,
          "Motion": { "BlendTree": "Run", "BlendType": "Simple1D", "Parameter": "Speed",
                      "Children": [ { "Motion": "Assets/Anim/Jog.anim", "Threshold": 0.5 }, { "Motion": "Assets/Anim/Sprint.anim", "Threshold": 1 } ] } },
        { "Op": "RenameParameter", "Name": "Speed", "NewName": "MoveSpeed" }
      ]
    }
  ]
}
```

Every op takes `Layer` (name or index, default 0). States are paths inside the layer: `Run`, or `Locomotion/Run` inside the sub-state machine `Locomotion`, exactly as `AnimatorControllerExport` prints them.

| Op | Keys |
| --- | --- |
| `AddParameter` | `Name`, `Type` (`Float`, `Int`, `Bool`, `Trigger`), `Default` |
| `SetParameter` | `Name`, `Default` |
| `RenameParameter` | `Name`, `NewName`. Also renames it in every condition, blend tree and state parameter field |
| `RemoveParameter` | `Name` |
| `AddLayer` | `Name`, `Weight`, `Blending` (`Override`, `Additive`), `IKPass`, `AvatarMask` |
| `SetLayer` | `Layer`, `NewName`, `Weight`, `Blending`, `IKPass`, `AvatarMask` (`null` clears) |
| `RemoveLayer` | `Layer` |
| `AddStateMachine` | `Path`, `Position` |
| `AddState` | `Path`, `Position`, and any `SetState` field |
| `SetState` | `State`, `NewName`, `Motion`, `Speed`, `Tag`, `WriteDefaultValues`, `SpeedParameter`, `TimeParameter`, `Mirror`, `MirrorParameter`, `CycleOffset`, `CycleOffsetParameter`, `IKOnFeet`, `Default`, `Properties` (raw serialized fields) |
| `RemoveState` | `State` |
| `SetDefaultState` | `State` |
| `AddTransition` | `From` (state path, `AnyState`, `Entry`), `To` (state path, `Exit`), `StateMachine` (whose AnyState or Entry, default the layer's top machine), and any `SetTransition` field |
| `SetTransition` | `From`, `To`, `Index` (when several match), `Name`, `Solo`, `Mute`, `HasExitTime`, `ExitTime`, `Duration`, `FixedDuration`, `Offset`, `Interruption` (`None`, `Source`, `Destination`, `SourceThenDestination`, `DestinationThenSource`), `OrderedInterruption`, `CanTransitionToSelf`, `Conditions` (replaces all) |
| `RemoveTransition` | `From`, `To`, `Index` |

`Motion` is a clip path, `{"Asset": "Assets/Models/Hero.fbx", "Sub": "Run", "Type": "AnimationClip"}` for a clip inside a model, `null`, or a blend tree block as exported (`BlendTree`, `BlendType`, `Parameter`, `ParameterY`, `Children[]` with `Motion`, `Threshold` / `Position` / `DirectParameter`, `TimeScale`, `Mirror`; children can be blend trees).

Conditions: `{"Parameter", "Mode", "Threshold"}`. Modes: `If`, `IfNot` for Bool and Trigger (the default for them), `Greater`, `Less` for Float and Int (`Greater` is the default), `Equals`, `NotEqual` for Int. A mode that does not fit the parameter type is refused.

All changes of a target are recorded on one undo group and reverted when the target fails or the run is dry. A controller may appear in one target only.
