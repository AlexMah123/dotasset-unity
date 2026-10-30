"""Tests for Scripts~/dotasset.py and for the naming rules the C# side relies on.

Unity itself is replaced by two fakes:
  - a fake editor: a thread that serves the file queue the way TaskQueue.cs does
  - a fake Unity executable: a script that answers -executeMethod DotAsset.Cli.Run the way Cli.cs does
Run: python -m unittest discover -s tests -v
"""

import base64
import importlib.util
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stdout
from io import StringIO

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGE = os.path.join(ROOT, "Package")
SPEC = importlib.util.spec_from_file_location("dotasset", os.path.join(PACKAGE, "Scripts~", "dotasset.py"))
ops = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ops)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def make_project(root, version="6000.0.40f1"):
    os.makedirs(os.path.join(root, "Assets"))
    os.makedirs(os.path.join(root, "ProjectSettings"))
    with open(os.path.join(root, "ProjectSettings", "ProjectVersion.txt"), "w") as f:
        f.write("m_EditorVersion: %s\nm_EditorVersionWithRevision: %s (abc)\n" % (version, version))
    return root


def result(run, code=0, lines=None, outputs=None, error=None):
    return {
        "RunName": run, "ExitCode": code, "Summary": "%s done" % run, "Error": error,
        "Lines": lines or ["I hello"], "Outputs": outputs or [], "Warnings": 0, "Errors": 0,
    }


class FakeEditor(threading.Thread):
    """Serves the queue like TaskQueue.cs: claim by move, write done, keep a heartbeat."""

    def __init__(self, project, respond, state="ready", claim=True, quit_refusal=None, on_quit=None):
        super().__init__(daemon=True)
        self.queue = os.path.join(project, ops.QUEUE_RELATIVE)
        self.respond = respond
        self.state = state
        self.claim = claim
        self.quit_refusal = quit_refusal
        self.on_quit = on_quit
        self.quit_requests = []
        self.open_requests = []
        self.scenes = ["Assets/Scenes/Main.unity"]
        self.stop = threading.Event()
        self.tasks = []
        for d in (ops.PENDING, ops.PROCESSING, ops.DONE):
            os.makedirs(os.path.join(self.queue, d), exist_ok=True)
        self.beat()

    def beat(self):
        with open(os.path.join(self.queue, ops.ALIVE), "w") as f:
            json.dump({"Pid": os.getpid(), "State": self.state, "UnityVersion": "6000.0.40f1", "DotAssetVersion": "test"}, f)

    def run(self):
        while not self.stop.is_set():
            self.beat()
            if self.claim and self.state == "ready":
                pending = os.path.join(self.queue, ops.PENDING)
                for name in sorted(os.listdir(pending)):
                    if not name.endswith(".json"):
                        continue
                    src = os.path.join(pending, name)
                    dst = os.path.join(self.queue, ops.PROCESSING, name)
                    try:
                        os.replace(src, dst)
                    except OSError:
                        continue
                    with open(dst) as f:
                        task = json.load(f)
                    self.tasks.append(task)
                    with open(os.path.join(self.queue, ops.DONE, name), "w") as f:
                        json.dump(self.respond(task), f)
                    os.remove(dst)
                self.serve_open_scene()
                if self.serve_quit():
                    return
            time.sleep(0.05)

    def serve_open_scene(self):
        """Answers openscene.json like TaskQueue.HandleOpenSceneRequest, refusing like quit does."""
        path = os.path.join(self.queue, ops.OPEN_SCENE_REQUEST)
        if not os.path.exists(path):
            return
        with open(path) as f:
            request = json.load(f)
        os.remove(path)
        self.open_requests.append(request)
        refusal = None if request.get("Force") else self.quit_refusal
        previous = list(self.scenes)
        if refusal is None:
            self.scenes = list(request["Scenes"])
        with open(os.path.join(self.queue, ops.OPEN_SCENE_RESULT), "w") as f:
            json.dump({"Opened": refusal is None, "Previous": previous, "PreviousActive": previous[0] if previous else None,
                       "Error": refusal}, f)

    def serve_quit(self):
        """Answers quit.json like TaskQueue.HandleQuitRequest. True when the fake editor closed."""
        path = os.path.join(self.queue, ops.QUIT_REQUEST)
        if not os.path.exists(path):
            return False
        with open(path) as f:
            request = json.load(f)
        os.remove(path)
        self.quit_requests.append(request)
        refusal = None if request.get("Force") else self.quit_refusal
        with open(os.path.join(self.queue, ops.QUIT_RESULT), "w") as f:
            json.dump({"Quit": refusal is None, "Error": refusal}, f)
        if refusal is not None:
            return False
        os.remove(os.path.join(self.queue, ops.ALIVE))
        if self.on_quit:
            self.on_quit()
        return True


