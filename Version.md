# Version

Defined in `Package/Editor/Core/DotAssetVersion.cs` and `Package/package.json`, written into every export as `ExporterVersion` and every result as `DotAssetVersion`.

## 0.x

Versions follow one rule. A patch (0.1.x) holds fixes, test upgrades and docs. A minor version (0.x.0) adds something new to call: a run, an op, a spec key, an argument or a wrapper option. While on 0.x, a change that breaks existing calls is also a minor version, marked breaking here. 1.0.0 comes when runs, specs and export JSON are promised to stay stable.

### 0.7.0

- Run journal: every writing run (Import, and Edit or Migrate with `-apply`) keeps the files it touches, as they were, in `Library/DotAsset/Journal/<id>`, printed as an `out:` line. The newest 30 are kept
- `UndoRun` (Migrate): `-run=<id>` or `-run=last` puts back every file the run wrote, created, moved or deleted, moved files with their GUID. Refused as a whole when any of them changed after the run, is open in the editor or has unsaved changes. Undoing an undo redoes the run
- `-atomic` on every Edit, Migrate and Import run: all or nothing across targets. When a target fails, the files the run already wrote are restored. Without it, other targets stay written as before
- Smoke test: journal, undo of an edit and a move, `-atomic`, a refused undo after a later change. Editor states: a paused queue refuses and withdraws the task, a compile and play mode make the run wait

### 0.6.1

- Smoke test: `RemapScript` by GUID, a prefab's component moves to another script with its field value kept, headless route
- README: the 0.6 runs, an animation clip row, `--serve`, the smoke coverage, a status line that no longer names a version

### 0.6.0

- `RebindAnimation` (Migrate): rewrites curve paths in `.anim` clips after a GameObject or bone was renamed or moved, `-from=Body/Arm -to=Body/ArmL`. Children move along, float and object reference curves both, keys unchanged. A clip that already animates the same property at the new path fails its target
- `RenameShaderProperty` (Migrate): renames a saved property in `.mat` files after a shader renamed it, `-from=_Color -to=_BaseColor`, value and type kept. `-shader` limits it to one shader, `-overwrite` replaces a value already saved under the new name. Scripts and clips that still name the old property are listed
- Fix: the `--serve` host no longer leaves the editor with an Untitled scene, `--quit` restores the last scene setup
- Smoke test: steps for both runs, read back through `MaterialExport` and `AssetExport`

### 0.5.0

- Wrapper `--serve`: starts a warm headless host, batchmode Unity that stays up and takes runs through the same task queue as an open editor, until `--quit`. Many headless steps in a row skip Unity's start-up without opening an editor window. Runs through the host report `inEditor` false, `--status` prints `host pid`. `--quit` puts back the scenes the editor reopens, which the host empties when it exits
- Smoke test: the headless route sends most steps through a `--serve` host. Create, a Prefab export, the first `-apply` edit, a failing edit and the two steps that need a fresh import still launch Unity per run
- Release order, from this version on: implement, `gen_meta` if a file was added, `compile_check`, unit tests, bump the version, release notes and docs, commit and push, then the smoke matrix (3 clean passes on every tested Unity version, both routes), then the tag

### 0.4.0

- Dry runs return a plan: `Plan` in the result JSON and `plan:` lines in the wrapper. Edit runs list each value change with its property path, before and after, every Edit and Migrate run lists the files it would write, copy, move, delete, rewrite or resave. At most 500 entries, then `PlanTruncated`. `PlanTotals` counts every change, file and target, past the 500 too, and the wrapper prints it as the first `plan:` line
- Wrapper `--agent`: compact output for AI agents, the exit code and summary on one line, then only the first error, warnings, failed targets, outputs, plan and, on failure, the log path. Lists stop at 10 entries
- `Docs/SKILL.md`: a short agent skill, about 1,000 tokens, that covers the call, the run picker, the export, dry run, `-apply` workflow and exit codes. Copy it to `.claude/skills/dotasset/SKILL.md`

### 0.3.0

- Wrapper: `--launch` starts the editor minimized and keeps its windows minimized without activating them until the queue is ready, so a launch no longer takes the mouse and keyboard focus. A minimized editor still runs its task queue. `--visible` opts out

