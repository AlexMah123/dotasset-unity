"""A small agent task done with DotAsset, every step checked through an export.

  1. an NPC prefab: an empty root with a nested cube
  2. the cube becomes a cylinder, its material green
  3. a script that wanders inside a sphere and idles, attached to the root
  4. 100 NPCs in a 10 x 10 square, 10 units apart, in a new scene
  5. a second prefab, a plain cube
  6. the scene is queried and the NPCs swapped for cubes in a checkerboard, dry run first
  7. the saved scene is checked: 50 NPCs, 50 cubes, every position kept, no broken reference

Everything is created under Assets/NpcDemo and removed at the end (--keep leaves it). The script is written
as a file and compiled by Unity on its next start, so run it with the editor closed.

  python examples/npc_scenario.py --project /path/to/UnityProject [--keep]
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

WRAPPER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Package", "Scripts~", "dotasset.py")
DEMO = "Assets/NpcDemo"
CUBE = {"Asset": "Library/unity default resources", "Sub": "Cube", "Type": "Mesh"}
CYLINDER = {"Asset": "Library/unity default resources", "Sub": "Cylinder", "Type": "Mesh"}
SCRIPT = r'''using System.Collections;
using UnityEngine;

// Wanders to random points inside a sphere around where it started, idling for a while at each one.
public class NpcWander : MonoBehaviour
{
    public float radius = 5f;
    public float speed = 2f;
    public float minIdle = 1f;
    public float maxIdle = 3f;

    Vector3 home;

    void Start()
    {
        home = transform.position;
        StartCoroutine(Wander());
    }

    IEnumerator Wander()
    {
        while (true)
        {
            Vector3 target = home + Random.insideUnitSphere * radius;
            target.y = home.y;
            while ((transform.position - target).sqrMagnitude > 0.01f)
            {
                transform.position = Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime);
                yield return null;
            }
            yield return new WaitForSeconds(Random.Range(minIdle, maxIdle));
        }
    }
}
'''

P = None
TMP = tempfile.mkdtemp(prefix="npc_")
step_no = [0]


def spec(name, data):
    path = os.path.join(TMP, name + ".json").replace("\\", "/")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    return path


def run(*argv, expect=(0,)):
    r = subprocess.run([sys.executable, WRAPPER, "--project", P, "--quiet", "--json"] + list(argv), capture_output=True, text=True)
    if r.returncode not in expect:
        raise SystemExit("FAILED: %s exit %s\n%s" % (" ".join(argv), r.returncode, (r.stdout + r.stderr)[-3000:]))
    start = r.stdout.rfind("\n{")
    return json.loads(r.stdout[start + 1:]) if start >= 0 else {}


def export(run_name, asset=None, args=None):
    res = run(*([run_name] + ([asset] if asset else []) + (["--args", args] if args else [])))
    with open(res["Outputs"][-1], encoding="utf-8") as f:
        return json.load(f)


def step(title):
    step_no[0] += 1
    print("\n[%d] %s" % (step_no[0], title), flush=True)


def ok(message):
    print("    ok: " + message, flush=True)


def check(condition, message):
    if not condition:
        raise SystemExit("CHECK FAILED: " + message)
    ok(message)


def node(exp, path):
    return next((n for n in exp.get("Hierarchy", []) if n.get("Path") == path), None)


def comp(exp, path, kind):
    return next((c for c in (node(exp, path) or {}).get("Components", []) if c.get("Type") == kind), None)


def position(inst):
    """World position of a scene root prefab instance, from its overrides."""
    o = {x["Property"]: x["Value"] for x in inst["PrefabInstance"]["Overrides"] if x.get("Component") == "Transform"}
    return tuple(float(o.get("m_LocalPosition." + a, "0")) for a in "xyz")


def main():
    global P
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", required=True)
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()
    P = args.project
    if os.path.isdir(os.path.join(P, *DEMO.split("/"))):
        run("DeleteAsset", DEMO, "--args", "-force -permanent -apply")

    step("Create the NPC prefab: an empty root with a nested cube")
    run("CreateAsset", "--args", '-spec="%s"' % spec("create_npc", {"Assets": [
        {"Kind": "Folder", "Path": DEMO},
        {"Kind": "Material", "Path": DEMO + "/NpcBody.mat", "Properties": {}},
        {"Kind": "Prefab", "Path": DEMO + "/NPC.prefab", "Root": "NPC", "Ops": [
            {"Op": "AddGameObject", "Parent": "NPC", "Name": "Body", "Components": [
                {"Type": "MeshFilter", "Properties": {"m_Mesh": CUBE}},
                {"Type": "MeshRenderer", "Properties": {"m_Materials": [DEMO + "/NpcBody.mat"]}}]}]}]}))
    npc = export("PrefabExport", DEMO + "/NPC.prefab")
    check([c["Type"] for c in node(npc, "NPC")["Components"]] == ["Transform"], "the root holds only a Transform")
    check(comp(npc, "NPC/Body", "MeshFilter")["Properties"]["m_Mesh"].get("Sub") == "Cube", "NPC/Body shows the built-in Cube mesh")

    step("Change the mesh to a cylinder and the material colour to green")
    edit = spec("cylinder", {"Targets": [{"AssetPath": DEMO + "/NPC.prefab", "Ops": [
        {"Op": "Set", "Path": "NPC/Body", "Component": "MeshFilter", "Properties": {"m_Mesh": CYLINDER}}]}]})
    run("EditPrefab", "--args", '-spec="%s"' % edit)
    run("EditPrefab", "--args", '-spec="%s" -apply' % edit)
    npc = export("PrefabExport", DEMO + "/NPC.prefab")
    check(comp(npc, "NPC/Body", "MeshFilter")["Properties"]["m_Mesh"].get("Sub") == "Cylinder", "NPC/Body now uses the Cylinder mesh")
    mat = export("MaterialExport", DEMO + "/NpcBody.mat", args="-full")
    colour = "_BaseColor" if "_BaseColor" in mat["PropertyInfo"] else "_Color"
    ok("the material's shader is %s, its colour property is %s (read from MaterialExport -full)" % (mat["Shader"], colour))
    run("EditMaterial", "--args", '-spec="%s" -apply' % spec("green", {"Targets": [{"AssetPath": DEMO + "/NpcBody.mat", "Properties": {colour: [0, 1, 0, 1]}}]}))
    mat = export("MaterialExport", DEMO + "/NpcBody.mat")
    check(mat["Properties"].get(colour) == [0, 1, 0, 1], "%s is green" % colour)

    step("Write the wander script (the agent writes the file, Unity compiles it on the next start)")
    with open(os.path.join(P, *DEMO.split("/"), "NpcWander.cs"), "w", encoding="utf-8") as f:
        f.write(SCRIPT)
    types = export("TypeCatalogExport", args="-filter=NpcWander")
    t = next((t for t in types["Types"] if t["Name"] == "NpcWander"), None)
    check(t is not None, "NpcWander compiled and is known to Unity")
    check({p["Path"] for p in t["Properties"]} >= {"radius", "speed", "minIdle", "maxIdle"}, "its fields: radius, speed, minIdle, maxIdle")

    step("Attach the script to the NPC prefab root")
    attach = spec("attach", {"Targets": [{"AssetPath": DEMO + "/NPC.prefab", "Ops": [
        {"Op": "AddComponent", "Path": "NPC", "Type": "NpcWander", "Properties": {"radius": 4}}]}]})
    run("EditPrefab", "--args", '-spec="%s" -apply' % attach)
    npc = export("PrefabExport", DEMO + "/NPC.prefab")
    wander = comp(npc, "NPC", "NpcWander")
    check(wander is not None and wander["Properties"].get("radius") == 4, "NPC has NpcWander with radius 4")

    step("Place 100 NPCs in a 10 x 10 square, 10 units apart")
    ops = []
    for r in range(10):
        for c in range(10):
            name = "NPC_r%d_c%d" % (r, c)
            ops.append({"Op": "AddPrefabInstance", "Prefab": DEMO + "/NPC.prefab", "Name": name})
            ops.append({"Op": "Set", "Path": name, "Component": "Transform", "Properties": {"m_LocalPosition": [c * 10, 0, r * 10]}})
    run("CreateAsset", "--args", '-spec="%s"' % spec("field", {"Assets": [{"Kind": "Scene", "Path": DEMO + "/NpcField.unity", "Ops": ops}]}))

    step("Verify the scene")
    scene = export("SceneExport", DEMO + "/NpcField.unity")
    roots = [n for n in scene["Hierarchy"] if "/" not in n["Path"]]
    npcs = [n for n in roots if n.get("PrefabInstance", {}).get("Source") == DEMO + "/NPC.prefab"]
    check(len(npcs) == 100, "100 NPC instances, all linked to NPC.prefab")
    spots = {position(n) for n in npcs}
    check(spots == {(c * 10.0, 0.0, r * 10.0) for r in range(10) for c in range(10)}, "one NPC on every point of the 10 x 10 grid, 10 units apart")
    code = run("AuditMissingReference", DEMO, expect=(0, 3)).get("ExitCode")
    check(code == 0, "AuditMissingReference finds nothing broken")

    step("Create a second prefab with just a cube mesh")
    run("CreateAsset", "--args", '-spec="%s"' % spec("prop", {"Assets": [{"Kind": "Prefab", "Path": DEMO + "/CubeProp.prefab", "Root": "CubeProp",
        "Components": [{"Type": "MeshFilter", "Properties": {"m_Mesh": CUBE}}, {"Type": "MeshRenderer", "Properties": {"m_Materials": [DEMO + "/NpcBody.mat"]}}]}]}))
    prop = export("PrefabExport", DEMO + "/CubeProp.prefab")
    check(comp(prop, "CubeProp", "MeshFilter")["Properties"]["m_Mesh"].get("Sub") == "Cube", "CubeProp is a cube")

    step("Query the scene and swap NPCs for CubeProp in a checkerboard")
    # Rows and columns come from the positions in the export, not from the names, the way an agent would query.
    swap = []
    ops = []
    for n in npcs:
        x, _, z = position(n)
        col, row = int(round(x / 10)), int(round(z / 10))
        # Odd positions (1st, 3rd, ...) in row 1, even ones in row 2, alternating row by row.
        if (col + row) % 2 == 0:
            swap.append((row, col))
            new = "Prop_r%d_c%d" % (row, col)
            ops += [{"Op": "Delete", "Path": n["Path"]},
                    {"Op": "AddPrefabInstance", "Prefab": DEMO + "/CubeProp.prefab", "Name": new},
                    {"Op": "Set", "Path": new, "Component": "Transform", "Properties": {"m_LocalPosition": [x, 0, z]}}]
    ok("%d NPCs to swap, row 0 columns %s, row 1 columns %s" % (
        len(swap), [c for r, c in sorted(swap) if r == 0], [c for r, c in sorted(swap) if r == 1]))
    migrate = spec("swap", {"Targets": [{"AssetPath": DEMO + "/NpcField.unity", "Ops": ops}]})
    run("EditScene", "--args", '-spec="%s"' % migrate)
    after_dry = export("SceneExport", DEMO + "/NpcField.unity")
    check(after_dry["Hierarchy"] == scene["Hierarchy"], "the dry run left the scene as it was")
    run("EditScene", "--args", '-spec="%s" -apply' % migrate)

    step("Verify the migration in the saved scene")
    scene = export("SceneExport", DEMO + "/NpcField.unity")
    roots = [n for n in scene["Hierarchy"] if "/" not in n["Path"]]
    by_source = {}
    for n in roots:
        by_source.setdefault(n.get("PrefabInstance", {}).get("Source"), []).append(n)
    npcs, props = by_source.get(DEMO + "/NPC.prefab", []), by_source.get(DEMO + "/CubeProp.prefab", [])
    check(len(roots) == 100 and len(npcs) == 50 and len(props) == 50, "100 objects: 50 NPC, 50 CubeProp, nothing else")
    grid = {}
    for n in npcs + props:
        x, _, z = position(n)
        grid[(int(round(z / 10)), int(round(x / 10)))] = "P" if n in props else "N"
    check(len(grid) == 100, "every grid point holds exactly one object")
    check(all(grid[(r, c)] == ("P" if (r + c) % 2 == 0 else "N") for r in range(10) for c in range(10)), "CubeProps sit on the checkerboard, NPCs on the rest")
    for r in range(3):
        ok("row %d: %s" % (r, " ".join(grid[(r, c)] for c in range(10))))
    code = run("AuditMissingReference", DEMO, expect=(0, 3)).get("ExitCode")
    check(code == 0, "AuditMissingReference still finds nothing broken")
    npc = export("PrefabExport", DEMO + "/NPC.prefab")
    check(comp(npc, "NPC", "NpcWander") is not None, "NPC.prefab still carries NpcWander")

    if not args.keep:
        run("DeleteAsset", DEMO, "--args", "-force -permanent -apply")
        ok("removed %s" % DEMO)
    print("\nscenario passed")


if __name__ == "__main__":
    try:
        main()
    finally:
        shutil.rmtree(TMP, ignore_errors=True)
