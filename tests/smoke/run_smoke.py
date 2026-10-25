#!/usr/bin/env python3
"""End-to-end smoke test in a real Unity project. Exercises every group through dotasset.py.

Creates everything under Assets/DotAssetSmoke and deletes it at the end. Run it once with the editor closed
(headless route) and once with the editor open (queue route):

  python tests/smoke/run_smoke.py --project /path/to/UnityProject [--unity /path/to/Unity] [--clean] [--keep]

With the editor open, save or close any Untitled scene first: Unity cannot open a scene next to one.
Steps that change assets read the result back through an export, dry runs and failed targets must leave files as
they were, and the audits get seeded issues to find and fix. Prints one line per step and exits 1 if any step
failed. The importer steps need a texture file imported, which only a Unity start does, so with the editor open
they print "skip".
"""
import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
WRAPPER = os.path.join(HERE, "..", "..", "Package", "Scripts~", "dotasset.py")
ROOT = "Assets/DotAssetSmoke"
BASE = []   # the wrapper command line, set by main
SPECS = {}  # spec files shared between steps, set by main


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


def component(export, path, kind):
    n = node(export, path) or {}
    return next((c for c in n.get("Components", []) if c.get("Type") == kind), None)


def override(export, path, prop):
    """The value of a prefab instance override, or None."""
    inst = (node(export, path) or {}).get("PrefabInstance", {})
    return next((o.get("Value") for o in inst.get("Overrides", []) if o.get("Property") == prop), None)


def call(base, argv):
    """Runs the wrapper with --json and returns the process and the parsed result ({} when there is none)."""
    r = subprocess.run(base + ["--json"] + argv, capture_output=True, text=True)
    try:
        res = result_json(r.stdout)
    except ValueError:
        res = {}
    return r, res


def drop_kept_log(r):
    """The wrapper keeps the Unity log of a run that did not succeed. When the failure was the expected one, the
    log is noise that piles up in Library/DotAsset/Queue/batch, so it goes."""
    for line in (r.stdout + r.stderr).splitlines():
        if "Unity log: " in line:
            path = line.split("Unity log: ", 1)[1].strip()
            if path.endswith(".unity.log") and os.path.isfile(path):
                try:
                    os.remove(path)
                except OSError:
                    # Still held by Unity's licensing client: the wrapper's next headless run sweeps what is listed.
                    try:
                        with open(os.path.join(os.path.dirname(path), "logs-to-delete.txt"), "a", encoding="utf-8") as f:
                            f.write(os.path.basename(path) + "\n")
                    except OSError:
                        pass


def expect(base, label, argv, code):
    """Runs a step that must exit with code. Returns a problem or None."""
    r, _ = call(base, argv)
    if r.returncode != code:
        return "%s exit %s, expected %s\n%s" % (label, r.returncode, code, (r.stdout + r.stderr)[-2000:])
    if code != 0:
        drop_kept_log(r)
    return None


def on_disk(base, asset):
    return os.path.exists(os.path.join(project_of(base), *asset.split("/")))


def crate():
    return export_of("PrefabExport", BASE, ROOT + "/Crate.prefab")


def crate_materials():
    return ((component(crate(), "Crate/Mesh", "MeshRenderer") or {}).get("Properties") or {}).get("m_Materials")


def check_create(base, tmp):
    # Read every created asset back, the exit code alone says nothing about what was written.
    exp = crate()
    if ((component(exp, "Crate", "Rigidbody") or {}).get("Properties") or {}).get("m_Mass") != 5:
        return "Crate's Rigidbody mass is not 5"
    if component(exp, "Crate", "BoxCollider") is None:
        return "Crate has no BoxCollider"
    if crate_materials() != [ROOT + "/Red.mat"]:
        return "Crate/Mesh materials are %s, expected Red.mat" % crate_materials()
    heavy = export_of("PrefabExport", base, ROOT + "/HeavyCrate.prefab")
    if heavy.get("VariantOf") != ROOT + "/Crate.prefab":
        return "HeavyCrate is not a variant of Crate"
    if override(heavy, "HeavyCrate", "m_Mass") != "50":
        return "HeavyCrate does not override the mass to 50"
    door = export_of("AnimatorControllerExport", base, ROOT + "/Door.controller")
    if [p["Name"] for p in door.get("Parameters", [])] != ["Open"]:
        return "Door's parameters are not [Open]"
    sm = door["Layers"][0]["StateMachine"]
    if sm.get("DefaultState") != "Closed" or [s["Path"] for s in sm.get("States", [])] != ["Closed", "Opened"]:
        return "Door's states are not Closed (default) and Opened"
    scene = export_of("SceneExport", base, ROOT + "/Smoke.unity")
    if (node(scene, "Crate") or {}).get("PrefabInstance", {}).get("Source") != ROOT + "/Crate.prefab":
        return "the scene has no Crate prefab instance"
    return None


