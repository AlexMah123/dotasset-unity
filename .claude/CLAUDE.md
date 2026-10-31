# DotAsset for Unity

Editor-only Unity package that lets scripts and AI agents do non-interactive asset operations, in five groups: Export / Import / Edit / Migrate / Audit. Its design follows uasset-workbench (Unreal), a separate project.

## Layout

```
Package/                         the UPM package (com.alexmah.dotasset), installed by git URL ?path=/Package
├── package.json
├── Editor/
│   ├── DotAsset.Editor.asmdef
│   ├── Core/                    shared layer: dispatcher, queue, batch entry, value codec, sessions, paths
│   ├── Export/ Import/ Edit/ Migrate/ Audit/   one <RunName>Command.cs per run
│   └── UI/                      Window > DotAsset
└── Scripts~/dotasset.py              the wrapper (the ~ keeps Unity from importing it)
Docs/                            agent-facing docs, one per group plus AI-Guide.md
tests/                           wrapper tests and naming-rule checks: python -m unittest discover -s tests
tests/smoke/run_smoke.py         end-to-end test in a real Unity project, both routes
tests/editmode/run_editmode.py   EditMode tests of the rewriters, ValueCodec and RunArgs, in DotAssetTest-Medium (has the Test Framework)
examples/                        agent tasks done with DotAsset, e.g. npc_scenario.py
tools/gen_meta.py                writes missing .meta files for the package
tools/compile_check.py           compiles the package with every test project's Unity compiler, plus .NET 4.8 and Standard 2.1
Version.md                       release notes
```

## Versioning

Version lives in `Package/Editor/Core/DotAssetVersion.cs` and `Package/package.json` "version". Change both, and add a `### <version>` section at the top of `Version.md`.

Patch (0.1.x) for fixes, test upgrades and docs. Minor (0.x.0) for anything new to call: a run, an op, a spec key, an argument, a wrapper option. While on 0.x a breaking change is also a minor version, marked breaking in `Version.md`. 1.0.0 only when runs, specs and export JSON are promised stable.

Keep the two apart: a verified fix ships as its own patch release instead of waiting for the next minor, and a minor release carries the new features plus only the fixes found while building them.

Release order: implement, `python tools/gen_meta.py` if a file was added, `python tools/compile_check.py`, unit tests, EditMode tests, bump the version, `Version.md` and docs, commit and push, then the smoke matrix, then the tag. The smoke matrix comes last because it is the slow part.

A release needs the smoke test to pass three times in a row on every tested Unity version, headless (most steps through a `--serve` host) and with the editor open (`--launch` before, `--quit` after), then an annotated `v<version>` tag whose message carries the matrix result (passes per project and route). Never edit the package or the smoke test while a pass runs.

## Code rules

- Allman braces, 4-space indent, `//` comments only (no `///` or `/** */`), no semicolons inside comments as sentence breaks
- Keep to C# 7.3 and APIs present in both .NET Standard 2.1 and .NET Framework 4.8 (no `string.Contains(char)`, no `switch` expressions, no `using var`)
- Every Unity API must exist in 2022.3 and 6000.x. Check `Unity-Technologies/UnityCsReference` branches `2022.3` and `6000.3` when unsure. Avoid APIs Unity 6 marks `[Obsolete]`
- No project-specific names, prefixes or asset paths in the package or docs, examples use neutral names
- Exit codes through `ExitCode` (Success 0, Failed 1, Refused 2, IssuesFound 3), never bare ints. IssuesFound is for Audit only
- Never save the whole project (`AssetDatabase.SaveAssets`) from a command, save exactly the targets (`SaveAssetIfDirty`, `SaveAsPrefabAsset`, `SaveScene`)
- Writes are all or nothing per target: `PrefabSession`, `SceneSession`, `AssetSnapshot`, or an undo group. A failed target logs `ctx.FailTarget(target, reason)` and saves nothing
- Edit and Migrate runs never write without `-apply`. Call `RunJournal.Keep(path)` before every write (it journals the file and refuses during a dry run, `SaveAssetIfDirty` skips the save hook) and write text files through `DotAssetPaths.ReplaceText`

## Group rule

Run name suffix `Export` is Export, suffix `Import` or prefix `Create` is Import, prefix `Edit` is Edit, prefix `Audit` is Audit, anything else is Migrate, checked in that order. Prefixes match letters, not words: `EditorTidy` is Edit. Written twice, in `Groups.Resolve` (ExitCodes.cs) and `resolve_group` (dotasset.py). Change both. The tests check every command sits in the folder its name resolves to.

## Adding a run

1. Pick the group by the rule above. Create `Package/Editor/<Group>/<RunName>Command.cs`, `public sealed class <RunName>Command : DotAssetCommand`
2. Header comment: one line of purpose, one `// Usage:` line, a `// Docs/<Group>.md` line
3. Override `Summary`, `Arguments` (every `-key` it reads; the group's common arguments are added automatically), `Execute`
4. Export: write through `ExportHelper.Header` and `ExportHelper.Save` (path under `Library/DotAsset/Export`, `ExporterVersion` and `ExportType` included). Audit: use `AuditReport`
5. Run `python tools/gen_meta.py` so the new file has a `.meta` (git-installed packages ignore files without one)
6. Update `Docs/<Group>.md`, the decision table in `Docs/AI-Guide.md`, the README matrix when a new asset type is covered, and `Version.md`
7. `python tools/compile_check.py` and `python -m unittest discover -s tests`
8. Add a smoke step that runs it and reads the result back through an export, never on its exit code alone, then run `python tests/smoke/run_smoke.py --project <test project> --clean` headless and with the editor open