FAKE_UNITY = r'''#!/usr/bin/env python3
import base64, json, os, sys
argv = sys.argv[1:]
def val(k):
    return argv[argv.index(k) + 1] if k in argv else None
record = os.environ.get("FAKE_UNITY_RECORD")
if record:
    with open(record, "w") as f:
        json.dump(argv, f)
mode = os.environ.get("FAKE_UNITY_MODE", "ok")
log = val("-logFile")
setup = os.path.join(val("-projectPath"), "Library", "LastSceneManagerSetup.txt")
if os.path.isdir(os.path.dirname(setup)):
    with open(setup, "w") as f:
        f.write("sceneSetups: []\n")
if "-batchmode" not in argv:
    # Opened as an editor by --launch: come up with a heartbeat the way TaskQueue.cs does, then linger.
    import time
    if mode == "exit_early":
        sys.exit(4)
    queue = os.path.join(val("-projectPath"), "Library", "DotAsset", "Queue")
    os.makedirs(queue, exist_ok=True)
    with open(os.path.join(queue, ".alive"), "w") as f:
        json.dump({"Pid": int(os.environ.get("FAKE_EDITOR_PID", os.getpid())), "State": "ready", "ProjectPath": val("-projectPath")}, f)
    time.sleep(float(os.environ.get("FAKE_UNITY_LINGER", "2")))
    sys.exit(0)
if mode == "compile_error":
    with open(log, "w") as f:
        f.write("Assets/Foo.cs(3,1): error CS1002: ; expected\nScripts have compiler errors.\n")
    sys.exit(1)
if mode == "hang":
    import time
    time.sleep(30)
run = val("-dotassetRun")
assets = base64.b64decode(val("-dotassetAssetsB64")).decode() if val("-dotassetAssetsB64") else ""
extra = base64.b64decode(val("-dotassetArgsB64")).decode() if val("-dotassetArgsB64") else ""
code = int(os.environ.get("FAKE_UNITY_EXIT", "0"))
with open(log, "w") as f:
    f.write("fake unity log\n")
with open(val("-dotassetResult"), "w") as f:
    json.dump({"RunName": run, "ExitCode": code, "Summary": run + " fake", "Lines": ["I assets=" + assets, "I extra=" + extra, "W careful"], "Outputs": []}, f)
sys.exit(code)
'''


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.project = make_project(os.path.join(self.tmp, "Proj"))
        self._orig_is_unity = ops.is_unity_process
        ops.is_unity_process = lambda pid: pid == os.getpid() and getattr(self, "editor_alive", True)
        self.fake_unity = os.path.join(self.tmp, "Unity")
        if sys.platform == "win32":
            # Windows cannot run a shebang script, so a .cmd shim hands it to this interpreter
            with open(self.fake_unity + ".py", "w") as f:
                f.write(FAKE_UNITY)
            self.fake_unity += ".cmd"
            with open(self.fake_unity, "w") as f:
                f.write('@"%s" "%%~dp0Unity.py" %%*\r\n' % sys.executable)
        else:
            with open(self.fake_unity, "w") as f:
                f.write(FAKE_UNITY)
            os.chmod(self.fake_unity, os.stat(self.fake_unity).st_mode | stat.S_IEXEC)
        for k in ("FAKE_UNITY_MODE", "FAKE_UNITY_EXIT", "FAKE_UNITY_RECORD", "FAKE_EDITOR_PID", "FAKE_UNITY_LINGER", "DOTASSET_UNITY", "DOTASSET_PROJECT"):
            os.environ.pop(k, None)

    def tearDown(self):
        ops.is_unity_process = self._orig_is_unity
        shutil.rmtree(self.tmp, ignore_errors=True)

    def call(self, *argv):
        out = StringIO()
        with redirect_stdout(out):
            code = ops.main(["--project", self.project] + list(argv))
        return code, out.getvalue()


