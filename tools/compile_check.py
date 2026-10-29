#!/usr/bin/env python3
"""Compiles Package/Editor with the exact compiler, references and defines each test project's Unity uses.

A Unity project records how it compiled DotAsset.Editor in Library/Bee/1900b0aE.dag.json and its .rsp files.
This reuses that command with the current sources (new files included) and C# 7.3, writes the dll to a
temporary folder, and touches nothing in the project. Each project must have opened once with the package.
Two more passes swap Unity's base class library for .NET Standard 2.1 and for Microsoft's .NET Framework 4.8
reference assemblies, which refuse what Unity's own profile lets through, e.g. string.Contains(char).
Warnings about obsolete APIs in the package fail the check, as the code rules forbid them.
Usage: python tools/compile_check.py [project ...]   (default: every ../DotAssetTest-* that uses this package)
"""
import glob
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EDITOR = os.path.join(REPO, "Package", "Editor")
DAG = os.path.join("Library", "Bee", "1900b0aE.dag.json")
NET48 = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
                     "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8")
OBSOLETE = ("CS0612", "CS0618", "CS0619", "CS0672")
DIAGNOSTIC = re.compile(r"^(.*?)\((\d+),(\d+)\): (error|warning) (CS\d+|\w+): (.*)$")


def sources():
    found = []
    for dirpath, dirnames, filenames in os.walk(EDITOR):
        dirnames[:] = sorted(d for d in dirnames if not d.endswith("~") and not d.startswith("."))
        found += [os.path.join(dirpath, f).replace(os.sep, "/") for f in sorted(filenames) if f.endswith(".cs")]
    return found


def default_projects():
    """Sibling DotAssetTest-* projects that install this package, one per Unity version."""
    seen, picked = set(), []
    for project in sorted(glob.glob(os.path.join(os.path.dirname(REPO), "DotAssetTest-*"))):
        manifest = os.path.join(project, "Packages", "manifest.json")
        if not os.path.isfile(manifest) or "DotAsset/Package" not in open(manifest, encoding="utf-8").read():
            continue
        version = unity_version(project)
        if version not in seen:
            seen.add(version)
            picked.append(project)
    return picked


def unity_version(project):
    try:
        with open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), encoding="utf-8") as f:
            return f.readline().split(":", 1)[1].strip()
    except (OSError, IndexError):
        return "?"


def compile_command(project):
    """The csc argv Unity runs for DotAsset.Editor, with the .rsp paths it names. None when not found."""
    path = os.path.join(project, DAG)
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        dag = json.load(f)
    for n in dag.get("Nodes", []):
        action = n.get("Action", "")
        if "DotAsset.Editor.rsp" in action and "csc" in action:
            return shlex.split(action, posix=False)
    return None


def swap_framework(lines, profile):
    """Unity's 4.8 API profile accepts more than .NET Framework 4.8 or .NET Standard 2.1 do, string.Contains(char)
    for one. These replace its base class references with Microsoft's 4.8 reference assemblies, or with
    netstandard.dll and the shims that let Unity's own dlls resolve against it."""
    if profile == "unity":
        return lines
    kept, unity_api = [], None
    for l in lines:
        m = re.match(r'^-r:"(.*/UnityReferenceAssemblies/unity-4\.8-api/)(.*)"$', l)
        if not m:
            kept.append(l)
            continue
        unity_api = m.group(1)
        if profile == "net48":
            ref = os.path.join(NET48, *m.group(2).split("/"))
            if m.group(2) == "Facades/netstandard.dll":
                kept.append(l)  # Unity's dlls need netstandard 2.1, its facade only forwards to the 4.8 types
            elif os.path.isfile(ref):
                kept.append('-r:"%s"' % ref)
    if unity_api and profile == "netstandard2.1":
        data = os.path.dirname(os.path.dirname(os.path.dirname(unity_api)))
        kept.append('-r:"%s/NetStandard/ref/2.1.0/netstandard.dll"' % data)
        kept += ['-r:"%s"' % s.replace(os.sep, "/") for s in
                 sorted(glob.glob(os.path.join(data, "NetStandard", "compat", "2.1.0", "shims", "netfx", "*.dll")))]
    return kept


