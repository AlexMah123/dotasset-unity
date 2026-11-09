# Version

Defined in `Package/Editor/Core/DotAssetVersion.cs` and `Package/package.json`, written into every export as `ExporterVersion` and every result as `DotAssetVersion`.

## 0.x

Versions follow one rule. A patch (0.1.x) holds fixes, test upgrades and docs. A minor version (0.x.0) adds something new to call: a run, an op, a spec key, an argument or a wrapper option. While on 0.x, a change that breaks existing calls is also a minor version, marked breaking here. 1.0.0 comes when runs, specs and export JSON are promised to stay stable.

### 0.12.0

The start of the format contract, two new things to call, and the fixes found while building them. The three migrations once planned here (`RenameMethod`, and `RenameField` and `RenameShaderProperty` following `.anim` curves) wait for the December Unity 7 preview and move to a later minor.

- Every run result, export header and audit report carries `"FormatVersion": 1`, and so does the wrapper's own `--json` report for a run it lost sight of. It goes up only on a breaking change to that JSON. A new Stability section in `Docs/AI-Guide.md` says what a script can rely on, and that a key, run, argument, spec key or exit code is deprecated for one minor release before it goes
- `RebindAnimation -property=<old> -toproperty=<new> -type=<Component>` renames an animated property on the components `-type` names (a list for several), with its parts (`m_Color` to `m_Tint` also rewrites `m_Color.r`, not `m_ColorTemperature`), so a field rename no longer leaves its curves "(Missing!)". The run now also reaches the clips inside `.controller` and `.playable` (Timeline) files, each file one target with all its clips, so `-from`/`-to` rewrites those too
- `dotasset.py --mcp` serves the runs as MCP tools (`dotasset_run`, `dotasset_list`) over stdin and stdout, standard library only, for MCP clients on Unity 2022.3 or in projects without `com.unity.pipeline`. Each call is routed like a `dotasset.py` call. See "As an MCP server" in `Docs/AI-Guide.md`
- Changed: export, audit report and log file names carry the time in UTC, like `ExportedUtc` inside them. Names written before 0.12.0 carry local time, so while older files remain, tell the newest by its write time or its `ExportedUtc`
- Fixed: logs are pruned and listed by write time, not by name. East of UTC, the name order deleted a run's own new log while older logs named in local time sorted after it
- Fixed: a failed run's `Error`, the `error:` line, names the run and its failed targets once. Before, a target failed through the usual path read "MoveAsset failed for A -> B. MoveAsset failed for A -> B. Reason: nothing at A"
- Docs: a "Which tool" table in `Docs/AI-Guide.md` says which jobs go to DotAsset, the Unity CLI, an MCP server, UniCli or a YAML parser, and SKILL.md says DotAsset changes assets only
- Not planned: `AuditPrefab`, since `AuditMissingReference` already reports Missing Scripts with a ready `RemapScript` line and a fix spec (R2) and prefab instances whose source is missing (R4). Unity in CI through GameCI, since it needs a Unity licence as a secret on a public repo, and `tools/compile_check.py` (six targets) and the EditMode tests already run before every release. Later: testing the Unity CLI adapter on a second version waits for CLI beta.16 (beta.15 is installed), and Unity 6.7 LTS joins the matrix in the first release after it ships

### 0.11.5

Fixes from the fifth review (2026-11-05), each with a test that failed on its parent commit. This closes the "Known issues in 0.11.4" list.

- With the editor open, `DeleteAsset` no longer deletes an asset whose only user is an open scene or the Prefab Mode prefab with unsaved changes. The target fails with exit 1, also with `-force`, and the reason names the scene or prefab. `AuditUnusedAsset` counts such an asset as used
- An Audit's `-report=<path>` is checked with the arguments, so the dry run refuses it too (exit 2): a path inside `Assets/`, `Packages/` or `ProjectSettings/`, a file there that is not a DotAsset report, or a `<stem>.fix.json` next to it that this report does not name. Running the same `-report` again still replaces its own report and fix spec
- A path with a `.` or `..` segment, or a root in the wrong case (`assets/`), in `-from`, `-to` or a spec is refused with exit 2 in the dry run too. Before, the dry run passed and `-apply` failed with exit 1
- `UndoRun -atomic=false` that a held file stopped partway can be run again and restores the rest. A file the run created keeps its `.meta` until the file itself is gone
- `DeleteAsset -spec` refuses an unknown key with exit 2, as every other spec run does, so a misspelt `Asets` no longer reads as "nothing to delete"
- `MoveAsset` refuses a rename that changes only the case (`Red.mat` to `RED.mat`) with a reason that says so and gives the way through: two moves in one spec via a temporary name
- A failed run that wrote nothing keeps no journal, logs "nothing was written, no journal kept" and prints no `undo with:` line
- Without `-scandir`, `DeleteAsset`'s users check reads `Assets` and every embedded or local package, so a package of the project's own that uses an asset keeps it from being deleted. Registry, git and tarball packages are left out. A `-scandir` you give replaces that default, as before
- When another program holds the file, `RemapReference` on a model (its `.meta`) fails the model with exit 1 and puts the importer back, and `CreateAsset` fails that entry instead of ending the run on an unhandled exception. `RemoveMissingScripts` and `DuplicateAsset` were checked the same way and were already right, the checks stay as tests
- Tests: a unit test lists every file write, move and delete in the package with its count, so a new unjournaled write fails until it is added with its reason. A unit test checks that every run reading `-spec` refuses unknown keys. `test_versions_agree` also checks the README's install pin and sample line. The smoke test's leak check covers `ProjectSettings/` and `Packages/`, "delete refuses project-named" runs with the editor open too, on a Build Settings scene of its own, and a new editor step runs `RemoveMissingScripts` on an open scene
- The release matrix logs the commit and a clean tree at its start and end, and fails if either changed
- Not planned: through the wrapper, `-apply=maybe` and `-apply=` count as runs that could write (exit 4 when lost) though the run refuses them, which errs on the safe side. EditMode tests stay on one Unity version, the smoke test covers all four. Testing the Unity CLI adapter on a second version is planned for 0.12.0

