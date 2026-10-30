# Unity CLI and MCP: what they do today

What Unity's own CLI and MCP server do for asset changes, tested on a real editor, and where DotAsset fits next to them. Both Unity tools are in beta and change quickly, so every finding carries the version it was tested on.

**Tested on:** 2026-10-30, Unity 6000.6.7f1, `com.unity.pipeline` 0.9.1-exp.1, Unity CLI 1.0.0-beta.13, Windows. A blank project with only the Pipeline package and the Input System added.

## Summary

| Question | Answer in 0.9.1 / beta.13 |
| :-- | :-- |
| Does `job batch` restore asset files when an op fails? | No. A transactional batch refuses asset-file ops, and a non-transactional one leaves earlier writes on disk |
| Does a dry run execute anything? | No. It only validates the ops, so it shows nothing about what would change |
| Is there a command that repoints references from one asset to another? | No |
| Does a failed op make the CLI exit non-zero? | Not inside a batch: the batch exits 0 with `"success": true` |
| Does `unity mcp` let you load only some tools? | No. Every tool is listed every time (about 26k tokens) |

## Calling commands

```
unity cmd <group> <name> --<param> <value> [--result-only]
unity cmd asset write_text --path Assets/A.txt --contents "a"
```

- Parameters are `--name value`. `name=value` is rejected with `INVALID_COMMAND_ARGS`.
- A misspelled parameter name (for example `--content` for `--contents`) can come back as "Command Not Found" with exit code 6, not as an argument error.
- `unity list --json` prints every command and its parameters (about 144 KB).

## Exit codes

| Case | Exit code |
| :-- | :-: |
| Command succeeded | 0 |
| Bad arguments | 1 |
| Server-side failure (`COMMAND_FAILED`) | 6 |
| `job batch` where an op failed | **0** |

A batch reports each op's failure only inside the JSON (`results[].success`, plus `valid` and `applied` counts). A script or agent has to parse the output; the exit code alone does not say an op failed.

## `job batch`

```
unity cmd job batch --operations '[{"command":"asset write_text","params":{"path":"Assets/B.txt","contents":"b"}}]' --transactional false --dry_run true
```

**Transactional (`--transactional true`).** Asset-file ops are refused with `not_batchable_transactional`, so nothing is written. The response still says `"reverted": true` and names an undo group. Rollback covers what Unity's Undo covers (scene objects and loaded components), not files on disk.

**Non-transactional.** Ops run one by one. In a test where `write_text` created a file and a later `asset delete` failed, the file stayed, and the response said `applied: 1`, `reverted: false`, `revertible: false`.

**Dry run (`--dry_run true`).** Validates and stops. A transactional dry run with an asset op reports `valid: false`, and a non-transactional one reports `valid: true`, `applied: 0`. No file is created either way, and nothing describes the changes the ops would make.

## Reference remapping

No command in the 0.9.1 list repoints references from one asset or script to another across scenes, prefabs and assets, or refuses to delete an asset that is still referenced. Doing this through `eval` means writing the C# yourself.

## Capture commands

`capture game_view`, `capture scene_view` and `capture screenshot`.

## `unity mcp`

- Runs a stdio MCP server. Its only subcommand in beta.13 is `configure`.
- `tools/list` returns 166 tools in one response of about 104 KB (roughly 26k tokens). There is no pagination and no option to load a subset.
- An agent pays that cost every session before making any call.

For comparison, one DotAsset run prints about 170–380 bytes (roughly 50–100 tokens) plus one line per target: a RemapReference migration printed 254–381 bytes and an EditMaterial dry run 170–235 bytes.

## Where DotAsset fits

Use the Unity CLI and `unity mcp` for general editor work: installs, builds, tests, the Console, captures, scene object edits covered by Undo. Use DotAsset for asset changes that must be safe and repeatable:

- **All or nothing per target.** A failed target restores its files and saves nothing.
- **Dry runs that show what would change.** Nothing is written without `-apply`.
- **Migrations.** Repoint references to an asset or script, moves, deletes that check for users.
- **Exit codes that report failure.** A run with a failed target exits non-zero (see the README).
- **Small output, and the editor can be closed.** Also runs on Unity 2022.3, which the Pipeline package does not support.

## Not tested yet

- Whether RQL queries save a scene's earlier unsaved edits.
- The `tools/list` sizes of third-party MCP servers (CoplayDev/unity-mcp, IvanMurzak/Unity-MCP).

These findings will be re-checked when the Production Pipeline launches and with the Unity 7 Preview.