def check_create_again(base, tmp):
    # A second CreateAsset with the same spec skips what exists, it must not rebuild Crate.
    before = crate()
    problem = expect(base, "CreateAsset again", ["CreateAsset", "--args", '-spec="%s"' % SPECS["create"]], 0)
    if problem:
        return problem
    if before.get("Hierarchy") != crate().get("Hierarchy"):
        return "the second CreateAsset changed Crate"
    return None


def fingerprint(base):
    """Bytes of every file under ROOT, .meta files included."""
    top = os.path.join(project_of(base), *ROOT.split("/"))
    files = {}
    for folder, _, names in os.walk(top):
        for n in names:
            path = os.path.join(folder, n)
            with open(path, "rb") as f:
                files[os.path.relpath(path, top)] = f.read()
    return files


def check_exports_read_only(base, tmp):
    # Every export over the edited assets, then not one byte under ROOT may differ. Red.mat gets a custom render
    # queue first: Unity resets it when the material is validated, and an export that let that happen would save it.
    queue = spec("queue", {"Targets": [{"AssetPath": ROOT + "/Red.mat", "Properties": {"m_CustomRenderQueue": 2460}}]}, tmp)
    problem = expect(base, "EditAsset render queue", ["EditAsset", "--args", '-spec="%s" -apply' % queue], 0)
    if problem:
        return problem
    before = fingerprint(base)
    for argv in (["PrefabExport", ROOT], ["SceneExport", ROOT], ["MaterialExport", ROOT],
                 ["AnimatorControllerExport", ROOT], ["AssetExport", ROOT + "/Red.mat"],
                 ["AssetReferenceExport", ROOT + "/Red.mat", "--args", "-scandir=" + ROOT]):
        problem = expect(base, argv[0], argv, 0)
        if problem:
            return problem
        after = fingerprint(base)
        changed = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
        if changed:
            return "%s changed %s" % (argv[0], ", ".join(changed))
    return None


def check_reference_export(base, tmp):
    exp = export_of("AssetReferenceExport", base, ROOT + "/Red.mat")
    if (ROOT + "/Crate.prefab") not in json.dumps(exp):
        return "AssetReferenceExport does not list Crate.prefab as a user of Red.mat"
    return None


def check_dry_run_left_prefab(base, tmp):
    # The dry run of edit_prefab adds Crate/Lid and removes the BoxCollider, none of which may reach disk.
    exp = export_of("PrefabExport", base, ROOT + "/Crate.prefab")
    if node(exp, "Crate/Lid") is not None:
        return "the dry run saved Crate/Lid"
    types = [c.get("Type") for c in node(exp, "Crate").get("Components", [])]
    if "BoxCollider" not in types:
        return "the dry run removed the BoxCollider"
    return None


def check_edit_prefab(base, tmp):
    problem = expect(base, "EditPrefab -apply", ["EditPrefab", "--args", '-spec="%s" -apply' % SPECS["edit_prefab"]], 0)
    if problem:
        return problem
    exp = crate()
    lid = component(exp, "Crate/Lid", "Transform")
    if lid is None or lid.get("Properties", {}).get("m_LocalPosition") != [0, 1, 0]:
        return "Crate/Lid is missing or not at (0, 1, 0)"
    if component(exp, "Crate", "BoxCollider") is not None:
        return "the BoxCollider is still on Crate"
    return None


def check_failed_edit(base, tmp):
    # All or nothing: the bad op fails the target and nothing of it is saved.
    before = crate()
    problem = expect(base, "EditPrefab bad op", ["EditPrefab", "--args", '-spec="%s" -apply' % SPECS["bad_prefab"]], 1)
    if problem:
        return problem
    if before.get("Hierarchy") != crate().get("Hierarchy"):
        return "the failed EditPrefab changed Crate"
    return None


