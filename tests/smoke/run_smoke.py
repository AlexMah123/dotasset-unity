#!/usr/bin/env python3
"""End-to-end smoke test in a real Unity project. Exercises every group through assetops.py.

Creates everything under Assets/AssetOpsSmoke and deletes it at the end. Run it once with the editor closed
(headless route) and once with the editor open (queue route):

  python tests/smoke/run_smoke.py --project /path/to/UnityProject [--unity /path/to/Unity] [--keep]

With the editor open, save or close any Untitled scene first: Unity cannot open a scene next to one.
Prints one line per step and exits 1 if any step returned an unexpected exit code.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
WRAPPER = os.path.join(HERE, "..", "..", "Package", "Scripts~", "assetops.py")
ROOT = "Assets/AssetOpsSmoke"


def spec(name, data, tmp):
    path = os.path.join(tmp, name + ".json")
    with open(path, "w") as f:
        json.dump(data, f, indent=2)
    return path.replace("\\", "/")


def result_json(stdout):
    """The result JSON --json prints after the log."""
    start = stdout.rfind("\n{")
    return json.loads(stdout[start + 1:] if start >= 0 else stdout)


def export_of(run, base, asset):
    """Runs an export and returns the parsed export file, or raises with the run's output."""
    r = subprocess.run(base + ["--json", run, asset], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError("%s exit %s\n%s" % (run, r.returncode, r.stdout + r.stderr))
    outputs = result_json(r.stdout).get("Outputs") or []
    if not outputs:
        raise RuntimeError(run + " wrote no output")
    with open(outputs[-1], encoding="utf-8") as f:
        return json.load(f)


def node(export, path):
    return next((n for n in export.get("Hierarchy", []) if n.get("Path") == path), None)


def check_dry_run_left_prefab(base, tmp):
    # The dry run of edit_prefab adds Crate/Lid and removes the BoxCollider, none of which may reach disk.
    exp = export_of("PrefabExport", base, ROOT + "/Crate.prefab")
    if node(exp, "Crate/Lid") is not None:
        return "the dry run saved Crate/Lid"
    types = [c.get("Type") for c in node(exp, "Crate").get("Components", [])]
    if "BoxCollider" not in types:
        return "the dry run removed the BoxCollider"
    return None


def check_round_trip(base, tmp):
    # Every exported Properties block must write back as is, and leave the export unchanged.
    before = export_of("PrefabExport", base, ROOT + "/Crate.prefab")
    ops = []
    for n in before.get("Hierarchy", []):
        if "PrefabInstance" in n:
            continue
        for c in n.get("Components", []):
            if c.get("Properties"):
                ops.append({"Op": "Set", "Path": n["Path"], "Component": c["Type"], "Properties": c["Properties"]})
    if not ops:
        return "the export has no properties to write back"
    path = spec("round_trip", {"Targets": [{"AssetPath": ROOT + "/Crate.prefab", "Ops": ops}]}, tmp)
    r = subprocess.run(base + ["EditPrefab", "--args", '-spec="%s" -apply' % path], capture_output=True, text=True)
    if r.returncode != 0:
        return "writing the export back failed, exit %s\n%s" % (r.returncode, (r.stdout + r.stderr)[-3000:])
    after = export_of("PrefabExport", base, ROOT + "/Crate.prefab")
    if before.get("Hierarchy") != after.get("Hierarchy"):
        return "the export changed after writing it back"
    return None


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--project", required=True)
    p.add_argument("--unity")
    p.add_argument("--keep", action="store_true", help="leave Assets/AssetOpsSmoke in place")
    p.add_argument("--clean", action="store_true", help="delete a leftover Assets/AssetOpsSmoke before starting")
    p.add_argument("--timeout", type=int, default=1200, help="per step, headless starts of a big project are slow (default 1200)")
    args = p.parse_args()
    tmp = tempfile.mkdtemp(prefix="assetops_smoke_")

    create = spec("create", {"Assets": [
        {"Kind": "Folder", "Path": ROOT},
        {"Kind": "Material", "Path": ROOT + "/Red.mat", "Properties": {}},
        {"Kind": "Prefab", "Path": ROOT + "/Crate.prefab", "Root": "Crate",
         "Components": ["BoxCollider", {"Type": "Rigidbody", "Properties": {"m_Mass": 5}}],
         "Ops": [{"Op": "AddGameObject", "Parent": "Crate", "Name": "Mesh", "Components": ["MeshFilter", "MeshRenderer"]},
                 {"Op": "Set", "Path": "Crate/Mesh", "Component": "MeshRenderer", "Properties": {"m_Materials": [ROOT + "/Red.mat"]}}]},
        {"Kind": "PrefabVariant", "Path": ROOT + "/HeavyCrate.prefab", "Base": ROOT + "/Crate.prefab",
         "Ops": [{"Op": "Set", "Path": "HeavyCrate", "Component": "Rigidbody", "Properties": {"m_Mass": 50}}]},
        {"Kind": "AnimatorController", "Path": ROOT + "/Door.controller",
         "Ops": [{"Op": "AddParameter", "Name": "Open", "Type": "Bool"},
                 {"Op": "AddState", "Path": "Closed", "Default": True},
                 {"Op": "AddState", "Path": "Opened"},
                 {"Op": "AddTransition", "From": "Closed", "To": "Opened", "Conditions": [{"Parameter": "Open", "Mode": "If"}]}]},
        {"Kind": "Scene", "Path": ROOT + "/Smoke.unity",
         "Ops": [{"Op": "AddPrefabInstance", "Prefab": ROOT + "/Crate.prefab", "Name": "Crate"}]},
    ]}, tmp)
    edit_prefab = spec("edit_prefab", {"Targets": [{"AssetPath": ROOT + "/Crate.prefab", "Ops": [
        {"Op": "AddGameObject", "Parent": "Crate", "Name": "Lid"},
        {"Op": "Set", "Path": "Crate/Lid", "Component": "Transform", "Properties": {"m_LocalPosition": [0, 1, 0]}},
        {"Op": "RemoveComponent", "Path": "Crate", "Type": "BoxCollider"}]}]}, tmp)
    bad_prefab = spec("bad_prefab", {"Targets": [{"AssetPath": ROOT + "/Crate.prefab", "Ops": [
        {"Op": "Rename", "Path": "Crate/DoesNotExist", "NewName": "X"}]}]}, tmp)
    edit_scene = spec("edit_scene", {"Targets": [{"AssetPath": ROOT + "/Smoke.unity", "Ops": [
        {"Op": "AddGameObject", "Name": "Light", "Components": [{"Type": "Light", "Properties": {"m_Intensity": 2}}]},
        {"Op": "Set", "Path": "Crate", "Component": "Rigidbody", "Properties": {"m_Mass": 9}}]}]}, tmp)
    edit_mat = spec("edit_mat", {"Targets": [{"AssetPath": ROOT + "/Red.mat", "RenderQueue": 2450, "EnableInstancing": True}]}, tmp)
    edit_anim = spec("edit_anim", {"Targets": [{"AssetPath": ROOT + "/Door.controller", "Ops": [
        {"Op": "AddTransition", "From": "Opened", "To": "Closed", "Conditions": [{"Parameter": "Open", "Mode": "IfNot"}]},
        {"Op": "RenameParameter", "Name": "Open", "NewName": "IsOpen"}]}]}, tmp)

    steps = [
        ("catalog", ["--list"], 0),
        ("create", ["CreateAsset", "--args", '-spec="%s"' % create], 0),
        ("create again skips", ["CreateAsset", "--args", '-spec="%s"' % create], 0),
        ("prefab export", ["PrefabExport", ROOT], 0),
        ("scene export", ["SceneExport", ROOT + "/Smoke.unity"], 0),
        ("material export", ["MaterialExport", ROOT + "/Red.mat"], 0),
        ("controller export", ["AnimatorControllerExport", ROOT + "/Door.controller"], 0),
        ("asset export", ["AssetExport", ROOT + "/Red.mat"], 0),
        ("reference export", ["AssetReferenceExport", ROOT + "/Red.mat", "--args", "-scandir=" + ROOT], 0),
        ("type catalog", ["TypeCatalogExport", "--args", "-filter=Rigidbody"], 0),
        ("unknown arg refused", ["EditPrefab", "--args", '-spec="%s" -aply' % edit_prefab], 2),
        ("edit prefab dry", ["EditPrefab", "--args", '-spec="%s"' % edit_prefab], 0),
        ("dry run left it unchanged", check_dry_run_left_prefab, None),
        ("edit prefab apply", ["EditPrefab", "--args", '-spec="%s" -apply' % edit_prefab], 0),
        ("edit prefab bad op fails", ["EditPrefab", "--args", '-spec="%s" -apply' % bad_prefab], 1),
        ("export round trip", check_round_trip, None),
        ("edit scene apply", ["EditScene", "--args", '-spec="%s" -apply' % edit_scene], 0),
        ("edit material apply", ["EditMaterial", "--args", '-spec="%s" -apply' % edit_mat], 0),
        ("edit controller apply", ["EditAnimatorController", "--args", '-spec="%s" -apply' % edit_anim], 0),
        ("duplicate", ["DuplicateAsset", "--args", "-from=%s/Red.mat -to=%s/Blue.mat -apply" % (ROOT, ROOT)], 0),
        ("remap dry", ["RemapReference", "--args", "-from=%s/Red.mat -to=%s/Blue.mat -scandir=%s" % (ROOT, ROOT, ROOT)], 0),
        ("remap apply", ["RemapReference", "--args", "-from=%s/Red.mat -to=%s/Blue.mat -scandir=%s -apply" % (ROOT, ROOT, ROOT)], 0),
        ("move", ["MoveAsset", "--args", "-from=%s/Blue.mat -to=%s/Materials/Blue.mat -apply" % (ROOT, ROOT)], 0),
        ("resave", ["ResaveAsset", ROOT, "--args", "-apply"], 0),
        ("delete unreferenced", ["DeleteAsset", ROOT + "/Red.mat", "--args", "-scandir=%s -permanent -apply" % ROOT], 0),
        ("delete referenced refused", ["DeleteAsset", ROOT + "/Materials/Blue.mat", "--args", "-scandir=%s -apply" % ROOT], 1),
        ("audit materials", ["AuditMaterial", ROOT], None),
        ("audit missing refs", ["AuditMissingReference", ROOT], 0),
        ("audit textures", ["AuditTexture", ROOT], 0),
        ("remove missing scripts", ["RemoveMissingScripts", ROOT, "--args", "-apply"], 0),
    ]
    if not args.keep:
        steps.append(("cleanup", ["DeleteAsset", ROOT, "--args", "-force -permanent -apply"], 0))

    base = [sys.executable, WRAPPER, "--project", args.project, "--quiet", "--timeout", str(args.timeout)]
    if args.unity:
        base += ["--unity", args.unity]

    # The steps assume a fresh folder: a second run on leftovers adds a second Lid and finds Red.mat gone.
    if os.path.isdir(os.path.join(args.project, *ROOT.split("/"))):
        if not args.clean:
            print("%s already exists, from a --keep or interrupted run. Delete it, or rerun with --clean" % ROOT)
            return 2
        r = subprocess.run(base + ["DeleteAsset", ROOT, "--args", "-force -permanent -apply"], capture_output=True, text=True)
        print("%-4s %-28s exit %s" % ("ok" if r.returncode == 0 else "FAIL", "clean leftovers", r.returncode), flush=True)
        if r.returncode != 0:
            print(r.stdout + r.stderr)
            return 1
    failed = 0
    for name, argv, expected in steps:
        if callable(argv):
            try:
                problem = argv(base, tmp)
            except Exception as e:  # a failed export inside the check
                problem = str(e)
            failed += 1 if problem else 0
            print("%-4s %-28s%s" % ("FAIL" if problem else "ok", name, ""), flush=True)
            if problem:
                print("\n".join("     " + l for l in problem.strip().splitlines()[-25:]))
            continue
        r = subprocess.run(base + argv, capture_output=True, text=True)
        ok = expected is None and r.returncode in (0, 3) or r.returncode == expected
        failed += 0 if ok else 1
        print("%-4s %-28s exit %s%s" % ("ok" if ok else "FAIL", name, r.returncode, "" if ok else " (expected %s)" % expected), flush=True)
        if not ok:
            print("\n".join("     " + l for l in (r.stdout + r.stderr).strip().splitlines()[-25:]))
    print("%d step(s) failed" % failed if failed else "all steps passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