### 0.11.4

Fixes from the fourth review (2026-11-05), each with a test that failed on the code before it.

- Saving an asset of a read-only package in the editor, with no run open, no longer throws inside Unity's save hook. The read-only backstop in `RunJournal.Keep` and `KeepMeta` applies during a run only, and every writing run still refuses such a file
- When another program holds the file, `RemapReference` (an asset saved outside a prefab or scene, such as a material) and `EditImporter` (the `.meta`) fail that target with exit 1 and put the object back, as the other runs do since 0.11.3. `EditImporter` imports the asset again with the settings put back
- The wrapper reads `-apply` the way the run does: `--apply` and `"-apply"` count as a run that could write (exit 4 when it is lost), and an `-apply` inside a quoted value does not
- Docs: the Unity CLI notes are marked as tested on CLI beta.15 and list beta.16's new exit codes 14, 17 and 18, `dotasset_run` being marked destructive in MCP is explained, and the comparison table adds unity-cli-loop and IvanMurzak/Unity-MCP 1.7.0
- CI runs the unit tests on Python 3.8, 3.9 and the newest 3.x

### 0.11.3

Exit codes and stricter input, the rest of the 2026-11-03 review. Each fix has a test that failed on the code before it.

- The wrapper returns 4 (outcome unknown) only for a lost run that could have written: an Import run, or an Edit or Migrate run with `-apply`, that had started. Any other lost run (an Export, an Audit, a dry run, a run that never started) is 1, and the message and JSON still name the task. `--result` keeps 4
- `--timeout` and `--startup-wait` refuse 0 and negative values with exit 2, instead of giving up at once
- The group of a run named in another case (`createasset`) is the registered command's group, so the editor's write check treats it as the Import run it is
- Through the Unity CLI, `--confirm true` adds `-apply` only to an Edit or Migrate run. An Export run with it no longer fails on an unknown argument
- Enum values in specs are member names only, in any case. A number (`"7"`) or a comma list (`"In, Out"`) for an enum that is not `[Flags]` is refused (EditImporter, EditMaterial, EditAnimatorController, hierarchy ops, curves). Writing a serialized enum property still takes a name, a display name or an integer, as before
- A curve key given as `In`, `Out`, `InWeight` or `OutWeight` must be a number, and `PreWrap`, `PostWrap` and a gradient's `Mode` must be a name that parses, instead of quietly falling back to a default
- An array size must be a whole number up to 2147483647, a float a value inside float range (or Infinity), a character a one-character string or a code from 0 to 65535. Before, they were truncated or wrapped
- When another program holds a file and Unity cannot save it, `EditMaterial`, `EditAsset`, `EditAnimatorController`, `RebindAnimation` and `RenameShaderProperty` fail that target with exit 1 and put the object back. Before, the run reported the file written, with only a warning
- Not planned: through the Unity CLI an Audit that finds issues is still CLI exit 6, with "exit 3" and the outputs in the message, as documented in AI-Guide. Changing it would break CLI callers
- Tests: EditMode tests for each fix, unit tests for the wrapper's exit 4 rule, the wait values and the order of the group rule in C#

### 0.11.2

Fixes from the 2026-11-03 review, each with a test that failed on the code before it.

- Every writing run refuses a file in a read-only package (registry or git), in the dry run too. `DeleteAsset`, `RemapReference`, `RebindAnimation`, `RenameShaderProperty`, `ResaveAsset`, `RenameField`, `RemapScript` and `RenameSerializeReferenceType` now fail that target with exit 1 and a "read-only" reason, as the other runs did. `RunJournal.Keep` refuses such a file as a backstop
- A path with a `.` or `..` segment, or starting `assets/` or `packages/` in the wrong case, is refused with exit 2 instead of being written past the journal, so `UndoRun` could not put it back
- `CreateAsset` checks every entry's `Kind` and `Path` before creating anything, so a bad later entry no longer leaves the earlier ones created. It now journals an `AnimatorController` and a new scene before writing them, as every other kind
- `-overwrite` (`RenameShaderProperty`) and `-buildonly` (`AuditUnusedAsset`) are switches, so `=false` turns them off instead of on
- `RemapReference` keeps and saves a non-prefab, non-scene file before it commits the change, as `EditAsset` does
- The 30-journal cap no longer prunes the journal of an interrupted run, or of an `-atomic` rollback that could not put every file back, until `UndoRun` has undone it. Such a rollback marks its journal `RollbackIncomplete`
- Not reproduced, no change: `RemapReference` on a model's `.meta` not being undone, and `UndoRun` leaving a moved folder's `.meta` behind
- Tests: EditMode tests for the atomic rollback through the dispatcher, a model remap and its undo, the path refusals, the `CreateAsset` pre-check and the journal cap. A unit test checks that every `Args.Has` key is a switch

