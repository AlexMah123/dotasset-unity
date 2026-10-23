#!/usr/bin/env python3
"""Writes a .meta for every file and folder of Package/ that lacks one.

A package installed from a git URL is immutable, and Unity ignores any file in it without a .meta.
GUIDs are derived from the path, so regenerating gives the same GUIDs. Folders ending in ~ are skipped,
Unity does not import them.
Usage: python tools/gen_meta.py [--check]
"""
import hashlib
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Package")

TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n" + TAIL,
    ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n" + TAIL,
    ".json": "PackageManifestImporter:\n  externalObjects: {}\n" + TAIL,
    ".md": "TextScriptImporter:\n  externalObjects: {}\n" + TAIL,
}
FOLDER = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n" + TAIL


def guid(rel):
    return hashlib.md5(("com.assetworkbench.editor/" + rel).encode("utf-8")).hexdigest()


def wanted():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = sorted(d for d in dirnames if not d.endswith("~") and not d.startswith("."))
        for d in dirnames:
            yield os.path.join(dirpath, d), True
        for f in sorted(filenames):
            if f.endswith(".meta") or f.startswith("."):
                continue
            yield os.path.join(dirpath, f), False


def body(path, is_dir):
    rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
    if is_dir:
        importer = FOLDER
    else:
        ext = os.path.splitext(path)[1].lower()
        importer = IMPORTERS.get(ext, "DefaultImporter:\n  externalObjects: {}\n" + TAIL)
    return "fileFormatVersion: 2\nguid: %s\n%s" % (guid(rel), importer)


def main():
    check = "--check" in sys.argv
    missing = []
    for path, is_dir in wanted():
        meta = path + ".meta"
        if os.path.exists(meta):
            continue
        missing.append(meta)
        if not check:
            with open(meta, "w", newline="\n") as f:
                f.write(body(path, is_dir))
    stale = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if not d.endswith("~")]
        for f in filenames:
            if f.endswith(".meta") and not os.path.exists(os.path.join(dirpath, f[:-5])):
                stale.append(os.path.join(dirpath, f))
    for m in missing:
        print(("missing " if check else "wrote ") + os.path.relpath(m, ROOT))
    for s in stale:
        print("stale " + os.path.relpath(s, ROOT))
    return 1 if (check and missing) or stale else 0


if __name__ == "__main__":
    sys.exit(main())