### 0.2.2

- Fix: `EditAsset`, `EditMaterial`, `EditAnimatorController`, `EditImporter`, `EditPrefab`, `RemapReference`, `ResaveAsset` and `DeleteAsset` saved over an asset with unsaved changes in the editor (edited in the Inspector, say), writing those changes to disk along with their own. Every run that writes now refuses such a target, dry runs included, and names it
- Fix: `CreateAsset` left a new Animator Controller marked unsaved, so its own ops, or the next `EditAnimatorController`, refused it under the fix above. It is saved right after it is created. New materials and other assets are saved the same way, since Unity 6.6 at times leaves a new material marked unsaved too
- Fix: `-maxarray`, `-maxstring` and `-maxsize` fell back to their defaults when the value was not a whole number. They are refused (exit 2)
- Fix: after a run saved a scene open in the editor, the queue's short wait for Unity to settle could lose the result on a script reload (reported as "the editor stopped") and could save an edit someone made in that wait. The result now survives the reload, and an edit made in the wait is left unsaved with a warning
- Docs: the comparison table covers the Unity CLI's headless runs and `batch`, and MCP servers that edit prefabs. The group rule notes that prefixes match letters (`EditorTidy` is Edit). Editing a Particle System's modules by dotted path
- Smoke test: unsaved changes in the open editor are refused and kept, a reload and an edit while a run settles, a bad number refused, a Particle System edited and read back. `--only` runs chosen steps. A small helper package, `com.alexmah.dotasset.smoke`, is installed into the test project for the editor-side steps
- `tools/compile_check.py` compiles the package with each test project's Unity compiler, and against .NET Standard 2.1 and .NET Framework 4.8, in seconds

### 0.2.1

- Fix: `DeleteAsset` deleted a scene open in the editor with unsaved changes, or the prefab in Prefab Mode with unsaved changes, along with those changes, also inside a deleted folder and with `-force`. It now fails that target with the reason
- Fix: `ResaveAsset` rewrote such a file from disk, leaving the editor and the file disagreeing. It now refuses that target and resaves the rest, and exits 1 when a target was refused instead of always 0

### 0.2.0

Same code as 0.1.2, renumbered by the rule above: 0.1.2 added a wrapper option, `--open-scene`, which makes it a minor release. The `v0.1.2` tag stays for anyone pinned to it. Nothing breaks.

- `examples/npc_scenario.py`: a small agent task done with DotAsset, every step checked, and a README section on what it shows

### 0.1.2

Stability for general and heavy use. Smoke test passed three times in a row on Unity 6.6, 6.3, 6.0 and 2022.3 and on a 2,000-asset project, headless and with the editor open.

- Fix: headless runs of one project now take turns. Two callers that both saw the project free started two Unity processes and one failed on Unity's lock (1 of 4 parallel calls). The wrapper holds `Library/DotAsset/Queue/batch.lock` for the whole run, later callers wait up to `--timeout` with a message that names a headless run, and `--launch` takes the same lock
- Fix: a scene open in the editor was left dirty after DotAsset added a Light to it, because Unity gives the new Light its baking state on a later frame. The next `EditScene` on that scene was refused. The editor queue now saves such a scene again once Unity settles, before it reports the run
- Exports keep the newest 10 per asset and audit reports the newest 20 per audit, older ones are deleted (one prefab had 436 exports). Files under `-outdir` or at a `-report` path are never deleted
- `--open-scene A.unity[,B.unity]` opens scenes in the editor without anyone at the machine, refused like `--quit` while a scene has unsaved changes, and prints the scenes that were open before
- Smoke test: edits a scene open in the editor, paths with spaces and non-ASCII letters, a refused write to a registry package, `-outdir` and `-report`, a Missing Script seeded into a prefab for `AuditMissingReference` R2 and `RemoveMissingScripts`, `ResaveAsset` keeping content, every export's header and version, and the export caps. No step passes on its exit code alone
- Wrapper tests run on Python 3.8, 3.9 and 3.15, and the two lock tests now run on Windows
- Windows is the tested platform. macOS and Linux are not tested