def check_array_ops(base, tmp):
    # ArrayOps Append, Insert and Remove on Crate/Mesh's m_Materials, the dry run first.
    def ops(*entries):
        return spec("array_ops", {"Targets": [{"AssetPath": ROOT + "/Crate.prefab", "Ops": [
            {"Op": "Set", "Path": "Crate/Mesh", "Component": "MeshRenderer", "ArrayOps": list(entries)}]}]}, tmp)
    red = ROOT + "/Red.mat"
    path = ops({"Property": "m_Materials", "Append": red})
    problem = expect(base, "ArrayOps dry run", ["EditPrefab", "--args", '-spec="%s"' % path], 0)
    if problem:
        return problem
    if crate_materials() != [red]:
        return "the ArrayOps dry run saved %s" % crate_materials()
    problem = expect(base, "ArrayOps Append", ["EditPrefab", "--args", '-spec="%s" -apply' % path], 0)
    if problem:
        return problem
    if crate_materials() != [red, red]:
        return "after Append the materials are %s, expected two Red.mat" % crate_materials()
    path = ops({"Property": "m_Materials", "Insert": 0, "Value": None}, {"Property": "m_Materials", "Remove": 2})
    problem = expect(base, "ArrayOps Insert and Remove", ["EditPrefab", "--args", '-spec="%s" -apply' % path], 0)
    if problem:
        return problem
    if crate_materials() != [None, red]:
        return "after Insert 0 and Remove 2 the materials are %s, expected [null, Red.mat]" % crate_materials()
    path = ops({"Property": "m_Materials", "Remove": 0})
    problem = expect(base, "ArrayOps Remove", ["EditPrefab", "--args", '-spec="%s" -apply' % path], 0)
    if problem:
        return problem
    if crate_materials() != [red]:
        return "after Remove 0 the materials are %s, expected [Red.mat]" % crate_materials()
    return None


def check_edit_scene(base, tmp):
    argv = ["EditScene", "--args", '-spec="%s"' % SPECS["edit_scene"]]
    problem = expect(base, "EditScene dry run", argv, 0)
    if problem:
        return problem
    if node(export_of("SceneExport", base, ROOT + "/Smoke.unity"), "Light") is not None:
        return "the EditScene dry run saved the Light"
    argv[-1] += " -apply"
    problem = expect(base, "EditScene -apply", argv, 0)
    if problem:
        return problem
    scene = export_of("SceneExport", base, ROOT + "/Smoke.unity")
    light = component(scene, "Light", "Light")
    if light is None or light.get("Properties", {}).get("m_Intensity") != 2:
        return "the scene has no Light with intensity 2"
    if override(scene, "Crate", "m_Mass") != "9":
        return "the scene's Crate does not override the mass to 9"
    return None


def check_edit_material(base, tmp):
    argv = ["EditMaterial", "--args", '-spec="%s"' % SPECS["edit_mat"]]
    problem = expect(base, "EditMaterial dry run", argv, 0)
    if problem:
        return problem
    if export_of("MaterialExport", base, ROOT + "/Red.mat").get("RenderQueue") == 2450:
        return "the EditMaterial dry run saved the render queue"
    argv[-1] += " -apply"
    problem = expect(base, "EditMaterial -apply", argv, 0)
    if problem:
        return problem
    mat = export_of("MaterialExport", base, ROOT + "/Red.mat")
    if mat.get("RenderQueue") != 2450 or mat.get("EnableInstancing") is not True:
        return "after EditMaterial -apply RenderQueue is %s and EnableInstancing %s" % (mat.get("RenderQueue"), mat.get("EnableInstancing"))
    return None


def door():
    exp = export_of("AnimatorControllerExport", BASE, ROOT + "/Door.controller")
    sm = exp["Layers"][0]["StateMachine"]
    states = {s["Path"]: s for s in sm.get("States", [])}
    return [p["Name"] for p in exp.get("Parameters", [])], states