### 0.11.1

- A headless Unity whose wrapper was killed no longer leaves the editor opening an Untitled scene. `Queue/batch/running.json` keeps the scene setup the wrapper saved, and once that Unity has ended the next call (a run, `--launch` or `--serve`) puts it back, but only over a setup that lists no scene, so one an editor saved since then is kept. Found when the 0.11.0 smoke matrix failed after a hand check had killed a wrapper
- AI-Guide: the Unity CLI's exit codes for `dotasset`. A failed run and a run past `--timeout` are both 6 (not 14), an unknown command 13, no editor or host 12, a bad CLI argument 2. 12 can be run again, 6 not before reading the run's log. `--timeout` takes whole seconds
- Checked, no change: a run with no `--timeout` gets the adapter's 1800 s, not Pipeline's 60 s default (a 70 s run on 6000.3 with CLI 1.0.0-beta.15 exited 0)
- Tests: unit tests for the scene setup after an orphaned run, through the headless route and `--launch`, the guard for a newer setup, and the record carrying the setup

### 0.11.0 (breaking)

- Unity CLI adapter: in a project with `com.unity.pipeline` 0.9 or later, `unity command dotasset run` (name, args, assets, confirm, timeout) and `unity command dotasset list` reach DotAsset in the open editor or a warm `--serve` host, and `unity mcp` lists them. A run that does not exit 0 fails the CLI command with `exit <code>, <summary>` and the run's lines. The adapter has its own asmdef and compiles only with that package. Tested with Pipeline 0.9.1-exp.1 and CLI 1.0.0-beta.15
- Saved scenes settle in `SceneSettle`, shared by the task queue and the adapter, with the same timings as before
- Breaking: `UndoRun -run=last` is refused (exit 2) when the newest run belongs to another caller or is itself an `UndoRun`. The wrapper takes `--caller NAME` (default `DOTASSET_CALLER`), which the journal keeps as `Caller`. With no caller on either side, `-run=last` takes the newest run as before
- Breaking: `--quit` closes only an editor or `--serve` host that `--launch` or `--serve` started for the same caller, recorded in `Queue/launched.json`. Any other editor is left open (exit 2). `--not-mine` closes it anyway. Checked on 6000.3 and 2022.3: Unity saves dirty assets when it quits, so only unsaved scenes block a quit, as before
- A headless Unity left running by a hard-killed wrapper is named (`a headless DotAsset run (<Run>, Unity pid N) outlived the wrapper that started it`). The next call waits up to `--timeout` for it, then exits 2 naming the pid, instead of waiting for an editor that never comes up
- The journal keeps the whole Build Settings scene list before a run's first write, so undoing a `MoveAsset` or `DeleteAsset` of a listed scene lists it at its old path again. A run that left the list unchanged keeps no copy of it. `UndoRun` refuses once the list changed after the run. Unity 6 custom build profiles keep their own scene list, which the journal still does not cover
- Tests: EditMode tests for `SceneSettle`, `EditorRun`, the `-run=last` refusals and the scene list undo (no longer ignored). Unit tests for `--caller`, the orphaned headless run, the `--quit` owner record and the adapter build in `compile_check`. Smoke step for the CLI adapter in the 6.3 project, which now has `com.unity.pipeline`

### 0.10.2

- The `--serve` host no longer takes the keyboard focus on Unity 6.3 and later. On Windows it runs on a desktop of its own (`DotAssetHost`), where the window batchmode Unity opens cannot come to the front. When Windows refuses that desktop, the host starts as before and the wrapper says so
- `UndoRun` of a `.meta`-only run (`EditImporter`, `ResaveAsset -metaonly`) checks only the `.meta` for read-only, so a read-only texture no longer blocks restoring its import settings. A rolled back or pruned journal whose backup copied a read-only file is now deleted
- A failed restore says the file is missing from disk whenever it is, not only after some other path was deleted
- A compile error that cleared before the run started no longer reads as a run that never started: a run that then dies without a result gives exit 4, not 1
- Tests: EditMode tests for the `.meta`-only undo of a read-only file, deleting a journal with read-only backups, the missing-file note, `UndoRun` refusing a path it cannot read, and `MoveAsset` of a folder holding unsaved work. A rollback that cannot delete its folder leaving a false interrupted run was not reproduced on 6000.0, the test stays as a guard. Unit tests for the compile error case and the host's desktop

### 0.10.1

