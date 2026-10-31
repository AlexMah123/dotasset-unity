# Migrate

Asset-level moves and project-wide reference changes, the repairs after a rename or a script change. Every Migrate run is a dry run until `-apply`. A dry run returns its plan, one `{Action, Path, Detail}` entry per file it would `copy`, `move`, `delete`, `rewrite` or `resave` (`Plan` in `--json`, `plan:` lines otherwise, at most 500 entries, then `PlanTruncated`, while `PlanTotals` and the first `plan:` line count them all).

## Runs

| RunName | Does |
| --- | --- |
| `MoveAsset` | moves or renames assets and folders, GUIDs kept, path strings in code flagged |
| `DuplicateAsset` | copies an asset or folder with new GUIDs |
| `DeleteAsset` | deletes only what no asset under `-scandir` and no project setting, Build Settings scene, Addressables entry or AssetBundle references (`-force` overrides), to the trash by default |
| `ResaveAsset` | reserializes assets: after `[FormerlySerializedAs]`, after an upgrade, before a diff |
| `RemapReference` | repoints every reference from one asset to another, prefab instances included |
| `RemapScript` | repoints components and ScriptableObjects from one script GUID to another script |
| `RenameField` | renames a serialized field of a script and the scripts that inherit it, in assets and prefab overrides, values kept |
| `RenameSerializeReferenceType` | repoints `[SerializeReference]` data from an old type name to a compiled type, values kept |
| `RemoveMissingScripts` | removes Missing Script components from prefabs and scenes |
| `RebindAnimation` | rewrites curve paths in `.anim` clips after a GameObject or bone was renamed or moved |
| `RenameShaderProperty` | renames a saved shader property in `.mat` files, values kept |
| `UndoRun` | puts back every file an earlier writing run wrote, from its journal |

## Which one after a change

| What changed | Run |
| --- | --- |
| An asset or folder needs a new path | `MoveAsset`. References are by GUID and survive; `Resources.Load` strings and hard-coded paths do not, the run lists the scripts that mention them |
| A C# field was renamed | `RenameField -script=Assets/Scripts/X.cs -from=oldName -to=newName -apply` after the C# rename, before Unity saves the assets. Or `[FormerlySerializedAs("oldName")]`, `ResaveAsset -dependson=Assets/Scripts/X.cs -apply`, then remove the attribute |
| A `.meta` was lost or regenerated, components show Missing Script | `AuditMissingReference` lists the old GUIDs, `RemapScript -from=<old guid> -to=Assets/Scripts/X.cs -apply` |
| A class behind a `[SerializeReference]` field was renamed or moved | `AuditMissingReference` R5 lists the old type names, `RenameSerializeReferenceType -from="<assembly> <old type>" -to=<new type> -apply` |
| A class moved to another file or assembly | `RemapScript -from=Assets/Old/X.cs -to=Assets/New/X.cs -apply` (by path while the old file still exists, by GUID after) |
| Two materials, textures or ScriptableObjects should become one | `RemapReference -from=<old> -to=<new> -apply`, then `DeleteAsset <old> -apply` |
| A prefab is replaced by another | `RemapReference` swaps its instances through Unity's Replace Prefab Asset and repoints fields that reference it |
| Leftover Missing Script components | `RemoveMissingScripts` |
| A GameObject or bone under an Animator was renamed or moved, clips show "(Missing!)" | `RebindAnimation -from=Body/Arm -to=Body/ArmL -apply` |
| A shader renamed a property, materials fall back to the default | `RenameShaderProperty -from=_Color -to=_BaseColor -apply` |

## MoveAsset

```bash
python dotasset.py MoveAsset --args '-from=Assets/Old/Gun.prefab -to=Assets/Weapons/Gun.prefab -apply'
python dotasset.py MoveAsset --args '-spec="C:/temp/moves.json" -apply'
```