### 0.1.1

- Fix: `MaterialExport` left the material changed in memory. Reading a shader property lets Unity validate the material, which on Standard and URP Lit resets a custom render queue; headless Unity then saved it on quit, and in the open editor the next Save Project would have. Values are now read from a temporary copy, so the asset is left as found
- Fix: `AuditMissingReference` missed references to an asset deleted earlier in the same editor session, Unity still answers its GUID with the old path. R1 now checks an asset is at that path
- Smoke test: every change is read back through an export, dry runs and failed targets are checked to leave files untouched, exports are checked to change nothing on disk, `ArrayOps` Append, Insert and Remove are covered, and the audits are given a seeded missing reference, stray keyword and misnamed normal map to find and fix through their fix specs. It fails if anything in `Assets/` outside its folder changed, stops at the first failure (`--keep-going` runs the rest), always cleans up, and drops the Unity logs of failures it expected
- Fix: headless runs on Windows left their Unity log in `Library/DotAsset/Queue/batch`, Unity's licensing client holds it open after Unity exits. A held log is now listed and swept by the next headless run, and logs of failed runs are pruned after 7 days
- `package.json` carries author, documentation, changelog and license links for the Package Manager
- Releases are tagged, pin one with `#v0.1.1` on the git URL

### 0.1.0

First release. The design follows uasset-workbench 2.6, a tool for Unreal Engine `.uasset` files.