class AgentOutput(unittest.TestCase):
    def setUp(self):
        ops.AGENT = True

    def tearDown(self):
        ops.AGENT = False

    def capture(self, fn, *a):
        out = StringIO()
        with redirect_stdout(out):
            fn(*a)
        return out.getvalue()

    def test_success_is_short(self):
        out = self.capture(ops.print_agent, {"ExitCode": 0, "Summary": "1 asset exported", "Lines": ["I step one", "I step two"], "Outputs": []})
        self.assertEqual(out, "exit 0 1 asset exported\n")
        self.assertLess(len(out.split()), 25)

    def test_failure_keeps_errors_and_caps_lists(self):
        res = {"ExitCode": 1, "Summary": "0 of 1 done", "Error": "bad spec", "FailedTargets": ["Assets/A.prefab"],
               "Lines": ["W w%d" % i for i in range(12)], "Plan": [], "LogFile": "x.log"}
        lines = self.capture(ops.print_agent, res).splitlines()
        self.assertEqual(lines[0], "exit 1 0 of 1 done")
        self.assertEqual(lines[1], "error: bad spec")
        self.assertEqual(len([l for l in lines if l.startswith("warning: w")]), 10)
        self.assertIn("warning: 2 more, --json for all", lines)
        self.assertIn("failed: Assets/A.prefab", lines)
        self.assertEqual(lines[-1], "log: x.log")

    def test_plan_totals_come_first_and_count_past_the_list(self):
        res = {"ExitCode": 0, "Summary": "dry run", "DryRun": True, "PlanTruncated": True,
               "PlanTotals": {"Changes": 1000, "Files": 1, "Targets": 120},
               "Plan": [{"Target": "Assets/A.prefab", "Property": "m_Speed", "Before": 1, "After": 2}] * 12}
        lines = self.capture(ops.print_agent, res).splitlines()
        self.assertEqual(lines[1], "plan: 1000 changes, 1 file in 120 targets")
        self.assertEqual(len(lines), 2 + 10 + 1)
        self.assertIsNone(ops.plan_totals({"PlanTotals": {"Changes": 0, "Files": 0, "Targets": 0}}))

    def test_wrapper_progress_is_muted(self):
        self.assertEqual(self.capture(ops.log, "route: editor"), "")
        self.assertIn("error: x", self.capture(ops.log, "error: x"))


class GroupRule(unittest.TestCase):
    def test_python_matches_folder_layout(self):
        """Every command lives in the folder of the group its name resolves to, the C# rule in ExitCodes.cs."""
        seen = 0
        for group in ("Export", "Import", "Edit", "Migrate", "Audit"):
            folder = os.path.join(PACKAGE, "Editor", group)
            for name in os.listdir(folder):
                if not name.endswith("Command.cs"):
                    continue
                run = name[:-len("Command.cs")]
                self.assertEqual(ops.resolve_group(run), group, run)
                seen += 1
        self.assertGreaterEqual(seen, 25)

    def test_csharp_rule_text_matches_python(self):
        src = read(os.path.join(PACKAGE, "Editor", "Core", "ExitCodes.cs"))
        for needle in ('EndsWith("Export"', 'EndsWith("Import"', 'StartsWith("Create"', 'StartsWith("Edit"', 'StartsWith("Audit"'):
            self.assertIn(needle, src)

    def test_every_command_class_is_sealed_and_named(self):
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith("Command.cs") and name != "DotAssetCommand.cs":
                    text = read(os.path.join(dirpath, name))
                    cls = name[:-3]
                    self.assertRegex(text, r"public sealed class %s : DotAssetCommand" % cls, name)
                    self.assertIn("Docs/", text, name + " header must point at its doc")

    def test_queue_constants_match(self):
        src = read(os.path.join(PACKAGE, "Editor", "Core", "TaskQueue.cs"))
        self.assertIn('QueueRelative = "Library/DotAsset/Queue"', src)
        self.assertEqual(ops.QUEUE_RELATIVE.replace(os.sep, "/"), "Library/DotAsset/Queue")
        for const in ("pending", "processing", "done", ".alive", "quit.json", "quit.result.json", "openscene.json",
                      "openscene.result.json"):
            self.assertIn('"%s"' % const, src)


