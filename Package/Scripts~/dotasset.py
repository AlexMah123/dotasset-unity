#!/usr/bin/env python3
"""DotAsset wrapper. One command line, editor open or closed.

Routing:
  1. <Project>/Library/DotAsset/Queue/.alive names a live Unity process: the editor has the project open
     and its task queue is running. The task goes to pending/, the result comes back from done/.
  2. Unity holds the project (Temp/UnityLockfile is locked) but no heartbeat is up yet: the editor is still
     starting, or the package has not compiled. Wait up to --startup-wait seconds for the heartbeat.
  3. Otherwise launch Unity headless: -batchmode -nographics -executeMethod DotAsset.Cli.Run.
Both routes produce the same result JSON and the same printed output.

Usage:
  python dotasset.py <RunName> [AssetList] [--args "<extra args>"] [--project DIR] [--unity PATH] [--timeout SEC]
  python dotasset.py --list          every run the project knows, with its summary
  python dotasset.py --status        what the editor's queue is doing
  python dotasset.py --launch [--visible]  open the project in the editor and wait until it takes tasks. The
                                       editor starts minimized and never takes the focus unless --visible
  python dotasset.py --quit [--force]  close the editor that has the project open, refused while a scene has
                                       unsaved changes unless --force
  python dotasset.py --open-scene A.unity[,B.unity] [--force]  open scenes in that editor, refused the same way.
                                       The last line names what was open before: "previously open: ..."

  AssetList   comma-separated asset paths or folders, e.g. "Assets/Prefabs/A.prefab,Assets/Prefabs/B.prefab".
              Spec-driven runs (Edit*, Create*, most Migrate runs) name targets in the spec, leave it out.
  --args      extra arguments for the run, e.g. '-spec="C:/temp/edit.json" -apply'

Exit codes, passed through from the run:
  0  success
  1  the run failed, or Unity failed before the run could start
  2  refused: unknown run, bad argument, bad spec, editor in a state that cannot take tasks
  3  audit runs only: the run succeeded and the report lists issues

Python 3.8+, standard library only.
"""

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
import uuid

# Same constants as TaskQueueProtocol in Editor/Core/TaskQueue.cs. Change both together.
QUEUE_RELATIVE = os.path.join("Library", "DotAsset", "Queue")
PENDING = "pending"
PROCESSING = "processing"
DONE = "done"
ALIVE = ".alive"
QUIT_REQUEST = "quit.json"
QUIT_RESULT = "quit.result.json"
OPEN_SCENE_REQUEST = "openscene.json"
OPEN_SCENE_RESULT = "openscene.result.json"
HEARTBEAT_FRESHNESS_SEC = 15.0
# Beyond this an idle heartbeat counts as dead for routing. Generous, an unfocused editor can tick slowly.
STALE_HEARTBEAT_SEC = 120.0

EXIT_SUCCESS = 0
EXIT_FAILED = 1
EXIT_REFUSED = 2
EXIT_ISSUES = 3

BUSY_STATES = {"busy", "compiling", "importing", "playmode", "reloading", "starting"}

# --agent: compact output for AI agents. Wrapper progress is muted, only its errors, warnings and hints print.
AGENT = False
AGENT_KEEP = ("error:", "warning:", "hint:", "interrupted")
AGENT_MAX_LINES = 10


def log(msg):
    if AGENT and not msg.startswith(AGENT_KEEP):
        return
    print("[dotasset] " + msg, flush=True)


def resolve_group(run_name):
    """Same rule as Groups.Resolve in Editor/Core/ExitCodes.cs. Change both together."""
    if run_name.endswith("Export"):
        return "Export"
    if run_name.endswith("Import") or run_name.startswith("Create"):
        return "Import"
    if run_name.startswith("Edit"):
        return "Edit"
    if run_name.startswith("Audit"):
        return "Audit"
    return "Migrate"


# ---------------------------------------------------------------- project and editor discovery

def find_project(start):
    cur = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(cur, "Assets")) and os.path.isfile(os.path.join(cur, "ProjectSettings", "ProjectVersion.txt")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            return None
        cur = parent


def project_unity_version(project):
    try:
        with open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), encoding="utf-8") as f:
            for line in f:
                m = re.match(r"\s*m_EditorVersion:\s*(\S+)", line)
                if m:
                    return m.group(1)
    except OSError:
        pass
    return None


def hub_secondary_path():
    if sys.platform == "win32":
        base = os.environ.get("APPDATA", "")
        candidate = os.path.join(base, "UnityHub", "secondaryInstallPath.json")
    elif sys.platform == "darwin":
        candidate = os.path.expanduser("~/Library/Application Support/UnityHub/secondaryInstallPath.json")
    else:
        candidate = os.path.expanduser("~/.config/UnityHub/secondaryInstallPath.json")
    try:
        with open(candidate, encoding="utf-8") as f:
            value = json.load(f)
            return value if isinstance(value, str) and value else None
    except (OSError, ValueError):
        return None


def editor_candidates(version):
    roots = []
    secondary = hub_secondary_path()
    if secondary:
        roots.append(secondary)
    if sys.platform == "win32":
        for env in ("ProgramFiles", "ProgramW6432"):
            if os.environ.get(env):
                roots.append(os.path.join(os.environ[env], "Unity", "Hub", "Editor"))
        exe = os.path.join("Editor", "Unity.exe")
    elif sys.platform == "darwin":
        roots.append("/Applications/Unity/Hub/Editor")
        exe = os.path.join("Unity.app", "Contents", "MacOS", "Unity")
    else:
        roots.append(os.path.expanduser("~/Unity/Hub/Editor"))
        exe = os.path.join("Editor", "Unity")
    return [os.path.join(r, version, exe) for r in roots]


