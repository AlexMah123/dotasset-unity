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
- Not yet compiled inside Unity: written and checked against Unity's C# reference source for 2022.3 and 6000.3