class EditorControl(Base):
    def test_launch_starts_the_editor_and_waits_for_ready(self):
        record = os.path.join(self.tmp, "argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        os.environ["FAKE_EDITOR_PID"] = str(os.getpid())
        code, out = self.call("--launch", "--unity", self.fake_unity, "--startup-wait", "30")
        self.assertEqual(code, 0, out)
        self.assertIn("the editor is ready", out)
        with open(record) as f:
            argv = json.load(f)
        self.assertNotIn("-batchmode", argv)
        self.assertIn("-projectPath", argv)

    def test_launch_reports_an_editor_that_exits_early(self):
        os.environ["FAKE_UNITY_MODE"] = "exit_early"
        code, out = self.call("--launch", "--unity", self.fake_unity, "--startup-wait", "30")
        self.assertEqual(code, 1)
        self.assertIn("exited", out)

    def test_launch_with_the_editor_already_open_starts_nothing(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]))
        editor.start()
        try:
            code, out = self.call("--launch", "--unity", os.path.join(self.tmp, "missing"))
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertIn("already has the project open", out)

    def test_quit_closes_the_editor(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), on_quit=lambda: setattr(self, "editor_alive", False))
        editor.start()
        try:
            code, out = self.call("--quit", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertIn("the editor closed", out)
        self.assertEqual(editor.quit_requests, [{"Force": False}])

    def test_quit_refused_with_unsaved_changes(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), quit_refusal="unsaved changes in Assets/Main.unity",
                            on_quit=lambda: setattr(self, "editor_alive", False))
        editor.start()
        try:
            code, out = self.call("--quit", "--timeout", "10")
            self.assertEqual(code, 2, out)
            self.assertIn("unsaved changes in Assets/Main.unity", out)
            code, out = self.call("--quit", "--force", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertEqual(editor.quit_requests[-1], {"Force": True})

    def test_quit_without_an_editor(self):
        code, out = self.call("--quit")
        self.assertEqual(code, 0)
        self.assertIn("no editor has this project open", out)

    def test_open_scene_reports_what_was_open(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]))
        editor.start()
        try:
            code, out = self.call("--open-scene", "Assets/A.unity,Assets/B.unity")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertEqual(editor.open_requests, [{"Scenes": ["Assets/A.unity", "Assets/B.unity"], "Force": False}])
        self.assertEqual(editor.scenes, ["Assets/A.unity", "Assets/B.unity"])
        self.assertEqual(out.strip().splitlines()[-1], "[dotasset] previously open: Assets/Scenes/Main.unity")

    def test_open_scene_refused_with_unsaved_changes(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), quit_refusal="unsaved changes in Assets/Scenes/Main.unity")
        editor.start()
        try:
            code, out = self.call("--open-scene", "Assets/A.unity")
            self.assertEqual(code, 2)
            self.assertIn("unsaved changes", out)
            self.assertEqual(editor.scenes, ["Assets/Scenes/Main.unity"])
            code, out = self.call("--open-scene", "Assets/A.unity", "--force")
            self.assertEqual(code, 0, out)
        finally:
            editor.stop.set()

    def test_open_scene_without_an_editor(self):
        code, out = self.call("--open-scene", "Assets/A.unity")
        self.assertEqual(code, 2)
        self.assertIn("--launch it first", out)

    def test_force_needs_quit(self):
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--force")
        self.assertEqual(code, 2)


