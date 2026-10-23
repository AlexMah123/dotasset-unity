# Migrate

Asset-level moves and project-wide reference changes, the repairs after a rename or a script change. Every Migrate run is a dry run until `-apply`.

## Runs

| RunName | Does |
| --- | --- |
| `MoveAsset` | moves or renames assets and folders, GUIDs kept, path strings in code flagged |
| `DuplicateAsset` | copies an asset or folder with new GUIDs |
| `DeleteAsset` | deletes only what nothing references (`-force` overrides), to the trash by default |
| `ResaveAsset` | reserializes assets: after `[FormerlySerializedAs]`, after an upgrade, before a diff |
| `RemapReference` | repoints every reference from one asset to another, prefab instances included |
| `RemapScript` | repoints components and ScriptableObjects from one script GUID to another script |
| `RemoveMissingScripts` | removes Missing Script components from prefabs and scenes |

## Which one after a change

| What changed | Run |
| --- | --- |
| An asset or folder needs a new path | `MoveAsset`. References are by GUID and survive; `Resources.Load` strings and hard-coded paths do not, the run lists the scripts that mention them |
| A C# field was renamed | add `[FormerlySerializedAs("oldName")]` to the field, `ResaveAsset -dependson=Assets/Scripts/X.cs -apply`, then remove the attribute |
| A `.meta` was lost or regenerated, components show Missing Script | `AuditMissingReference` lists the old GUIDs, `RemapScript -from=<old guid> -to=Assets/Scripts/X.cs -apply` |
| A class moved to another file or assembly | `RemapScript -from=Assets/Old/X.cs -to=Assets/New/X.cs -apply` (by path while the old file still exists, by GUID after) |
| Two materials, textures or ScriptableObjects should become one | `RemapReference -from=<old> -to=<new> -apply`, then `DeleteAsset <old> -apply` |
| A prefab is replaced by another | `RemapReference` swaps its instances through Unity's Replace Prefab Asset and repoints fields that reference it |
| Leftover Missing Script components | `RemoveMissingScripts` |

## MoveAsset

```bash
python assetops.py MoveAsset --args '-from=Assets/Old/Gun.prefab -to=Assets/Weapons/Gun.prefab -apply'
python assetops.py MoveAsset --args '-spec="C:/temp/moves.json" -apply'
```

```json
{ "Moves": [ { "From": "Assets/Art/Old", "To": "Assets/Art/Characters" }, { "From": "Assets/A.mat", "To": "Assets/Materials/A.mat" } ] }
```

Missing parent folders are created. `To` must not exist. Each move goes through `AssetDatabase.MoveAsset`, which keeps the GUID and the `.meta`. The run warns for every `.cs` under `Assets` that contains the old path, or, under a `Resources` folder, the old `Resources.Load` path.

## DuplicateAsset

`-from`, `-to`, or `-spec` with `{"Copies": [{"From", "To"}]}`. The copy gets new GUIDs; references inside a copied folder still point at the originals.

## DeleteAsset

```bash
python assetops.py DeleteAsset "Assets/Old/Unused.mat,Assets/Old/Props" --args "-apply"
```

| Argument | Effect |
| --- | --- |
| `-scandir=<dirs>` | where to look for referencers, default `Assets` |
| `-force` | delete even when referenced |
| `-permanent` | delete outright instead of moving to the OS trash |

A folder counts as referenced when something outside it uses something inside it. Assets deleted in the same run do not count as referencers of each other. The dry run lists every referencer.

## ResaveAsset

```bash
python assetops.py ResaveAsset --args "-dependson=Assets/Scripts/Inventory.cs -apply"
python assetops.py ResaveAsset "Assets/Prefabs" --args "-type=GameObject -apply"
```

| Argument | Effect |
| --- | --- |
| AssetList | these assets, folders expanded |
| `-dependson=<paths>` | every asset under `-scandir` that directly uses one of these (a script, a material) |
| `-scandir=<dirs>` | alone: every asset under it. With `-dependson`: where to look |
| `-type=<type>` | keep only assets whose main object is this type |
| `-metaonly` | rewrite only the `.meta` files |

Uses `AssetDatabase.ForceReserializeAssets`. Scripts and folders are skipped.

## RemapReference

```bash
python assetops.py RemapReference --args '-from=Assets/Materials/Old.mat -to=Assets/Materials/New.mat'
python assetops.py RemapReference --args '-spec="C:/temp/remap.json" -apply'
```

```json
{ "Pairs": [ { "From": "Assets/Textures/Rock_old.png", "To": "Assets/Textures/Rock.png" } ] }
```

| Argument | Effect |
| --- | --- |
| `-from`, `-to` or `-spec` | the pairs |
| `-scandir=<dirs>` | where to look for referencers, default `Assets` |
| `-keepinstances` | prefab to prefab: leave instances of the old prefab alone |

- Candidates are the assets under `-scandir` that directly depend on a `From` asset.
- Every object reference in every object of a candidate is checked: prefabs (loaded in isolation), scenes (undo-recorded when open in the editor), materials, ScriptableObjects, controllers, and model importers (remapped materials).
- Sub-assets map by name and type: `Hero_old.fbx`'s mesh `Body` goes to `Hero.fbx`'s mesh `Body`. Children and components of a prefab map by hierarchy path. A reference to a sub-object with no counterpart fails that candidate, nothing in it changes.
- Inside prefab instances only overridden values are rewritten. Inherited values change when the source prefab, a candidate of its own, is fixed.
- Prefab to prefab: outermost instances of `From` are swapped with `PrefabUtility.ReplacePrefabAssetOfPrefabInstance`, which carries overrides across by name. Instances nested inside other instances are reported, swap them in that prefab.
- `From` and `To` must be the same kind of asset.

## RemapScript

```bash
python assetops.py RemapScript --args '-from=4f2c9a1e0b8d4c7e9f3a2b1c0d9e8f7a -to=Assets/Scripts/Inventory.cs -apply'
```

| Argument | Effect |
| --- | --- |
| `-from` | the old script's GUID (32 hex digits), or its path while it still exists |
| `-to` | the script that should be used; its class must compile and match the file name |
| `-scandir=<dirs>` | default `Assets` |
| `-fromfileid` | the old reference's fileID, default `11500000` (a `.cs` script). Scripts inside a DLL use another fileID |

Rewrites `m_Script: {fileID: ..., guid: <old>, type: 3}` in every YAML asset (`.prefab`, `.unity`, `.asset`, `.controller`, ...) and reimports the changed files. Needs Asset Serialization Mode = Force Text. Field values survive when the new class has fields of the same names. With the editor open, a file that is loaded there (an open scene, a prefab in Prefab Mode, an asset with unsaved changes) fails its target: the loaded copy would write the old GUID back on its next save.

## RemoveMissingScripts

```bash
python assetops.py RemoveMissingScripts "Assets/Prefabs" --args "-apply"
python assetops.py RemoveMissingScripts --args '-spec="Library/AssetOps/Audit/AuditMissingReference_20261023-201500.fix.json" -apply'
```

AssetList, `-scandir`, or `-spec` with `{"Files": [...]}` (the fix spec `AuditMissingReference` writes). Missing scripts on objects inside a prefab instance belong to the source prefab: they are reported with the source path and left alone.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | done, or the dry run found nothing wrong |
| 1 | at least one target failed and was left as it was |
| 2 | bad arguments or spec |
