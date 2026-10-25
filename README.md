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
https://github.com/AlexMah123/dotasset-unity.git?path=/Package
```

Pin a version by adding a release tag to the URL, such as `#v0.1.1`, or `#<commit>`. Or copy `Package/` into your project as `Packages/com.alexmah.dotasset/`. It pulls in `com.unity.nuget.newtonsoft-json`.

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

No one needs to be at the machine for the editor route either: `--launch` opens the project in the editor and waits until its queue is ready, and `--quit` closes it again once it is idle (refused while a scene has unsaved changes, unless `--force`).

```
python dotasset.py --launch      # open the editor, wait until it takes tasks
python dotasset.py PrefabExport Assets/Prefabs
python dotasset.py --quit        # close it, the project is free for headless runs again
```

Output: `Library/DotAsset/Export/<AssetPath>_<timestamp>.json`. Logs: `Library/DotAsset/Logs/`, and `Window > DotAsset` in the editor.

**Requirements:** Unity 2022.3 or 6000.x, Python 3.8 or newer (standard library only) for the wrapper, Windows, macOS or Linux. Everything DotAsset writes stays under `Library/DotAsset/`, which version control ignores.

## A round trip, end to end

Export a prefab. Component properties come out as the delta from a fresh component, references as paths:

```bash
python dotasset.py PrefabExport Assets/Prefabs/Player.prefab
```

```json
{
  "ExportType": "PrefabExport",
  "AssetPath": "Assets/Prefabs/Player.prefab",
  "Hierarchy": [
    {
      "Path": "Player",
      "Components": [
        { "Type": "Rigidbody", "Properties": { "m_Mass": 2, "m_Interpolate": "Interpolate" } },
        { "Type": "PlayerController", "Script": "Assets/Scripts/PlayerController.cs",
          "Properties": { "speed": 6.5, "weapon": { "Object": "Player/Hand/Gun" } } }
      ]
    }
  ]
}
```

Write an edit spec in the same terms. A `Properties` block from the export can be pasted into a `Set` op as is:

```json
{
  "Targets": [
    {
      "AssetPath": "Assets/Prefabs/Player.prefab",
      "Ops": [
        { "Op": "AddGameObject", "Parent": "Player", "Name": "Hand.R", "Components": ["BoxCollider"] },
        { "Op": "Set", "Path": "Player", "Component": "PlayerController", "Properties": { "speed": 7 } }
      ]
    }
  ]
}
```

Run it without `-apply` first. The dry run does everything a real run does, checks it just as hard, and then drops the changes:

```
$ python dotasset.py EditPrefab --args '-spec="C:/temp/player.edit.json"'
[dotasset] route: batchmode (C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe)
DotAsset 0.1.0, Unity 6000.6.7f1, batchmode
dry run, nothing is written. Add -apply to write.
Assets/Prefabs/Player.prefab: 2 op(s) would apply
[dotasset] EditPrefab dry run passed, 0 warning(s), 0 error(s), 0.4s
```

Then add `-apply`. If any op fails, nothing of that prefab is saved and the run exits 1 with the reason. Had the `Set` op named `Player/Hnad` by mistake, it would read:

```
error: EditPrefab failed for Assets/Prefabs/Player.prefab. Reason: Ops[1] Set: no GameObject 'Hnad' under 'Player'. There: Hand, Hand.R
```

Audits close the loop the other way: `AuditTexture` and the other audits write a `.fix.json` next to their report, a ready spec for the Edit or Migrate run that fixes what they found.

## Exit codes

| Code | Meaning |
| :-: | :-- |
| 0 | Success, or a dry run that would succeed |
| 1 | Failed: a target could not be written (nothing of it was saved), or Unity failed before the run started |
| 2 | Refused: unknown run or argument, bad spec, misspelt key, or an editor that cannot take the task |
| 3 | Audits only: the run succeeded and the report lists issues |

`--json` prints the full result as well: every log line, the outputs, the failed targets.

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

## Compared with other tools

