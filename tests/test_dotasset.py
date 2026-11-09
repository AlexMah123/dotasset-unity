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
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from unittest import mock

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


def without_id(request, path):
    """The request as the fake editor records it, after checking its Id is the one in the file name."""
    request = dict(request)
    if os.path.basename(path).split(".")[1] != request.pop("Id", None):
        request["IdMismatch"] = True
    return request


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
                    response = self.respond(task)
                    with open(os.path.join(self.queue, ops.DONE, name), "w") as f:
                        json.dump(response, f)
                    os.remove(dst)
                self.serve_open_scene()
                if self.serve_quit():
                    return
            time.sleep(0.05)

    def request(self, kind):
        """The first <kind>.<id>.json request waiting, or None."""
        for name in sorted(os.listdir(self.queue)):
            if name.startswith(kind + ".") and name.endswith(ops.REQUEST_SUFFIX) and not name.endswith(ops.RESULT_SUFFIX):
                return os.path.join(self.queue, name)
        return None

    def serve_open_scene(self):
        """Answers openscene.<id>.json like TaskQueue.HandleOpenSceneRequest, refusing like quit does."""
        path = self.request(ops.OPEN_SCENE_REQUEST)
        if path is None:
            return
        with open(path) as f:
            request = json.load(f)
        os.remove(path)
        self.open_requests.append(without_id(request, path))
        refusal = None if request.get("Force") else self.quit_refusal
        previous = list(self.scenes)
        if refusal is None:
            self.scenes = list(request["Scenes"])
        with open(path[:-len(ops.REQUEST_SUFFIX)] + ops.RESULT_SUFFIX, "w") as f:
            json.dump({"Opened": refusal is None, "Previous": previous, "PreviousActive": previous[0] if previous else None,
                       "Error": refusal}, f)

    def serve_quit(self):
        """Answers quit.<id>.json like TaskQueue.HandleQuitRequest. True when the fake editor closed."""
        path = self.request(ops.QUIT_REQUEST)
        if path is None:
            return False
        with open(path) as f:
            request = json.load(f)
        os.remove(path)
        self.quit_requests.append(without_id(request, path))
        refusal = None if request.get("Force") else self.quit_refusal
        with open(path[:-len(ops.REQUEST_SUFFIX)] + ops.RESULT_SUFFIX, "w") as f:
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
seen = os.environ.get("FAKE_UNITY_RUNNING")
if seen and val("-dotassetResult"):
    running = os.path.join(os.path.dirname(val("-dotassetResult")), "running.json")
    with open(seen, "w") as f:
        f.write(open(running).read() if os.path.exists(running) else "")
setup = os.path.join(val("-projectPath"), "Library", "LastSceneManagerSetup.txt")
setup_seen = os.environ.get("FAKE_UNITY_SETUP_SEEN")
if setup_seen:
    with open(setup_seen, "w") as f:
        f.write(open(setup).read() if os.path.exists(setup) else "")
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
    open(os.path.join(queue, ".fake_started"), "w").close()
    with open(os.path.join(queue, ".alive"), "w") as f:
        json.dump({"Pid": int(os.environ.get("FAKE_EDITOR_PID", os.getpid())), "State": "ready", "ProjectPath": val("-projectPath")}, f)
    time.sleep(float(os.environ.get("FAKE_UNITY_LINGER", "2")))
    open(os.path.join(queue, ".fake_exited"), "w").close()
    sys.exit(0)
if mode == "compile_error":
    with open(log, "w") as f:
        f.write("Assets/Foo.cs(3,1): error CS1002: ; expected\nScripts have compiler errors.\n")
    sys.exit(1)
if mode == "crash":
    with open(log, "w") as f:
        f.write("Crash!!!\n")
    sys.exit(1)
if mode == "error_after_start":
    with open(log, "w") as f:
        f.write("[DotAsset] run started: " + val("-dotassetRun") + "\nAssets/Foo.cs(3,1): error CS0246: missing type\n")
    sys.exit(1)
if mode == "error_cleared_before_start":
    with open(log, "w") as f:
        f.write("Assets/Foo.cs(3,1): error CS0246: missing type\n[DotAsset] run started: " + val("-dotassetRun") + "\nCrash!!!\n")
    sys.exit(1)
if mode == "hang":
    import time
    time.sleep(30)
if mode == "hang_after_start":
    import time
    with open(log, "w") as f:
        f.write("[DotAsset] run started: " + val("-dotassetRun") + "\n")
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
        for k in ("FAKE_UNITY_MODE", "FAKE_UNITY_EXIT", "FAKE_UNITY_RECORD", "FAKE_EDITOR_PID", "FAKE_UNITY_LINGER", "FAKE_UNITY_RUNNING", "FAKE_UNITY_SETUP_SEEN", "DOTASSET_UNITY", "DOTASSET_PROJECT", "DOTASSET_CALLER"):
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

    @staticmethod
    def rule_steps(body, test, result):
        # The rule as (endswith|startswith, text, group) in the order the checks run, read from an if/return chain
        steps = []
        for cond, group in re.findall(test, body, re.S):
            for kind, text in re.findall(r'(?i)(endswith|startswith)\("(\w+)"', cond):
                steps.append((kind.lower(), text, re.search(result, group).group(1)))
        return steps

    def test_csharp_rule_matches_python_in_order(self):
        # F5: the same checks in the same order, not only the same strings somewhere in the file
        cs = read(os.path.join(PACKAGE, "Editor", "Core", "ExitCodes.cs"))
        cs = cs[cs.index("public static CommandGroup Resolve"):]
        cs = cs[:cs.index("return CommandGroup.Migrate;", cs.index("With"))]
        py = read(os.path.join(PACKAGE, "Scripts~", "dotasset.py"))
        py = py[py.index("def resolve_group"):]
        py = py[:py.index('return "Migrate"', py.index("startswith"))]
        cs_steps = self.rule_steps(cs, r"if \((run[^\n]*)\)\s*\{\s*(return CommandGroup\.\w+;)", r"CommandGroup\.(\w+)")
        py_steps = self.rule_steps(py, r"if (run[^\n]*):\s*(return \"\w+\")", r"\"(\w+)\"")
        self.assertEqual(cs_steps, py_steps)
        self.assertEqual(len(cs_steps), 5, cs_steps)
        self.assertIn("StringComparison.Ordinal", cs, "case-sensitive, as Python's str methods are")

    def test_sample_names_resolve_by_the_rule(self):
        samples = {"EditExport": "Export", "CreateX": "Import", "AuditImport": "Import", "EditorTidy": "Edit",
                   "AuditMaterial": "Audit", "Foo": "Migrate", "Createx": "Import", "Exportable": "Migrate"}
        for name, group in samples.items():
            self.assertEqual(ops.resolve_group(name), group, name)

    def test_could_write_reads_the_group_and_apply(self):
        # M2: only these may have written when the wrapper loses sight of the run
        writes = [("CreateAsset", ""), ("createasset", ""), ("EditMaterial", "-apply"), ("editmaterial", "-APPLY=yes"),
                  ("MoveAsset", "-from=a -apply=false -apply"), ("EditMaterial", '-apply="true"')]
        safe = [("MaterialExport", "-apply"), ("AuditMaterial", "-apply"), ("EditMaterial", ""),
                ("EditMaterial", "-apply=false"), ("MoveAsset", "-apply -apply=0"), ("EditMaterial", "-applyall")]
        for run, extra in writes:
            self.assertTrue(ops.could_write(run, extra), "%s %s" % (run, extra))
        for run, extra in safe:
            self.assertFalse(ops.could_write(run, extra), "%s %s" % (run, extra))

    def test_could_write_tokenizes_like_runargs(self):
        # T1: RunArgs strips the quotes and every leading dash, so all of these turn -apply on or leave it off
        on = ["--apply", "---apply", '"-apply"', "-spec=x.json --apply", '-spec="a b.json" -APPLY', '"--apply=yes"']
        off = ["--apply=false", '-spec="a -apply b"', '-spec="-apply"', '"-apply" "-apply=no"']
        for extra in on:
            self.assertTrue(ops.could_write("MoveAsset", extra), extra)
        for extra in off:
            self.assertFalse(ops.could_write("MoveAsset", extra), extra)

    def test_every_command_class_is_sealed_and_named(self):
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith("Command.cs") and name != "DotAssetCommand.cs":
                    text = read(os.path.join(dirpath, name))
                    cls = name[:-3]
                    self.assertRegex(text, r"public sealed class %s : DotAssetCommand" % cls, name)
                    group = os.path.basename(dirpath)
                    self.assertRegex(text, r"(?m)^\s*// Docs/%s\.md$" % group, name + " header must point at Docs/%s.md" % group)

    def test_queue_constants_match(self):
        src = read(os.path.join(PACKAGE, "Editor", "Core", "TaskQueue.cs"))
        self.assertIn('QueueRelative = "Library/DotAsset/Queue"', src)
        self.assertEqual(ops.QUEUE_RELATIVE.replace(os.sep, "/"), "Library/DotAsset/Queue")
        for const in ("pending", "processing", "done", ".alive", "quit", "openscene", ".json", ".result.json"):
            self.assertIn('"%s"' % const, src)