def check(project, profile="unity"):
    """(project, version, profile, problems, warnings) for one project."""
    version = unity_version(project)
    argv = compile_command(project)
    if not argv:
        return project, version, profile, ["no DotAsset.Editor compile recorded in %s, open the project once" % DAG], []
    out = tempfile.mkdtemp(prefix="dotasset_compile_")
    rsp = os.path.join(out, "DotAsset.Editor.rsp")
    command = []
    for a in argv:
        a = a.strip('"')
        if a == "/shared":
            continue  # the compiler server outlives the check
        if a.startswith("@") and a.endswith("DotAsset.Editor.rsp"):
            with open(os.path.join(project, a[1:]), encoding="utf-8-sig") as f:
                lines = [l.strip() for l in f if l.strip()]
            keep = swap_framework([l for l in lines if not l.startswith(("-out:", "-refout:", "-langversion:"))
                                   and not (l.startswith('"') and l.endswith('.cs"'))], profile)
            keep += ['-out:"%s"' % os.path.join(out, "DotAsset.Editor.dll"), "-langversion:7.3"]
            keep += ['"%s"' % s for s in sources()]
            with open(rsp, "w", encoding="utf-8") as f:
                f.write("\n".join(keep) + "\n")
            command.append("@" + rsp)
        else:
            command.append(a)
    r = subprocess.run(command, cwd=project, capture_output=True, text=True, encoding="utf-8", errors="replace")
    problems, warnings = [], []
    editor = os.path.normcase(os.path.normpath(EDITOR))
    for line in (r.stdout + r.stderr).splitlines():
        m = DIAGNOSTIC.match(line.strip())
        if not m:
            continue
        where = os.path.normcase(os.path.normpath(m.group(1)))
        text = "%s(%s): %s %s: %s" % (os.path.relpath(m.group(1), REPO).replace(os.sep, "/") if where.startswith(editor)
                                      else m.group(1), m.group(2), m.group(4), m.group(5), m.group(6))
        if m.group(4) == "error" or (where.startswith(editor) and m.group(5) in OBSOLETE):
            problems.append(text)
        elif where.startswith(editor):
            warnings.append(text)
    if r.returncode != 0 and not problems:
        problems.append("csc exit %d\n%s" % (r.returncode, (r.stdout + r.stderr).strip()[-2000:]))
    for name in os.listdir(out):
        os.remove(os.path.join(out, name))
    os.rmdir(out)
    return project, version, profile, sorted(set(problems)), sorted(set(warnings))


def main():
    projects = sys.argv[1:] or default_projects()
    if not projects:
        print("no test projects found, name them: python tools/compile_check.py <project> ...")
        return 2
    # Every Unity with its own profile, then the stricter base class libraries: .NET Standard 2.1 on the newest,
    # .NET Framework 4.8 on the oldest (newer Unity APIs add Span overloads, Microsoft's 4.8 set has no Span).
    by_version = sorted(projects, key=lambda p: [int(x) for x in re.findall(r"\d+", unity_version(p))[:3]] or [0])
    jobs = [(p, "unity") for p in projects] + [(by_version[-1], "netstandard2.1")]
    if os.path.isdir(NET48):
        jobs.append((by_version[0], "net48"))
    else:
        print("skip .NET Framework 4.8 pass, no reference assemblies at %s" % NET48)
    failed = 0
    with ThreadPoolExecutor(max_workers=len(jobs)) as pool:
        for project, version, profile, problems, warnings in pool.map(lambda j: check(*j), jobs):
            failed += 1 if problems else 0
            print("%-4s Unity %-12s %-15s %s" % ("FAIL" if problems else "ok", version,
                                                 "" if profile == "unity" else profile,
                                                 os.path.basename(os.path.normpath(project))))
            for p in problems:
                print("     " + p.replace("\n", "\n     "))
            for w in warnings:
                print("     " + w)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