def find_unity(explicit, project):
    if explicit:
        return explicit if os.path.isfile(explicit) else None
    env = os.environ.get("DOTASSET_UNITY")
    if env:
        return env if os.path.isfile(env) else None
    version = project_unity_version(project)
    if not version:
        return None
    for candidate in editor_candidates(version):
        if os.path.isfile(candidate):
            return candidate
    return None


# ---------------------------------------------------------------- process and lock checks

def pid_alive(pid):
    if pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = wintypes.DWORD()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def process_name(pid):
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return ""
        try:
            size = wintypes.DWORD(1024)
            buf = ctypes.create_unicode_buffer(1024)
            if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
                return os.path.basename(buf.value)
            return ""
        finally:
            kernel32.CloseHandle(handle)
    if os.path.exists("/proc/%d/comm" % pid):
        try:
            with open("/proc/%d/comm" % pid, encoding="utf-8") as f:
                return f.read().strip()
        except OSError:
            return ""
    try:
        out = subprocess.run(["ps", "-p", str(pid), "-o", "comm="], capture_output=True, text=True, timeout=5)
        return os.path.basename(out.stdout.strip())
    except (OSError, subprocess.SubprocessError):
        return ""


UNITY_NAMES = {"unity", "unity.exe"}


def is_unity_process(pid):
    """A pid from a stale heartbeat can be reused by another program. Only a live Unity editor counts,
    not Unity Hub, the shader compiler, the crash handler or the licensing client."""
    return pid_alive(pid) and process_name(pid).lower() in UNITY_NAMES


def project_locked(project):
    """True when a Unity editor holds the project. Unity keeps Temp/UnityLockfile locked while it runs."""
    path = os.path.join(project, "Temp", "UnityLockfile")
    if not os.path.exists(path):
        return False
    if sys.platform == "win32":
        try:
            with open(path, "a"):
                return False
        except PermissionError:
            return True
        except OSError:
            return False
    import fcntl
    try:
        fd = os.open(path, os.O_RDWR)
    except OSError:
        return False
    try:
        for lock in (lambda: fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB), lambda: fcntl.lockf(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)):
            try:
                lock()
            except (BlockingIOError, PermissionError):
                return True
            except OSError:
                continue
        return False
    finally:
        try:
            fcntl.flock(fd, fcntl.LOCK_UN)
        except OSError:
            pass
        os.close(fd)


BATCH_LOCK = "batch.lock"


class BatchLock:
    """One headless run per project at a time. Held from before the scene setup is saved until after Unity exits,
    released by the OS if this script dies, so it never goes stale. Without it two callers that both see the
    project free start two Unity processes, and the second one fails on Unity's own lock."""

    def __init__(self, queue_root):
        self.path = os.path.join(queue_root, BATCH_LOCK)
        self.fd = None

    def try_acquire(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        fd = os.open(self.path, os.O_RDWR | os.O_CREAT)
        try:
            if sys.platform == "win32":
                import msvcrt
                os.lseek(fd, 0, os.SEEK_SET)
                msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            os.close(fd)
            return False
        self.fd = fd
        return True

    def release(self):
        if self.fd is None:
            return
        try:
            if sys.platform == "win32":
                import msvcrt
                os.lseek(self.fd, 0, os.SEEK_SET)
                msvcrt.locking(self.fd, msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.fd, fcntl.LOCK_UN)
        except OSError:
            pass
        os.close(self.fd)
        self.fd = None


def batch_run_active(queue_root):
    """True while another DotAsset headless run has the project."""
    lock = BatchLock(queue_root)
    if lock.try_acquire():
        lock.release()
        return False
    return True


def read_alive(queue_root):
    path = os.path.join(queue_root, ALIVE)
    for _ in range(3):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            data["_age"] = time.time() - os.path.getmtime(path)
            return data
        except FileNotFoundError:
            return None
        except (OSError, ValueError):
            time.sleep(0.1)
    return None


def same_path(a, b):
    try:
        x = os.path.normcase(os.path.realpath(a)).rstrip("\\/")
        y = os.path.normcase(os.path.realpath(b)).rstrip("\\/")
    except (TypeError, ValueError):
        return False
    if sys.platform == "darwin":
        # APFS and HFS+ are case-insensitive by default, normcase does not fold case on macOS.
        return x.lower() == y.lower()
    return x == y


def live_editor(queue_root):
    """The heartbeat of a running editor of this project, or None."""
    alive = read_alive(queue_root)
    if not alive:
        return None
    # A heartbeat copied along with the project, or left by a crash whose pid now belongs to another editor,
    # names a different project.
    project = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(queue_root))))
    if alive.get("ProjectPath") and not same_path(alive["ProjectPath"], project):
        return None
    # A heartbeat that stopped long ago with a state that should be ticking belongs to a dead or hung editor whose
    # pid got reused. busy, reloading, compiling and importing legitimately block the editor's update loop.
    if alive.get("_age", 0) > STALE_HEARTBEAT_SEC and alive.get("State") not in ("busy", "reloading", "compiling", "importing"):
        return None
    try:
        pid = int(alive.get("Pid", 0))
    except (TypeError, ValueError):
        return None
    if not is_unity_process(pid):
        return None
    return alive


# ---------------------------------------------------------------- result printing