```json
{ "Moves": [ { "From": "Assets/Art/Old", "To": "Assets/Art/Characters" }, { "From": "Assets/A.mat", "To": "Assets/Materials/A.mat" } ] }
```

Missing parent folders are created, and removed again when the move fails. `To` must not exist, and a folder cannot move into itself. Moves run in spec order, each one sees the project as the previous one left it, so a chain (`A` to `B`, then `B` to `C`) works and a swap needs a temporary name. Each move goes through `AssetDatabase.MoveAsset`, which keeps the GUID and the `.meta`. The run warns for every `.cs` under `Assets` that contains the old path, or, under a `Resources` folder, the old `Resources.Load` path.

## DuplicateAsset

`-from`, `-to`, or `-spec` with `{"Copies": [{"From", "To"}]}`. The copy gets new GUIDs; references inside a copied folder still point at the originals. Missing parent folders are created and removed again when the copy fails. A folder cannot be copied into itself.

## DeleteAsset

```bash
python dotasset.py DeleteAsset "Assets/Old/Unused.mat,Assets/Old/Props" --args "-apply"
```

| Argument | Effect |
| --- | --- |
| `-scandir=<dirs>` | where to look for referencers, default `Assets` |
| `-force` | delete even when referenced |
| `-permanent` | delete outright instead of moving to the OS trash |
| `-spec=<file>` | `{"Assets": [...]}`, the fix spec of `AuditUnusedAsset`, in place of or beside AssetList |

A folder counts as referenced when something outside it uses something inside it. Assets deleted in the same run do not count as referencers of each other. The dry run lists every referencer.

Wherever `-scandir` points, an asset the project names outside any asset is refused too: what project settings reference (the render pipeline and its global settings, preloaded assets, config objects such as Input System settings), enabled Build Settings scenes, Build Profiles and the scenes they list (Unity 6), Addressables entries and AssetBundle-tagged assets. The warning names the setting. An asset in a `Resources` or `StreamingAssets` folder is deleted with a warning: code may load it by name, which no reference shows.

Not seen: references from code (`Resources.Load`, `AssetDatabase.LoadAssetAtPath`, string paths), and references from assets outside `-scandir`. Leave `-scandir` at its default `Assets` unless you know nothing outside the folder uses the targets.

A scene open in the editor with unsaved changes, the prefab open in Prefab Mode with unsaved changes, or any asset with unsaved changes in the editor (a material edited in the Inspector) is never deleted, also when it sits in a folder being deleted and with `-force`: the target fails with the reason. Save or discard the changes, then delete. `ResaveAsset` refuses such a target the same way and resaves the rest.

## ResaveAsset