class QueueRoute(Base):
    def test_round_trip(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"], lines=["I got " + ",".join(t["Assets"]) + " | " + t["ExtraArgs"], "W heads up", "E broken"]))
        editor.start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab,Assets/B.prefab", "--args", '-spec="C:/a b.json" -apply')
        finally:
            editor.stop.set()
        self.assertEqual(code, 0)
        self.assertIn("route: editor", out)
        self.assertIn('got Assets/A.prefab,Assets/B.prefab | -spec="C:/a b.json" -apply', out)
        self.assertIn("warning: heads up", out)
        self.assertIn("error: broken", out)
        self.assertEqual(editor.tasks[0]["Assets"], ["Assets/A.prefab", "Assets/B.prefab"])
        done = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.DONE)
        self.assertEqual(os.listdir(done), [], "the wrapper deletes the done file it read")

    def test_exit_code_passes_through(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"], code=3))
        editor.start()
        try:
            code, _ = self.call("AuditTexture")
        finally:
            editor.stop.set()
        self.assertEqual(code, 3)

    def test_missing_output_turns_success_into_failure(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"], outputs=[os.path.join(self.tmp, "nope.json")]))
        editor.start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab")
        finally:
            editor.stop.set()
        self.assertEqual(code, 1)
        self.assertIn("missing output", out)

    def test_timeout_withdraws_unclaimed_task(self):
        editor = FakeEditor(self.project, None, claim=False)
        editor.start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--timeout", "1")
        finally:
            editor.stop.set()
        self.assertEqual(code, 1)
        self.assertIn("withdrawn", out)
        pending = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.PENDING)
        self.assertEqual([f for f in os.listdir(pending) if f.endswith(".json")], [])

    def test_interrupt_withdraws_unclaimed_task(self):
        editor = FakeEditor(self.project, None, claim=False)
        editor.start()
        real = ops.read_alive
        calls = [0]

        def interrupting(root):
            calls[0] += 1
            if calls[0] >= 2:
                raise KeyboardInterrupt()
            return real(root)
        ops.read_alive = interrupting
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab")
        finally:
            ops.read_alive = real
            editor.stop.set()
        self.assertEqual(code, 1)
        self.assertIn("withdrawn", out)
        pending = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.PENDING)
        self.assertEqual([f for f in os.listdir(pending) if f.endswith(".json")], [])

    def test_paused_queue_refuses(self):
        editor = FakeEditor(self.project, None, state="paused")
        editor.start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab")
        finally:
            editor.stop.set()
        self.assertEqual(code, 2)
        self.assertIn("paused", out)

    def test_editor_exit_before_claim_falls_back_to_batch(self):
        editor = FakeEditor(self.project, None, claim=False)
        editor.start()

        def die():
            time.sleep(0.5)
            self.editor_alive = False
        threading.Thread(target=die, daemon=True).start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity)
        finally:
            editor.stop.set()
        self.assertEqual(code, 0)
        self.assertIn("running headless instead", out)
        self.assertIn("route: batchmode", out)

    def test_heartbeat_of_another_project_is_ignored(self):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        os.makedirs(queue, exist_ok=True)
        with open(os.path.join(queue, ops.ALIVE), "w") as f:
            json.dump({"Pid": os.getpid(), "State": "ready", "ProjectPath": os.path.join(self.tmp, "OtherProject")}, f)
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("route: batchmode", out)

    def test_heartbeat_of_this_project_is_used(self):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        os.makedirs(queue, exist_ok=True)
        with open(os.path.join(queue, ops.ALIVE), "w") as f:
            json.dump({"Pid": os.getpid(), "State": "ready", "ProjectPath": self.project.replace(os.sep, "/")}, f)
        self.assertIsNotNone(ops.live_editor(queue))

    def test_long_dead_idle_heartbeat_is_ignored(self):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        os.makedirs(queue, exist_ok=True)
        alive = os.path.join(queue, ops.ALIVE)
        with open(alive, "w") as f:
            json.dump({"Pid": os.getpid(), "State": "ready"}, f)
        old = time.time() - 600
        os.utime(alive, (old, old))
        self.assertIsNone(ops.live_editor(queue))
        with open(alive, "w") as f:
            json.dump({"Pid": os.getpid(), "State": "busy"}, f)
        os.utime(alive, (old, old))
        self.assertIsNotNone(ops.live_editor(queue), "a long run keeps the heartbeat old and busy")

    def test_process_names(self):
        self.assertIn("unity.exe", ops.UNITY_NAMES)
        self.assertNotIn("unity hub.exe", ops.UNITY_NAMES)

    def test_stale_heartbeat_pid_is_ignored(self):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        os.makedirs(queue, exist_ok=True)
        with open(os.path.join(queue, ops.ALIVE), "w") as f:
            json.dump({"Pid": 999999, "State": "ready"}, f)
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("route: batchmode", out)


