"""EditMode tests of the package's pure parts, run by Unity's Test Framework in a real test project.

  python tests/editmode/run_editmode.py --project ../DotAssetTest-Medium

Installs tests/editmode/package into the project as an embedded package, runs Unity headless with -runTests, prints
one line per failed test and removes the package again. The project must list com.unity.test-framework and must not
be open in an editor or a --serve host (dotasset.py --quit closes them). Exits 1 if a test failed or none ran.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE = os.path.join(HERE, "package")
PACKAGE_NAME = "com.alexmah.dotasset.tests"
sys.path.insert(0, os.path.join(HERE, "..", "..", "Package", "Scripts~"))
import dotasset  # noqa: E402


def install(project):
    target = os.path.join(project, "Packages", PACKAGE_NAME)
    shutil.rmtree(target, ignore_errors=True)
    shutil.copytree(PACKAGE, target)
    return target


def report(results):
    root = ET.parse(results).getroot()
    run = root if root.tag == "test-run" else root.find("test-run")
    total = int(run.get("total", "0"))
    passed = int(run.get("passed", "0"))
    failed = int(run.get("failed", "0"))
    skipped = int(run.get("skipped", "0"))
    for case in run.iter("test-case"):
        if case.get("result") == "Failed":
            message = case.findtext("failure/message") or ""
            print("FAIL %s\n     %s" % (case.get("fullname"), message.strip().replace("\n", "\n     ")))
    print("%d test(s): %d passed, %d failed, %d skipped" % (total, passed, failed, skipped))
    return total > 0 and failed == 0 and passed + skipped == total


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--project", required=True, help="test project path")
    parser.add_argument("--unity", help="Unity editor executable, found from the project's version by default")
    parser.add_argument("--timeout", type=int, default=1800, help="seconds before Unity is stopped")
    parser.add_argument("--keep", action="store_true", help="leave the test package installed after the run")
    args = parser.parse_args(argv)
    project = os.path.abspath(args.project)

    with open(os.path.join(project, "Packages", "manifest.json"), encoding="utf-8") as f:
        if '"com.unity.test-framework"' not in f.read():
            print("error: %s does not list com.unity.test-framework in Packages/manifest.json" % project)
            return 1
    if dotasset.project_locked(project):
        print("error: an editor or a --serve host holds %s, close it first (dotasset.py --project ... --quit)" % project)
        return 1
    unity = dotasset.find_unity(args.unity, project)
    if not unity:
        print("error: no Unity %s found, pass --unity" % (dotasset.project_unity_version(project) or "?"))
        return 1

    out = os.path.join(project, "Library", "DotAssetTests")
    os.makedirs(out, exist_ok=True)
    results = os.path.join(out, "results.xml")
    unity_log = os.path.join(out, "unity.log")
    if os.path.exists(results):
        os.remove(results)
    target = install(project)
    started = time.time()
    try:
        cmd = [unity, "-batchmode", "-nographics", "-projectPath", project, "-runTests", "-testPlatform", "EditMode",
               "-assemblyNames", "DotAsset.Tests", "-testResults", results, "-logFile", unity_log]
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **dotasset.hidden_console())
        try:
            code = proc.wait(timeout=args.timeout)
        except subprocess.TimeoutExpired:
            dotasset.kill_tree(proc)
            print("error: Unity did not finish within %ds, log: %s" % (args.timeout, unity_log))
            return 1
    finally:
        if not args.keep:
            shutil.rmtree(target, ignore_errors=True)
    print("Unity %s exited with %s after %.0fs" % (dotasset.project_unity_version(project), code, time.time() - started))
    if not os.path.exists(results):
        print("error: no test results, the tests did not compile or run. Log: " + unity_log)
        return 1
    return 0 if report(results) else 1


if __name__ == "__main__":
    sys.exit(main())