```bash
python dotasset.py ResaveAsset --args "-dependson=Assets/Scripts/Inventory.cs -apply"
python dotasset.py ResaveAsset "Assets/Prefabs" --args "-type=GameObject -apply"
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
python dotasset.py RemapReference --args '-from=Assets/Materials/Old.mat -to=Assets/Materials/New.mat'
python dotasset.py RemapReference --args '-spec="C:/temp/remap.json" -apply'
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
- A prefab variant whose base is `From` keeps its base: the run warns and changes only the variant's references.
- `To` itself, and the prefabs and models `To` nests or is a variant of, are left pointing at `From` with a warning: repointing them would nest `To` inside itself. Other assets `To` references are remapped, a cycle of plain field references is fine. With several pairs this is decided per pair.
- Children map by name and position among same-named siblings, components by type and position among that type.
- `From` and `To` must be the same kind of asset.

## RenameField

```bash
python dotasset.py RenameField --args '-script=Assets/Scripts/Enemy.cs -from=hp -to=health -apply'
```

| Argument | Effect |
| --- | --- |
| `-script` | the script whose field was renamed, by path or GUID |
| `-from` | the old serialized name |
| `-to` | the new name |
| `-scandir=<dirs>` | default `Assets` |

Rename the field in C# first, then run this before Unity saves the assets: Unity drops keys the class no longer has on the next save. It is refused while the class still has a field named `-from`, and warns when it has none named `-to`.

Every script whose class inherits the field from the class that declares it is renamed too, and logged: subclasses, the base class, and siblings of the given script. It is refused when one of those classes declares its own field named `-from`, or sits in a DLL (all classes in a DLL share one GUID). Without a compiled class, as when the script does not compile, only the given script is renamed and the run warns.

Rewrites the top-level key in every MonoBehaviour and ScriptableObject document that uses one of those scripts, and the `propertyPath` of prefab overrides that target one of its components, through variants, nested prefabs and scenes (`hp`, `hp.x` and `hp.Array.data[0]` alike). Overrides of other components with a field of the same name are left alone. An object that already has a `-to` key fails its file, so does a file loaded in the editor, as for `RemapScript`. Top-level fields only: a field inside a `[Serializable]` class, `[SerializeReference]` data and animation curves that name the field are not followed. Needs Asset Serialization Mode = Force Text.

## RenameSerializeReferenceType

```bash
python dotasset.py RenameSerializeReferenceType --args '-from="Assembly-CSharp Game.OldEffect" -to=Game.Effects.Burn -apply'
```

| Argument | Effect |
| --- | --- |
| `-from` | the old type as `<assembly> <namespace>.<class>`, the form `AuditMissingReference` R5 and exports use. Without the assembly, any assembly matches. A nested class is `Outer/Inner` |
| `-to` | the compiled type the data should load as: a `[Serializable]` class, not abstract, generic or a `UnityEngine.Object` |
| `-scandir=<dirs>` | default `Assets` |

Rewrites `type: {class: ..., ns: ..., asm: ...}` in every YAML asset and reimports the changed files. Field values survive when the new class has fields of the same names. Unity keeps the data of a type it cannot load in the file, also through a resave, so this works long after the rename. `[MovedFrom]` on the new class is the alternative that leaves the files alone. Files loaded in the editor fail their target, as for `RemapScript`. Needs Asset Serialization Mode = Force Text.

## RemapScript

```bash
python dotasset.py RemapScript --args '-from=4f2c9a1e0b8d4c7e9f3a2b1c0d9e8f7a -to=Assets/Scripts/Inventory.cs -apply'
```

| Argument | Effect |
| --- | --- |
| `-from` | the old script's GUID (32 hex digits), or its path while it still exists |
| `-to` | the script that should be used; its class must compile and match the file name |
| `-scandir=<dirs>` | default `Assets` |
| `-fromfileid` | the old reference's fileID, default `11500000` (a `.cs` script). Scripts inside a DLL use another fileID |

Rewrites `m_Script: {fileID: ..., guid: <old>, type: 3}` in every YAML asset (`.prefab`, `.unity`, `.asset`, `.controller`, ...) and reimports the changed files. Needs Asset Serialization Mode = Force Text. Field values survive when the new class has fields of the same names. With the editor open, a file that is loaded there (an open scene, a prefab in Prefab Mode, an asset with unsaved changes) fails its target: the loaded copy would write the old GUID back on its next save.

## RebindAnimation

```bash
python dotasset.py RebindAnimation --args '-from=Body/Arm -to=Body/ArmL -scandir=Assets/Animations -apply'
```

| Argument | Effect |
| --- | --- |
| `-from` | the old path, relative to the Animator, as the Animation window shows it |
| `-to` | the new path |
| `-scandir=<dirs>` | default `Assets`. An AssetList of clips or folders replaces the scan |

Curves on `-from` and everything beneath it move: `Body/Arm` -> `Body/ArmL` also rewrites `Body/Arm/Hand`. Float and object reference curves (sprite swaps) both move, keys and tangents unchanged. A clip that already animates the same property at the new path fails its target instead of overwriting it. Clips inside model files are read-only and skipped, extract them to `.anim` first. The dry run plans one change per old path with its curve count. `AssetExport` on a clip shows the paths under `m_FloatCurves`, `m_PPtrCurves` and `m_EditorCurves`.

## RenameShaderProperty

```bash
python dotasset.py RenameShaderProperty --args '-from=_Color -to=_BaseColor -shader=Custom/Toon -apply'
```

| Argument | Effect |
| --- | --- |
| `-from` | the old property name, as the material saved it |
| `-to` | the new property name |
| `-shader=<name>` | only materials using this shader (exact name, `Universal Render Pipeline/Lit`) |
| `-overwrite` | replace a value the material already saves under `-to` |
| `-scandir=<dirs>` | default `Assets`. An AssetList of materials or folders replaces the scan |

Renames the entry in `m_SavedProperties` (textures with their scale and offset, floats, ints, colors), so the value and its type stay. A material that already saves `-to` fails its target unless `-overwrite`, and always when the two are of different types. A warning names materials whose shader does not declare `-to`. Only `.mat` files are written. After the run, scripts under `Assets` that mention `"<from>"` and clips that animate `material.<from>` are listed, change those by hand. `MaterialExport` shows the saved names.

## UndoRun

```bash
python dotasset.py UndoRun --args "-run=20261031-101500-123"
python dotasset.py UndoRun --args "-run=last -apply"
```

Every writing run keeps the files it touches, as they were before it, in `Library/DotAsset/Journal/<id>`. The run prints `out: .../Journal/<id>/journal.json` and logs `undo with: UndoRun --args "-run=<id> -apply"`. The id is the run's start time in UTC, the order comes from a sequence number in each journal, so a clock change never reorders runs. The newest 30 journals are kept. `-run=last` takes the newest, which is the undo itself after an undo: undoing an undo redoes the run.

- Files the run wrote come back with their old content and `.meta`, files it created are deleted, moved files return to their old path with their GUID, deleted files come back.
- A scene `CreateAsset` added to the Build Settings (`AddToBuild`) leaves the scene list again. Refused when the list changed after the run, including another active build profile on Unity 6. The dry run lists it as `remove from build`.
- Refused as a whole, nothing restored, when any of those paths changed after the run (an edit by hand, a later run), is a scene open in the editor or the prefab in Prefab Mode, or has unsaved changes in the editor.
- A run is undone once. The dry run lists each path as `rewrite`, `restore` (absent now) or `delete` (created by the run).
- The undo writes a journal of its own.
- A run that stopped before it finished (Unity crashed or was killed) still has its journal: each path is logged to `journal.log` as it is kept, and the next run turns that into `journal.json` marked `"Interrupted": true`. Undo it as any other run, the log says it stopped before it finished.

`-atomic`, accepted by every Edit, Migrate and Import run, applies the same restore at the end of the run when a target failed: all or nothing across targets instead of per target. A scene open in the editor cannot be restored from disk, the run warns and its edit stays, one Ctrl+Z in the editor takes it back.

## RemoveMissingScripts

```bash
python dotasset.py RemoveMissingScripts "Assets/Prefabs" --args "-apply"
python dotasset.py RemoveMissingScripts --args '-spec="Library/DotAsset/Audit/AuditMissingReference_20261023-201500.fix.json" -apply'
```

Give at least one of AssetList, `-scandir`, or `-spec` with `{"Files": [...]}` (the fix spec `AuditMissingReference` writes); with none the run is refused rather than sweeping the whole project. An empty `Files` list does nothing. Missing scripts on objects inside a prefab instance are left alone and reported: those inherited from the source prefab with the source path to clean, those added as overrides on the instance (every component a variant adds is one) with a warning to remove them in the Inspector, unpack the instance, or `RemapScript` the GUID.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | done, or the dry run found nothing wrong |
| 1 | at least one target failed and was left as it was |
| 2 | bad arguments or spec |
