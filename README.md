# DotAsset for Unity

![Unity](https://img.shields.io/badge/Unity-2022.3%20%7C%206000.x-black?logo=unity)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![Status](https://img.shields.io/badge/status-0.2.1%20preview-orange)

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

Pin a version by adding a release tag to the URL, such as `#v0.2.1`, or `#<commit>`. Or copy `Package/` into your project as `Packages/com.alexmah.dotasset/`. It pulls in `com.unity.nuget.newtonsoft-json`.

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

No one needs to be at the machine for the editor route either: `--launch` opens the project in the editor and waits until its queue is ready, `--open-scene` opens scenes in it, and `--quit` closes it again once it is idle. `--open-scene` and `--quit` are refused while a scene has unsaved changes, unless `--force`.

```
python dotasset.py --launch                              # open the editor, wait until it takes tasks
python dotasset.py --open-scene Assets/Scenes/Main.unity # prints "previously open: ..." to put back later
python dotasset.py PrefabExport Assets/Prefabs
python dotasset.py --quit                                # close it, the project is free for headless runs again
```

A scene open in the editor is edited in place on one undo group. When Unity completes something the run added on a later frame (a new Light gets its baking state), the queue saves the scene again before it reports the run, so the scene is left clean.

Output: `Library/DotAsset/Export/<AssetPath>_<timestamp>.json`, the newest 10 per asset kept. Logs: `Library/DotAsset/Logs/`, the newest 200 kept, and `Window > DotAsset` in the editor.

**Requirements:** Unity 2022.3 or 6000.x on Windows, Python 3.8 or newer (standard library only) for the wrapper, tested on 3.8, 3.9 and 3.15. macOS and Linux are not tested: the wrapper has code paths for them, but nothing confirms they work. Apart from the assets a run is asked to change, everything DotAsset writes (exports, reports, logs, the task queue) stays under `Library/DotAsset/`, which version control ignores.

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
DotAsset 0.2.1, Unity 6000.6.7f1, batchmode
dry run, nothing is written. Add -apply to write.
Assets/Prefabs/Player.prefab: 2 op(s) would apply
[dotasset] EditPrefab dry run passed, 0 warning(s), 0 error(s), 0.4s
```

Then add `-apply`. If any op fails, nothing of that prefab is saved and the run exits 1 with the reason. Had the `Set` op named `Player/Hnad` by mistake, it would read:

```
error: EditPrefab failed for Assets/Prefabs/Player.prefab. Reason: Ops[1] Set: no GameObject 'Hnad' under 'Player'. There: Hand, Hand.R
```

Audits close the loop the other way: `AuditTexture` and the other audits write a `.fix.json` next to their report, a ready spec for the Edit or Migrate run that fixes what they found.

## With an agent: possible without it, reliable with it

An AI agent such as Claude can do most of this without DotAsset, by editing the YAML or by writing a one-off editor script and running Unity itself. We tried a small, typical task to see where the difference lies:

1. Make an NPC prefab: an empty root with a nested cube
2. Change the cube to a cylinder and its material to green
3. Write a script that wanders inside a sphere and idles, and attach it to the root
4. Place 100 NPCs in a 10 x 10 square, 10 units apart
5. Make a second prefab, a plain cube
6. Find the NPCs in the scene and swap them for cubes in a checkerboard
7. Save and check the result

| Approach | What happens |
| :-- | :-- |
| Edit the YAML | Fine for the first steps. The swap means pointing 50 prefab instances at another prefab and rewriting every override that targets the old prefab's objects. A wrong fileID does not fail, it leaves a broken asset that nothing reports until someone opens Unity |
| One-off editor script | Works, and Claude can write it. But each task rebuilds part of what DotAsset already is, without a dry run, without all-or-nothing writes, and without a way to read the result back. Running Unity by hand has its own traps: a batch run started directly empties the scene setup the editor reopens |
| DotAsset | Every step is a known run with a known result. The swap is proved by a dry run before it is written, and every step is read back through an export: 50 NPCs and 50 cubes, rows alternating `P N P N` and `N P N P`, every position kept, no broken reference |

[`examples/npc_scenario.py`](examples/npc_scenario.py) is that task as an agent would run it, with every check. It passes on Unity 6.6 (URP), 6.0 (Built-in) and inside a 2,071-asset project, in about 80 seconds headless. Changing one value in one file is just as safe as a plain edit. DotAsset pays off as the change grows and as it touches prefabs, scenes and references.

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
- Headless runs of one project take turns through `Queue/batch.lock`, held for the whole run and released by the OS if the wrapper dies, so parallel callers queue up instead of racing for the project.
- Work you have open and have not saved is never written over: every run that writes refuses a scene, a Prefab Mode prefab or any other asset (a material, controller or prefab edited in the Inspector) with unsaved changes and says why.
- Queue states: `ready`, `busy`, `compiling`, `importing`, `playmode`, `reloading`, `starting`, `paused`. Tasks wait until the editor is `ready`.
- Both routes return the same `RunResult` (exit code, summary, every log line, outputs), and the wrapper prints it the same way.

Commands are plain classes: derive from `DotAssetCommand`, name the class `<RunName>Command`, and it is registered — including from your own editor assemblies. The name picks the group, checked in this order: `*Export`, `*Import` / `Create*`, `Edit*`, `Audit*`, everything else Migrate. Prefixes match letters, not words: `EditorTidy` is listed and documented as an Edit run, so name a Migrate run `TidyEditor` instead. A name ending in `Export` takes the export arguments (`-outdir`, `-full`, `-maxarray`, `-maxstring`) whatever its prefix.

## Compared with other tools

| Tool | Editor closed | Writes | Best for |
| :-- | :-: | :-- | :-- |
| **DotAsset** | ✓ (headless) | Fixed runs: dry run by default, all or nothing per asset, unknown keys refused, never over unsaved work in the editor | Reference-aware migrations (remap references and scripts, moves, deletes that check users), audits with fix specs, repeatable asset edits, in CI or by an agent |
| [Unity CLI](https://unity.com/blog/meet-the-unity-cli) with `com.unity.pipeline` (Unity 6+) | Headless `unity run` | About 150 editor commands, `batch` of up to 200 ops on one undo with rollback and a dry run, `eval` for any C#, custom `[CliCommand]`s, `unity mcp` for agents | Editor installs, builds, tests, Project Auditor, general editor work |
| MCP servers for Unity (e.g. [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp), [IvanMurzak/Unity-MCP](https://github.com/IvanMurzak/Unity-MCP)) | ✗ | Editor tools, including editing prefab contents, some with all-or-nothing queries | Interactive work in an open editor |
| [UniCli](https://github.com/yucchiy/UniCli) | ✗ | 80+ editor commands (GameObjects, scenes, prefabs, materials, builds, tests, packages, `Eval`), refuses dirty scenes by default | Driving the whole editor from a terminal: compile, test, build, inspect |
| YAML parsers (e.g. [unityparser](https://pypi.org/project/unityparser)) | ✓ (no Unity at all) | Raw YAML, no validation, no reimport | Reading or patching files without Unity |

Scene and prefab editing, headless runs and agent access are now common. What DotAsset adds is the migrations: none of the tools above repoints every reference to an asset or a script, or refuses to delete an asset still in use. The table reflects their docs as of October 2026, and the Unity CLI and Pipeline package are still in beta.

They combine well: an agent can drive the editor through the Unity CLI, an MCP server or UniCli (builds, tests, the Console), and call DotAsset for asset changes that must be safe and repeatable, with the editor open or closed.

## Troubleshooting

| Symptom | Cause and fix |
| :-- | :-- |
| `python` opens the Microsoft Store | Windows ships an alias, not Python. Install Python 3.8+ or call a real interpreter by path |
| A task waits and never runs | The editor's queue only takes tasks when it is `ready` (not compiling, importing, in play mode or paused). `--status` shows the state |
| `CreateAsset` cannot create a scene, or `EditScene` cannot open one | The editor shows an unsaved Untitled scene. Open or save a scene first |
| `--launch` times out | Compile errors, a Safe Mode prompt or a license dialog hold the editor. The error names the Editor.log to check |
| `no Unity <version> found` | Pass `--unity <path to the editor>` or set `DOTASSET_UNITY` |
| `--quit` is refused | A scene or Prefab Mode has unsaved changes. Save them, or `--quit --force` to discard them |
| `another DotAsset headless run has the project, waiting` | Headless runs of one project take turns, each waits up to `--timeout` for the one before. For many runs in a row, `--launch` the editor once: queued runs skip Unity's start-up |

## Development

```
Package/                  the UPM package (com.alexmah.dotasset)
  Editor/Core/            dispatcher, task queue, headless entry, value codec, sessions
  Editor/<Group>/         one <RunName>Command.cs per run
  Scripts~/dotasset.py    the wrapper
Docs/                     agent-facing docs, one per group plus AI-Guide.md
tests/                    wrapper tests and naming-rule checks, tests/smoke/ the end-to-end test
examples/                 agent tasks done with DotAsset, e.g. npc_scenario.py
tools/gen_meta.py         writes missing .meta files
```

- `python -m unittest discover -s tests` runs the wrapper tests against a fake editor and a fake Unity
- `python tests/smoke/run_smoke.py --project <project> --clean` runs every group end to end in a real project, under `Assets/DotAssetSmoke`. Run it with the editor closed, then again after `--launch`. What it checks is under [Status](#what-the-smoke-test-checks)
- `python tools/gen_meta.py` after adding a file (a git-installed package ignores files without a `.meta`)
- A new run is one class: derive from `DotAssetCommand`, name it `<RunName>Command`, put it in the folder its name's group resolves to. The code rules are in [.claude/CLAUDE.md](.claude/CLAUDE.md)

## Status

0.2.1 is a preview. Tested Unity versions, each with the smoke test both with the editor closed and open:

| Unity | Version tested | Result | Date |
| :-- | :-- | :-- | :-- |
| 6.6 | 6000.6.7f1 | passes | 2026-10-25 |
| 6.3 LTS | 6000.3.27f1 | passes | 2026-10-25 |
| 6.0 LTS | 6000.0.85f1 | passes | 2026-10-25 |
| 2022.3 LTS | 2022.3.18f1 | passes | 2026-10-25 |

Each release also passes the smoke test in a 2,071-asset project (1,000 textures, 500 materials, 500 prefabs, 20 scenes with 100 instances each), where project-wide runs take 3.5 to 6 seconds headless and under 2 seconds with the editor open. Patches within a tested version are expected to work, Unity keeps the API stable within a version. Rerun the smoke test when moving to a new Unity version. The wrapper has its own unit tests (both routes, startup wait, fallback, quoting, timeouts, exit codes, log clean-up), see [Development](#development).

### What the smoke test checks

Every write is read back through an export. An exit code alone never counts as a pass.

| Area | Checked |
| :-- | :-- |
| Setup | The project runs this repository's DotAsset version, with every run registered in the group its name gives |
| Import | `CreateAsset` builds a material, a prefab with components, a prefab variant with an override, an Animator Controller with states and a transition, and a scene with a prefab instance. Running the same spec again skips what exists and changes nothing |
| Export | Every export names its type and version in its header. `AssetReferenceExport` lists the prefab as a user of its material, and `TypeCatalogExport` lists `Rigidbody.m_Mass` with its default. Writing an export's properties back leaves the export identical |
| Edit | `EditPrefab` adds, sets and removes. `EditScene` adds a Light and overrides an instance. `EditMaterial` and `EditAsset` set a render queue. `EditAnimatorController` adds a transition and renames a parameter, along with every condition that reads it. `ArrayOps` Append, Insert and Remove land where they should. A Particle System's start lifetime and emission rate are set through their module paths |
| Dry runs | Every Edit and Migrate run without `-apply` leaves the files as they were. A dry run in the open editor leaves nothing behind for the next run to save |
| All or nothing | An edit with one bad op fails the target and saves none of it. A misspelt argument (`-aply`) is refused |
| Migrate | `DuplicateAsset` copies, `RemapReference` swaps the reference, `MoveAsset` keeps it working, `ResaveAsset` rewrites the files without changing what they hold, and `DeleteAsset` deletes an unused asset but refuses one still in use |
| Audit | Each audit gets a seeded issue to find: a reference to a deleted material (R1), a Missing Script written into a prefab (R2), a keyword the shader does not declare (M5), and a texture named like a normal map but not imported as one (T1). The audit's own fix spec, run dry and then with `-apply`, must clear it, and `RemoveMissingScripts` must leave the prefab's other components alone |
| Read only | A full set of exports over the edited assets changes no byte on disk |
| Unsaved work | With the editor open, a material, a controller and a prefab are given unsaved changes. Every run that writes refuses them, the files stay byte-identical, and the changes are still there to save afterwards. The changes are made by a small helper run the smoke test installs as `Packages/com.alexmah.dotasset.smoke` in the test project, compiled at the next Unity start |
| Your project | The run fails if anything in `Assets/` outside `Assets/DotAssetSmoke` changed, or if it leaves a Unity log behind. It stops at the first failure (`--keep-going` runs the rest, `--only "edit asset,delete"` runs just the named steps after `create`) and removes its folder however the run ends, Ctrl+C included |

With the editor open, the texture and Missing Script steps are skipped: Unity only reads a new or edited file when it starts. The run with the editor closed covers them.

Still to be confirmed, because the smoke test cannot set it up: edits to a scene that is open in the editor, which go through one undo group, `RemoveMissingScripts` included. The smoke test never opens a scene in your editor. Please open issues with the Console output.

## Contributors

- Alex Mah, maintainer
- [Claude Code](https://claude.com/claude-code) (Anthropic), AI contributor, co-author on the commits it worked on

## Credits

The design of the five groups, the closed loop and the dual pipeline comes from [uasset-workbench](https://github.com/hchia93/uasset-workbench) by [hchia93](https://github.com/hchia93). DotAsset for Unity is a separate project, written for Unity, and is not maintained by or affiliated with the uasset-workbench author.

## Disclaimer

DotAsset for Unity is an independent project. It is not affiliated with, endorsed by or sponsored by Unity Technologies. Unity is a trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

## License

[MIT](LICENSE)
