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
tools/gen_meta.py                writes missing .meta files for the package
Version.md                       release notes
```

## Versioning

Version lives in `Package/Editor/Core/DotAssetVersion.cs` and `Package/package.json` "version". Change both, and add a `### <version>` section at the top of `Version.md`.

## Code rules

- Allman braces, 4-space indent, `//` comments only (no `///` or `/** */`), no semicolons inside comments as sentence breaks
- Keep to C# 7.3 and APIs present in both .NET Standard 2.1 and .NET Framework 4.8 (no `string.Contains(char)`, no `switch` expressions, no `using var`)
- Every Unity API must exist in 2022.3 and 6000.x. Check `Unity-Technologies/UnityCsReference` branches `2022.3` and `6000.3` when unsure. Avoid APIs Unity 6 marks `[Obsolete]`
- No project-specific names, prefixes or asset paths in the package or docs, examples use neutral names
- Exit codes through `ExitCode` (Success 0, Failed 1, Refused 2, IssuesFound 3), never bare ints. IssuesFound is for Audit only
- Never save the whole project (`AssetDatabase.SaveAssets`) from a command, save exactly the targets (`SaveAssetIfDirty`, `SaveAsPrefabAsset`, `SaveScene`)
- Writes are all or nothing per target: `PrefabSession`, `SceneSession`, `AssetSnapshot`, or an undo group. A failed target logs `ctx.FailTarget(target, reason)` and saves nothing
- Edit and Migrate runs never write without `-apply`

## Group rule

Run name suffix `Export` is Export, suffix `Import` or prefix `Create` is Import, prefix `Edit` is Edit, prefix `Audit` is Audit, anything else is Migrate. Written twice, in `Groups.Resolve` (ExitCodes.cs) and `resolve_group` (dotasset.py). Change both. The tests check every command sits in the folder its name resolves to.

## Adding a run

1. Pick the group by the rule above. Create `Package/Editor/<Group>/<RunName>Command.cs`, `public sealed class <RunName>Command : DotAssetCommand`
2. Header comment: one line of purpose, one `// Usage:` line, a `// Docs/<Group>.md` line
3. Override `Summary`, `Arguments` (every `-key` it reads; the group's common arguments are added automatically), `Execute`
4. Export: write through `ExportHelper.Header` and `ExportHelper.Save` (path under `Library/DotAsset/Export`, `ExporterVersion` and `ExportType` included). Audit: use `AuditReport`
5. Run `python tools/gen_meta.py` so the new file has a `.meta` (git-installed packages ignore files without one)
6. Update `Docs/<Group>.md`, the decision table in `Docs/AI-Guide.md`, the README matrix when a new asset type is covered, and `Version.md`
7. `python -m unittest discover -s tests`
