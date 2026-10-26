import importlib.util
import tempfile
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("bump_version", ROOT / "scripts/bump-version.py")
bump = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bump)

class ProductVersionTests(unittest.TestCase):
    def test_product_manifest_and_user_agents_match(self):
        current, next_version, changes = bump.plan(ROOT)
        self.assertEqual(4, len(changes))
        a, b, c = map(int, current.split("."))
        self.assertEqual(f"{a}.{b}.{c+1}", next_version)

    def test_mismatches_fail_before_any_files_are_written(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative, _, _ in bump.TARGETS:
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text((ROOT / relative).read_text())
            bad = root / bump.TARGETS[1][0]
            bad.write_text(re.sub(bump.TARGETS[1][1], "0.0.1.0", bad.read_text(), count=1))
            before = (root / bump.TARGETS[0][0]).read_text()
            with self.assertRaises(ValueError):
                bump.plan(root)
            self.assertEqual(before, (root / bump.TARGETS[0][0]).read_text())