| Tool | Editor closed | Writes | Best for |
| :-- | :-: | :-- | :-- |
| **DotAsset** | ✓ (headless) | Fixed runs: dry run by default, all or nothing per asset, unknown keys refused | Repeatable asset changes, migrations and audits, in CI or by an agent |
| [UniCli](https://github.com/yucchiy/UniCli) | ✗ | 80+ editor commands (GameObjects, scenes, prefabs, materials, builds, tests, packages, `Eval`), refuses dirty scenes by default | Driving the whole editor from a terminal: compile, test, build, inspect |
| MCP servers for Unity (e.g. [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp)) | ✗ | Free-form editor tools | Interactive work in an open editor |
| [Unity CLI](https://unity.com/blog/meet-the-unity-cli) with `com.unity.pipeline` | Editor features need a running Unity 6 editor | `eval` runs any C#, custom `[CliCommand]`s | Editor installs, builds, tests, ad-hoc C# |
| YAML parsers (e.g. [unityparser](https://pypi.org/project/unityparser)) | ✓ (no Unity at all) | Raw YAML, no validation, no reimport | Reading or patching files without Unity |

They combine well: an agent can drive the editor through UniCli, an MCP server or the Unity CLI (builds, tests, the Console), and call DotAsset for asset changes that must be safe and repeatable, with the editor open or closed.

## Troubleshooting

| Symptom | Cause and fix |
| :-- | :-- |
| `python` opens the Microsoft Store | Windows ships an alias, not Python. Install Python 3.8+ or call a real interpreter by path |
| A task waits and never runs | The editor's queue only takes tasks when it is `ready` (not compiling, importing, in play mode or paused). `--status` shows the state |
| `CreateAsset` cannot create a scene, or `EditScene` cannot open one | The editor shows an unsaved Untitled scene. Open or save a scene first |
| `--launch` times out | Compile errors, a Safe Mode prompt or a license dialog hold the editor. The error names the Editor.log to check |
| `no Unity <version> found` | Pass `--unity <path to the editor>` or set `DOTASSET_UNITY` |
| `--quit` is refused | A scene or Prefab Mode has unsaved changes. Save them, or `--quit --force` to discard them |

## Development

```
Package/                  the UPM package (com.alexmah.dotasset)
  Editor/Core/            dispatcher, task queue, headless entry, value codec, sessions
  Editor/<Group>/         one <RunName>Command.cs per run
  Scripts~/dotasset.py    the wrapper
Docs/                     agent-facing docs, one per group plus AI-Guide.md
tests/                    wrapper tests and naming-rule checks, tests/smoke/ the end-to-end test
tools/gen_meta.py         writes missing .meta files
```

- `python -m unittest discover -s tests` runs the wrapper tests against a fake editor and a fake Unity
- `python tests/smoke/run_smoke.py --project <project> --clean` runs every group end to end in a real project. Run it headless, then again after `--launch`
- `python tools/gen_meta.py` after adding a file (a git-installed package ignores files without a `.meta`)
- A new run is one class: derive from `DotAssetCommand`, name it `<RunName>Command`, put it in the folder its name's group resolves to. The code rules are in [.claude/CLAUDE.md](.claude/CLAUDE.md)

## Status

0.1.1 is a preview. Tested Unity versions, each with the smoke test both with the editor closed and open:

| Unity | Version tested | Result | Date |
| :-- | :-- | :-- | :-- |
| 6.6 | 6000.6.7f1 | passes | 2026-10-25 |
| 6.3 LTS | 6000.3.27f1 | passes | 2026-10-25 |
| 6.0 LTS | 6000.0.85f1 | passes | 2026-10-25 |
| 2022.3 LTS | 2022.3.18f1 | passes | 2026-10-25 |

Patches within a tested version are expected to work, Unity keeps the API stable within a version. Rerun the smoke test when moving to a new Unity version.

- `python -m unittest discover -s tests` covers the wrapper: both routes, startup wait, fallback, quoting, timeouts, exit codes.
- `python tests/smoke/run_smoke.py --project <your project>` runs every group end to end in a real project, under `Assets/DotAssetSmoke`, and cleans up (`--clean` removes leftovers of an interrupted run first). Run it once with the editor closed and once with it open.

The smoke test reads every change back through an export instead of trusting exit codes, checks that dry runs and failed targets leave files untouched and that exports change nothing on disk, and seeds a missing reference, a stray keyword and a misnamed normal map for the audits to find and fix. It is safe to run in a real project: it fails if anything in `Assets/` outside its own folder changed, stops at the first failure (`--keep-going` runs the rest), and removes its folder however the run ends.

Still to be confirmed, because the smoke test cannot set it up: undo coverage of `RemoveMissingScripts` in a scene open in the editor (it needs a Missing Script, which needs a script deleted and a recompile). Please open issues with the Console output.

## Contributors

- Alex Mah, maintainer
- [Claude Code](https://claude.com/claude-code) (Anthropic), AI contributor, co-author on the commits it worked on

## Credits

The design of the five groups, the closed loop and the dual pipeline comes from [uasset-workbench](https://github.com/hchia93/uasset-workbench) by [hchia93](https://github.com/hchia93). DotAsset for Unity is a separate project, written for Unity, and is not maintained by or affiliated with the uasset-workbench author.

## Disclaimer

DotAsset for Unity is an independent project. It is not affiliated with, endorsed by or sponsored by Unity Technologies. Unity is a trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

## License

[MIT](LICENSE)