def check_edit_controller(base, tmp):
    # The dry run adds a state too. In the editor every run shares one process, so a dry run whose undo revert
    # missed something would show up in the -apply that follows.
    dry = spec("edit_anim_dry", {"Targets": [{"AssetPath": ROOT + "/Door.controller", "Ops":
        json.load(open(SPECS["edit_anim"]))["Targets"][0]["Ops"] + [{"Op": "AddState", "Path": "Ghost"}]}]}, tmp)
    problem = expect(base, "EditAnimatorController dry run", ["EditAnimatorController", "--args", '-spec="%s"' % dry], 0)
    if problem:
        return problem
    params, states = door()
    if params != ["Open"] or "Ghost" in states or states["Opened"].get("Transitions"):
        return "the EditAnimatorController dry run saved changes"
    problem = expect(base, "EditAnimatorController -apply",
                     ["EditAnimatorController", "--args", '-spec="%s" -apply' % SPECS["edit_anim"]], 0)
    if problem:
        return problem
    params, states = door()
    if params != ["IsOpen"]:
        return "the parameters are %s, expected [IsOpen]" % params
    if "Ghost" in states:
        return "the dry run's Ghost state leaked into the -apply"
    back = [t for t in states["Opened"].get("Transitions", []) if t.get("To") == "Closed"]
    if not back or [c["Mode"] for c in back[0].get("Conditions", [])] != ["IfNot"]:
        return "Opened has no IfNot transition to Closed"
    used = [c["Parameter"] for s in states.values() for t in s.get("Transitions", []) for c in t.get("Conditions", [])]
    if set(used) != {"IsOpen"}:
        return "RenameParameter left conditions on %s" % sorted(set(used))
    return None


def check_duplicate(base, tmp):
    problem = expect(base, "DuplicateAsset", ["DuplicateAsset", "--args", "-from=%s/Red.mat -to=%s/Blue.mat -apply" % (ROOT, ROOT)], 0)
    if problem:
        return problem
    red = export_of("MaterialExport", base, ROOT + "/Red.mat").get("RenderQueue")
    blue = export_of("MaterialExport", base, ROOT + "/Blue.mat").get("RenderQueue")
    if red != 2460 or blue != red:
        return "Blue.mat is not a copy of Red.mat: render queue %s, Red.mat %s" % (blue, red)
    return None


def check_remap(base, tmp):
    argv = ["RemapReference", "--args", "-from=%s/Red.mat -to=%s/Blue.mat -scandir=%s" % (ROOT, ROOT, ROOT)]
    problem = expect(base, "RemapReference dry run", argv, 0)
    if problem:
        return problem
    if crate_materials() != [ROOT + "/Red.mat"]:
        return "the RemapReference dry run saved %s" % crate_materials()
    argv[-1] += " -apply"
    problem = expect(base, "RemapReference -apply", argv, 0)
    if problem:
        return problem
    if crate_materials() != [ROOT + "/Blue.mat"]:
        return "after RemapReference Crate uses %s, expected Blue.mat" % crate_materials()
    return None


def check_move(base, tmp):
    problem = expect(base, "MoveAsset", ["MoveAsset", "--args", "-from=%s/Blue.mat -to=%s/Materials/Blue.mat -apply" % (ROOT, ROOT)], 0)
    if problem:
        return problem
    if on_disk(base, ROOT + "/Blue.mat") or not on_disk(base, ROOT + "/Materials/Blue.mat"):
        return "Blue.mat was not moved"
    if crate_materials() != [ROOT + "/Materials/Blue.mat"]:
        return "after the move Crate uses %s, the reference broke" % crate_materials()
    return None


def check_delete(base, tmp):
    problem = expect(base, "DeleteAsset unreferenced", ["DeleteAsset", ROOT + "/Red.mat", "--args", "-scandir=%s -permanent -apply" % ROOT], 0)
    if problem:
        return problem
    if on_disk(base, ROOT + "/Red.mat"):
        return "Red.mat is still there"
    problem = expect(base, "DeleteAsset referenced", ["DeleteAsset", ROOT + "/Materials/Blue.mat", "--args", "-scandir=%s -apply" % ROOT], 1)
    if problem:
        return problem
    if not on_disk(base, ROOT + "/Materials/Blue.mat"):
        return "the refused DeleteAsset deleted Blue.mat"
    return None