class EditorControl(Base):
    def tearDown(self):
        # A launched fake editor lingers, wait for it before deleting the files it holds.
        queue = os.path.join(self.project, "Library", "DotAsset", "Queue")
        deadline = time.time() + 10
        while os.path.exists(os.path.join(queue, ".fake_started")) and not os.path.exists(os.path.join(queue, ".fake_exited")) \
                and time.time() < deadline:
            time.sleep(0.1)
        Base.tearDown(self)

    def test_launch_starts_the_editor_and_waits_for_ready(self):
        record = os.path.join(self.tmp, "argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        os.environ["FAKE_EDITOR_PID"] = str(os.getpid())
        # The wrapper leaves the editor running on purpose. The test waits for the fake one to exit, not leaving it behind.
        started = []
        start = ops.start_detached
        with mock.patch.object(ops, "start_detached", lambda *a: started.append(start(*a)) or started[-1]):
            code, out = self.call("--launch", "--unity", self.fake_unity, "--startup-wait", "30")
        for proc in started:
            proc.wait(30)
        self.assertEqual(code, 0, out)
        self.assertIn("the editor is ready", out)
        with open(record) as f:
            argv = json.load(f)
        self.assertNotIn("-batchmode", argv)
        self.assertIn("-projectPath", argv)
        # --quit closes it for this caller only.
        with open(os.path.join(self.project, ops.QUEUE_RELATIVE, "launched.json")) as f:
            self.assertEqual(json.load(f), {"Pid": os.getpid(), "Caller": None, "Served": False})

    def test_launch_starts_minimized_unless_visible(self):
        os.environ["FAKE_EDITOR_PID"] = str(os.getpid())
        seen = []
        start = ops.start_detached

        def recording(cmd, visible=True, hidden=False):
            seen.append(visible)
            return start(cmd, visible, hidden)
        for flags in ((), ("--visible",)):
            with mock.patch.object(ops, "start_detached", recording):
                code, out = self.call("--launch", "--unity", self.fake_unity, "--startup-wait", "30", *flags)
            self.assertEqual(code, 0, out)
            queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
            deadline = time.time() + 10
            while not os.path.exists(os.path.join(queue, ".fake_exited")) and time.time() < deadline:
                time.sleep(0.1)
            for name in (".fake_started", ".fake_exited", ops.ALIVE, "launched.json"):
                if os.path.exists(os.path.join(queue, name)):
                    os.remove(os.path.join(queue, name))
        self.assertEqual(seen, [False, True])

    def test_launch_after_an_ended_orphan_opens_the_saved_scenes(self):
        # The v0.11.0 matrix failure: a killed wrapper's Unity emptied the scene setup on exit, --launch opened Untitled.
        saved = "sceneSetups:\n- path: Assets/Scenes/Main.unity\n  isLoaded: 1\n"
        orphan_with_emptied_setup(self.project, saved)
        seen = os.path.join(self.tmp, "setup_seen.txt")
        os.environ["FAKE_UNITY_SETUP_SEEN"] = seen
        os.environ["FAKE_EDITOR_PID"] = str(os.getpid())
        started = []
        start = ops.start_detached
        with mock.patch.object(ops, "start_detached", lambda *a: started.append(start(*a)) or started[-1]):
            code, out = self.call("--launch", "--unity", self.fake_unity, "--startup-wait", "30")
        for proc in started:
            proc.wait(30)
        self.assertEqual(code, 0, out)
        with open(seen) as f:
            self.assertEqual(f.read(), saved)

    @unittest.skipIf(sys.platform != "win32", "desktops are Windows only")
    def test_serve_host_starts_on_its_own_desktop(self):
        # The child writes the name of the desktop it runs on, a window there cannot take the user's focus.
        out = os.path.join(self.tmp, "desktop.txt")
        child = ("import ctypes, sys; u = ctypes.windll.user32; u.GetThreadDesktop.restype = ctypes.c_void_p; "
                 "h = u.GetThreadDesktop(ctypes.windll.kernel32.GetCurrentThreadId()); b = ctypes.create_unicode_buffer(256); "
                 "u.GetUserObjectInformationW(ctypes.c_void_p(h), 2, b, ctypes.sizeof(b), None); "
                 "open(sys.argv[1], 'w').write(b.value); sys.exit(7)")
        proc = ops.start_detached([sys.executable, "-c", child, out], False, True)
        deadline = time.time() + 30
        while proc.poll() is None and time.time() < deadline:
            time.sleep(0.1)
        self.assertEqual(proc.returncode, 7)
        with open(out) as f:
            self.assertEqual(f.read(), "DotAssetHost")

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
        self.assertIn("--quit leaves it open", out)
        self.assertFalse(os.path.exists(os.path.join(self.project, ops.QUEUE_RELATIVE, "launched.json")))

    def launched_by(self, caller, pid=None):
        ops.record_launch(os.path.join(self.project, ops.QUEUE_RELATIVE), pid or os.getpid(), caller, False)

    def test_quit_closes_the_editor(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), on_quit=lambda: setattr(self, "editor_alive", False))
        self.launched_by(None)
        editor.start()
        try:
            code, out = self.call("--quit", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertIn("the editor closed", out)
        self.assertEqual(editor.quit_requests, [{"Force": False}])
        self.assertFalse(os.path.exists(os.path.join(self.project, ops.QUEUE_RELATIVE, "launched.json")))

    def test_quit_leaves_an_editor_it_did_not_start(self):
        # Dec 4: an editor the user opened, or one an earlier Unity's record no longer matches, is not closed.
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), on_quit=lambda: setattr(self, "editor_alive", False))
        editor.start()
        try:
            for record in (False, True):
                if record:
                    self.launched_by(None, pid=os.getpid() + 1)
                code, out = self.call("--quit", "--timeout", "10")
                self.assertEqual(code, 2, out)
                self.assertIn("(pid %d) was not started by --launch or --serve" % os.getpid(), out)
                self.assertEqual(editor.quit_requests, [])
            code, out = self.call("--quit", "--not-mine", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertEqual(editor.quit_requests, [{"Force": False}])

    def test_quit_leaves_another_callers_editor(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), on_quit=lambda: setattr(self, "editor_alive", False))
        self.launched_by("agent-a")
        editor.start()
        try:
            code, out = self.call("--quit", "--caller", "agent-b", "--timeout", "10")
            self.assertEqual(code, 2, out)
            self.assertIn("was started by caller agent-a, not by caller agent-b", out)
            code, out = self.call("--quit", "--timeout", "10")
            self.assertEqual(code, 2, out)
            self.assertIn("not by this caller (no --caller)", out)
            self.assertEqual(editor.quit_requests, [])
            os.environ["DOTASSET_CALLER"] = "agent-a"
            code, out = self.call("--quit", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertEqual(editor.quit_requests, [{"Force": False}])

    def test_quit_refused_with_unsaved_changes(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), quit_refusal="unsaved changes in Assets/Main.unity",
                            on_quit=lambda: setattr(self, "editor_alive", False))
        self.launched_by(None)
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

    def test_open_scene_takes_only_its_own_answer(self):
        # Another caller's answer waiting in the queue is neither read nor removed.
        os.makedirs(os.path.join(self.project, ops.QUEUE_RELATIVE), exist_ok=True)
        foreign = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.OPEN_SCENE_REQUEST + ".other" + ops.RESULT_SUFFIX)
        with open(foreign, "w") as f:
            json.dump({"Opened": False, "Previous": [], "Error": "the other caller's answer"}, f)
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]))
        editor.start()
        try:
            code, out = self.call("--open-scene", "Assets/A.unity")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertTrue(os.path.exists(foreign))

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

    def requests_left(self, kind):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        return [n for n in os.listdir(queue) if n.startswith(kind + ".")]

    def exit_on_request(self, kind):
        """The fake editor exits without an answer once a <kind> request is waiting."""
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)

        def watch():
            deadline = time.time() + 10
            while time.time() < deadline:
                if any(n.endswith(ops.REQUEST_SUFFIX) for n in os.listdir(queue) if n.startswith(kind + ".")):
                    self.editor_alive = False
                    return
                time.sleep(0.02)
        threading.Thread(target=watch, daemon=True).start()

    def test_unanswered_requests_are_withdrawn(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), claim=False)
        self.launched_by(None)
        editor.start()
        try:
            code, out = self.call("--open-scene", "Assets/A.unity", "--timeout", "1")
            self.assertEqual(code, 1, out)
            self.assertIn("the editor did not answer within 1s. Request withdrawn", out)
            self.assertEqual(self.requests_left(ops.OPEN_SCENE_REQUEST), [])
            code, out = self.call("--quit", "--timeout", "1")
            self.assertEqual(code, 1, out)
            self.assertIn("the editor did not answer within 1s (state: ready). Request withdrawn", out)
            self.assertEqual(self.requests_left(ops.QUIT_REQUEST), [])
        finally:
            editor.stop.set()

    def test_open_scene_fails_when_the_editor_exits_without_answering(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), claim=False)
        editor.start()
        try:
            self.exit_on_request(ops.OPEN_SCENE_REQUEST)
            code, out = self.call("--open-scene", "Assets/A.unity", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 1, out)
        self.assertEqual(self.requests_left(ops.OPEN_SCENE_REQUEST), [])
        # R6-1: the editor exited, no 10 s passed. Say so, as --quit does.
        self.assertIn("the editor exited without answering. Request withdrawn", out)
        self.assertNotIn("did not answer within", out)

    def test_quit_counts_an_editor_that_exits_without_answering_as_closed(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), claim=False)
        self.launched_by(None)
        editor.start()
        try:
            self.exit_on_request(ops.QUIT_REQUEST)
            code, out = self.call("--quit", "--timeout", "10")
        finally:
            editor.stop.set()
        self.assertEqual(code, 0, out)
        self.assertIn("the editor closed, the project is free", out)
        self.assertFalse(os.path.exists(os.path.join(self.project, ops.QUEUE_RELATIVE, "launched.json")))

    def test_force_needs_quit(self):
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--force")
        self.assertEqual(code, 2)

    def test_not_mine_needs_quit(self):
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--not-mine")
        self.assertEqual(code, 2)
        self.assertIn("--not-mine only goes with --quit", out)


