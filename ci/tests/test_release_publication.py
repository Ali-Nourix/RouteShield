import importlib.util
import io
import json
from pathlib import Path
import stat
import tempfile
import unittest
import urllib.request
import warnings
import zipfile

spec = importlib.util.spec_from_file_location('release_module', Path(__file__).parents[1] / 'scripts/publish-built-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def zip_bytes(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        for name, data in entries:
            archive.writestr(name, data)
    stream.seek(0)
    return stream


class PublicationSafetyTest(unittest.TestCase):
    def test_windows_keeps_every_application_byte_but_no_addon_code(self):
        with tempfile.TemporaryDirectory() as root:
            source = Path(root) / 'original.zip'
            with zipfile.ZipFile(source, 'w') as archive:
                archive.writestr('RouteShield.exe', b'unit-test-application')
                archive.writestr('sing-box.exe', b'unit-test-core')
                archive.writestr('extensions\\chrome\\manifest.json', '{}')
                archive.writestr('extensions/RouteShield-firefox.xpi', b'unit-test-addon')
            target = Path(root) / 'separate.zip'
            self.assertEqual(2, release.repack_windows(source, target, 'Separate downloads'))
            with zipfile.ZipFile(target) as archive:
                self.assertEqual({'RouteShield.exe', 'sing-box.exe', 'extensions/README.txt'}, set(archive.namelist()))
                self.assertEqual(b'unit-test-application', archive.read('RouteShield.exe'))
                self.assertEqual(b'unit-test-core', archive.read('sing-box.exe'))

    def test_repacking_is_deterministic_for_safe_retry(self):
        with tempfile.TemporaryDirectory() as root:
            source = Path(root) / 'source.zip'
            source.write_bytes(zip_bytes([('RouteShield.exe', b'fixed')]).getvalue())
            paths = [Path(root) / 'a.zip', Path(root) / 'b.zip']
            for path in paths:
                release.repack_windows(source, path, 'Fixed instructions')
            self.assertEqual(release.digest(paths[0]), release.digest(paths[1]))

    def test_zip_slip_and_drive_paths_rejected(self):
        for name in ('../x', '/x', 'C:/x', 'folder/../../x'):
            with self.subTest(name=name), zipfile.ZipFile(zip_bytes([(name, b'x')])) as archive:
                with self.assertRaises(ValueError):
                    list(release.checked_entries(archive))

    def test_symlinks_rejected(self):
        info = zipfile.ZipInfo('link')
        info.external_attr = (stat.S_IFLNK | 0o777) << 16
        with zipfile.ZipFile(zip_bytes([(info, '../private')])) as archive:
            with self.assertRaises(ValueError):
                list(release.checked_entries(archive))

    def test_duplicate_paths_rejected(self):
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            stream = zip_bytes([('x', b'a'), ('x', b'b')])
        with zipfile.ZipFile(stream) as archive:
            with self.assertRaises(ValueError):
                list(release.checked_entries(archive))

    def test_expanded_size_limit(self):
        with zipfile.ZipFile(zip_bytes([('x', b'X' * 32)])) as archive:
            with self.assertRaises(ValueError):
                list(release.checked_entries(archive, 16))

    def test_addon_is_separate_with_root_manifest_and_expected_version(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'chrome.zip'
            path.write_bytes(zip_bytes([('manifest.json', json.dumps({'name': 'RouteShield', 'version': '1.7.1'})), ('background.js', '// fixture')]).getvalue())
            release.verify_addon(path, '1.7.1')
            with self.assertRaises(ValueError):
                release.verify_addon(path, '1.7.2')

    def test_nested_manifest_is_not_an_installable_addon(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'bad.zip'
            path.write_bytes(zip_bytes([('folder/manifest.json', '{}'), ('folder/background.js', '// fixture')]).getvalue())
            with self.assertRaises(ValueError):
                release.verify_addon(path, '1.7.1')

    def test_only_successful_owner_main_manual_run_accepted(self):
        base = {'status': 'completed', 'conclusion': 'success', 'event': 'workflow_dispatch', 'head_branch': 'main',
                'head_repository': {'full_name': 'Ali-Nourix/RouteShield'}, 'workflow_id': 10, 'head_sha': 'a' * 40}
        release.validate_run(base, 'Ali-Nourix/RouteShield', 10, 'a' * 40)
        for patch in ({'event': 'pull_request'}, {'head_branch': 'fix/x'}, {'conclusion': 'failure'},
                      {'head_repository': {'full_name': 'foreign/RouteShield'}}, {'workflow_id': 11}):
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                release.validate_run(base | patch, 'Ali-Nourix/RouteShield', 10)
        with self.assertRaises(ValueError):
            release.validate_run(base, 'Ali-Nourix/RouteShield', 10, 'b' * 40)

    def test_cross_host_redirect_does_not_forward_github_token(self):
        req = urllib.request.Request('https://api.github.com/artifact', headers={'Authorization': 'Bearer fixture-token'})
        redirect = release.SafeRedirect().redirect_request(req, None, 302, 'Found', {}, 'https://blob.example/file')
        self.assertFalse(any(name.lower() == 'authorization' for name in redirect.headers))
        same = release.SafeRedirect().redirect_request(req, None, 302, 'Found', {}, 'https://api.github.com/other')
        self.assertEqual('Bearer fixture-token', same.get_header('Authorization'))
        with self.assertRaises(ValueError):
            release.SafeRedirect().redirect_request(req, None, 302, 'Found', {}, 'http://blob.example/file')
