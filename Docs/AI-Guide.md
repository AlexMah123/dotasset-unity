# AI Guide

The call manual for agents. Arguments, spec shapes, exit codes and JSON fields are defined in the group docs (`Export.md`, `Import.md`, `Edit.md`, `Migrate.md`, `Audit.md`); this page is the way in.

## Decision table

| I want to know / do | RunName | Group |
| --- | --- | --- |
| What a prefab contains: hierarchy, components, property values, nested prefab overrides | `PrefabExport` | Export |
| What a scene contains, what its prefab instances override | `SceneExport` | Export |
| The fields of a ScriptableObject, or the objects inside any asset file | `AssetExport` | Export |
| A material's shader properties, keywords, render queue, variant parent | `MaterialExport` | Export |
| An Animator Controller's parameters, layers, states, transitions, blend trees | `AnimatorControllerExport` | Export |
| Import settings of a texture, model or audio clip | `ImporterExport` | Export |
| What an asset uses, and what uses it | `AssetReferenceExport` | Export |
| Which property paths a component or ScriptableObject type has, their defaults and enum options | `TypeCatalogExport` with `-filter=` | Export |
| Which runs exist, including project-specific ones | `CommandCatalogExport`, or `dotasset.py --list` | Export |
| Create folders, ScriptableObjects, materials, prefabs, variants, controllers, scenes | `CreateAsset` | Import |
| Add, delete, rename, move GameObjects in a prefab; add or remove components; set fields and references | `EditPrefab` | Edit |
| The same in a scene, including a scene open in the editor | `EditScene` | Edit |
| Put a prefab instance into a prefab or scene, revert or unpack an instance | `EditPrefab` / `EditScene` ops `AddPrefabInstance`, `RevertOverrides`, `UnpackPrefab` | Edit |
| Change fields of a ScriptableObject or any asset object, insert or remove list entries | `EditAsset` | Edit |
| Change material values, textures, keywords, shader, render queue, variant parent | `EditMaterial` | Edit |
| Change texture import settings (type, sRGB, compression, max size, mipmaps, platform overrides), or any importer field | `EditImporter` | Edit |
| Add or change Animator parameters, layers, states, blend trees, transitions and conditions | `EditAnimatorController` | Edit |
| Rename a parameter and every condition and blend tree that reads it | `EditAnimatorController` op `RenameParameter` | Edit |
| Move or rename assets and folders without breaking references | `MoveAsset` | Migrate |
| Copy an asset or folder before a risky change | `DuplicateAsset` | Migrate |
| Delete assets, refusing while something still uses them | `DeleteAsset` | Migrate |
| Replace every use of one asset with another (material, texture, prefab, ScriptableObject) | `RemapReference` | Migrate |
| Fix "Missing Script" after a `.meta` was lost or a class moved to another file | `RemapScript` (GUIDs from `AuditMissingReference`) | Migrate |
| Remove Missing Script components | `RemoveMissingScripts` | Migrate |
| Rename a serialized field without losing data | add `[FormerlySerializedAs("old")]`, then `ResaveAsset -dependson=<script>` | Migrate |
| Normalize YAML after a Unity or package upgrade | `ResaveAsset` | Migrate |
| References to deleted assets, missing scripts, missing prefab sources | `AuditMissingReference` | Audit |
| Textures with wrong type, sRGB, compression, mipmaps or size | `AuditTexture` | Audit |
| Materials that render pink, stale properties, stray keywords | `AuditMaterial` | Audit |

Before editing, export. The export shows the exact property names (`m_Mass`, not `mass`), paths (`Player/Model/Hand.R`) and reference forms the Edit spec needs. When a property name is unknown, `TypeCatalogExport -filter=<type>` lists them without touching any asset.

Audit first, then fix with the spec the audit wrote: `AuditTexture` writes `<report>.fix.json` for `EditImporter`, `AuditMaterial` for `EditMaterial`, `AuditMissingReference` for `RemoveMissingScripts`. Review it, run once without `-apply`, then with it.

## Call templates

