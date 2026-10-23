#!/usr/bin/env python3
"""Asset Workbench wrapper. One command line, editor open or closed.

Routing:
  1. <Project>/Library/AssetWorkbench/Queue/.alive names a live Unity process: the editor has the project open
     and its task queue is running. The task goes to pending/, the result comes back from done/.
  2. Unity holds the project (Temp/UnityLockfile is locked) but no heartbeat is up yet: the editor is still
     starting, or the package has not compiled. Wait up to --startup-wait seconds for the heartbeat.
  3. Otherwise launch Unity headless: -batchmode -nographics -executeMethod AssetWorkbench.Cli.Run.
Both routes produce the same result JSON and the same printed output.

Usage:
  python uaw.py <RunName> [AssetList] [--args "<extra args>"] [--project DIR] [--unity PATH] [--timeout SEC]
  python uaw.py --list          every run the project knows, with its summary
  python uaw.py --status        what the editor's queue is doing

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
QUEUE_RELATIVE = os.path.join("Library", "AssetWorkbench", "Queue")
PENDING = "pending"
PROCESSING = "processing"
DONE = "done"
ALIVE = ".alive"
HEARTBEAT_FRESHNESS_SEC = 15.0

EXIT_SUCCESS = 0
EXIT_FAILED = 1
EXIT_REFUSED = 2
EXIT_ISSUES = 3

BUSY_STATES = {"busy", "compiling", "importing", "playmode", "reloading", "starting"}


def log(msg):
    print("[uaw] " + msg, flush=True)


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
    env = os.environ.get("UAW_UNITY")
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


def is_unity_process(pid):
    """A pid from a stale heartbeat can be reused by another program. Only a live Unity counts."""
    return pid_alive(pid) and "unity" in process_name(pid).lower()


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


def live_editor(queue_root):
    """The heartbeat of a running editor, or None."""
    alive = read_alive(queue_root)
    if not alive:
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
    for line in result.get("Lines") or []:
        level, _, text = line.partition(" ")
        if level == "E":
            print("error: " + text, flush=True)
        elif level == "W":
            print("warning: " + text, flush=True)
        elif not quiet:
            print(text, flush=True)
    for out in result.get("Outputs") or []:
        if not os.path.exists(out):
            print("missing output: " + out, flush=True)
    if result.get("Error"):
        log("error: " + result["Error"])
    log(result.get("Summary") or "no summary")
    if result.get("LogFile"):
        log("log: " + result["LogFile"])


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
            try:
                os.remove(done_path)
            except OSError:
                pass
            if result is None:
                log("error: unreadable result " + done_path)
                return EXIT_FAILED
            print_result(result, args.quiet)
            if args.json:
                print(json.dumps(result, indent=2))
            return exit_code_of(result)

        now = time.time()
        if now > deadline:
            if withdraw(queue_root, task_id):
                log("error: timed out after %ds, the editor never picked the task up (state: %s). Task withdrawn" % (args.timeout, last_state))
            else:
                log("error: timed out after %ds while the editor runs the task, its result will land in %s" % (args.timeout, done_path))
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
                log("error: the editor's queue is paused (Window > Asset Workbench > Accept tasks). Task withdrawn")
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
    (r"executeMethod class '?AssetWorkbench.Cli'? could not be found|Could not find method AssetWorkbench", "the Asset Workbench package is not in this project, or did not compile"),
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


def b64(text):
    return base64.b64encode(text.encode("utf-8")).decode("ascii")


def run_batch(args, project, queue_root, run, assets, extra):
    unity = find_unity(args.unity, project)
    if not unity:
        version = project_unity_version(project) or "?"
        log("error: no Unity %s found. Pass --unity <path to the editor executable> or set UAW_UNITY" % version)
        return EXIT_FAILED
    task_id = uuid.uuid4().hex
    batch_dir = os.path.join(queue_root, "batch")
    os.makedirs(batch_dir, exist_ok=True)
    result_path = os.path.join(batch_dir, task_id + ".result.json")
    unity_log = os.path.join(batch_dir, task_id + ".unity.log")
    cmd = [unity, "-batchmode"]
    if not args.graphics:
        cmd.append("-nographics")
    cmd += ["-projectPath", project, "-executeMethod", "AssetWorkbench.Cli.Run",
            "-uawRun", run, "-uawResult", result_path, "-logFile", unity_log]
    if assets:
        cmd += ["-uawAssetsB64", b64(",".join(assets))]
    if extra:
        cmd += ["-uawArgsB64", b64(extra)]
    log("route: batchmode (%s)" % unity)
    started = time.time()
    try:
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        log("error: cannot start Unity: %s" % e)
        return EXIT_FAILED
    try:
        code = proc.wait(timeout=args.timeout)
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        log("error: Unity did not finish within %ds and was stopped" % args.timeout)
        explain_unity_log(unity_log)
        return EXIT_FAILED
    elapsed = time.time() - started
    if not os.path.exists(result_path):
        log("error: Unity exited with %s after %.0fs without running %s" % (code, elapsed, run))
        explain_unity_log(unity_log)
        return code if code not in (0, None) else EXIT_FAILED
    with open(result_path, encoding="utf-8") as f:
        result = json.load(f)
    os.remove(result_path)
    print_result(result, args.quiet)
    if args.json:
        print(json.dumps(result, indent=2))
    try:
        if exit_code_of(result) in (EXIT_SUCCESS, EXIT_ISSUES):
            os.remove(unity_log)
        else:
            log("Unity log: " + unity_log)
    except OSError:
        pass
    return exit_code_of(result)


# ---------------------------------------------------------------- main

def route(args, project, run, assets, extra):
    queue_root = os.path.join(project, QUEUE_RELATIVE)
    if args.route in ("auto", "editor"):
        alive = live_editor(queue_root)
        if alive is None and project_locked(project):
            log("Unity has the project open but its task queue is not up yet, waiting up to %ds" % args.startup_wait)
            deadline = time.time() + args.startup_wait
            while time.time() < deadline:
                time.sleep(1.0)
                alive = live_editor(queue_root)
                if alive or not project_locked(project):
                    break
            if alive is None and project_locked(project):
                log("error: the editor holds the project and its task queue never came up. Is the Asset Workbench package installed and compiling? Check the Console")
                return EXIT_REFUSED
        if alive is not None:
            if alive.get("State") == "paused":
                log("error: the editor's queue is paused (Window > Asset Workbench > Accept tasks)")
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
        log("editor pid %s, state %s, heartbeat %.0fs ago, Unity %s, Asset Workbench %s, %d pending" % (
            alive.get("Pid"), alive.get("State"), alive.get("_age", 0), alive.get("UnityVersion"), alive.get("WorkbenchVersion"), pending))
        return EXIT_SUCCESS
    if project_locked(project):
        log("Unity has the project open, no task queue heartbeat")
        return EXIT_SUCCESS
    log("no editor has this project open, runs go headless")
    return EXIT_SUCCESS


def main(argv=None):
    p = argparse.ArgumentParser(prog="uaw.py", description="Asset Workbench: run export, import, edit, migrate and audit tasks on a Unity project.")
    p.add_argument("run", nargs="?", help="run name, e.g. PrefabExport")
    p.add_argument("assets", nargs="?", default="", help="comma-separated asset paths or folders")
    p.add_argument("--args", dest="extra", default="", help="extra arguments for the run, e.g. '-spec=\"C:/t/x.json\" -apply'")
    p.add_argument("--project", default=os.environ.get("UAW_PROJECT"), help="Unity project folder (default: the one containing the current directory)")
    p.add_argument("--unity", default=None, help="Unity editor executable for the headless route")
    p.add_argument("--timeout", type=int, default=600, help="seconds before giving up (default 600)")
    p.add_argument("--startup-wait", type=int, default=300, help="seconds to wait for a starting editor's queue (default 300)")
    p.add_argument("--route", choices=["auto", "editor", "batch"], default="auto")
    p.add_argument("--graphics", action="store_true", help="headless route without -nographics")
    p.add_argument("--quiet", action="store_true", help="print only warnings, errors and the summary")
    p.add_argument("--json", action="store_true", help="also print the full result JSON")
    p.add_argument("--list", action="store_true", help="list every run the project knows")
    p.add_argument("--status", action="store_true", help="show what the editor's queue is doing")
    argv = list(sys.argv[1:] if argv is None else argv)
    # "--args -apply" would make argparse read -apply as an option of its own. Glue the value on.
    for i in range(len(argv) - 1):
        if argv[i] == "--args":
            argv[i:i + 2] = ["--args=" + argv[i + 1]]
            break
    args = p.parse_args(argv)

    project = os.path.abspath(args.project) if args.project else find_project(os.getcwd())
    if not project or not os.path.isdir(os.path.join(project, "Assets")):
        log("error: no Unity project found, pass --project <folder with Assets/ and ProjectSettings/>")
        return EXIT_REFUSED

    if args.status:
        return status(project)
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