class BatchRoute(Base):
    def test_arguments_survive_quoting(self):
        record = os.path.join(self.tmp, "argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        extra = '-spec="C:/My Specs/edit spec.json" -apply -note="a \\"quoted\\" word"'
        code, out = self.call("EditPrefab", "--args", extra, "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("extra=" + extra, out)
        argv = json.loads(read(record))
        self.assertIn("-batchmode", argv)
        self.assertIn("-nographics", argv)
        self.assertEqual(argv[argv.index("-executeMethod") + 1], "DotAsset.Cli.Run")
        self.assertEqual(argv[argv.index("-projectPath") + 1], self.project)
        self.assertEqual(base64.b64decode(argv[argv.index("-dotassetArgsB64") + 1]).decode(), extra)
        self.assertNotIn("-dotassetAssetsB64", argv, "no AssetList, no flag")

    def test_assets_are_normalized(self):
        code, out = self.call("PrefabExport", r"Assets\Prefabs\A.prefab, Assets/B.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("assets=Assets/Prefabs/A.prefab,Assets/B.prefab", out)

    def test_exit_code_passes_through(self):
        os.environ["FAKE_UNITY_EXIT"] = "3"
        code, _ = self.call("AuditMaterial", "--unity", self.fake_unity)
        self.assertEqual(code, 3)

    def test_scene_setup_survives_batch_run(self):
        # Batch Unity empties the scenes the editor reopens, the wrapper puts them back
        setup = os.path.join(self.project, "Library", "LastSceneManagerSetup.txt")
        os.makedirs(os.path.dirname(setup), exist_ok=True)
        saved = "sceneSetups:\n- path: Assets/Scenes/Main.unity\n  isLoaded: 1\n"
        with open(setup, "w") as f:
            f.write(saved)
        for mode in ("ok", "compile_error"):
            os.environ["FAKE_UNITY_MODE"] = mode
            self.call("AuditMaterial", "--unity", self.fake_unity)
            with open(setup) as f:
                self.assertEqual(f.read(), saved, mode)

    def test_compile_errors_are_explained(self):
        os.environ["FAKE_UNITY_MODE"] = "compile_error"
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 1)
        self.assertIn("error CS1002", out)
        self.assertIn("does not compile", out)

    def test_timeout_kills_unity(self):
        os.environ["FAKE_UNITY_MODE"] = "hang"
        started = time.time()
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity, "--timeout", "2")
        self.assertEqual(code, 1)
        self.assertLess(time.time() - started, 15)
        self.assertIn("did not finish", out)

    def test_no_unity_found(self):
        code, out = self.call("PrefabExport", "Assets/A.prefab")
        self.assertEqual(code, 1)
        self.assertIn("no Unity 6000.0.40f1 found", out)

    def test_hub_location_from_project_version(self):
        if sys.platform != "linux":
            self.skipTest("linux layout")
        home = os.path.join(self.tmp, "home")
        exe = os.path.join(home, "Unity", "Hub", "Editor", "6000.0.40f1", "Editor", "Unity")
        os.makedirs(os.path.dirname(exe))
        shutil.copy(self.fake_unity, exe)
        old = os.environ.get("HOME")
        os.environ["HOME"] = home
        try:
            self.assertEqual(ops.find_unity(None, self.project), exe)
        finally:
            os.environ["HOME"] = old

    def test_args_value_starting_with_dash(self):
        code, out = self.call("EditPrefab", "--args", "-apply", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("extra=-apply", out)

    def test_assets_flag_in_args_is_refused(self):
        code, out = self.call("PrefabExport", "--args", "-assets=Assets/A.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 2)


class HeadlessLock(Base):
    def queue_root(self):
        return os.path.join(self.project, ops.QUEUE_RELATIVE)

    def test_lock_is_exclusive(self):
        first, second = ops.BatchLock(self.queue_root()), ops.BatchLock(self.queue_root())
        self.assertTrue(first.try_acquire())
        self.assertTrue(ops.batch_run_active(self.queue_root()))
        self.assertFalse(second.try_acquire())
        first.release()
        self.assertFalse(ops.batch_run_active(self.queue_root()))
        self.assertTrue(second.try_acquire())
        second.release()

    def test_headless_run_waits_its_turn(self):
        held = ops.BatchLock(self.queue_root())
        self.assertTrue(held.try_acquire())
        threading.Timer(1.5, held.release).start()
        started = time.time()
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 0, out)
        self.assertIn("another DotAsset headless run has the project", out)
        self.assertGreaterEqual(time.time() - started, 1.4)

    def test_headless_run_gives_up_after_timeout(self):
        held = ops.BatchLock(self.queue_root())
        self.assertTrue(held.try_acquire())
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity, "--timeout", "1")
        finally:
            held.release()
        self.assertEqual(code, 2, out)
        self.assertIn("did not finish within 1s", out)

    def test_parallel_runs_all_succeed(self):
        codes = []

        def one():
            r = subprocess.run([sys.executable, os.path.join(PACKAGE, "Scripts~", "dotasset.py"), "--project", self.project,
                                "--unity", self.fake_unity, "PrefabExport", "Assets/A.prefab"], capture_output=True, text=True)
            codes.append(r.returncode)

        threads = [threading.Thread(target=one) for _ in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(codes, [0, 0, 0, 0])


class LogSweep(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def touch(self, name, age_days=0):
        path = os.path.join(self.dir, name)
        with open(path, "w") as f:
            f.write("log")
        stamp = time.time() - age_days * 86400
        os.utime(path, (stamp, stamp))
        return path

    def test_listed_logs_are_swept_and_the_list_goes(self):
        self.touch("a.unity.log")
        with open(os.path.join(self.dir, ops.LOG_SWEEP_LIST), "w") as f:
            f.write("a.unity.log\ngone.unity.log\n")
        ops.sweep_logs(self.dir)
        self.assertEqual(os.listdir(self.dir), [])

    def test_failed_logs_kept_for_a_week(self):
        self.touch("recent.unity.log", age_days=1)
        self.touch("old.unity.log", age_days=ops.FAILED_LOG_DAYS + 1)
        self.touch("other.json", age_days=30)
        ops.sweep_logs(self.dir)
        self.assertEqual(sorted(os.listdir(self.dir)), ["other.json", "recent.unity.log"])

    def test_removed_log_is_not_listed(self):
        path = self.touch("ok.unity.log")
        ops.remove_log(self.dir, path)
        ops.remove_log(self.dir, path)
        self.assertEqual(os.listdir(self.dir), [])

    @unittest.skipIf(sys.platform != "win32", "only Windows refuses to delete an open file")
    def test_locked_log_is_listed_then_swept(self):
        path = self.touch("held.unity.log")
        held = open(path)
        ops.remove_log(self.dir, path)
        ops.sweep_logs(self.dir)
        self.assertIn("held.unity.log", os.listdir(self.dir))
        held.close()
        ops.sweep_logs(self.dir)
        self.assertEqual(os.listdir(self.dir), [])


# Holds Temp/UnityLockfile the way Unity does: flock on macOS and Linux, an open handle that shares nothing on Windows.
HOLD_LOCK_POSIX = "import fcntl,sys,time;f=open(sys.argv[1],'w');fcntl.flock(f,fcntl.LOCK_EX);time.sleep(30)"
HOLD_LOCK_WIN = ("import ctypes,sys,time;k=ctypes.windll.kernel32;k.CreateFileW.restype=ctypes.c_void_p;"
                 "h=k.CreateFileW(sys.argv[1],0x40000000,0,None,4,0x80,None);"
                 "sys.exit(3) if h in (None,ctypes.c_void_p(-1).value) else time.sleep(30)")


def hold_unity_lock(path):
    holder = subprocess.Popen([sys.executable, "-c", HOLD_LOCK_WIN if sys.platform == "win32" else HOLD_LOCK_POSIX, path])
    deadline = time.time() + 10
    while not ops.project_locked(os.path.dirname(os.path.dirname(path))) and time.time() < deadline:
        if holder.poll() is not None:
            raise RuntimeError("the lock holder exited with %s" % holder.returncode)
        time.sleep(0.1)
    return holder


class StartupWait(Base):
    def test_locked_project_waits_for_heartbeat(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = hold_unity_lock(lock)
        try:
            time.sleep(0.5)
            self.assertTrue(ops.project_locked(self.project))
            started = {}

            def late_editor():
                time.sleep(1.5)
                started["editor"] = FakeEditor(self.project, lambda t: result(t["RunName"]))
                started["editor"].start()
            threading.Thread(target=late_editor, daemon=True).start()
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--startup-wait", "20")
            self.assertEqual(code, 0)
            self.assertIn("waiting up to", out)
            self.assertIn("route: editor", out)
            started["editor"].stop.set()
        finally:
            holder.kill()
            holder.wait()
        self.assertFalse(ops.project_locked(self.project))

    def test_locked_project_without_queue_refuses(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = hold_unity_lock(lock)
        try:
            time.sleep(0.5)
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--startup-wait", "1")
        finally:
            holder.kill()
            holder.wait()
        self.assertEqual(code, 2)
        self.assertIn("never came up", out)

    def test_unlocked_leftover_lockfile_is_not_an_editor(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        self.assertFalse(ops.project_locked(self.project))


class Discovery(Base):
    def test_find_project_walks_up(self):
        deep = os.path.join(self.project, "Assets", "Art", "Textures")
        os.makedirs(deep)
        self.assertEqual(ops.find_project(deep), self.project)

    def test_version(self):
        self.assertEqual(ops.project_unity_version(self.project), "6000.0.40f1")

    def test_real_process_check(self):
        # The test runner is alive but is not Unity.
        self.assertTrue(ops.pid_alive(os.getpid()))
        self.assertFalse(self._orig_is_unity(os.getpid()))
        self.assertFalse(ops.pid_alive(999999))



class PackageFiles(unittest.TestCase):
    def test_every_package_file_has_a_meta(self):
        """A git-installed package is immutable: Unity ignores files without a .meta."""
        r = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "gen_meta.py"), "--check"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_versions_agree(self):
        pkg = json.loads(read(os.path.join(PACKAGE, "package.json")))
        cs = read(os.path.join(PACKAGE, "Editor", "Core", "DotAssetVersion.cs"))
        self.assertIn('Version = "%s"' % pkg["version"], cs)
        self.assertIn("### " + pkg["version"], read(os.path.join(ROOT, "Version.md")))

    def test_no_char_contains(self):
        """string.Contains(char) is missing from .NET Framework 4.8."""
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs"):
                    text = read(os.path.join(dirpath, name))
                    self.assertIsNone(re.search(r"\.Contains\('.'\)", text), name)

    def test_docs_mention_every_run(self):
        docs = "".join(read(os.path.join(ROOT, "Docs", f)) for f in os.listdir(os.path.join(ROOT, "Docs")))
        guide = read(os.path.join(ROOT, "Docs", "AI-Guide.md"))
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith("Command.cs") and name != "DotAssetCommand.cs":
                    run = name[:-len("Command.cs")]
                    self.assertIn("`%s`" % run, docs, run)
                    self.assertIn(run, guide, run + " missing from the AI-Guide decision table")

    def test_doc_json_examples_parse(self):
        for name in os.listdir(os.path.join(ROOT, "Docs")):
            text = read(os.path.join(ROOT, "Docs", name))
            for m in re.finditer(r"```json\n(.*?)```", text, re.S):
                body = m.group(1)
                try:
                    json.loads(body)
                except ValueError:
                    json.loads("{" + body + "}")  # a fragment such as "ArrayOps": [...]


if __name__ == "__main__":
    unittest.main()