The wrapper is `dotasset.py` (in the package's `Scripts~/`, or copied to the project root by the **Copy dotasset.py** button in `Window > DotAsset`). Run it from anywhere inside the project, or give `--project`.

| Wrapper flag | Effect |
| --- | --- |
| `--project DIR` | the Unity project, default the one containing the current directory, or `DOTASSET_PROJECT` |
| `--unity PATH` | editor executable for the headless route, default from `ProjectVersion.txt` through Unity Hub, or `DOTASSET_UNITY` |
| `--route auto\|editor\|batch` | `editor` refuses when no editor has the project open, `batch` never uses the queue. Default `auto` |
| `--timeout SEC` | give up after this long, default 600. A task the editor has not taken yet is withdrawn |
| `--startup-wait SEC` | how long to wait for a starting editor's queue, default 300 |
| `--graphics` | headless route without `-nographics` (GPU checks in `AuditMaterial` need it) |
| `--quiet` | print only warnings, errors and the summary |
| `--json` | also print the full result JSON |
| `--list`, `--status` | every run with its summary; what the editor's queue is doing |
| `--launch` | open the project in the editor (no window interaction needed) and wait until its queue is `ready`. Exit 0 when ready, also when an editor was already open |
| `--quit [--force]` | close the editor that has the project open, once it is idle. Refused (exit 2) while a scene or Prefab Mode has unsaved changes, `--force` discards them |
| `--open-scene A.unity[,B.unity] [--force]` | open scenes in that editor: the first replaces what is open, the rest are added. Refused like `--quit`. The last line is `previously open: <paths>`, pass them back to `--open-scene` to restore the user's layout |

Ctrl+C withdraws a task the editor has not taken, and stops a headless Unity.

Export. Entry assets are a comma-separated `AssetList`; folders expand to the matching assets beneath them.

```bash
python dotasset.py PrefabExport "Assets/Prefabs/Player.prefab,Assets/Prefabs/Enemies"
python dotasset.py SceneExport Assets/Scenes/Main.unity --args "-subtree=Environment"
python dotasset.py TypeCatalogExport --args "-filter=Rigidbody"
```

Import and Edit. Targets are named in the spec, `AssetList` stays empty. Without `-apply`, Edit runs every op, reports, and drops the result.

```bash
python dotasset.py CreateAsset --args '-spec="C:/temp/create.json"'
python dotasset.py EditPrefab --args '-spec="C:/temp/player.json"'           # dry run
python dotasset.py EditPrefab --args '-spec="C:/temp/player.json" -apply'    # write
```

Migrate. Dry run by default as well.

```bash
python dotasset.py MoveAsset --args '-from=Assets/Old/Gun.prefab -to=Assets/Weapons/Gun.prefab -apply'
python dotasset.py ResaveAsset --args "-dependson=Assets/Scripts/Inventory.cs -apply"
```

Audit. Entry assets or `-scandir` (default `Assets`).

```bash
python dotasset.py AuditTexture --args "-scandir=Assets/Art -maxsize=2048"
python dotasset.py EditImporter --args '-spec="Library/DotAsset/Audit/AuditTexture_20261023-201500.fix.json" -apply'
```

Spec paths are absolute or relative to the project root. Quote values that contain spaces: `-spec="C:/My Specs/a.json"`. On Git Bash for Windows, `--args` values that start with `/` may need `MSYS_NO_PATHCONV=1`.

## Reading results

Every run prints its log, then `[dotasset] <summary>` and the path of its log file. Lines starting `error:` and `warning:` are the ones to read first.

| Exit code | Meaning |
| --- | --- |
| 0 | success (Edit and Migrate without `-apply`: the dry run passed) |
| 1 | a target failed and nothing of it was saved, or the run logged an error (an AssetList entry that does not exist, an export whose assets hold nothing of its kind), or Unity failed before the run (compile errors, license), or the wrapper timed out or was interrupted |
| 2 | refused: unknown run, unknown argument, unreadable spec, unknown spec key, editor queue paused |
| 3 | audit only: the run worked and the report lists issues. Not a failure |

Failure lines have one shape: `<RunName> failed for <target>. Reason: <first error while writing it>`.

Exports land in `Library/DotAsset/Export/<AssetPath>_<YYYYMMDD-HHMMSS>.json` and are printed as `output:` lines. They can be long: grep first, then read the line range.

```bash
grep -n '"Path": "Player/Model' Library/DotAsset/Export/Assets/Prefabs/Player.prefab_*.json
```

Audit reports land in `Library/DotAsset/Audit/`, with `-report=` to choose the path. Run logs: `Library/DotAsset/Logs/`, and `Window > DotAsset` lists them in the editor.

## Pitfalls

| Pitfall | Do this |
| --- | --- |
| Starting Unity with `-batchmode` by hand while the editor is open | Always go through `dotasset.py`. Unity refuses a second instance on the same project, the wrapper routes to the open editor instead |
| Calling the wrapper right after opening the editor | The wrapper sees the locked project, prints `waiting up to 300s` and waits for the queue heartbeat. Let it wait |
| The task waits forever while the editor is open | The queue only runs when the editor is `ready`: not compiling, importing, in play mode, or showing a modal dialog. `dotasset.py --status` shows the state. On macOS an editor in the background can be throttled by App Nap; bring it forward or disable App Nap for Unity |
| Edit run returns 0 but nothing changed | It was a dry run. Add `-apply` |
| `-apply=false` or `-apply=no` | A switch takes `=true/1/yes` (on) or `=false/0/no` (off). Anything else is refused |
| A quote left open in `--args` | Refused with exit 2, the run never guesses where the value ends |
| A quote inside a quoted value | Write `\"`. Right before a space or the end of `--args` it closes the value instead, so `-scandir="Assets\Art\"` works while `-x="a \" b"` ends at `a \` and is refused as an unclosed quote. Keep a literal quote away from a following space |
| Editing an asset in a package from the registry or git | Refused, those packages are read-only (Unity restores them). Embed the package (move it into `Packages/`) first |
| Editing a material inside a model | Refused, it comes from the importer. Extract it, or remap it with `EditImporter` |
| Writing `m_GameObject`, `m_Children`, `m_PrefabInstance` and similar | Refused, Unity maintains them. Use the hierarchy ops |
| `apply` without the dash in `--args` |  Refused with exit 2 as a stray argument, it does not act as `-apply` |
| `-aply` or another typo | Refused with exit 2 and the list of accepted arguments, the run does not fall back to a dry run silently |
| Writing `mass` instead of `m_Mass` | A key without `m_` is retried with it, so `mass` finds `m_Mass`. The error lists the real names when neither exists |
| Two GameObjects with the same name under one parent | Paths need `Name[0]`, `Name[1]`. Exports write the index whenever names repeat |
| Setting a value on an object inside a prefab instance | Allowed: it becomes an override, exactly as in the Inspector. Deleting such an object is refused; delete the instance, edit the source prefab, or `UnpackPrefab` first |
| Expecting an export to show the inside of a prefab instance | Instances export as `Source` plus `Overrides`, added and removed objects. Export the source prefab for its contents, or add `-expand` |
| Comparing an export to defaults | Component properties are the delta from a fresh component of that type, materials leave out values at the shader default. `-full` exports everything |
| Writing back a truncated array | Arrays longer than `-maxarray` (100) end with `{"$truncated": <total>}` and are refused on write. Re-export with a higher `-maxarray`, or use `ArrayOps` |
| `EditScene` on a scene with unsaved changes in the editor | Refused, the run would mix your changes with the user's. Save or discard first |
| `EditScene` while the editor shows an Untitled scene | Unity cannot open a second scene next to an Untitled one. Save it first |
| `EditPrefab` on a prefab open in Prefab Mode with unsaved changes | Refused for the same reason |
| Editing a model (`.fbx`) as a prefab | Refused, its hierarchy comes from the importer. Create a variant with `CreateAsset` `PrefabVariant`, or change the import with `EditImporter` |
| `RemapScript` or the fast pass of `AuditMissingReference` reports nothing | They read YAML. Project Settings > Editor > Asset Serialization Mode must be Force Text |
| Renaming a C# field loses its data | Add `[FormerlySerializedAs("oldName")]`, then `ResaveAsset -dependson=Assets/Scripts/X.cs -apply`, then the attribute can go |
| `MoveAsset` warns that a script mentions the path | GUIDs keep serialized references, not strings. Fix `Resources.Load` paths and hard-coded asset paths in code by hand |
| `DeleteAsset` refuses | Something under `-scandir` still references it, the warnings list what. Fix those first, or add `-force`. A scene open with unsaved changes, or the prefab in Prefab Mode with unsaved changes, is refused even with `-force`: ask the user to save or discard first. `ResaveAsset` refuses those too |
| Treating exit code 3 as failure | 3 means the audit worked and found something |
| Exports and reports in version control | `Library/` is ignored by Unity's default `.gitignore`. Copy what you need to keep |
| Several headless calls at once | They take turns: a later call prints `another DotAsset headless run has the project, waiting` and starts when the one before ends. For many steps in a row, `--launch` the editor once instead, queued runs skip Unity's start-up |
| Finding the latest export by listing the folder | Read the path from the run's `Outputs` (`--json`). Only the newest 10 exports per asset and 20 reports per audit are kept, older ones are deleted unless written with `-outdir` or `-report` |
| Showing the user a scene, or editing one in their open editor | `--open-scene <path>` opens it, refused while the user has unsaved changes. Its last line is `previously open: <paths>`, pass those back to `--open-scene` when done |
| A headless run times out on a large project | The first headless start imports or refreshes the project and can take minutes. Raise `--timeout` (default 600 seconds), or keep the editor open so runs go to its queue |
| The project does not compile | Headless runs cannot start; the wrapper prints the `error CS` lines from the Unity log. The editor route waits in `compiling` |