class QueueRoute(Base):
    def test_caller_reaches_the_task(self):
        # --caller, or DOTASSET_CALLER when it is not given, goes into the task for the run's journal (B5).
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]))
        editor.start()
        os.environ["DOTASSET_CALLER"] = "agent-a"
        try:
            self.assertEqual(self.call("MaterialExport", "Assets/A.mat")[0], 0)
            self.assertEqual(self.call("MaterialExport", "Assets/A.mat", "--caller", "agent-b")[0], 0)
            del os.environ["DOTASSET_CALLER"]
            self.assertEqual(self.call("MaterialExport", "Assets/A.mat")[0], 0)
        finally:
            editor.stop.set()
        self.assertEqual([t.get("Caller") for t in editor.tasks], ["agent-a", "agent-b", None])

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

    def test_timeout_after_claim_of_a_writing_run_is_unknown(self):
        gate = threading.Event()
        editor = FakeEditor(self.project, lambda t: (gate.wait(10), result(t["RunName"]))[1])
        editor.start()
        try:
            code, out = self.call("EditMaterial", "--timeout", "1", "--json", "--args", "-apply")
            self.assertEqual(code, 4, "the editor took the task, it may have written")
            self.assertIn("outcome: unknown, task ", out)
        finally:
            gate.set()
            editor.stop.set()

    def test_timeout_after_claim_names_the_task_and_result_reads_it_later(self):
        gate = threading.Event()
        editor = FakeEditor(self.project, lambda t: (gate.wait(10), result(t["RunName"], code=3))[1])
        editor.start()
        try:
            code, out = self.call("AuditTexture", "--timeout", "1", "--json")
            self.assertEqual(code, 1, "an Audit run cannot write, so a timeout is a failure, not an unknown")
            self.assertNotIn('"Outcome": "unknown"', out)
            task = re.search(r'"Task": "([0-9a-f]{32})"', out).group(1)
            self.assertIn(task, out.split("{")[0], "the task is named in the log line too")
            code, out = self.call("--result", task)
            self.assertEqual(code, 4)
            self.assertIn("still running", out)
            gate.set()
            done = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.DONE, task + ".json")
            deadline = time.time() + 5
            while not os.path.exists(done) and time.time() < deadline:
                time.sleep(0.05)
            time.sleep(0.1)
            code, out = self.call("--result", task)
            self.assertEqual(code, 3, "the run's own exit code")
            self.assertIn("AuditTexture", out)
            code, out = self.call("--result", task)
            self.assertEqual(code, 2, "a result is read once")
        finally:
            gate.set()
            editor.stop.set()

    def test_result_with_unknown_outcome_is_4(self):
        done = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.DONE)
        os.makedirs(done)
        unknown, old = "a" * 32, "b" * 32
        with open(os.path.join(done, unknown + ".json"), "w") as f:
            json.dump(dict(result("EditMaterial", code=1, error="the editor stopped"), Outcome="unknown"), f)
        with open(os.path.join(done, old + ".json"), "w") as f:
            json.dump(result("EditMaterial", code=1, error="the editor stopped"), f)
        code, out = self.call("--result", unknown)
        self.assertEqual(code, 4, "the editor stopped mid-run, it may have written")
        code, out = self.call("--result", old)
        self.assertEqual(code, 1, "a result without the marker keeps its exit code")

    def test_result_of_a_task_whose_editor_is_gone(self):
        processing = os.path.join(self.project, ops.QUEUE_RELATIVE, ops.PROCESSING)
        os.makedirs(processing)
        task = "c" * 32
        with open(os.path.join(processing, task + ".json"), "w") as f:
            json.dump({"Id": task, "RunName": "EditMaterial", "Assets": [], "ExtraArgs": "-apply"}, f)
        code, out = self.call("--result", task)
        self.assertEqual(code, 4)
        self.assertIn("the editor that took it is gone", out)
        self.assertNotIn("still running", out)

    def test_task_expires_after_its_timeout_and_its_id_prints_for_agents(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]))
        editor.start()
        try:
            start = time.time()
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--timeout", "30", "--agent")
            end = time.time()
        finally:
            editor.stop.set()
        self.assertEqual(code, 0)
        task = editor.tasks[0]
        self.assertGreaterEqual(task["ExpiresUnix"], start + 30 + ops.TASK_EXPIRY_MARGIN_SEC)
        self.assertLessEqual(task["ExpiresUnix"], end + 30 + ops.TASK_EXPIRY_MARGIN_SEC)
        self.assertIn("task: " + task["Id"], out, "kept under --agent, a killed caller needs it for --result")

    def test_paused_editor_that_took_the_task_is_waited_for(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), claim=False)
        editor.start()
        orig = ops.withdraw
        # The editor pauses after the task was queued, and takes it just before the wrapper withdraws it.
        ops.withdraw = lambda queue_root, task_id: False

        def pause_then_run():
            time.sleep(0.3)
            editor.state = "paused"
            time.sleep(1.5)
            editor.claim = True
            editor.state = "ready"
        threading.Thread(target=pause_then_run, daemon=True).start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--timeout", "20")
        finally:
            ops.withdraw = orig
            editor.stop.set()
        self.assertEqual(code, 0)
        self.assertNotIn("withdrawn", out)

    def test_withdraw_retries_a_held_file(self):
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        with mock.patch.object(ops.os, "remove", side_effect=[PermissionError("held"), None]):
            self.assertTrue(ops.withdraw(queue, "d" * 32))
        out = StringIO()
        with mock.patch.object(ops.os, "remove", side_effect=PermissionError("held")), redirect_stdout(out):
            self.assertFalse(ops.withdraw(queue, "d" * 32), "still held, the task counts as taken")
        self.assertIn("could not withdraw", out.getvalue())

    def test_result_refuses_a_bad_task_id(self):
        code, out = self.call("--result", "../x")
        self.assertEqual(code, 2)

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

    def test_route_editor_never_runs_headless(self):
        # R6-2: every way route() reaches the headless route refuses under --route editor, and none starts Unity.
        record = os.path.join(self.tmp, "unity-argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        call = ("PrefabExport", "Assets/A.prefab", "--route", "editor", "--unity", self.fake_unity)
        code, out = self.call(*call)
        self.assertEqual(code, 2, out)
        self.assertIn("no editor has this project open", out)

        held = ops.BatchLock(os.path.join(self.project, ops.QUEUE_RELATIVE))
        self.assertTrue(held.try_acquire())
        try:
            with mock.patch.object(ops, "project_locked", return_value=True):
                code, out = self.call(*call)
        finally:
            held.release()
        self.assertEqual(code, 2, out)
        self.assertIn("held by a headless DotAsset run", out)

        editor = FakeEditor(self.project, None, claim=False)
        editor.start()

        def die():
            time.sleep(0.5)
            self.editor_alive = False
        threading.Thread(target=die, daemon=True).start()
        try:
            code, out = self.call(*call)
        finally:
            editor.stop.set()
        self.assertEqual(code, 2, out)
        self.assertIn("the editor exited before taking the task, --route editor does not run it headless", out)
        self.assertNotIn("route: batchmode", out)
        self.assertFalse(os.path.exists(record), "--route editor started a headless Unity:\n" + out)

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

    def test_heartbeat_being_replaced_is_read_again(self):
        # The editor replaces .alive with File.Replace, which on Windows leaves a moment with no file. A --quit that
        # read it then refused with "task queue is not up" (0.13.0 matrix, pass 3, 2022.3 headless).
        queue = os.path.join(self.project, ops.QUEUE_RELATIVE)
        os.makedirs(queue, exist_ok=True)
        alive = os.path.join(queue, ops.ALIVE)

        def replaced():
            with open(alive, "w") as f:
                json.dump({"Pid": os.getpid(), "State": "ready", "ProjectPath": self.project.replace(os.sep, "/")}, f)

        timer = threading.Timer(0.1, replaced)
        timer.start()
        try:
            with mock.patch.object(ops, "project_locked", return_value=True):
                self.assertIsNotNone(ops.live_editor(queue), "an editor holds the project, its heartbeat comes back")
        finally:
            timer.join()
        os.remove(alive)
        started = time.time()
        with mock.patch.object(ops, "project_locked", return_value=False):
            self.assertIsNone(ops.live_editor(queue))
        self.assertLess(time.time() - started, 0.05, "with no editor holding the project nothing waits")

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
        with mock.patch.object(ops, "project_locked", return_value=True):
            self.assertIsNotNone(ops.live_editor(queue), "a long run keeps the heartbeat old and busy")
        with mock.patch.object(ops, "project_locked", return_value=False):
            self.assertIsNone(ops.live_editor(queue), "a dead editor's pid reused by another Unity holds no lock")

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
    def test_caller_reaches_unity(self):
        # The journal keeps who started a run, so UndoRun -run=last never takes another caller's run (B5).
        record = os.path.join(self.tmp, "argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        self.assertEqual(self.call("MaterialExport", "Assets/A.mat", "--unity", self.fake_unity, "--caller", "agent-a")[0], 0)
        argv = json.loads(read(record))
        self.assertEqual(argv[argv.index("-dotassetCaller") + 1], "agent-a")
        self.assertEqual(self.call("MaterialExport", "Assets/A.mat", "--unity", self.fake_unity)[0], 0)
        self.assertNotIn("-dotassetCaller", json.loads(read(record)), "no caller, no flag")

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

    def test_compile_error_after_the_run_started_is_unknown(self):
        # An import the run started can log error CS after it wrote, so the run did start and may have written
        os.environ["FAKE_UNITY_MODE"] = "error_after_start"
        code, out = self.call("EditMaterial", "--unity", self.fake_unity, "--args", "-apply")
        self.assertEqual(code, 4)
        self.assertIn("error CS0246", out)
        self.assertNotIn("does not compile", out)

    def test_compile_error_that_cleared_before_the_run_is_unknown(self):
        # A startup compile error that cleared before the marker does not mean the run never started
        os.environ["FAKE_UNITY_MODE"] = "error_cleared_before_start"
        code, out = self.call("EditMaterial", "--unity", self.fake_unity, "--args", "-apply")
        self.assertEqual(code, 4)
        self.assertIn("error CS0246", out)
        self.assertNotIn("does not compile", out)

    def test_timeout_kills_unity(self):
        os.environ["FAKE_UNITY_MODE"] = "hang_after_start"
        started = time.time()
        code, out = self.call("EditMaterial", "--unity", self.fake_unity, "--timeout", "2", "--args", "-apply")
        self.assertEqual(code, 4, "stopped mid-run, a target may have been written")
        self.assertLess(time.time() - started, 15)
        self.assertIn("did not finish", out)
        self.assertIn("outcome: unknown", out)

    def test_timeout_of_a_run_that_cannot_write_is_failed(self):
        # M2, N10: 4 means "may have written". An Export, a dry run, an Audit, or a run that never started cannot have
        cases = [
            ("hang_after_start", ["PrefabExport", "Assets/A.prefab"]),
            ("hang_after_start", ["RenameShaderProperty", "--args", "-from=_A -to=_B"]),
            ("hang_after_start", ["AuditMaterial"]),
            ("hang", ["EditMaterial", "--args", "-apply"]),
        ]
        for mode, argv in cases:
            os.environ["FAKE_UNITY_MODE"] = mode
            code, out = self.call(*(argv + ["--unity", self.fake_unity, "--timeout", "2"]))
            self.assertEqual(code, 1, "%s %s" % (mode, argv))
            self.assertIn("did not finish", out)
            self.assertNotIn("outcome: unknown", out, "%s %s" % (mode, argv))

    def test_timeout_of_an_import_after_start_is_unknown(self):
        # An Import run writes without -apply
        os.environ["FAKE_UNITY_MODE"] = "hang_after_start"
        code, out = self.call("CreateAsset", "--unity", self.fake_unity, "--timeout", "2")
        self.assertEqual(code, 4)

    def test_crash_without_result_is_unknown(self):
        os.environ["FAKE_UNITY_MODE"] = "error_after_start"
        code, out = self.call("EditMaterial", "--unity", self.fake_unity, "--json", "--args", "-apply")
        self.assertEqual(code, 4)
        self.assertIn('"Outcome": "unknown"', out)

    def test_crash_of_an_export_is_failed(self):
        os.environ["FAKE_UNITY_MODE"] = "crash"
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity, "--json")
        self.assertEqual(code, 1)
        self.assertNotIn('"Outcome": "unknown"', out)

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

    def test_graphics_drops_nographics(self):
        record = os.path.join(self.tmp, "argv.json")
        os.environ["FAKE_UNITY_RECORD"] = record
        self.assertEqual(self.call("MaterialExport", "--unity", self.fake_unity, "--graphics")[0], 0)
        argv = json.loads(read(record))
        self.assertIn("-batchmode", argv)
        self.assertNotIn("-nographics", argv)

    def test_quiet_keeps_warnings_and_the_summary(self):
        code, out = self.call("MaterialExport", "--unity", self.fake_unity, "--quiet")
        self.assertEqual(code, 0)
        self.assertNotIn("assets=", out)
        self.assertIn("warning: careful", out)
        self.assertIn("MaterialExport fake", out)

    def test_list_runs_the_command_catalog(self):
        code, out = self.call("--list", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("CommandCatalogExport fake", out)

    def test_visible_without_launch_is_refused(self):
        code, out = self.call("MaterialExport", "--visible", "--unity", self.fake_unity)
        self.assertEqual(code, 2)
        self.assertIn("--visible only goes with --launch", out)

    def test_timeouts_below_one_second_are_refused(self):
        # L6: 0 or a negative wait would give up at once, or never, instead of after a sane time.
        for option in ("--timeout", "--startup-wait"):
            for value in ("0", "-5"):
                err = StringIO()
                with redirect_stderr(err), self.assertRaises(SystemExit) as raised:
                    self.call("MaterialExport", option, value, "--unity", self.fake_unity)
                self.assertEqual(raised.exception.code, 2, option + " " + value)
                self.assertIn("at least 1", err.getvalue(), option + " " + value)


class ModeFlags(Base):
    # R6-3: a mode flag ran its mode and dropped a run given beside it, often with exit 0.
    MODES = [("--status", []), ("--list", []), ("--result", ["0123456789abcdef0123456789abcdef"]), ("--launch", []),
             ("--serve", []), ("--quit", []), ("--open-scene", ["Assets/A.unity"])]

    def stubbed(self, argv):
        """Calls main with every mode and run_once stubbed to succeed. Returns the exit code, output and what ran."""
        calls = []
        stubs = [mock.patch.object(ops, n, lambda *a, _n=n, **k: calls.append(_n) or ops.EXIT_SUCCESS)
                 for n in ("status", "task_result", "launch", "quit_editor", "open_scene", "run_once")]
        for s in stubs:
            s.start()
        try:
            code, out = self.call(*argv)
        finally:
            for s in stubs:
                s.stop()
        return code, out, calls

    def test_a_run_beside_any_mode_is_refused(self):
        for flag, value in self.MODES:
            for given in (["EditPrefab", "Assets/A.prefab", "--args=-spec=C:/x.json -apply"], ["EditPrefab"],
                          ["--args=-spec=C:/x.json"]):
                if flag == "--list" and given == ["--args=-spec=C:/x.json"]:
                    continue
                with self.subTest(flag=flag, given=given):
                    code, out, calls = self.stubbed(given + [flag] + value)
                    self.assertEqual(code, 2, "%s beside %s: exit %s, ran %s" % (flag, given, code, calls))
                    self.assertEqual(calls, [])
                    self.assertIn(flag + " takes no", out)

    def test_two_modes_are_refused(self):
        code, out, calls = self.stubbed(["--launch", "--open-scene", "Assets/A.unity"])
        self.assertEqual(code, 2, out)
        self.assertEqual(calls, [])
        self.assertIn("--launch and --open-scene are separate calls", out)

    def test_list_takes_args(self):
        code, out, calls = self.stubbed(["--list", "--args=-full"])
        self.assertEqual(code, 0, out)
        self.assertEqual(calls, ["run_once"])


class Status(Base):
    def test_no_editor(self):
        code, out = self.call("--status")
        self.assertEqual(code, 0)
        self.assertIn("no editor has this project open", out)

    def test_editor_heartbeat_with_pending_tasks(self):
        editor = FakeEditor(self.project, lambda t: result(t["RunName"]), state="paused")
        open(os.path.join(editor.queue, ops.PENDING, "t1.json"), "w").close()
        code, out = self.call("--status")
        self.assertEqual(code, 0)
        self.assertIn("editor pid %d, state paused" % os.getpid(), out)
        self.assertIn("1 pending", out)

    def test_locked_project_without_heartbeat(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = hold_unity_lock(lock)
        try:
            code, out = self.call("--status")
        finally:
            holder.kill()
            holder.wait()
        self.assertEqual(code, 0)
        self.assertIn("no task queue heartbeat", out)


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


class McpMode(Base):
    """q-later-5 (F16): --mcp serves the runs as MCP tools over stdin and stdout, for Unity 2022.3, which has no unity
    mcp. The same two tools as the Unity CLI adapter, each call going the way a dotasset.py call goes."""

    def serve(self, messages, respond):
        editor = FakeEditor(self.project, respond)
        editor.start()
        raw_in = __import__("io").BytesIO("".join(m if isinstance(m, str) else json.dumps(m) + "\n" for m in messages).encode("utf-8"))
        raw_out = __import__("io").BytesIO()
        text_out = __import__("io").TextIOWrapper(raw_out, encoding="utf-8", write_through=True)
        try:
            with mock.patch.object(ops.sys, "stdin", __import__("io").TextIOWrapper(raw_in, encoding="utf-8")), \
                    mock.patch.object(ops.sys, "stdout", text_out):
                code = ops.main(["--project", self.project, "--mcp"])
        finally:
            editor.stop.set()
        lines = raw_out.getvalue().decode("utf-8").splitlines()
        # Nothing but protocol messages on stdout: the run's own lines go into the tool result.
        replies = [json.loads(l) for l in lines if l.strip()]
        return code, {r.get("id"): r for r in replies}, editor

    def test_tools_list_and_call(self):
        catalog = os.path.join(self.tmp, "catalog.json")
        with open(catalog, "w") as f:
            json.dump({"Commands": [{"RunName": "EditMaterial", "Group": "Edit", "Summary": "Edits materials", "Arguments": ["-spec", "-apply"]}]}, f)

        def respond(t):
            if t["RunName"] == "CommandCatalogExport":
                return result(t["RunName"], outputs=[catalog])
            if t["RunName"] == "MoveAsset":
                return result(t["RunName"], code=2, error="needs -from")
            return result(t["RunName"], lines=["I working", "W careful"])

        code, replies, editor = self.serve([
            {"jsonrpc": "2.0", "id": 1, "method": "initialize",
             "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
             "params": {"name": "dotasset_run", "arguments": {"name": "EditMaterial", "args": "-spec=C:/t/red.json", "assets": "Assets/A.mat", "confirm": True}}},
            {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "dotasset_run", "arguments": {"name": "MoveAsset"}}},
            {"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "dotasset_list", "arguments": {}}},
            {"jsonrpc": "2.0", "id": 6, "method": "resources/list"},
            "not json\n",
            {"jsonrpc": "2.0", "id": 7, "method": "tools/call", "params": {"name": "dotasset_run", "arguments": {}}},
        ], respond)
        self.assertEqual(code, 0)
        self.assertEqual(sorted(k for k in replies if k is not None), [1, 2, 3, 4, 5, 6, 7], "a notification got a reply")
        init = replies[1]["result"]
        self.assertEqual(init["protocolVersion"], "2025-06-18")
        self.assertIn("tools", init["capabilities"])
        self.assertEqual(init["serverInfo"]["name"], "dotasset")
        tools = replies[2]["result"]["tools"]
        self.assertEqual([t["name"] for t in tools], ["dotasset_run", "dotasset_list"])
        self.assertEqual(tools[0]["inputSchema"]["required"], ["name"])

        run = replies[3]["result"]
        self.assertFalse(run["isError"])
        self.assertEqual(run["content"][0]["type"], "text")
        self.assertIn("exit 0 EditMaterial done", run["content"][0]["text"].splitlines(), "the --agent lines")
        self.assertIn("warning: careful", run["content"][0]["text"])
        task = next(t for t in editor.tasks if t["RunName"] == "EditMaterial")
        self.assertEqual(task["Assets"], ["Assets/A.mat"])
        self.assertEqual(task["ExtraArgs"], "-spec=C:/t/red.json -apply", "confirm adds -apply")

        refused = replies[4]["result"]
        self.assertTrue(refused["isError"])
        self.assertIn("error: needs -from", refused["content"][0]["text"])
        self.assertNotIn("-apply", next(t for t in editor.tasks if t["RunName"] == "MoveAsset")["ExtraArgs"])

        listed = replies[5]["result"]
        self.assertFalse(listed["isError"])
        self.assertIn("EditMaterial (Edit): Edits materials. -spec -apply", listed["content"][0]["text"])

        self.assertEqual(replies[6]["error"]["code"], -32601)
        self.assertEqual(replies[None]["error"]["code"], -32700)
        self.assertEqual(replies[7]["error"]["code"], -32602)

    def test_mcp_takes_no_run(self):
        err = StringIO()
        with redirect_stderr(err):
            code, out = self.call("PrefabExport", "--mcp")
        self.assertEqual(code, ops.EXIT_REFUSED)
        self.assertEqual(out, "", "stdout carries protocol messages only")
        self.assertIn("--mcp", err.getvalue())


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


def orphan_with_emptied_setup(project, saved):
    """What a wrapper killed mid-run leaves once its Unity has exited: running.json with the scenes it saved, the
    scene setup as batch Unity writes it on exit. Returns the record's path."""
    batch = os.path.join(project, ops.QUEUE_RELATIVE, "batch")
    os.makedirs(batch, exist_ok=True)
    path = os.path.join(batch, "running.json")
    with open(path, "w") as f:
        json.dump({"Pid": os.getpid() + 1, "Run": "EditMaterial", "Task": "t", "Started": time.time(),
                   "SceneSetupB64": base64.b64encode(saved.encode()).decode()}, f)
    with open(os.path.join(project, "Library", "LastSceneManagerSetup.txt"), "w") as f:
        f.write("sceneSetups: []\n")
    return path


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

    def orphan(self, pid):
        # What a wrapper killed mid-run leaves: running.json naming its Unity, no batch lock.
        batch = os.path.join(self.project, "Library", "DotAsset", "Queue", "batch")
        os.makedirs(batch, exist_ok=True)
        path = os.path.join(batch, "running.json")
        with open(path, "w") as f:
            json.dump({"Pid": pid, "Run": "EditMaterial", "Task": "t", "Started": time.time()}, f)
        return path

    def test_orphaned_headless_run_is_waited_for(self):
        # A14: its Unity holds the project with no heartbeat and no batch lock. That is not a starting editor.
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = hold_unity_lock(lock)
        self.orphan(os.getpid())
        threading.Timer(1.5, holder.kill).start()
        try:
            code, out = self.call("PrefabExport", "Assets/A.prefab", "--unity", self.fake_unity, "--startup-wait", "1")
        finally:
            holder.kill()
            holder.wait()
        self.assertEqual(code, 0, out)
        self.assertIn("(EditMaterial, Unity pid %d) outlived the wrapper" % os.getpid(), out)
        self.assertIn("route: batchmode", out)

    def test_orphan_still_running_refuses_naming_its_pid(self):
        for route in ("auto", "batch"):
            lock = os.path.join(self.project, "Temp", "UnityLockfile")
            os.makedirs(os.path.dirname(lock), exist_ok=True)
            open(lock, "w").close()
            holder = hold_unity_lock(lock)
            self.orphan(os.getpid())
            try:
                code, out = self.call("PrefabExport", "Assets/A.prefab", "--route", route, "--unity", self.fake_unity, "--timeout", "1", "--startup-wait", "1")
            finally:
                holder.kill()
                holder.wait()
            self.assertEqual(code, 2, route + ": " + out)
            self.assertIn("(Unity pid %d) is still running after 1s" % os.getpid(), out)
            self.assertNotIn("not a DotAsset run", out)

    def test_ended_orphan_record_is_removed(self):
        path = self.orphan(os.getpid() + 1)
        self.assertIsNone(ops.orphan_batch(self.project, os.path.join(self.project, "Library", "DotAsset", "Queue")))
        self.assertFalse(os.path.exists(path))

    def test_ended_orphan_gives_the_scene_setup_back(self):
        # Its Unity emptied the setup on exit with no wrapper left to restore it. The next run does, before saving its own.
        saved = "sceneSetups:\n- path: Assets/Scenes/Main.unity\n  isLoaded: 1\n"
        path = orphan_with_emptied_setup(self.project, saved)
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--route", "batch", "--unity", self.fake_unity)
        self.assertEqual(code, 0, out)
        with open(os.path.join(self.project, "Library", "LastSceneManagerSetup.txt")) as f:
            self.assertEqual(f.read(), saved)
        self.assertFalse(os.path.exists(path))

    def test_ended_orphan_leaves_a_newer_scene_setup(self):
        # An editor opened and closed since then saved scenes of its own, the record's are older.
        path = orphan_with_emptied_setup(self.project, "sceneSetups:\n- path: Assets/Scenes/Old.unity\n  isLoaded: 1\n")
        newer = "sceneSetups:\n- path: Assets/Scenes/New.unity\n  isLoaded: 1\n"
        setup = os.path.join(self.project, "Library", "LastSceneManagerSetup.txt")
        with open(setup, "w") as f:
            f.write(newer)
        self.assertIsNone(ops.orphan_batch(self.project, os.path.join(self.project, ops.QUEUE_RELATIVE)))
        with open(setup) as f:
            self.assertEqual(f.read(), newer)
        self.assertFalse(os.path.exists(path))

    def test_headless_run_is_recorded_while_unity_runs(self):
        seen = os.path.join(self.tmp, "seen.json")
        os.environ["FAKE_UNITY_RUNNING"] = seen
        setup = os.path.join(self.project, "Library", "LastSceneManagerSetup.txt")
        os.makedirs(os.path.dirname(setup), exist_ok=True)
        saved = "sceneSetups:\n- path: Assets/Scenes/Main.unity\n  isLoaded: 1\n"
        with open(setup, "w", newline="") as f:
            f.write(saved)
        code, out = self.call("PrefabExport", "Assets/A.prefab", "--route", "batch", "--unity", self.fake_unity)
        self.assertEqual(code, 0, out)
        with open(seen) as f:
            running = json.load(f)
        self.assertEqual(running["Run"], "PrefabExport")
        # The scenes to give back if this wrapper is killed before Unity exits.
        self.assertEqual(base64.b64decode(running["SceneSetupB64"]).decode(), saved)
        self.assertFalse(os.path.exists(os.path.join(self.project, "Library", "DotAsset", "Queue", "batch", "running.json")))

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

    def test_compile_check_builds_the_adapter_apart(self):
        """Editor/UnityCli has an .asmdef of its own and needs com.unity.pipeline, so it is not part of DotAsset.Editor."""
        sys.path.insert(0, os.path.join(ROOT, "tools"))
        try:
            import compile_check
        finally:
            sys.path.pop(0)
        editor = [os.path.basename(s) for s in compile_check.sources()]
        self.assertIn("EditorRun.cs", editor)
        self.assertNotIn("UnityCliCommands.cs", editor)
        name, folder = compile_check.OPTIONAL[0]
        self.assertEqual(name, "DotAsset.Editor.UnityCli")
        self.assertEqual([os.path.basename(s) for s in compile_check.sources(folder)], ["UnityCliCommands.cs"])

    def test_every_run_has_an_editmode_test(self):
        """Every run is executed in process by an EditMode test, through RunDispatcher.Run or RunFixture's helpers."""
        tests = os.path.join(ROOT, "tests", "editmode", "package")
        called = set()
        for name in os.listdir(tests):
            if name.endswith(".cs"):
                called |= set(re.findall(r'\b(?:RunDispatcher\.Run|Ok|Run|Export|Report)\("(\w+)"', read(os.path.join(tests, name))))
        runs = set()
        for group in ("Export", "Import", "Edit", "Migrate", "Audit"):
            runs |= {f[:-len("Command.cs")] for f in os.listdir(os.path.join(PACKAGE, "Editor", group)) if f.endswith("Command.cs")}
        self.assertEqual(sorted(runs - called), [])

    def test_every_spec_reader_refuses_unknown_keys(self):
        """R5-5: a misspelt top-level spec key is refused, never read as an empty list. A run that loads -spec itself
        checks its keys with Json.UnknownKeys, the others go through Spec.LoadTargets or Spec.Targets."""
        readers = []
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs"):
                    text = read(os.path.join(dirpath, name))
                    if re.search(r"\bSpec\.Load\(", text):
                        readers.append(name)
                        self.assertTrue(re.search(r"Json\.UnknownKeys\(spec\b|Spec\.Targets\(", text),
                                        name + " loads -spec without checking its top-level keys")
        self.assertIn("RemoveMissingScriptsCommand.cs", readers)

    # Every place the package writes, moves or deletes a file outside the AssetDatabase, with the count of call sites.
    # Writes to the project go through RunJournal.Keep and DotAssetPaths.ReplaceText. A new call site must be added
    # here with its reason, so no unjournaled write slips in unnoticed (R5-2).
    FILE_WRITES = {
        "Core/RunJournal.cs": (23, "the journal: backups, restores, its own records"),
        "Core/TaskQueue.cs": (14, "queue tasks, results and the heartbeat under Library/DotAsset"),
        "Core/DotAssetPaths.cs": (6, "ReplaceText (temp file then replace) and pruning old exports and reports"),
        "Audit/AuditReport.cs": (3, "the report and its own fix spec, -report checked by PathRefusal"),
        "Core/Cli.cs": (2, "the -resultFile of a command-line run"),
        "Import/CreateAssetCommand.cs": (2, "a temp spec for the AnimatorController ops, deleted after"),
        "UI/DotAssetWindow.cs": (2, "Library/DotAsset and the wrapper copy the window offers"),
        "Core/RunDispatcher.cs": (2, "the run log under Library/DotAsset/Logs"),
        "Export/ExportHelper.cs": (1, "the export file"),
        "Migrate/UndoRunCommand.cs": (1, "the journal after a partial undo"),
    }

    def test_spec_values_are_read_strictly(self):
        """R6-4: Json.Bool and Json.Int throw on a wrong-typed spec value instead of falling back to the default. The
        loose forms, which fall back, are for files DotAsset writes itself and appear only in TaskQueue.cs."""
        loose = []
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs") and name != "Json.cs" and re.search(r"\bJson\.Loose(Bool|Int)\(", read(os.path.join(dirpath, name))):
                    loose.append(name)
        self.assertEqual(loose, ["TaskQueue.cs"])
        json_cs = read(os.path.join(PACKAGE, "Editor", "Core", "Json.cs"))
        for kind, reader in (("bool", "Bool"), ("int", "Int")):
            body = json_cs[json_cs.index("public static %s %s(" % (kind, reader)):]
            body = body[:body.index("\n        }\n")]
            self.assertIn("throw new SpecValueException", body, "Json.%s falls back on a wrong type" % reader)

    def test_file_writes_stay_in_known_places(self):
        pattern = re.compile(r"File\.(WriteAll\w*|Delete|Move|Copy|Replace|Create\w*|AppendAll\w*|SetAttributes|Open\w*)\("
                             r"|Directory\.(Delete|Move|CreateDirectory)\(|new (FileStream|StreamWriter)\(|Json\.WriteFile\("
                             r"|FileUtil\.\w+\(")
        found = {}
        editor = os.path.join(PACKAGE, "Editor")
        for dirpath, _, files in os.walk(editor):
            for name in files:
                if name.endswith(".cs"):
                    path = os.path.join(dirpath, name)
                    code = "\n".join(line.split("//")[0] for line in read(path).splitlines())
                    n = len(pattern.findall(code))
                    if n:
                        found[os.path.relpath(path, editor).replace(os.sep, "/")] = n
        self.assertEqual(found, {k: v[0] for k, v in self.FILE_WRITES.items()})

    def test_versions_agree(self):
        pkg = json.loads(read(os.path.join(PACKAGE, "package.json")))
        cs = read(os.path.join(PACKAGE, "Editor", "Core", "DotAssetVersion.cs"))
        self.assertIn('Version = "%s"' % pkg["version"], cs)
        self.assertIn("### " + pkg["version"], read(os.path.join(ROOT, "Version.md")))
        # R5-8: the README's install pin and sample output slipped twice, at 0.11.1 and 0.11.4.
        readme = read(os.path.join(ROOT, "README.md"))
        self.assertIn("`#v%s`" % pkg["version"], readme, "README install pin")
        self.assertIn("DotAsset %s, Unity" % pkg["version"], readme, "README sample output")

    def test_format_version_agrees(self):
        """N3: the wrapper's own --json report for a run it lost sight of carries the same FormatVersion as a
        RunResult, so a caller can check it on every run's JSON."""
        cs = read(os.path.join(PACKAGE, "Editor", "Core", "DotAssetVersion.cs"))
        self.assertIn("Format = %d;" % getattr(ops, "FORMAT_VERSION", -1), cs)
        out = StringIO()
        with redirect_stdout(out), redirect_stderr(StringIO()):
            code = ops.unknown_outcome(mock.Mock(json=True), "a" * 32, None)
        self.assertEqual(code, ops.EXIT_UNKNOWN)
        text = out.getvalue()
        report = json.loads(text[text.index("{"):])
        self.assertEqual(report.get("FormatVersion"), 1, text)

    def test_no_char_contains(self):
        """string.Contains(char) is missing from .NET Framework 4.8."""
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs"):
                    text = read(os.path.join(dirpath, name))
                    self.assertIsNone(re.search(r"\.Contains\('.'\)", text), name)

    def test_exit_codes_go_through_the_enum(self):
        """Exit codes come from ExitCode, never a bare int (CLAUDE.md code rules)."""
        bare = re.compile(r"\bExit\(\s*-?\d|\bExitCode\s*=\s*-?\d|\(ExitCode\)\s*-?\d")
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs"):
                    for n, line in enumerate(read(os.path.join(dirpath, name)).splitlines(), 1):
                        self.assertIsNone(bare.search(line), "%s:%d %s" % (name, n, line.strip()))

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


class SmokeLeakCheck(unittest.TestCase):
    """The smoke test's check that a run wrote nothing outside its own folder."""

    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("run_smoke", os.path.join(ROOT, "tests", "smoke", "run_smoke.py"))
        cls.smoke = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.smoke)

    def setUp(self):
        self.project = make_project(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.project, True)
        for rel, text in (("Assets/Keep.mat", "a"), ("ProjectSettings/TagManager.asset", "tags"),
                          ("Packages/manifest.json", "{}"), ("Packages/packages-lock.json", "{}"),
                          ("Packages/com.alexmah.dotasset.smoke/Helper.cs", "class A {}"),
                          ("Assets/DotAssetSmoke/Red.mat", "red")):
            self.write(rel, text)
        self.before = self.smoke.outside_state(self.project)

    def write(self, rel, text):
        path = os.path.join(self.project, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as f:
            f.write(text)

    def changes(self):
        return self.smoke.outside_changes(self.before, self.smoke.outside_state(self.project))

    def test_a_changed_project_setting_is_a_leak(self):
        # R5-12: the check walked Assets/ only.
        self.write("ProjectSettings/TagManager.asset", "tags and more")
        self.assertIn("changed ProjectSettings/TagManager.asset", self.changes())

    def test_a_new_file_in_packages_is_a_leak(self):
        self.write("Packages/com.example.tools/New.mat", "x")
        self.assertIn("added Packages/com.example.tools/New.mat", self.changes())

    def test_the_smoke_folder_helper_and_lock_file_are_not(self):
        self.write("Assets/DotAssetSmoke/Red.mat", "blue")
        self.write("Packages/com.alexmah.dotasset.smoke/Helper.cs.meta", "guid")
        self.write("Packages/packages-lock.json", '{"dependencies": {}}')
        # Unity writing a setting again with the same bytes is not a change.
        time.sleep(0.05)
        self.write("ProjectSettings/TagManager.asset", "tags")
        self.assertEqual("", self.changes())


if __name__ == "__main__":
    unittest.main()
