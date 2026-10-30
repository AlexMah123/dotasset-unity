---
name: dotasset
description: Read, create, edit, move and audit Unity assets (prefabs, scenes, ScriptableObjects, materials, animator controllers, import settings) from the command line through the DotAsset wrapper. Use for any Unity asset change instead of hand-editing YAML.
---

# DotAsset

Copy this file to `.claude/skills/dotasset/SKILL.md` in a Unity project that has the DotAsset package. Full manual: `Docs/AI-Guide.md`, then the group docs (`Export.md`, `Import.md`, `Edit.md`, `Migrate.md`, `Audit.md`).

## Call

```bash
python dotasset.py <RunName> "<AssetList>" --args "<-key=value ...>" --agent
```

- `dotasset.py` sits in the package's `Scripts~/` or the project root. Run it inside the project, or pass `--project DIR`.
- It uses the open editor when there is one, headless Unity otherwise. `--launch` opens the editor, `--quit` closes it.
- Always pass `--agent`. A clean run prints one line, `exit <code> <summary>`. Lines that follow start with `error:`, `warning:`, `failed:`, `out:`, `plan:` or `log:`.
- `--list` prints every run, project-specific ones included.

## Pick a run

| Need | Run |
| --- | --- |
| Look inside a prefab, scene, asset, material, controller, importer | `PrefabExport`, `SceneExport`, `AssetExport`, `MaterialExport`, `AnimatorControllerExport`, `ImporterExport` |
| What uses an asset, what it uses | `AssetReferenceExport` |
| Property names and defaults of a type | `TypeCatalogExport --args "-filter=Rigidbody"` |
| Create folders, ScriptableObjects, materials, prefabs, variants, controllers, scenes | `CreateAsset` |
| Change a prefab or scene: GameObjects, components, fields, prefab instances | `EditPrefab`, `EditScene` |
| Change a ScriptableObject, material, importer, animator controller | `EditAsset`, `EditMaterial`, `EditImporter`, `EditAnimatorController` |
| Move, copy, delete assets with references kept | `MoveAsset`, `DuplicateAsset`, `DeleteAsset` |
| Swap every use of one asset for another | `RemapReference` |
| Fix Missing Script | `AuditMissingReference`, then `RemapScript` or `RemoveMissingScripts` |
| Renamed a serialized field | `[FormerlySerializedAs("old")]`, then `ResaveAsset --args "-dependson=<script>"` |
| Find broken textures, materials, references | `AuditTexture`, `AuditMaterial`, `AuditMissingReference` |

## Workflow

1. Export first. The export gives exact property names (`m_Mass`, not `mass`), hierarchy paths (`Player/Model/Hand.R`) and reference forms. Exports can be long, so grep the `out:` file before reading it.
2. Write a spec JSON for Create and Edit runs. Targets live in the spec, so leave AssetList empty:

```json
{ "Targets": [ { "AssetPath": "Assets/Prefabs/Player.prefab", "Ops": [
  { "Op": "Set", "Path": "Player", "Component": "PlayerController", "Properties": { "speed": 7 } }
] } ] }
```

3. Dry run, `--args '-spec="C:/temp/player.json"'`, then read the `plan:` lines (before and after values, files it would write).
4. Apply with `--args '-spec="C:/temp/player.json" -apply'`. Edit and Migrate runs never write without `-apply`.
5. Export again to confirm the change. Do not trust the exit code alone.

Audits write `<report>.fix.json` for the matching fix run (`EditImporter`, `EditMaterial`, `RemoveMissingScripts`). Review it, dry run it, then apply it.

## Exit codes

| Code | Meaning | Do |
| --- | --- | --- |
| 0 | success, or a clean dry run | continue |
| 1 | a target failed and nothing of it was saved | read `error:`, fix the spec, retry |
| 2 | refused: unknown run, argument or spec key | check the group doc, `--list` |
| 3 | audit found issues | read the report or fix spec |

## Rules

- Never hand-edit `.prefab`, `.unity`, `.asset` or `.meta` files, and never call `AssetDatabase.SaveAssets`.
- Spec paths are absolute or relative to the project root. Quote values that contain spaces.
- Writes are all or nothing per target, so a failed target leaves its file unchanged.
- `--quit` and `--open-scene` refuse while there are unsaved changes. Ask the user before adding `--force`.
