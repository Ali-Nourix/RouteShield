import base64
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/restore-android-key.py'
class RestoreKeyTests(unittest.TestCase):
    def invoke(self, root, **changes):
        env = os.environ.copy()
        env.update(RUNNER_TEMP=str(root), ANDROID_KEYSTORE_PATH=str(root / 'key.jks'),
                   ANDROID_KEYSTORE_BASE64=base64.b64encode(b'test-fixture-not-a-real-key' * 10).decode(),
                   ANDROID_KEYSTORE_PASSWORD='fixture', ANDROID_KEY_ALIAS='fixture',
                   ANDROID_KEY_PASSWORD='fixture', ANDROID_SIGNING_CERT_SHA256='0' * 64)
        env.update(changes)
        return subprocess.run([sys.executable, str(SCRIPT)], env=env, capture_output=True, text=True)
    def test_valid_restore_is_private_and_not_logged(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); result = self.invoke(root)
            self.assertEqual(0, result.returncode)
            self.assertEqual(0o600, (root / 'key.jks').stat().st_mode & 0o777)
            self.assertEqual('', result.stdout)
    def test_missing_secret_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertNotEqual(0, self.invoke(Path(tmp), ANDROID_KEY_PASSWORD='').returncode)
    def test_invalid_base64_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertNotEqual(0, self.invoke(Path(tmp), ANDROID_KEYSTORE_BASE64='not-base64!').returncode)
    def test_outside_temporary_directory_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertNotEqual(0, self.invoke(Path(tmp), ANDROID_KEYSTORE_PATH=str(Path(tmp).parent / 'unsafe.jks')).returncode)
    def test_existing_file_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root/'key.jks').write_text('existing')
            self.assertNotEqual(0, self.invoke(root).returncode)
            self.assertEqual('existing', (root/'key.jks').read_text())
    def test_oversize_secret_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertNotEqual(0, self.invoke(Path(tmp), ANDROID_KEYSTORE_BASE64='a' * 48001).returncode)
if __name__ == '__main__': unittest.main()
