# DotAsset for Unity

![Unity](https://img.shields.io/badge/Unity-2022.3%20%7C%206000.x-black?logo=unity)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![Status](https://img.shields.io/badge/status-0.1.0%20preview-orange)

An editor package that lets scripts and AI agents read, write, edit and audit Unity assets — with the editor open or closed, through one command line.

Its design follows [uasset-workbench](https://github.com/hchia93/uasset-workbench) by [hchia93](https://github.com/hchia93), a tool for Unreal Engine `.uasset` files: the same five groups, the same closed loop (export output reads back as an edit spec, audit reports carry fix specs), the same safety rules (all or nothing per asset, dry run by default), and the same dual pipeline (an in-editor task queue when the editor is open, a headless run when it is closed, identical output either way).

## What it solves

Unity serializes most assets as YAML, so an agent can already read them. What it cannot do safely is change them:

| Problem | With text edits | Group |
| :-- | :-- | :-: |
| References are opaque | `{fileID: 2100000, guid: 6e1f..., type: 2}` says nothing; overrides of nested prefabs are a modification list | Export |
| Building assets by hand | Creating a prefab, a material variant or an Animator Controller in YAML means inventing fileIDs | Import |
| Edits are unchecked | A wrong fileID or a corrupt `m_Modifications` block breaks silently; nothing compiles or reimports | Edit |
| Moves and script changes break things | `Resources.Load` strings, lost `.meta` files (Missing Script), renamed fields without `FormerlySerializedAs` | Migrate |
| No batch questions | Which textures are uncompressed, which materials render pink, which prefabs have missing scripts | Audit |

Every write goes through Unity's own API (`SerializedObject`, `PrefabUtility`, `AnimatorController`, importers), so Unity validates it, reimports it, and keeps GUIDs intact.

## Five groups

| Group | Runs |
| :-- | :-- |
| **Export** | `PrefabExport` `SceneExport` `AssetExport` `MaterialExport` `AnimatorControllerExport` `ImporterExport` `AssetReferenceExport` `TypeCatalogExport` `CommandCatalogExport` |
| **Import** | `CreateAsset` (folders, ScriptableObjects, materials, prefabs, prefab variants, Animator Controllers, scenes) |
| **Edit** | `EditPrefab` `EditScene` `EditAsset` `EditMaterial` `EditImporter` `EditAnimatorController` |
| **Migrate** | `MoveAsset` `DuplicateAsset` `DeleteAsset` `ResaveAsset` `RemapReference` `RemapScript` `RemoveMissingScripts` |
| **Audit** | `AuditMissingReference` `AuditTexture` `AuditMaterial` |

- **Closed loop**: an export's `Properties` block is what `Set` ops and `EditAsset` take; `ImporterExport`'s texture `Summary` is what `EditImporter`'s `Texture` block takes; audits write a `.fix.json` the matching Edit or Migrate run takes as is
- **Editor open**: the task goes to the editor's queue and runs on its main thread, the editor stays open, a scene open in the editor is edited on one undo group (one Ctrl+Z)
- **Editor closed**: Unity starts with `-batchmode -nographics`, runs the task, exits
- **Writes**: all or nothing per target, Edit and Migrate are dry runs until `-apply`, unknown spec keys and arguments are refused rather than ignored

## Supported assets

| Asset | Export | Import | Edit | Migrate | Audit |
| :-- | :-: | :-: | :-: | :-: | :-: |
| Prefab, prefab variant | ✓ | ✓ | ✓ | ✓ | ✓ |
| Scene | ✓ | ✓ | ✓ | ✓ | ✓ |
| ScriptableObject | ✓ | ✓ | ✓ | ✓ | ✓ |
| Material | ✓ | ✓ | ✓ | ✓ | ✓ |
| Animator Controller | ✓ | ✓ | ✓ | ✓ | ✓ |
| Texture, model, audio import settings | ✓ | | ✓ | | ✓ (textures) |
| Any other asset (sub-assets included) | ✓ | | ✓ | ✓ | ✓ (references) |

## Quick start

**1. Add the package** (Unity 2022.3 or 6000.x)

Window > Package Manager > + > Add package from git URL:

```
https://github.com/<you>/DotAsset.git?path=/Package
```

Or copy `Package/` into your project as `Packages/com.alexmah.dotasset/`. It pulls in `com.unity.nuget.newtonsoft-json`.

**2. Get the wrapper**

The **Copy dotasset.py** button in `Window > DotAsset` puts it at the project root. It is one Python 3.8+ file with no dependencies, so it also works copied anywhere.

**3. Run**

```bash
# Export: a prefab's hierarchy, components and overrides as JSON
python dotasset.py PrefabExport Assets/Prefabs/Player.prefab

# Import: create assets from a spec
python dotasset.py CreateAsset --args '-spec="C:/temp/create.json"'

# Edit: change a prefab, -apply writes it
python dotasset.py EditPrefab --args '-spec="C:/temp/player.edit.json" -apply'

# Migrate: point every reference to an old material at a new one
python dotasset.py RemapReference --args '-from=Assets/Mat/Old.mat -to=Assets/Mat/New.mat -apply'

# Audit: check every texture's import settings
python dotasset.py AuditTexture --args '-scandir=Assets'
```

The wrapper finds the project from the current directory (or `--project`) and the editor from `ProjectSettings/ProjectVersion.txt` through Unity Hub's install folder (or `--unity`, or `DOTASSET_UNITY`). `python dotasset.py --list` prints every run, `--status` shows what the editor's queue is doing.

Output: `Library/DotAsset/Export/<AssetPath>_<timestamp>.json`. Logs: `Library/DotAsset/Logs/`, and `Window > DotAsset` in the editor.

## For AI agents

| Doc | Content |
| :-- | :-- |
| [Docs/AI-Guide.md](Docs/AI-Guide.md) | Entry point: decision table, call templates, how to read results, pitfalls |
| [Docs/Export.md](Docs/Export.md) | Every export and its JSON, the value and reference formats |
| [Docs/Import.md](Docs/Import.md) | The `CreateAsset` spec |
| [Docs/Edit.md](Docs/Edit.md) | Every Edit spec, every hierarchy and controller op |
| [Docs/Migrate.md](Docs/Migrate.md) | Moves, deletes, reference and script remaps, field renames |
| [Docs/Audit.md](Docs/Audit.md) | Rule tables and the fix-spec workflow |

## How it works

```
                         ┌───────── editor open ──────────┐
python dotasset.py ──┬──▶│ Library/DotAsset/Queue/pending │──▶ TaskQueue (main thread) ──┐
                     │   └────────────────────────────────┘                              │
                     │                                                                   ├─▶ RunDispatcher ─▶ command ─▶ RunResult JSON
                     │   ┌──────── editor closed ─────────┐                              │
                     └──▶│ Unity -batchmode               │──▶ Cli.Run ──────────────────┘
                         │   -executeMethod               │
                         │   DotAsset.Cli.Run             │
                         └────────────────────────────────┘
```

- The editor writes a heartbeat (`Queue/.alive`, its pid and state) every 2 seconds. The wrapper trusts the pid only when it belongs to a live Unity process, so a heartbeat left by a crash is ignored.
- When Unity holds the project (`Temp/UnityLockfile` locked) but no heartbeat is up yet, the editor is still starting or compiling. The wrapper waits for the heartbeat instead of starting a second Unity that would fail on the lock.
- Queue states: `ready`, `busy`, `compiling`, `importing`, `playmode`, `reloading`, `starting`, `paused`. Tasks wait until the editor is `ready`.
- Both routes return the same `RunResult` (exit code, summary, every log line, outputs), and the wrapper prints it the same way.

Commands are plain classes: derive from `DotAssetCommand`, name the class `<RunName>Command`, and it is registered — including from your own editor assemblies. The name picks the group: `*Export`, `*Import` / `Create*`, `Edit*`, `Audit*`, everything else Migrate.

## Status

0.1.0 is a preview. It compiles and passes the smoke test on Unity 6000.6, both with the editor closed and open. It was written against Unity's C# reference source for 2022.3 and 6000.3, but has not been compiled on 2022.3 or 6000.0-6000.3 yet.

- `python -m unittest discover -s tests` covers the wrapper: both routes, startup wait, fallback, quoting, timeouts, exit codes.
- `python tests/smoke/run_smoke.py --project <your project>` runs every group end to end in a real project, under `Assets/DotAssetSmoke`, and cleans up (`--clean` removes leftovers of an interrupted run first). Run it once with the editor closed and once with it open.

Behaviour that lives in Unity's native code, and that the smoke test does not reach, is still to be confirmed: `ArrayOps` `Append` (`InsertArrayElementAtIndex` at the end), undo coverage of `RemoveMissingScripts` in an open scene, and the undo revert of an `EditAnimatorController` dry run in batch mode (a failed or dry target is never saved either way). Please open issues with the Console output.

## Credits

The design of the five groups, the closed loop and the dual pipeline comes from [uasset-workbench](https://github.com/hchia93/uasset-workbench) by [hchia93](https://github.com/hchia93). DotAsset for Unity is a separate project, written for Unity, and is not maintained by or affiliated with the uasset-workbench author.

## Disclaimer

DotAsset for Unity is an independent project. It is not affiliated with, endorsed by or sponsored by Unity Technologies. Unity is a trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

## License

[MIT](LICENSE)
