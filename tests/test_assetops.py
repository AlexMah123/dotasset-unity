"""Tests for Scripts~/assetops.py and for the naming rules the C# side relies on.

Unity itself is replaced by two fakes:
  - a fake editor: a thread that serves the file queue the way TaskQueue.cs does
  - a fake Unity executable: a script that answers -executeMethod AssetOps.Cli.Run the way Cli.cs does
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
SPEC = importlib.util.spec_from_file_location("assetops", os.path.join(PACKAGE, "Scripts~", "assetops.py"))
ops = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ops)


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

    def __init__(self, project, respond, state="ready", claim=True):
        super().__init__(daemon=True)
        self.queue = os.path.join(project, ops.QUEUE_RELATIVE)
        self.respond = respond
        self.state = state
        self.claim = claim
        self.stop = threading.Event()
        self.tasks = []
        for d in (ops.PENDING, ops.PROCESSING, ops.DONE):
            os.makedirs(os.path.join(self.queue, d), exist_ok=True)
        self.beat()

    def beat(self):
        with open(os.path.join(self.queue, ops.ALIVE), "w") as f:
            json.dump({"Pid": os.getpid(), "State": self.state, "UnityVersion": "6000.0.40f1", "AssetOpsVersion": "test"}, f)

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
            time.sleep(0.05)


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
if mode == "compile_error":
    with open(log, "w") as f:
        f.write("Assets/Foo.cs(3,1): error CS1002: ; expected\nScripts have compiler errors.\n")
    sys.exit(1)
if mode == "hang":
    import time
    time.sleep(30)
run = val("-assetopsRun")
assets = base64.b64decode(val("-assetopsAssetsB64")).decode() if val("-assetopsAssetsB64") else ""
extra = base64.b64decode(val("-assetopsArgsB64")).decode() if val("-assetopsArgsB64") else ""
code = int(os.environ.get("FAKE_UNITY_EXIT", "0"))
with open(log, "w") as f:
    f.write("fake unity log\n")
with open(val("-assetopsResult"), "w") as f:
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
        with open(self.fake_unity, "w") as f:
            f.write(FAKE_UNITY)
        os.chmod(self.fake_unity, os.stat(self.fake_unity).st_mode | stat.S_IEXEC)
        for k in ("FAKE_UNITY_MODE", "FAKE_UNITY_EXIT", "FAKE_UNITY_RECORD", "ASSETOPS_UNITY", "ASSETOPS_PROJECT"):
            os.environ.pop(k, None)

    def tearDown(self):
        ops.is_unity_process = self._orig_is_unity
        shutil.rmtree(self.tmp, ignore_errors=True)

    def call(self, *argv):
        out = StringIO()
        with redirect_stdout(out):
            code = ops.main(["--project", self.project] + list(argv))
        return code, out.getvalue()


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
        src = open(os.path.join(PACKAGE, "Editor", "Core", "ExitCodes.cs")).read()
        for needle in ('EndsWith("Export"', 'EndsWith("Import"', 'StartsWith("Create"', 'StartsWith("Edit"', 'StartsWith("Audit"'):
            self.assertIn(needle, src)

    def test_every_command_class_is_sealed_and_named(self):
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith("Command.cs") and name != "AssetOpsCommand.cs":
                    text = open(os.path.join(dirpath, name)).read()
                    cls = name[:-3]
                    self.assertRegex(text, r"public sealed class %s : AssetOpsCommand" % cls, name)
                    self.assertIn("Docs/", text, name + " header must point at its doc")

    def test_queue_constants_match(self):
        src = open(os.path.join(PACKAGE, "Editor", "Core", "TaskQueue.cs")).read()
        self.assertIn('QueueRelative = "Library/AssetOps/Queue"', src)
        self.assertEqual(ops.QUEUE_RELATIVE.replace(os.sep, "/"), "Library/AssetOps/Queue")
        for const in ("pending", "processing", "done", ".alive"):
            self.assertIn('"%s"' % const, src)


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
        argv = json.load(open(record))
        self.assertIn("-batchmode", argv)
        self.assertIn("-nographics", argv)
        self.assertEqual(argv[argv.index("-executeMethod") + 1], "AssetOps.Cli.Run")
        self.assertEqual(argv[argv.index("-projectPath") + 1], self.project)
        self.assertEqual(base64.b64decode(argv[argv.index("-assetopsArgsB64") + 1]).decode(), extra)
        self.assertNotIn("-assetopsAssetsB64", argv, "no AssetList, no flag")

    def test_assets_are_normalized(self):
        code, out = self.call("PrefabExport", r"Assets\Prefabs\A.prefab, Assets/B.prefab", "--unity", self.fake_unity)
        self.assertEqual(code, 0)
        self.assertIn("assets=Assets/Prefabs/A.prefab,Assets/B.prefab", out)

    def test_exit_code_passes_through(self):
        os.environ["FAKE_UNITY_EXIT"] = "3"
        code, _ = self.call("AuditMaterial", "--unity", self.fake_unity)
        self.assertEqual(code, 3)

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


class StartupWait(Base):
    @unittest.skipIf(sys.platform == "win32", "fcntl lock")
    def test_locked_project_waits_for_heartbeat(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = subprocess.Popen([sys.executable, "-c",
                                   "import fcntl,sys,time;f=open(sys.argv[1],'w');fcntl.flock(f,fcntl.LOCK_EX);time.sleep(30)", lock])
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

    @unittest.skipIf(sys.platform == "win32", "fcntl lock")
    def test_locked_project_without_queue_refuses(self):
        lock = os.path.join(self.project, "Temp", "UnityLockfile")
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        holder = subprocess.Popen([sys.executable, "-c",
                                   "import fcntl,sys,time;f=open(sys.argv[1],'w');fcntl.flock(f,fcntl.LOCK_EX);time.sleep(30)", lock])
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


if __name__ == "__main__":
    unittest.main()


class PackageFiles(unittest.TestCase):
    def test_every_package_file_has_a_meta(self):
        """A git-installed package is immutable: Unity ignores files without a .meta."""
        r = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "gen_meta.py"), "--check"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_versions_agree(self):
        pkg = json.load(open(os.path.join(PACKAGE, "package.json")))
        cs = open(os.path.join(PACKAGE, "Editor", "Core", "AssetOpsVersion.cs")).read()
        self.assertIn('Version = "%s"' % pkg["version"], cs)
        self.assertIn("### " + pkg["version"], open(os.path.join(ROOT, "Version.md")).read())

    def test_no_char_contains(self):
        """string.Contains(char) is missing from .NET Framework 4.8."""
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith(".cs"):
                    text = open(os.path.join(dirpath, name)).read()
                    self.assertIsNone(re.search(r"\.Contains\('.'\)", text), name)

    def test_docs_mention_every_run(self):
        docs = "".join(open(os.path.join(ROOT, "Docs", f)).read() for f in os.listdir(os.path.join(ROOT, "Docs")))
        guide = open(os.path.join(ROOT, "Docs", "AI-Guide.md")).read()
        for dirpath, _, files in os.walk(os.path.join(PACKAGE, "Editor")):
            for name in files:
                if name.endswith("Command.cs") and name != "AssetOpsCommand.cs":
                    run = name[:-len("Command.cs")]
                    self.assertIn("`%s`" % run, docs, run)
                    self.assertIn(run, guide, run + " missing from the AI-Guide decision table")

    def test_doc_json_examples_parse(self):
        for name in os.listdir(os.path.join(ROOT, "Docs")):
            text = open(os.path.join(ROOT, "Docs", name)).read()
            for m in re.finditer(r"```json\n(.*?)```", text, re.S):
                body = m.group(1)
                try:
                    json.loads(body)
                except ValueError:
                    json.loads("{" + body + "}")  # a fragment such as "ArrayOps": [...]
