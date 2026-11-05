"""Every key a command reads with Args.Has is a switch, so "-x=false" turns it off, or a key that takes a value."""
import os
import re
import unittest

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EDITOR = os.path.join(ROOT, "Package", "Editor")
# Keys that take a value, where Has only asks whether one was given.
VALUE_KEYS = {"datasuffix", "normalsuffix", "scandir", "spec"}


def switches():
    with open(os.path.join(EDITOR, "Core", "RunArgs.cs"), encoding="utf-8") as f:
        m = re.search(r"Switches\s*=\s*\{([^}]*)\}", f.read())
    return set(re.findall(r'"([a-z]+)"', m.group(1)))


def has_keys():
    keys = {}
    for folder, _, files in os.walk(EDITOR):
        for name in files:
            if name.endswith(".cs"):
                with open(os.path.join(folder, name), encoding="utf-8") as f:
                    for key in re.findall(r'Args\.Has\("([A-Za-z]+)"\)', f.read()):
                        keys.setdefault(key.lower(), name)
    return keys


class SwitchTests(unittest.TestCase):
    def test_every_has_key_is_a_switch_or_takes_a_value(self):
        known = switches()
        self.assertIn("apply", known)
        missing = {k: f for k, f in has_keys().items() if k not in known and k not in VALUE_KEYS}
        self.assertEqual({}, missing, "read with Args.Has but not in RunArgs.Switches, so =false turns them on")


if __name__ == "__main__":
    unittest.main()