- Folder entries in a journal hash what is under them (relative path, length and write time of every file, plus the folder's `.meta`), not the constant `folder`, so `UndoRun` refuses to delete a folder the run created once something was added to it. A file another program holds hashes to `unreadable: <reason>`, which never matches. An older journal's bare `folder` passes only when everything under it is an entry of that journal
- Rollbacks and `UndoRun` restore all or nothing: every path is checked first (a scene open in the editor or a prefab in Prefab Mode, also inside a journaled folder, or a read-only file the run wrote), and nothing is restored when one is blocked. A file still as it was before the run is left alone, read-only or not. A rollback that cannot finish keeps its journal and prints a W line with the `UndoRun` command that finishes it. `MoveAsset` refuses a source with unsaved changes, as `DeleteAsset` does
- A failed `UndoRun` can be retried: the run counts as undone only when every path was restored
- An interrupted run is recovered before anything else runs, at editor or `--serve` start and before every run, so its journal no longer takes in edits made after the crash. `UndoRun` of an interrupted run says what it cannot check
- An editor that dies mid-task, or a run that throws past the dispatcher, gives exit 4 when the run could write, and names the task's interrupted journal with its `UndoRun` line. A compile error logged after the run started no longer reads as "never started"
- Queued tasks expire `--timeout` plus 120 s after they were queued, so a task left by a killed wrapper never runs later with nobody watching. The wrapper prints `task: <id>` right after queuing
- A stale heartbeat counts only while Unity holds the project lock, so a dead editor's pid reused by another Unity no longer gets the task
- `ResaveAsset` journals only the `.meta` with `-metaonly` and for textures, models, audio, video and fonts, whose source a resave only reads. Prefabs, scenes and other native assets keep a full copy
- Known issue: undoing a `MoveAsset` of a Build Settings scene brings the scene back but leaves the scene list at the moved path. Fix it in File > Build Settings by hand, whole-list journaling comes in 0.11.0. Unity 6 custom build profiles keep their own scene list, which the journal does not cover
- Docs: pass a run id rather than `-run=last` when callers share the project or an undo is retried, only `--quit` what you started, the misleading message after a hard-killed headless call, the comparison re-checked and dated with a narrower claim
- Tests: EditMode tests for folder hashes, recovery, the all-or-nothing restore, task expiry, the retry, which assets resave `.meta`-only, and the scene list after an undo (ignored until 0.11.0). Unit tests for the wrapper fixes. Smoke steps for the read-only and held-file undo and the `.meta`-only resave. CI runs the Python unit tests on windows-latest

### 0.10.0 (breaking)

- Migrate runs are atomic by default: a failed target rolls back every file the run wrote. `-atomic=false` keeps the targets that worked (it used to turn `-atomic` on)
- Dry runs refuse writes: a save, create, move, delete or `DotAssetPaths.ReplaceText` under `Assets/` or `Packages/` during an Edit or Migrate run without `-apply` is refused, logged and fails the run. `RunJournal.Keep` throws in a dry run, and an asset created anyway is deleted when the run ends. Unity's own imports still go through: a `.meta` it creates during the run may be saved, and a file already on disk is never counted as created
- Exit code 4 Unknown, from the wrapper only: it was interrupted, timed out or lost Unity while the run could still write. It prints `outcome: unknown, task <id>` (an `Outcome` object under `--json`), and `--result <task>` prints the editor's result once it lands, 4 while the task still runs. A headless Unity that exits without a result stays 1 only when its log shows the run never started
- Commands declare their group and `WritesWithoutApply`. An Import run must override `WritesWithoutApply` to return true, and a declared `Group` must match the name rule, or the registry leaves the command out. Running it is refused with the reason, and `CommandCatalogExport` lists it under `Rejected` and shows `WritesWithoutApply` per run. Custom Import runs need the override
- EditMode runner restores the scenes the editor reopens, so the next editor start no longer shows an unsaved Untitled scene
- Smoke test: dry-run writes refused and the files left as they were, an undeclared Import run refused, the smoke helpers that write take `-apply`. Unit tests for exit code 4 and `--result`, EditMode tests for the registry checks

### 0.9.6

- EditMode tests in Unity's Test Framework (`tests/editmode/run_editmode.py`): `RenameField` on fixture YAML (top-level keys only, line endings kept, a clash refused, overrides followed through variants, nested prefabs and stripped objects in any scan order), the `RemapScript` and `RenameSerializeReferenceType` patterns, `ValueCodec` (each kind written, wrong values refused, an export written back unchanged) and the argument parser. No change in behaviour: the rewriters' text passes moved into internal helpers the tests call

### 0.9.5

- Journal ids are taken in UTC, and each journal carries a sequence number. `-run=last` and pruning go by that number, so a clock change or a time zone switch no longer picks the wrong run. Journals from before 0.9.5 count as older than any new one
- `RenameField`, `RemapScript` and `RenameSerializeReferenceType` write each rewritten file to a temporary file and swap it in with `File.Replace`, so a crash never leaves a half-written asset
- Quit and open-scene requests from the wrapper carry the caller's own id and are answered per id, so two callers never take each other's answer
- `UndoRun` takes a scene that `CreateAsset` added to the Build Settings back out of the list, and refuses when the list has changed since
- Smoke test: a legacy journal without a sequence number is never picked as `last`. A scene added to the Build Settings is undone. The migration steps (texture, Missing Script, `RebindAnimation`, `RemapScript`, `RenameField`, `[SerializeReference]`) also run with the editor open, through a helper run that refreshes the editor. A unit test checks that an open-scene caller takes only its own answer

### 0.9.4

- Write-ahead journal: each path a writing run keeps is logged to `journal.log` the moment its backup exists. A run that dies (crash, kill, power loss) before it closes its journal is finalized as `Interrupted` by the next run, and `UndoRun` restores it. Before, such a run left no journal and could not be undone
- Exports, reports and journals are written to a temporary file and swapped in with `File.Replace`, so a reader never sees a half-written or missing file
- `EditImporter`: the dry run's plan lists each texture key before and after (and each platform block the spec names), like `Properties`. An asset that already has every value is left alone, not reimported or journaled. The journal keeps only the `.meta`, the source file is never copied
- The wrapper waits for Unity to exit after killing it, so the next start does not find the project still locked
- Smoke test: an interrupted run's journal (cut-off last line) is recovered and undone. `EditImporter` checks its plan, the `.meta`-only journal, a rerun with nothing to change and the undo. The unit tests no longer leave the fake editor running

### 0.9.3

- `DeleteAsset` refuses an asset the project names outside any asset, wherever `-scandir` points: what project settings reference (render pipeline, preloaded assets, config objects), enabled Build Settings scenes, Build Profiles and their scenes on Unity 6, Addressables entries and AssetBundle-tagged assets. Before, only assets under `-scandir` counted, so `-scandir=Assets/Art` could delete the render pipeline asset. An asset in a `Resources` or `StreamingAssets` folder is deleted with a warning that code may load it by name
- `AuditUnusedAsset` reads project settings and Addressables through the loaded objects, so binary serialization no longer lists the render pipeline or Addressables entries as unused. Build Profiles, their scenes and AssetBundle-tagged assets are roots too
- `RenameField` renames the field in every script that inherits it from the declaring class, not only the given script. Before, prefabs of a subclass kept the old key and lost the value on the next save. Refused when one of those classes declares its own field named `-from` or sits in a DLL
- `RenameField -apply`: a file that could not be read stopped the run before the files already written were reimported. It now fails that target and goes on
- Smoke test: `DeleteAsset` dry runs on a Build Settings scene (6.6), a render pipeline asset (Medium) and an AssetBundle material must be refused, and `AuditUnusedAsset` must keep the material. `RenameField` renames a subclass prefab, read back through an export

### 0.9.2

- The `--serve` host starts hidden instead of minimized. This does not yet stop it taking the keyboard focus: on Unity 6.3 and later its window still comes to the front once per host start. Unity 6.0 and 2022.3 hosts never did
- Smoke test: "create wrote the assets" and "create again skips" ran on the cold route because their names start with a cold step's name, they now go through the host
- Smoke test: "editor states" waits for the compile to start before it sends a run, as it already did for play mode. A run sent sooner went through before the compile

### 0.9.1

- Headless runs on Unity 6 no longer take the keyboard focus. Unity 6 opens a console window in batchmode, the wrapper now starts it hidden. While `--launch` waits for the editor, its windows are minimized every 0.1 s instead of every second
- `EditImporter` dry runs in an open editor or a `--serve` host left the importer changed in memory, and the next save of it wrote the dry run's settings. Importers are now restored from a `SerializedObject` copy
- `EditPrefab`, `EditScene` and `EditAsset` no longer save a target whose values are already there, a rerun of a spec leaves the files alone
- Smoke test: steps that write files restart the warm host instead of launching Unity per call, about a third faster headless. `RenameField -apply` stays on the cold batch route. New step: an `-apply` with no change must leave the files alone

### 0.9.0

- `RenameSerializeReferenceType` (Migrate): repoints `[SerializeReference]` data from an old type name to a compiled type after the class was renamed or moved, `-from="Assembly-CSharp Game.OldEffect" -to=Game.Effects.Burn`. Rewrites `type: {class, ns, asm}` in the YAML, field values kept
- `AuditMissingReference` R5: `[SerializeReference]` data whose type no compiled class has, which Unity reads as null without a warning. Confirmed by Unity outside scenes, so `[MovedFrom]` renames are not reported. The report's `MissingTypes` lists candidates of the same class name and a ready argument line
- Smoke test: a `[SerializeReference]` class moves into a namespace, R5 finds it with the moved class as candidate, the rename brings `x` back

### 0.8.0

- `RenameField` (Migrate): renames a serialized field after its C# rename, `-script=Assets/Scripts/Enemy.cs -from=hp -to=health`. Rewrites the key in every component and ScriptableObject of that script, and the prefab overrides that target them through variants, nested prefabs and scenes. Refused while the class still has the old field. Top-level fields only
- `AuditUnusedAsset` (Audit): U1 lists assets no scene, Resources or Editor folder, project setting or Addressables entry reaches, U2 scenes outside Build Settings with `-buildonly`. The fix spec deletes U1 through `DeleteAsset`
- `DeleteAsset -spec=<file>` takes `{"Assets": [...]}`, the fix spec of `AuditUnusedAsset`
- Edit runs read back every property they wrote. A value `OnValidate` or a range check changed fails the target with both values, nothing of it is saved
- Smoke test: every run that writes with `-apply` must keep a journal at least once. Steps for `RenameField`, `AuditUnusedAsset` with its fix, and a value `OnValidate` clamps

### 0.7.0

- Run journal: every writing run (Import, and Edit or Migrate with `-apply`) keeps the files it touches, as they were, in `Library/DotAsset/Journal/<id>`, printed as an `out:` line. The newest 30 are kept
- `UndoRun` (Migrate): `-run=<id>` or `-run=last` puts back every file the run wrote, created, moved or deleted, moved files with their GUID. Refused as a whole when any of them changed after the run, is open in the editor or has unsaved changes. Undoing an undo redoes the run
- `-atomic` on every Edit, Migrate and Import run: all or nothing across targets. When a target fails, the files the run already wrote are restored. Without it, other targets stay written as before
- Smoke test: journal, undo of an edit and a move, `-atomic`, a refused undo after a later change. Editor states: a paused queue refuses and withdraws the task, a compile and play mode make the run wait

### 0.6.1

- Smoke test: `RemapScript` by GUID, a prefab's component moves to another script with its field value kept, headless route
- README: the 0.6 runs, an animation clip row, `--serve`, the smoke coverage, a status line that no longer names a version

### 0.6.0

- `RebindAnimation` (Migrate): rewrites curve paths in `.anim` clips after a GameObject or bone was renamed or moved, `-from=Body/Arm -to=Body/ArmL`. Children move along, float and object reference curves both, keys unchanged. A clip that already animates the same property at the new path fails its target
- `RenameShaderProperty` (Migrate): renames a saved property in `.mat` files after a shader renamed it, `-from=_Color -to=_BaseColor`, value and type kept. `-shader` limits it to one shader, `-overwrite` replaces a value already saved under the new name. Scripts and clips that still name the old property are listed
- Fix: the `--serve` host no longer leaves the editor with an Untitled scene, `--quit` restores the last scene setup
- Smoke test: steps for both runs, read back through `MaterialExport` and `AssetExport`

### 0.5.0

- Wrapper `--serve`: starts a warm headless host, batchmode Unity that stays up and takes runs through the same task queue as an open editor, until `--quit`. Many headless steps in a row skip Unity's start-up without opening an editor window. Runs through the host report `inEditor` false, `--status` prints `host pid`. `--quit` puts back the scenes the editor reopens, which the host empties when it exits
- Smoke test: the headless route sends most steps through a `--serve` host. Create, a Prefab export, the first `-apply` edit, a failing edit and the two steps that need a fresh import still launch Unity per run
- Release order, from this version on: implement, `gen_meta` if a file was added, `compile_check`, unit tests, bump the version, release notes and docs, commit and push, then the smoke matrix (3 clean passes on every tested Unity version, both routes), then the tag

### 0.4.0

- Dry runs return a plan: `Plan` in the result JSON and `plan:` lines in the wrapper. Edit runs list each value change with its property path, before and after, every Edit and Migrate run lists the files it would write, copy, move, delete, rewrite or resave. At most 500 entries, then `PlanTruncated`. `PlanTotals` counts every change, file and target, past the 500 too, and the wrapper prints it as the first `plan:` line
- Wrapper `--agent`: compact output for AI agents, the exit code and summary on one line, then only the first error, warnings, failed targets, outputs, plan and, on failure, the log path. Lists stop at 10 entries
- `Docs/SKILL.md`: a short agent skill, about 1,000 tokens, that covers the call, the run picker, the export, dry run, `-apply` workflow and exit codes. Copy it to `.claude/skills/dotasset/SKILL.md`

### 0.3.0

- Wrapper: `--launch` starts the editor minimized and keeps its windows minimized without activating them until the queue is ready, so a launch no longer takes the mouse and keyboard focus. A minimized editor still runs its task queue. `--visible` opts out

### 0.2.2

- Fix: `EditAsset`, `EditMaterial`, `EditAnimatorController`, `EditImporter`, `EditPrefab`, `RemapReference`, `ResaveAsset` and `DeleteAsset` saved over an asset with unsaved changes in the editor (edited in the Inspector, say), writing those changes to disk along with their own. Every run that writes now refuses such a target, dry runs included, and names it
- Fix: `CreateAsset` left a new Animator Controller marked unsaved, so its own ops, or the next `EditAnimatorController`, refused it under the fix above. It is saved right after it is created. New materials and other assets are saved the same way, since Unity 6.6 at times leaves a new material marked unsaved too
- Fix: `-maxarray`, `-maxstring` and `-maxsize` fell back to their defaults when the value was not a whole number. They are refused (exit 2)
- Fix: after a run saved a scene open in the editor, the queue's short wait for Unity to settle could lose the result on a script reload (reported as "the editor stopped") and could save an edit someone made in that wait. The result now survives the reload, and an edit made in the wait is left unsaved with a warning
- Docs: the comparison table covers the Unity CLI's headless runs and `batch`, and MCP servers that edit prefabs. The group rule notes that prefixes match letters (`EditorTidy` is Edit). Editing a Particle System's modules by dotted path
- Smoke test: unsaved changes in the open editor are refused and kept, a reload and an edit while a run settles, a bad number refused, a Particle System edited and read back. `--only` runs chosen steps. A small helper package, `com.alexmah.dotasset.smoke`, is installed into the test project for the editor-side steps
- `tools/compile_check.py` compiles the package with each test project's Unity compiler, and against .NET Standard 2.1 and .NET Framework 4.8, in seconds

### 0.2.1

- Fix: `DeleteAsset` deleted a scene open in the editor with unsaved changes, or the prefab in Prefab Mode with unsaved changes, along with those changes, also inside a deleted folder and with `-force`. It now fails that target with the reason
- Fix: `ResaveAsset` rewrote such a file from disk, leaving the editor and the file disagreeing. It now refuses that target and resaves the rest, and exits 1 when a target was refused instead of always 0

### 0.2.0

Same code as 0.1.2, renumbered by the rule above: 0.1.2 added a wrapper option, `--open-scene`, which makes it a minor release. The `v0.1.2` tag stays for anyone pinned to it. Nothing breaks.

- `examples/npc_scenario.py`: a small agent task done with DotAsset, every step checked, and a README section on what it shows

### 0.1.2

Stability for general and heavy use. Smoke test passed three times in a row on Unity 6.6, 6.3, 6.0 and 2022.3 and on a 2,000-asset project, headless and with the editor open.

- Fix: headless runs of one project now take turns. Two callers that both saw the project free started two Unity processes and one failed on Unity's lock (1 of 4 parallel calls). The wrapper holds `Library/DotAsset/Queue/batch.lock` for the whole run, later callers wait up to `--timeout` with a message that names a headless run, and `--launch` takes the same lock
- Fix: a scene open in the editor was left dirty after DotAsset added a Light to it, because Unity gives the new Light its baking state on a later frame. The next `EditScene` on that scene was refused. The editor queue now saves such a scene again once Unity settles, before it reports the run
- Exports keep the newest 10 per asset and audit reports the newest 20 per audit, older ones are deleted (one prefab had 436 exports). Files under `-outdir` or at a `-report` path are never deleted
- `--open-scene A.unity[,B.unity]` opens scenes in the editor without anyone at the machine, refused like `--quit` while a scene has unsaved changes, and prints the scenes that were open before
- Smoke test: edits a scene open in the editor, paths with spaces and non-ASCII letters, a refused write to a registry package, `-outdir` and `-report`, a Missing Script seeded into a prefab for `AuditMissingReference` R2 and `RemoveMissingScripts`, `ResaveAsset` keeping content, every export's header and version, and the export caps. No step passes on its exit code alone
- Wrapper tests run on Python 3.8, 3.9 and 3.15, and the two lock tests now run on Windows
- Windows is the tested platform. macOS and Linux are not tested

### 0.1.1

- Fix: `MaterialExport` left the material changed in memory. Reading a shader property lets Unity validate the material, which on Standard and URP Lit resets a custom render queue; headless Unity then saved it on quit, and in the open editor the next Save Project would have. Values are now read from a temporary copy, so the asset is left as found
- Fix: `AuditMissingReference` missed references to an asset deleted earlier in the same editor session, Unity still answers its GUID with the old path. R1 now checks an asset is at that path
- Smoke test: every change is read back through an export, dry runs and failed targets are checked to leave files untouched, exports are checked to change nothing on disk, `ArrayOps` Append, Insert and Remove are covered, and the audits are given a seeded missing reference, stray keyword and misnamed normal map to find and fix through their fix specs. It fails if anything in `Assets/` outside its folder changed, stops at the first failure (`--keep-going` runs the rest), always cleans up, and drops the Unity logs of failures it expected
- Fix: headless runs on Windows left their Unity log in `Library/DotAsset/Queue/batch`, Unity's licensing client holds it open after Unity exits. A held log is now listed and swept by the next headless run, and logs of failed runs are pruned after 7 days
- `package.json` carries author, documentation, changelog and license links for the Package Manager
- Releases are tagged, pin one with `#v0.1.1` on the git URL

### 0.1.0

First release. The design follows uasset-workbench 2.6, a tool for Unreal Engine `.uasset` files.

- Core: `DotAssetCommand` registry (class name is the run name, name decides the group), `RunDispatcher` with one result shape for both routes, refusal of unknown arguments and spec keys, Unity's own errors during a target fail that target
- Routes: in-editor task queue under `Library/DotAsset/Queue` with a heartbeat that carries pid and state, started from the first editor update so an editor that never gets focus still takes tasks, headless `-batchmode -executeMethod DotAsset.Cli.Run` with base64 arguments
- Wrapper `Scripts~/dotasset.py`: routing, startup wait on a locked project, fallback to headless when the editor exits before taking a task, Unity Hub discovery from `ProjectVersion.txt`, hints from the Unity log when a headless run cannot start, a headless run leaves the scenes the editor reopens as it found them, `--launch` and `--quit` open and close the editor without anyone at the machine
- Export: `PrefabExport`, `SceneExport`, `AssetExport`, `MaterialExport`, `AnimatorControllerExport`, `ImporterExport`, `AssetReferenceExport`, `TypeCatalogExport`, `CommandCatalogExport`. Delta from defaults, prefab instances as overrides, capped arrays and strings
- Import: `CreateAsset` for folders, ScriptableObjects, materials, prefabs, variants, Animator Controllers, scenes
- Edit: `EditPrefab`, `EditScene` (undo group when the scene is open), `EditAsset`, `EditMaterial`, `EditImporter`, `EditAnimatorController`
- Migrate: `MoveAsset`, `DuplicateAsset`, `DeleteAsset`, `ResaveAsset`, `RemapReference` (prefab instances swapped through Replace Prefab Asset), `RemapScript`, `RemoveMissingScripts`
- Audit: `AuditMissingReference` (R1-R4), `AuditTexture` (T1-T8), `AuditMaterial` (M1-M5), with fix specs for the matching Edit or Migrate run
- `Window > DotAsset`: queue state, pause, run history and logs, copy the wrapper to the project
- Writes never touch scenes the user has open: scene edits, new scenes and new prefabs make their own scene active while they build, and hand it back after
- `EditAsset` refuses imported assets (textures, models, audio), whose objects the importer rebuilds
- A string reference that is not an asset path fails with a hint to use `{"Object": ...}`
- `Static: true` sets every defined flag, the `RemoveMissingScripts` op skips objects owned by a source prefab
- Integer writes are range-checked against the field's size, a Hash128 must be 32 hex digits
- A failed property write is not applied (a new `[SerializeReference]` instance is, and the target's session rolls it back), appends grow the array instead of inserting past the end
- New GameObjects are created directly in the target scene, never parked in another open scene
- A failed target reports its own first error, not the run's
- Transition targets are matched by full state path, `ImporterExport` folders skip native-format files
- Op keys, `Components` entries, conditions, blend tree blocks and move/copy/remap pairs are checked, a misspelt key fails instead of being dropped
- A failed `CreateAsset` entry removes the folders it made, `-apply` is accepted (and ignored) by Import and Audit runs
- A scene listed but unloaded in the editor stays listed after a run, a rolled-back asset keeps its hide flags
- `DeleteAsset` ignores referencers inside any deleted folder, Int64 and UInt64 writes are range-checked too
- The wrapper ignores a heartbeat that names another project, the smoke test refuses leftovers unless `--clean`
- A run that logs an error of its own exits 1 even when its targets succeeded, e.g. an AssetList entry that does not exist
- A target is never reported failed after it reached disk: checks run before the save, errors during it are warnings
- Bare words in `--args` are refused, so `apply` cannot act as `-apply`
- `RemapScript` with the editor open refuses files loaded there, `EditImporter` folders skip native-format files
- Replacing a state's blend tree removes the old one from the controller, `RenameParameter` skips empty states
- A grown `[SerializeReference]` array no longer shares the last element's instance
- Root `SiblingIndex` is refused under undo, the queue survives malformed task files, a broken `.prefab` fails only its entry
- The wrapper only trusts `Unity`/`Unity.exe` processes and ignores long-dead idle heartbeats, macOS paths compare case-insensitively
- Targets in read-only packages (registry, git, tarball) are refused, a material inside a model is refused by `EditMaterial`, Unity's bookkeeping fields are refused as `Properties` keys
- `CreateAsset` checks every entry's ops before it creates anything, a typo refuses the whole spec
- Undo is recorded for a new object before anything else can fail, a negative component `Index` is refused
- `RemapReference` maps children by position among same-named siblings and components by position among their type, leaves `To` and the prefabs it nests alone (per pair), keeps a variant's base, contains a failed swap to its target
- `MoveAsset` and `DuplicateAsset` refuse a folder into itself and remove the folders they made when the move fails
- An export whose assets hold nothing of its kind exits 1 instead of 2, an audit without fixes removes the old `.fix.json` and writes none
- Switch values (`-apply=false`), unclosed quotes and trailing backslashes in `--args` are handled, the wrapper withdraws its task on Ctrl+C and never fails on unprintable asset names
- Smoke test checks that a dry run leaves the prefab as it was and that an export's properties write back unchanged
- Edit runs check every target's op names and keys (and importer platform blocks) before the first target is written
- `RemoveLayer` refuses a layer others sync to and shifts synced indices, Bool and Int parameter defaults are type-checked, `SetLayer` refuses a taken name
- Script ScriptableObjects are snapshotted as JSON, so a rollback never runs their `Awake`/`OnEnable`, a reverted controller is not left dirty
- `RemapReference` snapshots materials, ScriptableObjects, controllers and importers before writing, a failed check restores them
- A `MoveAsset` dry run follows chains the way `-apply` does, `DeleteAsset` keeps a target's references alive when that target is refused
- `RemoveMissingScripts` reports missing scripts added on a prefab instance instead of passing over them
- Malformed spec values (non-string paths, keywords, parents) fail their target or refuse the run instead of throwing, a prefab save that throws fails only its entry
- Animator edits fail on a non-numeric `Weight`, `Speed`, `ExitTime`, `Duration`, `Offset`, `CycleOffset`, `TimeScale` or `Threshold` instead of skipping it, `"Shader": null` is accepted again
- A `MoveAsset` dry run follows the contents of folders earlier moves would carry, `DeleteAsset` drops targets inside other targets
- `RemoveMissingScripts` leaves instance-added missing scripts with a warning, `RemapReference` warns about a dependency it cannot rewrite, the controller export skips dangling states
- Compiles on Unity 6.4 and later: missing references are detected through `objectReferenceEntityIdValue` there (the instance ID accessor is a compile error), the assembly scan's UAC0005 warning is suppressed with its reason
- Not yet compiled inside Unity 2022.3 or 6000.0-6000.3: written and checked against Unity's C# reference source for 2022.3 and 6000.3