def audit(base, run, target):
    """Runs an audit and returns (exit code, report, fix spec path or None)."""
    r, res = call(base, [run, target])
    if r.returncode not in (0, 3):
        raise RuntimeError(run_failed(run, r))
    reports = [o for o in res.get("Outputs") or [] if not o.endswith(".fix.json")]
    if not reports:
        raise RuntimeError(run + " wrote no report")
    with open(reports[-1], encoding="utf-8") as f:
        report = json.load(f)
    fix = report.get("FixSpec")
    if fix and not os.path.isabs(fix):
        fix = os.path.join(project_of(base), fix)
    return r.returncode, report, fix and fix.replace("\\", "/")


def issues(report, rule, asset):
    return [i for i in report.get("Issues", []) if i.get("Rule") == rule and i.get("Asset") == asset]


def check_audit_missing_reference(base, tmp):
    # Seed an R1: a prefab whose material is deleted behind its back. The audit must find it.
    broken = ROOT + "/Broken.prefab"
    seed = spec("seed_r1", {"Assets": [
        {"Kind": "Material", "Path": ROOT + "/Green.mat", "Properties": {}},
        {"Kind": "Prefab", "Path": broken, "Root": "Broken",
         "Components": ["MeshFilter", {"Type": "MeshRenderer", "Properties": {"m_Materials": [ROOT + "/Green.mat"]}}]}]}, tmp)
    problem = expect(base, "CreateAsset seed", ["CreateAsset", "--args", '-spec="%s"' % seed], 0) or \
        expect(base, "DeleteAsset Green.mat", ["DeleteAsset", ROOT + "/Green.mat", "--args", "-force -permanent -apply"], 0)
    if problem:
        return problem
    code, report, _ = audit(base, "AuditMissingReference", ROOT)
    if code != 3 or not issues(report, "R1", broken):
        return "AuditMissingReference exit %s did not report R1 on Broken.prefab: %s" % (code, report.get("Counts"))
    problem = expect(base, "DeleteAsset Broken.prefab", ["DeleteAsset", broken, "--args", "-force -permanent -apply"], 0)
    if problem:
        return problem
    code, report, _ = audit(base, "AuditMissingReference", ROOT)
    if code != 0:
        return "AuditMissingReference still reports %s after the seed was removed" % report.get("Counts")
    return None


def check_audit_material(base, tmp):
    # Seed an M5: a keyword the shader does not declare. Audit, then fix it with the fix spec the audit wrote.
    blue = ROOT + "/Materials/Blue.mat"
    keyword = "_DOTASSET_STRAY"
    seed = spec("seed_m5", {"Targets": [{"AssetPath": blue, "EnableKeywords": [keyword]}]}, tmp)
    problem = expect(base, "EditMaterial seed", ["EditMaterial", "--args", '-spec="%s" -apply' % seed], 0)
    if problem:
        return problem
    if keyword not in export_of("MaterialExport", base, blue).get("Keywords", []):
        return "EditMaterial did not enable %s" % keyword
    code, report, fix = audit(base, "AuditMaterial", ROOT)
    if code != 3 or not issues(report, "M5", blue) or not fix:
        return "AuditMaterial exit %s did not report M5 on Blue.mat with a fix spec: %s" % (code, report.get("Counts"))
    problem = expect(base, "EditMaterial fix dry run", ["EditMaterial", "--args", '-spec="%s"' % fix], 0)
    if problem:
        return problem
    if keyword not in export_of("MaterialExport", base, blue).get("Keywords", []):
        return "the fix spec dry run removed the keyword"
    problem = expect(base, "EditMaterial fix -apply", ["EditMaterial", "--args", '-spec="%s" -apply' % fix], 0)
    if problem:
        return problem
    if keyword in export_of("MaterialExport", base, blue).get("Keywords", []):
        return "the fix spec left %s enabled" % keyword
    _, report, _ = audit(base, "AuditMaterial", ROOT)
    if issues(report, "M5", blue):
        return "AuditMaterial still reports M5 after the fix"
    return None


def check_cleanup(base, tmp):
    problem = expect(base, "DeleteAsset " + ROOT, ["DeleteAsset", ROOT, "--args", "-force -permanent -apply"], 0)
    if problem:
        return problem
    if on_disk(base, ROOT):
        return ROOT + " is still there"
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


def project_of(base):
    return base[base.index("--project") + 1]


