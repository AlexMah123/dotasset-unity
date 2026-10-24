# Handoff

Status of DotAsset 0.1.0 for whoever picks it up next (a person or Claude Code). Read `.claude/CLAUDE.md` first for the code rules.

## Where it stands

- The package compiles without errors on Unity 6000.6.7f1. Unity 2022.3 and 6000.0-6000.3 have not been compiled yet.
- Seven review passes have been made, written and checked without a compiler against Unity's C# reference source (6000.3 and 2022.3). Their history is in `git log`.
- The end-to-end smoke test passes on 6000.6.7f1 on Windows, both headless and with the editor open (all 31 steps each).

## Next step: compile and smoke test on Unity 2022.3 and 6000.0-6000.3

## Running the smoke test

From the repo root, against a Unity project that has this package installed (Package Manager > + > Install package from disk > `Package/package.json`):

```
python tests/smoke/run_smoke.py --project "<path to the Unity project>" --clean
```

Run it twice:

1. With the Unity editor closed (headless route). Add `--unity "<path to Unity.exe>"` if the editor is not found.
2. With the project open in the editor (queue route). The open scene must be saved, not Untitled. The editor should be idle.

If the editor shows an Untitled scene, open a saved one first, or `CreateAsset` cannot create its scene and four steps fail. Headless runs used to cause this: batch Unity empties `Library/LastSceneManagerSetup.txt` on quit. The wrapper now puts the file back after every headless run.

On Windows, `python` may only be the Microsoft Store alias. Use a real interpreter (3.9 or newer works).

Each step prints `ok` or `FAIL` with its exit code. For a failure, read the printed output, the Unity Console, and the `Unity log:` path the run prints. Fix the code, then rerun with `--clean`.

## Checks to run after any change

- `python -m unittest discover -s tests` checks the wrapper and the naming rules (37 tests).
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
