# Handoff

Status of DotAsset 0.1.0 for whoever picks it up next (a person or Claude Code). Read `.claude/CLAUDE.md` first for the code rules.

## Where it stands

- The package compiles without errors on Unity 6000.6.7f1. Unity 2022.3 and 6000.0-6000.3 have not been compiled yet.
- Seven review passes have been made, written and checked without a compiler against Unity's C# reference source (6000.3 and 2022.3). Their history is in `git log`.
- The end-to-end smoke test passes on 6000.6.7f1 on Windows, both headless and with the editor open (all 31 steps each), last run on commit 0e5681a on 2026-10-24. The editor-open run used `--launch` and `--quit` over remote control.

## Tested Unity versions

| Version | Tested | State on 2026-10-24 |
| :-- | :-- | :-- |
| 6.6 (6000.6.7f1) | yes, smoke test headless and editor open | done |
| 6.3 LTS (6000.3.27f1) | no | installing through Unity Hub |
| 6.0 LTS (6000.0.85f1) | no | installing through Unity Hub |
| 2022.3 (2022.3.18f1) | no | installed, not tested |

Only 6.6 counts as supported until the others pass. Update this table as each one does.

## Queued for the next session

The owner asked for these, in this order:

1. Test 6.3 LTS, 2022.3 and 6.0 LTS, see the next section.
2. Another review pass over the whole package (Core, every command, the wrapper, the docs), the same kind as the
   cleanup pass of 2026-10-24 (commit b7e4c40): dead code, duplication, stale comments and docs, CLAUDE.md rule
   breaks. No behaviour changes without asking. Commit and push it on its own after the unit tests and the smoke
   test pass.
3. Ask whether to add "Developed with the help of Claude Code (Anthropic)" to the README's Credits.

## Next step: compile and smoke test on Unity 6.3 LTS, 6.0 LTS and 2022.3

Agreed on 2026-10-24. 6.6 (6000.6.7f1) is done. In order:

1. **6.3 LTS** (6000.3.27f1). Install with Unity Hub's command line if missing:
   `"C:/Program Files/Unity Hub/Unity Hub.exe" -- --headless install --version 6000.3.27f1`
2. **2022.3** (2022.3.18f1, already installed). The oldest supported version, the most likely to break.
3. **6.0 LTS** (6000.0.85f1). Install the same way.

Both installs were started on 2026-10-24 (logs in `%LOCALAPPDATA%/Temp/dotasset-hub-install2/`). Check
`C:/Program Files/Unity/Hub/Editor/` before starting them again. From a shell inside VS Code, clear
`ELECTRON_RUN_AS_NODE` first: VS Code sets it, and Unity Hub then runs as plain Node and fails with MODULE_NOT_FOUND.

For each version: create an empty project headless (`Unity.exe -batchmode -quit -createProject <dir>`), add
`"com.alexmah.dotasset": "file:<absolute path to this repo>/Package"` to its `Packages/manifest.json`, then run the
smoke test headless (`--unity <that version's Unity.exe>`), and with the editor open through `--launch` / `--quit`.
Compile errors show in the `Unity log:` path the run prints. Fix, commit each fix on its own, rerun. Record each
version's result in "Where it stands".

## Running the smoke test

From the repo root, against a Unity project that has this package installed (Package Manager > + > Install package from disk > `Package/package.json`):

```
python tests/smoke/run_smoke.py --project "<path to the Unity project>" --clean
```

Run it twice:

1. With the Unity editor closed (headless route). Add `--unity "<path to Unity.exe>"` if the editor is not found.
2. With the project open in the editor (queue route). The open scene must be saved, not Untitled. No one needs to be at the machine: `python Package/Scripts~/dotasset.py --project <project> --launch` opens the editor and waits until it is ready, and `--quit` closes it after the run.

If the editor shows an Untitled scene, open a saved one first, or `CreateAsset` cannot create its scene and four steps fail. Headless runs used to cause this: batch Unity empties `Library/LastSceneManagerSetup.txt` on quit. The wrapper now puts the file back after every headless run.

On Windows, `python` may only be the Microsoft Store alias. Use a real interpreter (3.9 or newer works).

Each step prints `ok` or `FAIL` with its exit code. For a failure, read the printed output, the Unity Console, and the `Unity log:` path the run prints. Fix the code, then rerun with `--clean`.

## Checks to run after any change

- `python -m unittest discover -s tests` checks the wrapper and the naming rules (44 tests).
- `python tools/gen_meta.py --check` checks that every package file has a `.meta`.
- Unity itself is the only real compiler. Watch for CS0136 (a local redeclared inside a nested block), which slipped through once.

## Things only a run in Unity can confirm

The smoke test does not reach these, so they are still open.

- `EditorJsonUtility.FromJsonOverwrite` restores `[SerializeReference]` fields of a script ScriptableObject. `AssetSnapshot` relies on it.
- `Undo.RegisterFullObjectHierarchyUndo` brings missing-script components back when an `EditScene` dry run or `RemoveMissingScripts` in an open scene reverts.
- Undo revert of an `EditAnimatorController` dry run in batch mode.
- `ArrayOps` `Append` growing the array.
- `PrefabUtility.ReplacePrefabAssetOfPrefabInstance` in `RemapReference`, with `-keepinstances` off.
- On newer Unity 6, `objectReferenceEntityIdValue != EntityId.None` in `ObjectRefs.IsMissing`. It is guarded by `UNITY_6000_4_OR_NEWER`.