def editor_open(base):
    r = subprocess.run(base + ["--status"], capture_output=True, text=True)
    return "editor pid" in r.stdout


def write_png(path, size=4):
    """A size x size grey RGBA PNG, enough for Unity to import a texture."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    rows = b"".join(b"\x00" + b"\x80\x80\x80\xff" * size for _ in range(size))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png)


def run_failed(label, r):
    return "%s exit %s\n%s" % (label, r.returncode, (r.stdout + r.stderr)[-2000:])


def check_edit_asset(base, tmp):
    # EditAsset on a native asset: the material's render queue, read back through MaterialExport.
    path = spec("edit_asset", {"Targets": [{"AssetPath": ROOT + "/Red.mat", "Properties": {"m_CustomRenderQueue": 2460}}]}, tmp)
    r = subprocess.run(base + ["EditAsset", "--args", '-spec="%s"' % path], capture_output=True, text=True)
    if r.returncode != 0:
        return run_failed("EditAsset dry run", r)
    if export_of("MaterialExport", base, ROOT + "/Red.mat").get("RenderQueue") == 2460:
        return "the EditAsset dry run saved the render queue"
    r = subprocess.run(base + ["EditAsset", "--args", '-spec="%s" -apply' % path], capture_output=True, text=True)
    if r.returncode != 0:
        return run_failed("EditAsset -apply", r)
    queue = export_of("MaterialExport", base, ROOT + "/Red.mat").get("RenderQueue")
    if queue != 2460:
        return "after EditAsset -apply the render queue is %s, expected 2460" % queue
    return None


def texture_type(base, texture):
    return export_of("ImporterExport", base, texture).get("Summary", {}).get("TextureType")


def check_importer(base, tmp):
    # ImporterExport, AuditTexture and EditImporter on a texture the test writes itself, named like a normal map so
    # the audit reports T1 and writes the fix. A new file is only imported when Unity starts, or when an editor with
    # focus refreshes, so this needs the headless route.
    if editor_open(base):
        return "skip: needs the editor closed, a new texture file is only imported when Unity starts"
    texture = ROOT + "/Rock_n.png"
    write_png(os.path.join(project_of(base), *texture.split("/")))
    kind = texture_type(base, texture)
    if kind != "Default":
        return "a fresh texture exports TextureType %s, expected Default" % kind
    code, report, fix = audit(base, "AuditTexture", ROOT)
    if code != 3 or not issues(report, "T1", texture) or not fix:
        return "AuditTexture exit %s did not report T1 on Rock_n.png with a fix spec: %s" % (code, report.get("Counts"))
    r = subprocess.run(base + ["EditImporter", "--args", '-spec="%s"' % fix], capture_output=True, text=True)
    if r.returncode != 0:
        return run_failed("EditImporter dry run", r)
    if texture_type(base, texture) != "Default":
        return "the EditImporter dry run changed the import settings"
    r = subprocess.run(base + ["EditImporter", "--args", '-spec="%s" -apply' % fix], capture_output=True, text=True)
    if r.returncode != 0:
        return run_failed("EditImporter -apply", r)
    kind = texture_type(base, texture)
    if kind != "NormalMap":
        return "after EditImporter -apply the texture is %s, expected NormalMap" % kind
    _, report, _ = audit(base, "AuditTexture", ROOT)
    if issues(report, "T1", texture):
        return "AuditTexture still reports T1 after the fix"
    return None


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--project", required=True)
    p.add_argument("--unity")
    p.add_argument("--keep", action="store_true", help="leave Assets/DotAssetSmoke in place")
    p.add_argument("--clean", action="store_true", help="delete a leftover Assets/DotAssetSmoke before starting")
    p.add_argument("--timeout", type=int, default=1200, help="per step, headless starts of a big project are slow (default 1200)")
    p.add_argument("--keep-going", action="store_true",
                   help="run every step after a failure too. Later steps build on earlier ones, so they mostly fail along")
    args = p.parse_args()
    if not os.path.isdir(os.path.join(args.project, "Assets")):
        print("%s is not a Unity project (no Assets folder)" % args.project)
        return 2
    tmp = tempfile.mkdtemp(prefix="dotasset_smoke_")
    try:
        return run(args, tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def run(args, tmp):

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

    SPECS.update(create=create, edit_prefab=edit_prefab, bad_prefab=bad_prefab, edit_scene=edit_scene,
                 edit_mat=edit_mat, edit_anim=edit_anim)

    # Steps that change assets read the result back through an export, the exit code alone is not enough.
    steps = [
        ("catalog", ["--list"], 0),
        ("create", ["CreateAsset", "--args", '-spec="%s"' % create], 0),
        ("create wrote the assets", check_create, None),
        ("create again skips", check_create_again, None),
        ("prefab export", ["PrefabExport", ROOT], 0),
        ("scene export", ["SceneExport", ROOT + "/Smoke.unity"], 0),
        ("material export", ["MaterialExport", ROOT + "/Red.mat"], 0),
        ("controller export", ["AnimatorControllerExport", ROOT + "/Door.controller"], 0),
        ("asset export", ["AssetExport", ROOT + "/Red.mat"], 0),
        ("reference export", check_reference_export, None),
        ("type catalog", ["TypeCatalogExport", "--args", "-filter=Rigidbody"], 0),
        ("unknown arg refused", ["EditPrefab", "--args", '-spec="%s" -aply' % edit_prefab], 2),
        ("edit prefab dry", ["EditPrefab", "--args", '-spec="%s"' % edit_prefab], 0),
        ("dry run left it unchanged", check_dry_run_left_prefab, None),
        ("edit prefab apply", check_edit_prefab, None),
        ("bad op fails, saves nothing", check_failed_edit, None),
        ("array ops", check_array_ops, None),
        ("export round trip", check_round_trip, None),
        ("edit scene dry and apply", check_edit_scene, None),
        ("edit material dry and apply", check_edit_material, None),
        ("edit asset dry and apply", check_edit_asset, None),
        ("edit controller dry, apply", check_edit_controller, None),
        ("exports leave assets as is", check_exports_read_only, None),
        ("duplicate", check_duplicate, None),
        ("remap dry and apply", check_remap, None),
        ("move keeps references", check_move, None),
        ("resave", ["ResaveAsset", ROOT, "--args", "-apply"], 0),
        ("delete, refused if used", check_delete, None),
        ("audit missing refs clean", ["AuditMissingReference", ROOT], 0),
        ("audit missing refs seeded", check_audit_missing_reference, None),
        ("audit material and fix", check_audit_material, None),
        ("remove missing scripts", ["RemoveMissingScripts", ROOT, "--args", "-apply"], 0),
        ("texture audit, export, fix", check_importer, None),
    ]
    base = [sys.executable, WRAPPER, "--project", args.project, "--quiet", "--timeout", str(args.timeout)]
    if args.unity:
        base += ["--unity", args.unity]
    BASE.extend(base)
    started = time.time()
    print("DotAsset smoke test: %s, %s, Unity %s" % (
        args.project, "editor open (queue route)" if editor_open(base) else "editor closed (headless route)",
        unity_version(args.project)), flush=True)

    # The steps assume a fresh folder: a second run on leftovers adds a second Lid and finds Red.mat gone.
    if os.path.isdir(os.path.join(args.project, *ROOT.split("/"))):
        if not args.clean:
            print("%s already exists, from a --keep or interrupted run. Delete it, or rerun with --clean" % ROOT)
            return 2
        problem = check_cleanup(base, tmp)
        print("%-4s %-28s" % ("FAIL" if problem else "ok", "clean leftovers"), flush=True)
        if problem:
            print(indent(problem))
            return 1

    outside_before = outside_state(args.project)
    failed, skipped, not_run = 0, 0, 0
    try:
        for name, argv, expected in steps:
            if failed and not args.keep_going:
                not_run += 1
                continue
            t0 = time.time()
            problem = run_step(base, tmp, argv, expected)
            took = "%5.1fs" % (time.time() - t0)
            if problem and problem.startswith("skip:"):
                skipped += 1
                print("%-4s %-28s %s  %s" % ("skip", name, took, problem[len("skip:"):].strip()), flush=True)
                continue
            failed += 1 if problem else 0
            print("%-4s %-28s %s" % ("FAIL" if problem else "ok", name, took), flush=True)
            if problem:
                print(indent(problem))
    except KeyboardInterrupt:
        print("interrupted, cleaning up. If this is interrupted too, rerun with --clean to remove %s" % ROOT, flush=True)
        return 130
    finally:
        # Whatever happened above, the project gets its folder back unless --keep asked otherwise.
        if not args.keep and os.path.isdir(os.path.join(args.project, *ROOT.split("/"))):
            problem = check_cleanup(base, tmp)
            print("%-4s %-28s" % ("FAIL" if problem else "ok", "cleanup"), flush=True)
            if problem:
                failed += 1
                print(indent(problem))

    # The package promises to write only its targets. Anything else under Assets/ that changed is a leak.
    leaks = outside_changes(outside_before, outside_state(args.project))
    print("%-4s %-28s" % ("FAIL" if leaks else "ok", "nothing else in Assets/"), flush=True)
    if leaks:
        failed += 1
        print(indent(leaks))

    # Every headless run leaves a Unity log. The wrapper deletes it, or lists it while Unity's licensing client still
    # holds it, and the smoke test does the same for failures it expected. A log that is neither piles up for good.
    stray = stray_logs(args.project, started)
    print("%-4s %-28s" % ("FAIL" if stray else "ok", "no Unity logs left behind"), flush=True)
    if stray:
        failed += 1
        print(indent("\n".join(stray)))

    summary = "all steps passed" if not failed else "%d step(s) failed" % failed
    if not_run:
        summary += ", %d not run after the first failure (--keep-going runs them)" % not_run
    if skipped:
        summary += ", %d skipped" % skipped
    print("%s in %.0fs" % (summary, time.time() - started))
    return 1 if failed else 0


def run_step(base, tmp, argv, expected):
    """One step: a check function, or a wrapper call with its expected exit code (None: 0 or 3). A problem or None."""
    if callable(argv):
        try:
            return argv(base, tmp)
        except Exception as e:  # a failed export inside the check, or an export shaped unlike the check expects
            return "%s: %s" % (type(e).__name__, e)
    r = subprocess.run(base + argv, capture_output=True, text=True)
    if expected is None and r.returncode in (0, 3) or r.returncode == expected:
        if r.returncode not in (0, 3):
            drop_kept_log(r)
        return None
    return "exit %s, expected %s\n%s" % (r.returncode, "0 or 3" if expected is None else expected, (r.stdout + r.stderr).strip())


def outside_state(project):
    """Size and modification time of every file under Assets/ except the smoke folder. Cheap enough for a big
    project, and any write that leaks out of the smoke folder changes one of them."""
    top = os.path.join(project, "Assets")
    smoke = os.path.normcase(os.path.join(project, *ROOT.split("/")))
    state = {}
    for folder, dirs, names in os.walk(top):
        if os.path.normcase(folder) == smoke:
            dirs[:] = []
            continue
        for n in names:
            path = os.path.join(folder, n)
            if os.path.normcase(path) == smoke + ".meta":
                continue
            try:
                st = os.stat(path)
            except OSError:
                continue
            state[os.path.relpath(path, project).replace("\\", "/")] = (st.st_size, st.st_mtime_ns)
    return state


def outside_changes(before, after):
    added = sorted(set(after) - set(before))
    removed = sorted(set(before) - set(after))
    changed = sorted(k for k in set(before) & set(after) if before[k] != after[k])
    lines = ["%s %s" % (what, p) for what, paths in (("added", added), ("removed", removed), ("changed", changed)) for p in paths]
    return "\n".join(lines[:20] + (["... %d more" % (len(lines) - 20)] if len(lines) > 20 else []))


def stray_logs(project, since):
    batch = os.path.join(project, "Library", "DotAsset", "Queue", "batch")
    try:
        with open(os.path.join(batch, "logs-to-delete.txt"), encoding="utf-8") as f:
            listed = {n.strip() for n in f}
    except OSError:
        listed = set()
    try:
        names = os.listdir(batch)
    except OSError:
        return []
    return sorted(n for n in names if n.endswith(".unity.log") and n not in listed
                  and os.path.getmtime(os.path.join(batch, n)) >= since)


def indent(problem):
    return "\n".join("     " + l for l in problem.strip().splitlines()[-25:])


def unity_version(project):
    try:
        with open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), encoding="utf-8") as f:
            for line in f:
                if line.startswith("m_EditorVersion:"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return "?"


if __name__ == "__main__":
    sys.exit(main())