- Core: `DotAssetCommand` registry (class name is the run name, name decides the group), `RunDispatcher` with one result shape for both routes, refusal of unknown arguments and spec keys, Unity's own errors during a target fail that target
- Routes: in-editor task queue under `Library/DotAsset/Queue` with a heartbeat that carries pid and state, started from the first editor update so an editor that never gets focus still takes tasks, headless `-batchmode -executeMethod DotAsset.Cli.Run` with base64 arguments
- Wrapper `Scripts~/dotasset.py`: routing, startup wait on a locked project, fallback to headless when the editor exits before taking a task, Unity Hub discovery from `ProjectVersion.txt`, hints from the Unity log when a headless run cannot start, a headless run leaves the scenes the editor reopens as it found them, `--launch` and `--quit` open and close the editor without anyone at the machine
- Export: `PrefabExport`, `SceneExport`, `AssetExport`, `MaterialExport`, `AnimatorControllerExport`, `ImporterExport`, `AssetReferenceExport`, `TypeCatalogExport`, `CommandCatalogExport`. Delta from defaults, prefab instances as overrides, capped arrays and strings
- Import: `CreateAsset` for folders, ScriptableObjects, materials, prefabs, variants, Animator Controllers, scenes
- Edit: `EditPrefab`, `EditScene` (undo group when the scene is open), `EditAsset`, `EditMaterial`, `EditImporter`, `EditAnimatorController`
- Migrate: `MoveAsset`, `DuplicateAsset`, `DeleteAsset`, `ResaveAsset`, `RemapReference` (prefab instances swapped through Replace Prefab Asset), `RemapScript`, `RemoveMissingScripts`
- Audit: `AuditMissingReference` (R1-R4), `AuditTexture` (T1-T8), `AuditMaterial` (M1-M5), with fix specs for the matching Edit or Migrate run
- `Window > DotAsset`: queue state, pause, run history and logs, copy the wrapper to the project
- Writes never touch scenes the user has open: scene edits, new scenes and new prefabs make their own scene active while they build, and hand it back after
- `EditAsset` refuses imported assets (textures, models, audio), whose objects the importer rebuilds
- A string reference that is not an asset path fails with a hint to use `{"Object": ...}`
- `Static: true` sets every defined flag, the `RemoveMissingScripts` op skips objects owned by a source prefab
- Integer writes are range-checked against the field's size, a Hash128 must be 32 hex digits
- A failed property write is not applied (a new `[SerializeReference]` instance is, and the target's session rolls it back), appends grow the array instead of inserting past the end
- New GameObjects are created directly in the target scene, never parked in another open scene
- A failed target reports its own first error, not the run's
- Transition targets are matched by full state path, `ImporterExport` folders skip native-format files
- Op keys, `Components` entries, conditions, blend tree blocks and move/copy/remap pairs are checked, a misspelt key fails instead of being dropped
- A failed `CreateAsset` entry removes the folders it made, `-apply` is accepted (and ignored) by Import and Audit runs
- A scene listed but unloaded in the editor stays listed after a run, a rolled-back asset keeps its hide flags
- `DeleteAsset` ignores referencers inside any deleted folder, Int64 and UInt64 writes are range-checked too
- The wrapper ignores a heartbeat that names another project, the smoke test refuses leftovers unless `--clean`
- A run that logs an error of its own exits 1 even when its targets succeeded, e.g. an AssetList entry that does not exist
- A target is never reported failed after it reached disk: checks run before the save, errors during it are warnings
- Bare words in `--args` are refused, so `apply` cannot act as `-apply`
- `RemapScript` with the editor open refuses files loaded there, `EditImporter` folders skip native-format files
- Replacing a state's blend tree removes the old one from the controller, `RenameParameter` skips empty states
- A grown `[SerializeReference]` array no longer shares the last element's instance
- Root `SiblingIndex` is refused under undo, the queue survives malformed task files, a broken `.prefab` fails only its entry
- The wrapper only trusts `Unity`/`Unity.exe` processes and ignores long-dead idle heartbeats, macOS paths compare case-insensitively
- Targets in read-only packages (registry, git, tarball) are refused, a material inside a model is refused by `EditMaterial`, Unity's bookkeeping fields are refused as `Properties` keys
- `CreateAsset` checks every entry's ops before it creates anything, a typo refuses the whole spec
- Undo is recorded for a new object before anything else can fail, a negative component `Index` is refused
- `RemapReference` maps children by position among same-named siblings and components by position among their type, leaves `To` and the prefabs it nests alone (per pair), keeps a variant's base, contains a failed swap to its target
- `MoveAsset` and `DuplicateAsset` refuse a folder into itself and remove the folders they made when the move fails
- An export whose assets hold nothing of its kind exits 1 instead of 2, an audit without fixes removes the old `.fix.json` and writes none
- Switch values (`-apply=false`), unclosed quotes and trailing backslashes in `--args` are handled, the wrapper withdraws its task on Ctrl+C and never fails on unprintable asset names
- Smoke test checks that a dry run leaves the prefab as it was and that an export's properties write back unchanged
- Edit runs check every target's op names and keys (and importer platform blocks) before the first target is written
- `RemoveLayer` refuses a layer others sync to and shifts synced indices, Bool and Int parameter defaults are type-checked, `SetLayer` refuses a taken name
- Script ScriptableObjects are snapshotted as JSON, so a rollback never runs their `Awake`/`OnEnable`, a reverted controller is not left dirty
- `RemapReference` snapshots materials, ScriptableObjects, controllers and importers before writing, a failed check restores them
- A `MoveAsset` dry run follows chains the way `-apply` does, `DeleteAsset` keeps a target's references alive when that target is refused
- `RemoveMissingScripts` reports missing scripts added on a prefab instance instead of passing over them
- Malformed spec values (non-string paths, keywords, parents) fail their target or refuse the run instead of throwing, a prefab save that throws fails only its entry
- Animator edits fail on a non-numeric `Weight`, `Speed`, `ExitTime`, `Duration`, `Offset`, `CycleOffset`, `TimeScale` or `Threshold` instead of skipping it, `"Shader": null` is accepted again
- A `MoveAsset` dry run follows the contents of folders earlier moves would carry, `DeleteAsset` drops targets inside other targets
- `RemoveMissingScripts` leaves instance-added missing scripts with a warning, `RemapReference` warns about a dependency it cannot rewrite, the controller export skips dangling states
- Compiles on Unity 6.4 and later: missing references are detected through `objectReferenceEntityIdValue` there (the instance ID accessor is a compile error), the assembly scan's UAC0005 warning is suppressed with its reason
- Not yet compiled inside Unity 2022.3 or 6000.0-6000.3: written and checked against Unity's C# reference source for 2022.3 and 6000.3
