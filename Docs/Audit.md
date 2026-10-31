# Audit

Read-only checks that write a report JSON, and a fix spec where a fix is mechanical. Nothing is modified.

## Runs

| RunName | Checks |
| --- | --- |
| `AuditMissingReference` | references to deleted assets, Missing Scripts, missing sub-objects, missing prefab sources, `[SerializeReference]` data of a missing type (R1-R5) |
| `AuditTexture` | texture import settings against how the texture is used (T1-T8) |
| `AuditMaterial` | missing, broken or unsupported shaders, stale properties, stray keywords (M1-M5) |
| `AuditUnusedAsset` | assets nothing in the project reaches, scenes outside Build Settings (U1-U2) |

```bash
python dotasset.py AuditTexture --args "-scandir=Assets"
python dotasset.py AuditMaterial "Assets/Materials/Characters"
```

Entry assets in AssetList (folders expand), or `-scandir` (default `Assets`). `-report=<path>` picks the report path, default `Library/DotAsset/Audit/<RunName>_<timestamp>.json`. The newest 20 default reports of each audit are kept, older ones are deleted with their fix specs. A report written to `-report` is never deleted.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | nothing found |
| 3 | the report lists issues |
| 1 | the run failed |
| 2 | bad arguments |

3 is not a failure: the audit worked. CI can gate on it.

