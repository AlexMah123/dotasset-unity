# Version

Defined in `Package/Editor/Core/AssetOpsVersion.cs` and `Package/package.json`, written into every export as `ExporterVersion` and every result as `AssetOpsVersion`.

## 0.x

### 0.1.0

First release, the Unity counterpart of uasset-workbench 2.6.

- Core: `AssetOpsCommand` registry (class name is the run name, name decides the group), `RunDispatcher` with one result shape for both routes, refusal of unknown arguments and spec keys, Unity's own errors during a target fail that target
- Routes: in-editor task queue under `Library/AssetOps/Queue` with a heartbeat that carries pid and state, headless `-batchmode -executeMethod AssetOps.Cli.Run` with base64 arguments
- Wrapper `Scripts~/assetops.py`: routing, startup wait on a locked project, fallback to headless when the editor exits before taking a task, Unity Hub discovery from `ProjectVersion.txt`, hints from the Unity log when a headless run cannot start
- Export: `PrefabExport`, `SceneExport`, `AssetExport`, `MaterialExport`, `AnimatorControllerExport`, `ImporterExport`, `AssetReferenceExport`, `TypeCatalogExport`, `CommandCatalogExport`. Delta from defaults, prefab instances as overrides, capped arrays and strings
- Import: `CreateAsset` for folders, ScriptableObjects, materials, prefabs, variants, Animator Controllers, scenes
- Edit: `EditPrefab`, `EditScene` (undo group when the scene is open), `EditAsset`, `EditMaterial`, `EditImporter`, `EditAnimatorController`
- Migrate: `MoveAsset`, `DuplicateAsset`, `DeleteAsset`, `ResaveAsset`, `RemapReference` (prefab instances swapped through Replace Prefab Asset), `RemapScript`, `RemoveMissingScripts`
- Audit: `AuditMissingReference` (R1-R4), `AuditTexture` (T1-T8), `AuditMaterial` (M1-M5), with fix specs for the matching Edit or Migrate run
- `Window > AssetOps`: queue state, pause, run history and logs, copy the wrapper to the project
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
- Not yet compiled inside Unity: written and checked against Unity's C# reference source for 2022.3 and 6000.3