def print_result(result, quiet):
    if AGENT:
        print_agent(result)
        return
    for line in result.get("Lines") or []:
        level, _, text = line.partition(" ")
        if level == "E":
            print("error: " + text, flush=True)
        elif level == "W":
            print("warning: " + text, flush=True)
        elif not quiet:
            print(text, flush=True)
    if plan_totals(result):
        print("plan: " + plan_totals(result), flush=True)
    for entry in result.get("Plan") or []:
        print("plan: " + plan_line(entry), flush=True)
    if result.get("PlanTruncated"):
        print("plan: truncated, narrow the run to see the rest", flush=True)
    for out in result.get("Outputs") or []:
        if not os.path.exists(out):
            print("missing output: " + out, flush=True)
    if result.get("Error"):
        log("error: " + result["Error"])
    log(result.get("Summary") or "no summary")
    if result.get("LogFile"):
        log("log: " + result["LogFile"])


def print_agent(result):
    # One line per fact, no prefix: exit code and summary, the first error, warnings, failed targets, outputs, plan.
    code = exit_code_of(result)
    print("exit %d %s" % (code, result.get("Summary") or "no summary"), flush=True)
    lines = result.get("Lines") or []
    error = result.get("Error") or next((l[2:] for l in lines if l.startswith("E ")), None)
    if error:
        print("error: " + error, flush=True)
    capped("warning: ", [l[2:] for l in lines if l.startswith("W ")])
    if result.get("FailedTargets"):
        print("failed: " + ", ".join(result["FailedTargets"]), flush=True)
    for out in result.get("Outputs") or []:
        print(("out: " if os.path.exists(out) else "missing output: ") + out, flush=True)
    if plan_totals(result):
        print("plan: " + plan_totals(result), flush=True)
    capped("plan: ", [plan_line(e) for e in result.get("Plan") or []], result.get("PlanTruncated"))
    if code not in (EXIT_SUCCESS, EXIT_ISSUES) and result.get("LogFile"):
        print("log: " + result["LogFile"], flush=True)


def capped(prefix, items, truncated=False):
    for item in items[:AGENT_MAX_LINES]:
        print(prefix + item, flush=True)
    rest = len(items) - AGENT_MAX_LINES
    if rest > 0 or truncated:
        print(prefix + ("%d more" % rest if rest > 0 else "more") + ", --json for all", flush=True)


def plan_totals(result):
    # The complete count, past the 500 listed entries too: "12 changes, 3 files in 4 targets". None when nothing is planned.
    totals = result.get("PlanTotals") or {}
    changes, files = totals.get("Changes") or 0, totals.get("Files") or 0
    if not changes and not files:
        return None
    parts = []
    if changes:
        parts.append("%d change%s" % (changes, "" if changes == 1 else "s"))
    if files:
        parts.append("%d file%s" % (files, "" if files == 1 else "s"))
    targets = totals.get("Targets") or 0
    return "%s in %d target%s" % (", ".join(parts), targets, "" if targets == 1 else "s")


def plan_line(entry):
    # A property change: {Target, Object?, Property, Before, After}. A file action: {Action, Path, Detail?}.
    if "Action" in entry:
        return " ".join(str(entry[k]) for k in ("Action", "Path", "Detail") if entry.get(k) is not None)
    where = entry.get("Target", "")
    if entry.get("Object"):
        where += " [" + str(entry["Object"]) + "]"
    return "%s %s: %s -> %s" % (where, entry.get("Property"),
                                json.dumps(entry.get("Before")), json.dumps(entry.get("After")))


def exit_code_of(result):
    try:
        code = int(result.get("ExitCode", EXIT_FAILED))
    except (TypeError, ValueError):
        code = EXIT_FAILED
    for out in result.get("Outputs") or []:
        if code == EXIT_SUCCESS and not os.path.exists(out):
            code = EXIT_FAILED
    return code


# ---------------------------------------------------------------- queue route