The wrapper can also return 4, unknown: it lost track of a run that could still write. See [AI-Guide.md](AI-Guide.md#reading-results).

## Report

```json
{
  "ReportType": "AuditTexture",
  "Rules": { "T1": "named like a normal map but not imported as one", "T3": "..." },
  "Checked": 412,
  "IssueCount": 9,
  "Counts": { "T1": 2, "T3": 7 },
  "Issues": [ { "Rule": "T1", "Severity": "Warning", "Asset": "Assets/Art/Rock_n.png", "Message": "named like a normal map, imported as Default" } ],
  "FixSpec": "Library/DotAsset/Audit/AuditTexture_20261023-201500.fix.json",
  "FixRun": "EditImporter"
}
```

## Fix specs

When issues have a mechanical fix the audit writes `<report>.fix.json` next to the report, in the spec format of `FixRun`:

| Audit | FixRun | Covers |
| --- | --- | --- |
| `AuditTexture` | `EditImporter` | T1 T2 T3 T4 T5 T6 T8 |
| `AuditMaterial` | `EditMaterial` | M4 M5 |
| `AuditUnusedAsset` | `DeleteAsset` | U1, as `{"Assets": [...]}`. `DeleteAsset` checks the references and the project-named roots again and refuses anything still used |
| `AuditMissingReference` | `RemoveMissingScripts` | R2 on ordinary objects. Missing scripts added on prefab instances (variants included) are listed but left, with a warning. The report's `MissingScripts` lists each GUID for `RemapScript`, which fixes both |

Read it, delete what should stay, run `FixRun` without `-apply`, then with it:

```bash
python dotasset.py EditImporter --args '-spec="Library/DotAsset/Audit/AuditTexture_20261023-201500.fix.json"'
python dotasset.py EditImporter --args '-spec="Library/DotAsset/Audit/AuditTexture_20261023-201500.fix.json" -apply'
```

T4 (Read/Write) is in the fix spec too, though a script may need the pixels; remove those entries when it does.

## AuditMissingReference

| Rule | Severity | Finds |
| --- | --- | --- |
| R1 | Error | a reference to a GUID no asset has: the asset was deleted, or its `.meta` was lost |
| R2 | Error | Missing Script: a component or ScriptableObject whose script GUID no script has |
| R3 | Error | `-deep`: a reference that loads as nothing. Catches deleted sub-objects inside assets that still exist, which the text pass cannot see. Repeats R1 hits |
| R4 | Error | `-deep`: a prefab instance whose source prefab is missing |
| R5 | Error | `[SerializeReference]` data saved under a type no compiled class has, after a class was renamed or moved. Unity reads it as null and keeps it in the file |

| Argument | Effect |
| --- | --- |
| `-scandir=<dirs>` | default `Assets` |
| `-deep` | also load every asset and check each reference (slower) |

The default pass reads the YAML of every `.prefab`, `.unity`, `.asset`, `.mat`, `.controller`, `.anim` and similar file without loading it, and checks every `{fileID, guid, type}` against the AssetDatabase. It needs Force Text serialization; without it the run warns and does the deep pass instead. Built-in resources are not reported. The deep pass skips scenes; for a scene, `SceneExport` and a grep for `"Missing": true` does the same.

R5 reads the `type: {class, ns, asm}` entries in the YAML. Outside scenes, Unity confirms each hit, so a type renamed with `[MovedFrom]` is not reported. The report's `MissingTypes` groups R5 by type with the files, compiled `Candidates` of the same class name (a type moved to another namespace or assembly), and a ready `RenameSerializeReferenceType` argument line.

Issues carry `Guid`, `FileID`, `Field` (the YAML key) and `LocalId` (the object inside the file). The report's `MissingScripts` groups R2 by GUID with the files that use it and a ready `RemapScript` argument line.

## AuditTexture

| Rule | Severity | Finds | Fix |
| --- | --- | --- | --- |
| T1 | Warning | named like a normal map (`_n`, `_normal`, `_nrm`, `_nm`, `_norm`, `_normalmap`) but not imported as one | `TextureType: NormalMap` |
| T2 | Warning | named like a data map (`_mask`, `_orm`, `_mra`, `_arm`, `_ao`, `_rough`, `_roughness`, `_metal`, `_metallic`, `_height`, `_disp`, `_displacement`, `_ms`, `_mt`, `_smoothness`, `_spec`, `_specular`) with sRGB on | `sRGB: false` |
| T3 | Warning | uncompressed at 256 px or larger | `Compression: Compressed` |
| T4 | Info | Read/Write enabled, the texture stays in CPU memory too | `ReadWrite: false` |
| T5 | Warning | mipmaps on a Sprite or GUI texture | `Mipmaps: false` |
| T6 | Warning | mipmaps off on a Default or NormalMap texture of 256 px or larger | `Mipmaps: true` |
| T7 | Warning | not a power of two, NPOT scale None, a side not a multiple of 4: block compression cannot apply | resize the source |
| T8 | Warning | imported larger than the budget | `MaxSize: <budget>` |

| Argument | Effect |
| --- | --- |
| `-maxsize=N` | the T8 budget, default 2048, a power of two from 32 to 16384, anything else is refused |
| `-rules=T1,T3` | only these rules |
| `-normalsuffix=_n,_nrm` | replaces the T1 name list |
| `-datasuffix=_mask,_orm` | replaces the T2 name list |

Names are matched at the end of the file name, case-insensitive.

## AuditMaterial

| Rule | Severity | Finds | Fix |
| --- | --- | --- | --- |
| M1 | Error | no shader, or the error shader (the shader is missing) | pick a shader, `EditMaterial` `Shader` |
| M2 | Error | the shader has compile errors | fix the shader |
| M3 | Error | the shader is not supported here: a Built-in Render Pipeline shader (`Standard`, `Legacy Shaders/...`, `Mobile/...`) under URP or HDRP, or `isSupported` is false | pick a pipeline shader |
| M4 | Info | saved values for properties the shader no longer declares | `RemoveStaleProperties: true` |
| M5 | Warning | enabled keywords the shader does not declare | `DisableKeywords: [...]` |

| Argument | Effect |
| --- | --- |
| `-scandir=<dirs>` | default `Assets` |
| `-rules=M1,M3` | only these rules |

The report's `RenderPipeline` names the active pipeline the M3 check used. Headless runs have no graphics device (`-nographics`), so there M3 only catches Built-in shaders under a render pipeline; run with the editor open, or pass `--graphics` to the wrapper, for the GPU support check. Converting Built-in materials to URP or HDRP is the render pipeline's converter's job; the report lists which materials need it.

## AuditUnusedAsset

```bash
python dotasset.py AuditUnusedAsset --args "-scandir=Assets/Art"
python dotasset.py DeleteAsset --args '-spec="Library/DotAsset/Audit/<report>.fix.json"'          # dry run, then -apply
```

| Rule | Severity | Finds | Fix |
| --- | --- | --- | --- |
| U1 | Warning | an asset nothing reachable uses | `DeleteAsset` with the fix spec |
| U2 | Info | a scene outside Build Settings that nothing in the build uses, with `-buildonly` | add it to Build Settings, or delete it |

| Argument | Effect |
| --- | --- |
| `-scandir=<dirs>` | which assets to list, default `Assets`. The roots always come from the whole project |
| `-buildonly` | only the enabled Build Settings scenes are roots, instead of every scene |
| `-rules=U1` | only these rules |

An asset is used when a root reaches it, directly or through other assets. Roots: every scene (the enabled Build Settings scenes with `-buildonly`), everything under a `Resources`, `StreamingAssets`, `Editor`, `Editor Default Resources` or `Gizmos` folder, every asset `ProjectSettings/*.asset` names (render pipeline, preloaded assets, package settings), Build Profiles and their scenes (Unity 6, with `-buildonly` too), Addressables entries, folders included, and assets with an AssetBundle name. Project settings and Addressables are read through the loaded objects, so binary serialization works as well. Code, shaders, shader includes, plugins and files Unity imports as no asset type are never listed, because code loads them by name. An asset loaded only by a path or name string from code (`AssetDatabase.LoadAssetAtPath`, `Shader.Find`, an address string) is listed too: read the list before deleting. The report's `UnusedBytes` adds up the file sizes.
