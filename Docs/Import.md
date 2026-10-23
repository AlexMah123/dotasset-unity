# Import

Builds new assets from a spec. Changing existing assets is [Edit](Edit.md), moving and deleting is [Migrate](Migrate.md).

## CreateAsset

```bash
python assetops.py CreateAsset --args '-spec="C:/temp/create.json"'
```

Import writes without `-apply` (it is accepted and ignored): creating is not destructive, and an existing path is never overwritten. It is skipped with a warning and counted under `already existed`.

Entries run in order, so a later entry can use what an earlier one created. Each entry is all or nothing: a failing entry leaves no file behind and the run continues with the next. Folders that a `Path` needs are created.

```json
{
  "Assets": [
    { "Kind": "Folder", "Path": "Assets/Data/Items" },

    { "Kind": "ScriptableObject", "Type": "ItemDefinition", "Path": "Assets/Data/Items/Sword.asset",
      "Properties": { "displayName": "Sword", "damage": 12, "icon": "Assets/UI/sword.png" } },

    { "Kind": "Material", "Path": "Assets/Materials/Rust.mat", "Shader": "Universal Render Pipeline/Lit",
      "Properties": { "_BaseColor": "#8A4B2A", "_Smoothness": 0.2, "_BaseMap": "Assets/Textures/Rust.png" },
      "EnableKeywords": ["_NORMALMAP"] },

    { "Kind": "Material", "Path": "Assets/Materials/RustDark.mat", "Parent": "Assets/Materials/Rust.mat",
      "Properties": { "_BaseColor": "#3A2010" } },

    { "Kind": "Prefab", "Path": "Assets/Prefabs/Crate.prefab", "Root": "Crate",
      "Components": [ "BoxCollider", { "Type": "Rigidbody", "Properties": { "m_Mass": 5 } } ],
      "Ops": [
        { "Op": "AddGameObject", "Parent": "Crate", "Name": "Mesh", "Components": ["MeshFilter", "MeshRenderer"] },
        { "Op": "Set", "Path": "Crate/Mesh", "Component": "MeshRenderer",
          "Properties": { "m_Materials": ["Assets/Materials/Rust.mat"] } }
      ] },

    { "Kind": "PrefabVariant", "Path": "Assets/Prefabs/HeavyCrate.prefab", "Base": "Assets/Prefabs/Crate.prefab",
      "Ops": [ { "Op": "Set", "Path": "HeavyCrate", "Component": "Rigidbody", "Properties": { "m_Mass": 50 } } ] },

    { "Kind": "AnimatorController", "Path": "Assets/Anim/Door.controller",
      "Ops": [
        { "Op": "AddParameter", "Name": "Open", "Type": "Bool" },
        { "Op": "AddState", "Path": "Closed", "Motion": "Assets/Anim/DoorClosed.anim", "Default": true },
        { "Op": "AddState", "Path": "Opened", "Motion": "Assets/Anim/DoorOpen.anim" },
        { "Op": "AddTransition", "From": "Closed", "To": "Opened", "Conditions": [ { "Parameter": "Open", "Mode": "If" } ] }
      ] },

    { "Kind": "Scene", "Path": "Assets/Scenes/Sandbox.unity", "Setup": "Default", "AddToBuild": true,
      "Ops": [ { "Op": "AddPrefabInstance", "Prefab": "Assets/Prefabs/Crate.prefab", "Name": "Crate" } ] }
  ]
}
```

| Kind | Keys |
| --- | --- |
| `Folder` | `Path` |
| `ScriptableObject` | `Type` (class name, full name, or the `.cs` path), `Properties`, `ArrayOps` ([value format](Export.md#value-format)) |
| `Material` | `Shader` (name or shader asset path; default: the render pipeline's default material shader, `Standard` without one), and every [EditMaterial](Edit.md#editmaterial) key: `Parent`, `Properties`, `EnableKeywords`, `DisableKeywords`, `RenderQueue`, ... |
| `Prefab` | `Root` (the root's name while the ops run, default the file name; Unity names a saved prefab's root after its file, so keep them equal), `RectTransform: true` for UI, `Components` (type names, or `{Type, Properties}`), `Ops` ([hierarchy ops](Edit.md#hierarchy-ops)) |
| `PrefabVariant` | `Base` (prefab or model), `Ops`. The variant's root is named after the file |
| `AnimatorController` | `Ops` ([controller ops](Edit.md#editanimatorcontroller)) |
| `Scene` | `Setup`: `Empty` (default) or `Default` (camera and light), `Ops` (hierarchy ops, `Parent` omitted means a scene root), `AddToBuild` |

Paths must be under `Assets/`. Unknown keys are refused with exit 2 before anything is created, so a misspelt `Propeties` cannot be dropped silently.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | every entry created or already existed |
| 1 | at least one entry failed, the others were created |
| 2 | the spec could not be read, or has an unknown key |