def write_task(queue_root, task_id, run, assets, extra):
    pending = os.path.join(queue_root, PENDING)
    os.makedirs(pending, exist_ok=True)
    task = {"Id": task_id, "RunName": run, "Assets": assets, "ExtraArgs": extra}
    tmp = os.path.join(pending, task_id + ".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(task, f)
    os.replace(tmp, os.path.join(pending, task_id + ".json"))


def withdraw(queue_root, task_id):
    """Removes a task the editor has not claimed yet. True when it was still pending."""
    try:
        os.remove(os.path.join(queue_root, PENDING, task_id + ".json"))
        return True
    except FileNotFoundError:
        return False


def run_queue(args, queue_root, run, assets, extra, alive):
    task_id = uuid.uuid4().hex
    try:
        return wait_queue(args, queue_root, task_id, run, assets, extra, alive)
    except KeyboardInterrupt:
        done_path = os.path.join(queue_root, DONE, task_id + ".json")
        if withdraw(queue_root, task_id):
            log("interrupted, task withdrawn before the editor took it")
        else:
            log("interrupted, but the editor already took the task and finishes it. Its result will land in " + done_path)
        return EXIT_FAILED


def wait_queue(args, queue_root, task_id, run, assets, extra, alive):
    log("route: editor (pid %s, %s)" % (alive.get("Pid"), alive.get("State")))
    write_task(queue_root, task_id, run, assets, extra)
    done_path = os.path.join(queue_root, DONE, task_id + ".json")
    processing_path = os.path.join(queue_root, PROCESSING, task_id + ".json")
    deadline = time.time() + args.timeout
    last_state = alive.get("State")
    pid = int(alive.get("Pid", 0))
    next_check = 0.0
    warned_stale = False
    while True:
        if os.path.exists(done_path):
            result = None
            for _ in range(20):
                try:
                    with open(done_path, encoding="utf-8") as f:
                        result = json.load(f)
                    break
                except (OSError, ValueError):
                    time.sleep(0.1)
            if result is None:
                log("error: unreadable result " + done_path)
                return EXIT_FAILED
            print_result(result, args.quiet)
            if args.json:
                print(json.dumps(result, indent=2))
            # Removed only once printed, so an interrupted print leaves the result on disk.
            try:
                os.remove(done_path)
            except OSError:
                pass
            return exit_code_of(result)

        now = time.time()
        if now > deadline:
            if withdraw(queue_root, task_id):
                log("error: timed out after %ds, the editor never picked the task up (state: %s). Task withdrawn" % (args.timeout, last_state))
            else:
                log("error: timed out after %ds, but the editor is still running the task and may still write its targets. "
                    "Its result will land in %s" % (args.timeout, done_path))
            return EXIT_FAILED

        if now >= next_check:
            next_check = now + 1.0
            current = read_alive(queue_root)
            state = current.get("State") if current else None
            if state != last_state and state in BUSY_STATES | {"paused"}:
                log("editor is %s, waiting" % state)
            last_state = state or last_state
            age = current.get("_age", 0) if current else 0
            if age > HEARTBEAT_FRESHNESS_SEC and state != "busy" and not warned_stale:
                warned_stale = True
                log("the editor's heartbeat is %.0fs old: a modal dialog, a long import, or Unity throttled in the background" % age)
            if state == "paused" and os.path.exists(os.path.join(queue_root, PENDING, task_id + ".json")):
                withdraw(queue_root, task_id)
                log("error: the editor's queue is paused (Window > DotAsset > Accept tasks). Task withdrawn")
                return EXIT_REFUSED
            if not is_unity_process(pid):
                if withdraw(queue_root, task_id):
                    log("the editor exited before taking the task, running headless instead")
                    return None
                if os.path.exists(processing_path):
                    log("error: the editor exited while running the task. Check its targets, a write may have reached disk")
                    return EXIT_FAILED
        time.sleep(0.2)


# ---------------------------------------------------------------- headless route

LOG_HINTS = [
    (r"Scripts have compiler errors|error CS\d+", "the project does not compile, fix the errors above (the Unity log has them) and rerun"),
    (r"another Unity instance is running|Multiple Unity instances cannot open the same project", "another Unity instance has the project open"),
    (r"No valid Unity Editor license|Couldn't find a valid license|License is not active", "Unity has no active license for batch mode, open the Hub once and sign in"),
    (r"executeMethod class '?DotAsset.Cli'? could not be found|Could not find method DotAsset", "the DotAsset package is not in this project, or did not compile"),
    (r"Aborting batchmode due to failure", "Unity aborted batch mode, see the Unity log"),
]


def explain_unity_log(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        log("no Unity log at " + path)
        return
    hints = [hint for pattern, hint in LOG_HINTS if re.search(pattern, text)]
    errors = [l for l in text.splitlines() if re.search(r"error CS\d+", l)]
    for line in errors[:15]:
        print(line, flush=True)
    for hint in hints:
        log("hint: " + hint)
    if not hints and not errors:
        print("\n".join(text.splitlines()[-30:]), flush=True)
    log("Unity log: " + path)


def kill_tree(proc):
    try:
        if sys.platform == "win32":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
        else:
            proc.kill()
    except OSError:
        pass
    # Wait for it to let go of the project and the log, so a run right after does not find them held.
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        log("warning: Unity (pid %d) is still exiting" % proc.pid)


def b64(text):
    return base64.b64encode(text.encode("utf-8")).decode("ascii")


def scene_setup_path(project):
    return os.path.join(project, "Library", "LastSceneManagerSetup.txt")


def read_scene_setup(project):
    try:
        with open(scene_setup_path(project), "rb") as f:
            return f.read()
    except OSError:
        return None


def restore_scene_setup(project, saved):
    """Batch Unity rewrites the scenes the editor reopens with the ones it had loaded on quit, none,
    so the next editor start would show an Untitled scene. Put the user's back."""
    if saved is None or read_scene_setup(project) == saved:
        return
    try:
        with open(scene_setup_path(project), "wb") as f:
            f.write(saved)
    except OSError as e:
        log("warning: could not restore %s: %s" % (scene_setup_path(project), e))


def run_batch(args, project, queue_root, run, assets, extra):
    lock = BatchLock(queue_root)
    if not lock.try_acquire():
        log("another DotAsset headless run has the project, waiting for it to finish (up to %ds)" % args.timeout)
        deadline = time.time() + args.timeout
        while not lock.try_acquire():
            if time.time() > deadline:
                log("error: the other headless run did not finish within %ds" % args.timeout)
                return EXIT_REFUSED
            time.sleep(0.5)
    try:
        # The previous run's Unity may still be letting go of the project, or someone opened an editor meanwhile.
        deadline = time.time() + 60
        while project_locked(project) and live_editor(queue_root) is None and time.time() < deadline:
            time.sleep(0.5)
        if project_locked(project):
            if live_editor(queue_root) is not None:
                log("error: an editor opened the project while this run waited, run it again to use the editor")
            else:
                log("error: another Unity holds the project (Temp/UnityLockfile) and it is not a DotAsset run. Close it and retry")
            return EXIT_REFUSED
        saved = read_scene_setup(project)
        try:
            return run_batch_unity(args, project, queue_root, run, assets, extra)
        finally:
            restore_scene_setup(project, saved)
    finally:
        lock.release()


def run_batch_unity(args, project, queue_root, run, assets, extra):
    unity = find_unity(args.unity, project)
    if not unity:
        version = project_unity_version(project) or "?"
        log("error: no Unity %s found. Pass --unity <path to the editor executable> or set DOTASSET_UNITY" % version)
        return EXIT_FAILED
    task_id = uuid.uuid4().hex
    batch_dir = os.path.join(queue_root, "batch")
    os.makedirs(batch_dir, exist_ok=True)
    sweep_logs(batch_dir)
    result_path = os.path.join(batch_dir, task_id + ".result.json")
    unity_log = os.path.join(batch_dir, task_id + ".unity.log")
    cmd = [unity, "-batchmode"]
    if not args.graphics:
        cmd.append("-nographics")
    cmd += ["-projectPath", project, "-executeMethod", "DotAsset.Cli.Run",
            "-dotassetRun", run, "-dotassetResult", result_path, "-logFile", unity_log]
    if assets:
        cmd += ["-dotassetAssetsB64", b64(",".join(assets))]
    if extra:
        cmd += ["-dotassetArgsB64", b64(extra)]
    log("route: batchmode (%s)" % unity)
    started = time.time()
    try:
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **hidden_console())
    except OSError as e:
        log("error: cannot start Unity: %s" % e)
        return EXIT_FAILED
    try:
        code = proc.wait(timeout=args.timeout)
    except KeyboardInterrupt:
        kill_tree(proc)
        log("interrupted, Unity was stopped. A target it was writing may be half saved, check the Unity log: " + unity_log)
        return EXIT_FAILED
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        log("error: Unity did not finish within %ds and was stopped" % args.timeout)
        explain_unity_log(unity_log)
        return EXIT_FAILED
    elapsed = time.time() - started
    if not os.path.exists(result_path):
        log("error: Unity exited with %s after %.0fs without running %s" % (code, elapsed, run))
        explain_unity_log(unity_log)
        # Unity's own exit code (a crash, a license failure) is not one of ours, report it as a failure.
        return EXIT_FAILED
    try:
        with open(result_path, encoding="utf-8") as f:
            result = json.load(f)
    except (OSError, ValueError) as e:
        log("error: unreadable result %s: %s" % (result_path, e))
        return EXIT_FAILED
    print_result(result, args.quiet)
    if args.json:
        print(json.dumps(result, indent=2))
    try:
        os.remove(result_path)
    except OSError:
        pass
    if exit_code_of(result) in (EXIT_SUCCESS, EXIT_ISSUES):
        remove_log(batch_dir, unity_log)
    else:
        log("Unity log: " + unity_log)
    return exit_code_of(result)


LOG_SWEEP_LIST = "logs-to-delete.txt"
FAILED_LOG_DAYS = 7


def remove_log(batch_dir, unity_log):
    """The log of a run that worked is not kept. On Windows Unity's licensing client inherits the open log and
    outlives Unity, so the file can stay locked for minutes: it is then listed for the next run to sweep."""
    try:
        os.remove(unity_log)
    except FileNotFoundError:
        pass
    except OSError:
        try:
            with open(os.path.join(batch_dir, LOG_SWEEP_LIST), "a", encoding="utf-8") as f:
                f.write(os.path.basename(unity_log) + "\n")
        except OSError:
            pass


def sweep_logs(batch_dir, now=None):
    """Deletes the logs earlier runs could not, and failed runs' logs older than FAILED_LOG_DAYS."""
    now = time.time() if now is None else now
    listed = os.path.join(batch_dir, LOG_SWEEP_LIST)
    pending = []
    try:
        with open(listed, encoding="utf-8") as f:
            pending = [n.strip() for n in f if n.strip()]
    except OSError:
        pass
    still = []
    for name in pending:
        try:
            os.remove(os.path.join(batch_dir, os.path.basename(name)))
        except FileNotFoundError:
            pass
        except OSError:
            still.append(name)
    try:
        if still:
            with open(listed, "w", encoding="utf-8") as f:
                f.write("".join(n + "\n" for n in still))
        elif pending:
            os.remove(listed)
    except OSError:
        pass
    try:
        names = os.listdir(batch_dir)
    except OSError:
        return
    for name in names:
        path = os.path.join(batch_dir, name)
        if name.endswith(".unity.log") and name not in still:
            try:
                if now - os.path.getmtime(path) > FAILED_LOG_DAYS * 86400:
                    os.remove(path)
            except OSError:
                pass


# ---------------------------------------------------------------- main

def route(args, project, run, assets, extra):
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    if args.route in ("auto", "editor"):
        alive = live_editor(queue_root)
        if alive is None and project_locked(project) and batch_run_active(queue_root):
            # A headless DotAsset run holds the project, not a starting editor. run_batch waits its turn.
            if args.route == "editor":
                log("error: --route editor, but the project is held by a headless DotAsset run, not an editor")
                return EXIT_REFUSED
            return run_batch(args, project, queue_root, run, assets, extra)
        if alive is None and project_locked(project):
            log("Unity has the project open but its task queue is not up yet, waiting up to %ds" % args.startup_wait)
            deadline = time.time() + args.startup_wait
            while time.time() < deadline:
                time.sleep(1.0)
                alive = live_editor(queue_root)
                if alive or not project_locked(project):
                    break
            if alive is None and project_locked(project):
                log("error: the editor holds the project and its task queue never came up. Is the DotAsset package installed and compiling? Check the Console")
                return EXIT_REFUSED
        if alive is not None:
            if alive.get("State") == "paused":
                log("error: the editor's queue is paused (Window > DotAsset > Accept tasks)")
                return EXIT_REFUSED
            code = run_queue(args, queue_root, run, assets, extra, alive)
            if code is not None:
                return code
        elif args.route == "editor":
            log("error: --route editor, but no editor has this project open")
            return EXIT_REFUSED
    return run_batch(args, project, queue_root, run, assets, extra)


def status(project):
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    alive = read_alive(queue_root)
    if alive and is_unity_process(int(alive.get("Pid", 0))):
        pending = len([f for f in os.listdir(os.path.join(queue_root, PENDING)) if f.endswith(".json")]) if os.path.isdir(os.path.join(queue_root, PENDING)) else 0
        log("%s pid %s, state %s, heartbeat %.0fs ago, Unity %s, DotAsset %s, %d pending" % (
            "host" if alive.get("Served") else "editor", alive.get("Pid"), alive.get("State"), alive.get("_age", 0), alive.get("UnityVersion"), alive.get("DotAssetVersion"), pending))
        return EXIT_SUCCESS
    if project_locked(project):
        log("Unity has the project open, no task queue heartbeat")
        return EXIT_SUCCESS
    log("no editor has this project open, runs go headless")
    return EXIT_SUCCESS


def hidden_console():
    """Popen arguments that keep batchmode Unity's console hidden. Unity 6 opens a console window in batchmode when
    started without one, and that window takes the focus from whatever the user is typing in. Windows only."""
    if sys.platform != "win32":
        return {}
    info = subprocess.STARTUPINFO()
    info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    info.wShowWindow = 0  # SW_HIDE
    return {"startupinfo": info}


def start_detached(cmd, visible=True, hidden=False):
    """Starts the editor so it outlives this script and the terminal that ran it. Not visible: it starts minimized
    and never takes the focus. Hidden: for the batchmode host, whose windows Unity 6 activates even when minimized."""
    kwargs = {"stdin": subprocess.DEVNULL, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL, "close_fds": True}
    if sys.platform == "win32":
        kwargs["creationflags"] = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        if hidden:
            kwargs.update(hidden_console())
        elif not visible:
            info = subprocess.STARTUPINFO()
            info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            info.wShowWindow = 7  # SW_SHOWMINNOACTIVE
            kwargs["startupinfo"] = info
    else:
        kwargs["start_new_session"] = True
    return subprocess.Popen(cmd, **kwargs)


def minimize_windows(pid):
    """Minimizes the visible top-level windows of a process without activating them. Unity opens its splash and
    main window itself and ignores the start flags, so the launch wait calls this every second. Windows only."""
    if sys.platform != "win32":
        return
    import ctypes
    from ctypes import wintypes
    user32 = ctypes.windll.user32

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def visit(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd) and not user32.IsIconic(hwnd):
            user32.ShowWindow(hwnd, 7)  # SW_SHOWMINNOACTIVE
        return True

    user32.EnumWindows(visit, 0)


def launch(args, project, serve=False):
    """Opens the project in the editor, or finds it already open, and waits until its queue is ready. Holds the
    headless lock meanwhile: a headless run waiting its turn must not start between the launch and the heartbeat.
    With serve, starts a warm headless host instead of the editor: batchmode Unity serving the same queue."""
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    lock = BatchLock(queue_root)
    if not lock.try_acquire():
        log("a DotAsset headless run has the project, waiting for it to finish (up to %ds)" % args.timeout)
        deadline = time.time() + args.timeout
        while not lock.try_acquire():
            if time.time() > deadline:
                log("error: the headless run did not finish within %ds" % args.timeout)
                return EXIT_REFUSED
            time.sleep(0.5)
        # Its Unity may still be letting go of the project.
        deadline = time.time() + 60
        while project_locked(project) and live_editor(queue_root) is None and time.time() < deadline:
            time.sleep(0.5)
    try:
        return launch_locked(args, project, queue_root, serve)
    finally:
        lock.release()


def launch_locked(args, project, queue_root, serve=False):
    proc = None
    what = "host" if serve else "editor"
    if live_editor(queue_root) is None and not project_locked(project):
        unity = find_unity(args.unity, project)
        if not unity:
            log("error: no Unity %s found. Pass --unity <path to the editor executable> or set DOTASSET_UNITY" % (project_unity_version(project) or "?"))
            return EXIT_FAILED
        if serve:
            os.makedirs(os.path.join(queue_root, "batch"), exist_ok=True)
            keep_scene_setup(project, queue_root)
            cmd = [unity, "-batchmode", "-nographics", "-projectPath", project, "-executeMethod", "DotAsset.Cli.Serve",
                   "-logFile", os.path.join(queue_root, "batch", "serve.unity.log")]
        else:
            cmd = [unity, "-projectPath", project]
        try:
            proc = start_detached(cmd, args.visible and not serve, serve)
        except OSError as e:
            log("error: cannot start Unity: %s" % e)
            return EXIT_FAILED
        log("launched %s (pid %d), waiting up to %ds for the %s to take tasks" % (unity, proc.pid, args.startup_wait, what))
    else:
        log("Unity already has the project open, waiting for it to take tasks")
    deadline = time.time() + args.startup_wait
    last_state = None
    while time.time() < deadline:
        alive = live_editor(queue_root)
        state = alive.get("State") if alive else None
        if state and state != last_state:
            log("%s state: %s" % (what, state))
            last_state = state
        if state == "ready":
            log("the %s is ready (pid %s)" % (what, alive.get("Pid")))
            return EXIT_SUCCESS
        if state == "paused":
            log("error: the editor is open but its queue is paused (Window > DotAsset > Accept tasks)")
            return EXIT_REFUSED
        if proc is not None and alive is None and proc.poll() is not None:
            log("error: Unity exited with %s before its task queue came up. Check %s" % (proc.returncode, editor_log_hint()))
            return EXIT_FAILED
        # A tenth of a second between window checks: a window Unity opens takes the focus until it is minimized.
        for _ in range(10):
            if proc is not None and not args.visible and not serve:
                minimize_windows(proc.pid)
            time.sleep(0.1)
    log("error: the editor did not take tasks within %ds (last state: %s). Compile errors, a Safe Mode prompt or a license "
        "dialog can hold it, check %s" % (args.startup_wait, last_state or "no heartbeat", editor_log_hint()))
    return EXIT_FAILED


def served_scene_copy(queue_root):
    return os.path.join(queue_root, "batch", "serve.scenes.txt")


def keep_scene_setup(project, queue_root):
    # A --serve host empties the scene setup on quit like any batch run, keep it for quit_editor
    saved = read_scene_setup(project)
    if saved is not None:
        with open(served_scene_copy(queue_root), "wb") as f:
            f.write(saved)


def restore_served_scene_setup(project, queue_root):
    copy = served_scene_copy(queue_root)
    try:
        with open(copy, "rb") as f:
            saved = f.read()
    except OSError:
        return
    restore_scene_setup(project, saved)
    os.remove(copy)


def editor_log_hint():
    if sys.platform == "win32":
        return os.path.join(os.environ.get("LOCALAPPDATA", ""), "Unity", "Editor", "Editor.log") + " and <Project>/Logs/Editor.log"
    if sys.platform == "darwin":
        return os.path.expanduser("~/Library/Logs/Unity/Editor.log")
    return os.path.expanduser("~/.config/unity3d/Editor.log")


def open_scene(args, project):
    """Asks the editor that has the project open to open scenes: the first replaces what is open, the rest are
    added. Prints the scenes that were open before as a last line, 'previously open: a.unity,b.unity'."""
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    scenes = [s.strip().replace("\\", "/") for s in args.open_scene.split(",") if s.strip()]
    if not scenes:
        log("error: --open-scene needs scene paths, e.g. --open-scene Assets/Scenes/Main.unity")
        return EXIT_REFUSED
    alive = live_editor(queue_root)
    if alive is None:
        log("error: no editor has this project open, --launch it first")
        return EXIT_REFUSED
    if alive.get("State") == "paused":
        log("error: the editor's queue is paused (Window > DotAsset > Accept tasks), it cannot take the request")
        return EXIT_REFUSED
    request = os.path.join(queue_root, OPEN_SCENE_REQUEST)
    answer = os.path.join(queue_root, OPEN_SCENE_RESULT)
    try:
        os.remove(answer)
    except FileNotFoundError:
        pass
    with open(request + ".tmp", "w", encoding="utf-8") as f:
        json.dump({"Scenes": scenes, "Force": bool(args.force)}, f)
    os.replace(request + ".tmp", request)
    log("asked the editor to open %s%s" % (", ".join(scenes), ", discarding unsaved changes" if args.force else ""))
    deadline = time.time() + args.timeout
    result = None
    pid = int(alive.get("Pid", 0))
    while time.time() < deadline and result is None:
        if os.path.exists(answer):
            try:
                with open(answer, encoding="utf-8") as f:
                    result = json.load(f)
            except (OSError, ValueError):
                time.sleep(0.1)
                continue
        elif not is_unity_process(pid):
            break
        else:
            time.sleep(0.25)
    if result is None:
        try:
            os.remove(request)
        except FileNotFoundError:
            pass
        log("error: the editor did not answer within %ds. Request withdrawn" % args.timeout)
        return EXIT_FAILED
    try:
        os.remove(answer)
    except FileNotFoundError:
        pass
    if args.json:
        print(json.dumps(result, indent=2))
    previous = result.get("Previous") or []
    if not result.get("Opened"):
        log("error: the editor did not open the scene: " + (result.get("Error") or "no reason given"))
        return EXIT_REFUSED
    log("opened %s" % ", ".join(scenes))
    log("previously open: %s" % ",".join(previous))
    return EXIT_SUCCESS


def quit_editor(args, project):
    """Asks the editor that has the project open to close, and waits until the project is free."""
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    alive = live_editor(queue_root)
    if alive is None:
        if project_locked(project):
            log("error: Unity has the project open but its task queue is not up, close it by hand")
            return EXIT_REFUSED
        log("no editor has this project open")
        return EXIT_SUCCESS
    if alive.get("State") == "paused":
        log("error: the editor's queue is paused (Window > DotAsset > Accept tasks), it cannot take the quit request")
        return EXIT_REFUSED
    pid = int(alive.get("Pid", 0))
    request = os.path.join(queue_root, QUIT_REQUEST)
    answer = os.path.join(queue_root, QUIT_RESULT)
    try:
        os.remove(answer)
    except FileNotFoundError:
        pass
    with open(request + ".tmp", "w", encoding="utf-8") as f:
        json.dump({"Force": bool(args.force)}, f)
    os.replace(request + ".tmp", request)
    log("asked the editor (pid %d) to close%s" % (pid, ", discarding unsaved changes" if args.force else ""))

    deadline = time.time() + args.timeout
    result = None
    while time.time() < deadline and result is None:
        if os.path.exists(answer):
            try:
                with open(answer, encoding="utf-8") as f:
                    result = json.load(f)
            except (OSError, ValueError):
                time.sleep(0.1)
                continue
        elif not is_unity_process(pid):
            result = {"Quit": True}
        else:
            time.sleep(0.25)
    if result is None:
        try:
            os.remove(request)
        except FileNotFoundError:
            pass
        state = (live_editor(queue_root) or {}).get("State")
        log("error: the editor did not answer within %ds (state: %s). Request withdrawn" % (args.timeout, state))
        return EXIT_FAILED
    try:
        os.remove(answer)
    except FileNotFoundError:
        pass
    if not result.get("Quit"):
        log("error: the editor refused to close: " + (result.get("Error") or "no reason given"))
        return EXIT_REFUSED

    while time.time() < deadline:
        if not is_unity_process(pid) and not project_locked(project):
            if alive.get("Served"):
                restore_served_scene_setup(project, queue_root)
            log("the editor closed, the project is free")
            return EXIT_SUCCESS
        time.sleep(0.5)
    log("error: the editor agreed to close but is still running after %ds" % args.timeout)
    return EXIT_FAILED


def main(argv=None):
    # Asset names can hold characters a Windows console code page cannot print, never fail on those.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(errors="replace")
            except (ValueError, OSError):
                pass
    p = argparse.ArgumentParser(prog="dotasset.py", description="DotAsset: run export, import, edit, migrate and audit tasks on a Unity project.")
    p.add_argument("run", nargs="?", help="run name, e.g. PrefabExport")
    p.add_argument("assets", nargs="?", default="", help="comma-separated asset paths or folders")
    p.add_argument("--args", dest="extra", default="", help="extra arguments for the run, e.g. '-spec=\"C:/t/x.json\" -apply'")
    p.add_argument("--project", default=os.environ.get("DOTASSET_PROJECT"), help="Unity project folder (default: the one containing the current directory)")
    p.add_argument("--unity", default=None, help="Unity editor executable for the headless route")
    p.add_argument("--timeout", type=int, default=600, help="seconds before giving up (default 600)")
    p.add_argument("--startup-wait", type=int, default=300, help="seconds to wait for a starting editor's queue (default 300)")
    p.add_argument("--route", choices=["auto", "editor", "batch"], default="auto")
    p.add_argument("--graphics", action="store_true", help="headless route without -nographics")
    p.add_argument("--quiet", action="store_true", help="print only warnings, errors and the summary")
    p.add_argument("--json", action="store_true", help="also print the full result JSON")
    p.add_argument("--agent", action="store_true", help="compact output for AI agents: exit code, summary, first error, warnings, failed targets, outputs, plan")
    p.add_argument("--list", action="store_true", help="list every run the project knows")
    p.add_argument("--status", action="store_true", help="show what the editor's queue is doing")
    p.add_argument("--launch", action="store_true", help="open the project in the editor and wait until it takes tasks")
    p.add_argument("--visible", action="store_true", help="with --launch: show the editor window, by default it starts minimized and never takes the focus")
    p.add_argument("--serve", action="store_true", help="start a warm headless host: batchmode Unity that stays up and takes runs like an open editor, until --quit")
    p.add_argument("--quit", action="store_true", help="close the editor or headless host that has the project open")
    p.add_argument("--open-scene", help="open these scenes (comma-separated) in the editor that has the project open")
    p.add_argument("--force", action="store_true", help="with --quit or --open-scene: go ahead even when a scene has unsaved changes")
    argv = list(sys.argv[1:] if argv is None else argv)
    # "--args -apply" would make argparse read -apply as an option of its own. Glue the value on.
    for i in range(len(argv) - 1):
        if argv[i] == "--args":
            argv[i:i + 2] = ["--args=" + argv[i + 1]]
            break
    args = p.parse_args(argv)
    global AGENT
    AGENT = args.agent

    project = os.path.abspath(args.project) if args.project else find_project(os.getcwd())
    if not project or not os.path.isdir(os.path.join(project, "Assets")):
        log("error: no Unity project found, pass --project <folder with Assets/ and ProjectSettings/>")
        return EXIT_REFUSED

    if args.status:
        return status(project)
    if args.launch:
        return launch(args, project)
    if args.serve:
        return launch(args, project, serve=True)
    if args.quit:
        return quit_editor(args, project)
    if args.open_scene is not None:
        return open_scene(args, project)
    if args.force:
        log("error: --force only goes with --quit or --open-scene")
        return EXIT_REFUSED
    if args.visible:
        log("error: --visible only goes with --launch")
        return EXIT_REFUSED
    run = args.run
    if args.list:
        run = "CommandCatalogExport"
    if not run:
        p.print_usage()
        return EXIT_REFUSED
    if re.search(r"(^|\s)-assets=", args.extra):
        log("error: entry assets go in the AssetList argument, not in --args")
        return EXIT_REFUSED
    assets = [a.strip().replace("\\", "/") for a in args.assets.split(",") if a.strip()]
    return route(args, project, run, assets, args.extra)


if __name__ == "__main__":
    sys.exit(main())
