"""Publish verified, already-built artifacts. No private signing key is downloaded or used."""
import argparse
import copy
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import zipfile

MAX_ARCHIVE = 600 * 1024 * 1024
MAX_EXPANDED = 2 * 1024 * 1024 * 1024
TAG_PATTERN = r"v[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?"


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def checked_entries(archive, limit=MAX_EXPANDED):
    entries = archive.infolist()
    if len(entries) > 10000 or sum(e.file_size for e in entries) > limit:
        raise ValueError('Archive exceeds size/count limits')
    seen = set()
    for entry in entries:
        name = entry.filename.replace('\\', '/')
        parts = PurePosixPath(name).parts
        if (not parts or name.startswith('/') or ':' in name or '..' in parts or
                '\x00' in name or name in seen or stat.S_ISLNK(entry.external_attr >> 16)):
            raise ValueError('Unsafe or duplicate archive path')
        seen.add(name)
        yield name, entry


def repack_windows(source, destination, instructions):
    """Preserve every non-extension payload byte; retain only an installation README."""
    removed = 0
    with zipfile.ZipFile(source) as original, zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for name, entry in checked_entries(original):
            if name.lower().startswith('extensions/') or name.lower().startswith('extensions\\'):
                removed += 1
                continue
            if entry.is_dir() or name.endswith('/') or (entry.external_attr & 0x10):
                continue
            if re.search(r'(^|/)RouteShield-(chrome|firefox)\.(zip|xpi)$', name, re.I):
                raise ValueError('Unexpected embedded browser extension')
            item = copy.copy(entry)
            item.filename = name
            with original.open(entry) as src, output.open(item, 'w', force_zip64=True) as dst:
                shutil.copyfileobj(src, dst, 1024 * 1024)
        readme = zipfile.ZipInfo('extensions/README.txt', date_time=(2020, 1, 1, 0, 0, 0))
        readme.compress_type = zipfile.ZIP_DEFLATED
        output.writestr(readme, instructions)
    with zipfile.ZipFile(destination) as output:
        for name, entry in checked_entries(output):
            if name.lower().startswith('extensions/') and name != 'extensions/README.txt':
                raise ValueError('An extension remains in the Windows ZIP')
    return removed


def verify_addon(path, version):
    with zipfile.ZipFile(path) as addon:
        names = {name for name, _ in checked_entries(addon, 20 * 1024 * 1024)}
        if 'manifest.json' not in names or 'background.js' not in names:
            raise ValueError('Add-on manifest/background must be at archive root')
        manifest = json.loads(addon.read('manifest.json'))
        if manifest.get('version') != version or manifest.get('name') != 'RouteShield':
            raise ValueError('Add-on identity/version mismatch')


def validate_run(run, repository, workflow_id, expected_sha=None):
    if (run.get('status') != 'completed' or run.get('conclusion') != 'success' or
            run.get('event') != 'workflow_dispatch' or run.get('head_branch') != 'main' or
            run.get('head_repository', {}).get('full_name') != repository or
            run.get('workflow_id') != workflow_id or
            not re.fullmatch('[0-9a-f]{40}', run.get('head_sha', ''))):
        raise ValueError('Source must be a successful, manual, same-repository main build')
    if expected_sha and run['head_sha'] != expected_sha:
        raise ValueError('Source commit does not match the release request')


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlparse(newurl).scheme != 'https':
            raise ValueError('Non-HTTPS artifact redirect')
        result = super().redirect_request(req, fp, code, msg, headers, newurl)
        if urllib.parse.urlparse(newurl).hostname != urllib.parse.urlparse(req.full_url).hostname:
            for key in list(result.headers):
                if key.lower() == 'authorization':
                    del result.headers[key]
        return result


class GitHub:
    def __init__(self, repository, token):
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not token:
            raise ValueError('Repository/token missing')
        self.repository, self.token = repository, token
        self.opener = urllib.request.build_opener(SafeRedirect())

    def request(self, path, method='GET', data=None):
        raw = None if data is None else json.dumps(data).encode()
        req = urllib.request.Request('https://api.github.com/repos/' + self.repository + path,
            data=raw, method=method, headers={'Authorization': 'Bearer ' + self.token,
            'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
            'Content-Type': 'application/json', 'User-Agent': 'RouteShield-Release'})
        with self.opener.open(req, timeout=60) as response:
            return json.load(response)

    def download(self, artifact, destination):
        expected = artifact.get('digest', '')
        if artifact.get('expired') or not re.fullmatch('sha256:[0-9a-f]{64}', expected):
            raise ValueError('Artifact expired or trusted GitHub digest missing')
        req = urllib.request.Request('https://api.github.com/repos/' + self.repository +
            '/actions/artifacts/' + str(artifact['id']) + '/zip', headers={
                'Authorization': 'Bearer ' + self.token, 'User-Agent': 'RouteShield-Release'})
        size = 0
        with self.opener.open(req, timeout=60) as response, destination.open('wb') as output:
            while chunk := response.read(1024 * 1024):
                size += len(chunk)
                if size > MAX_ARCHIVE:
                    raise ValueError('Artifact download too large')
                output.write(chunk)
        if 'sha256:' + digest(destination) != expected:
            raise ValueError('Downloaded artifact digest does not match GitHub')


def write_output(name, value):
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write(name + '=' + str(value) + '\n')


def prepare(api, output):
    event = os.environ['GITHUB_EVENT_NAME']
    expected_sha = None
    if event == 'push':
        plan = json.loads(Path('ci/release-request.json').read_text())
        run_id, tag, expected_sha = plan['run_id'], plan['tag'], plan['source_sha']
    elif event == 'workflow_run':
        run_id = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())['workflow_run']['id']
        tag = ''
    else:
        run_id, tag = int(os.environ['SOURCE_RUN_ID']), os.environ.get('RELEASE_TAG', '').strip()
    if isinstance(run_id, bool) or not isinstance(run_id, int) or run_id <= 0:
        raise ValueError('Invalid source run ID')
    run = api.request('/actions/runs/' + str(run_id))
    workflow = api.request('/actions/workflows/build-apps.yml')
    validate_run(run, api.repository, workflow['id'], expected_sha)
    subprocess.run(['git', 'merge-base', '--is-ancestor', run['head_sha'], 'HEAD'], check=True)
    jobs = api.request('/actions/runs/' + str(run_id) + '/jobs?per_page=100')['jobs']
    names = {job['name']: job['conclusion'] for job in jobs}
    if names.get('Android owner-signed release') != 'success':
        if event == 'workflow_run':
            print('No owner-signed output; no release will be published.')
            write_output('ready', 'false')
            return
        raise ValueError('Owner-signed Android job did not succeed')
    for name in ('Windows portable ZIP', 'Android preview and unsigned release'):
        if names.get(name) != 'success':
            raise ValueError('Required build/test job did not succeed')
    number = run['run_number']
    artifacts = api.request('/actions/runs/' + str(run_id) + '/artifacts?per_page=100')['artifacts']
    inputs = {}
    temp = Path(os.environ.get('RUNNER_TEMP', 'build')) / 'release-source'
    temp.mkdir(parents=True, exist_ok=True)
    for kind, name in [('windows', 'RouteShield-Windows-win-x64-' + str(number)),
                       ('android', 'RouteShield-Android-signed-release-' + str(number))]:
        matches = [artifact for artifact in artifacts if artifact['name'] == name]
        if len(matches) != 1:
            raise ValueError('Expected source artifact missing or ambiguous')
        inputs[kind] = matches[0]
        api.download(matches[0], temp / (kind + '.zip'))
    if output.exists() and (output.is_symlink() or any(output.iterdir())):
        raise ValueError("Release output must be a fresh empty directory")
    output.mkdir(parents=True, exist_ok=True)
    files = {}
    with zipfile.ZipFile(temp / 'windows.zip') as archive:
        for name, entry in checked_entries(archive, MAX_ARCHIVE):
            if entry.is_dir() or name.endswith('/') or (entry.external_attr & 0x10):
                continue
            base = PurePosixPath(name).name
            if base in files:
                raise ValueError('Duplicate artifact basename')
            if not re.fullmatch(r'RouteShield-[A-Za-z0-9.-]+\.(zip|xpi)(?:\.sha256)?', base):
                raise ValueError('Unexpected Windows artifact filename')
            path = temp / base
            with archive.open(entry) as src, path.open('wb') as dst:
                shutil.copyfileobj(src, dst, 1024 * 1024)
            files[base] = path
    windows = [name for name in files if re.fullmatch(r'RouteShield-[0-9]+(?:\.[0-9]+){1,3}-win-x64\.zip', name)]
    if len(windows) != 1:
        raise ValueError('Expected exactly one Windows distribution')
    windows_name = windows[0]
    version = windows_name.removeprefix('RouteShield-').removesuffix('-win-x64.zip')
    if not tag:
        tag = 'v' + version + '-build' + str(number)
        if run.get('run_attempt', 1) > 1:
            tag += '-attempt' + str(run['run_attempt'])
    if not re.fullmatch(TAG_PATTERN, tag):
        raise ValueError('Unsafe/invalid release tag')
    # Legacy builds bundled an XPI inside the desktop ZIP. Recover it before removing the bundle.
    for browser, suffix in [('chrome', 'zip'), ('firefox', 'xpi')]:
        addon_name = f'RouteShield-Extension-{browser}-{version}.{suffix}'
        if addon_name in files:
            shutil.copyfile(files[addon_name], output / addon_name)
        else:
            with zipfile.ZipFile(files[windows_name]) as archive:
                match = [entry for name, entry in checked_entries(archive)
                         if name == f'extensions/RouteShield-{browser}.{suffix}']
                if len(match) != 1:
                    raise ValueError('Expected separately built add-on missing')
                with archive.open(match[0]) as src, (output / addon_name).open('wb') as dst:
                    shutil.copyfileobj(src, dst, 1024 * 1024)
        verify_addon(output / addon_name, version)
    instructions = ('Browser extensions are separate release downloads, NOT bundled in this Windows ZIP.\n'
        f'https://github.com/{api.repository}/releases/tag/{tag}\n\n'
        'Chrome: download the Chrome ZIP, extract to extensions/chrome, then use chrome://extensions -> Developer mode -> Load unpacked.\n'
        'Firefox: download the XPI separately. Standard Firefox requires Mozilla/AMO signing for permanent installation; '
        'this self-hosted package can be tested temporarily through about:debugging.\n')
    removed = repack_windows(files[windows_name], output / windows_name, instructions)
    expected_pin = os.environ.get('ANDROID_SIGNING_CERT_SHA256', '').replace(':', '').strip().lower()
    if not re.fullmatch('[0-9a-f]{64}', expected_pin):
        raise ValueError('Owner signing certificate pin missing')
    apksigner = Path(os.environ['ANDROID_HOME']) / 'build-tools/35.0.0/apksigner'
    android_versions = {}
    with zipfile.ZipFile(temp / 'android.zip') as archive:
        entries = dict(checked_entries(archive, MAX_ARCHIVE))
        apks = {PurePosixPath(name).name: entry for name, entry in entries.items() if name.endswith('.apk')}
        if set(apks) != {'app-arm64-v8a-release.apk', 'app-x86_64-release.apk'}:
            raise ValueError('Expected exactly two owner release APKs')
        for abi in ('arm64-v8a', 'x86_64'):
            name = f'RouteShield-Android-{abi}-build{number}.apk'
            with archive.open(apks[f'app-{abi}-release.apk']) as src, (output / name).open('wb') as dst:
                shutil.copyfileobj(src, dst, 1024 * 1024)
            result = subprocess.run([str(apksigner), 'verify', '--print-certs', str(output / name)], capture_output=True, text=True)
            pins = re.findall(r'certificate SHA-256 digest:\s*([0-9a-fA-F]{64})', result.stdout)
            if result.returncode or len(pins) != 1 or pins[0].lower() != expected_pin:
                raise ValueError('APK signature or owner certificate verification failed')
            aapt = apksigner.with_name('aapt')
            badging = subprocess.run([str(aapt), 'dump', 'badging', str(output / name)], capture_output=True, text=True, check=True).stdout
            package = re.search(r"package: name='([^']+)' versionCode='([0-9]+)' versionName='([^']+)'", badging)
            if (not package or package[1] != 'com.nourix.routeshield' or int(package[2]) != number + 1000
                    or not re.fullmatch(r'[0-9A-Za-z.+_-]{1,80}', package[3])):
                raise ValueError('APK package/version code does not match the owner build')
            with zipfile.ZipFile(output / name) as apk:
                abis = {p.split('/')[1] for p in apk.namelist() if p.startswith('lib/') and p.endswith('.so')}
            if abis != {abi}:
                raise ValueError('Unexpected APK ABI')
            android_versions[abi] = {'version_code': int(package[2]), 'version_name': package[3]}
    (output / 'signing-certificate-sha256.txt').write_text(expected_pin + '\n')
    (output / 'INSTALL.txt').write_text(instructions + '\nAndroid: choose arm64-v8a for most phones; x86_64 is for matching devices/emulators. '
        'These are owner-signed release APKs, not the debug/diagnostic variants. Different package IDs keep separate local profiles. '
        'Do not uninstall an existing app without considering local profile loss.\n\n'
        'Windows is not Authenticode signed. Firefox XPI is not claimed to be Mozilla-signed. '
        'This is a pre-release snapshot: the user reports functional testing passed; independent DNS/IPv6/leakage/lockdown verification and libbox provenance/audit gates remain.\n')
    provenance = {'tag': tag, 'source_run_id': run_id, 'source_run_number': number, 'source_sha': run['head_sha'],
        'source_url': run['html_url'], 'publisher_sha': os.environ.get('GITHUB_SHA'), 'source_artifacts':
        {key: {'id': item['id'], 'name': item['name'], 'digest': item['digest']} for key, item in inputs.items()},
        'android': android_versions, 'windows_version': version, 'removed_windows_extension_entries': removed,
        'binary_checksums': {file.name: digest(file) for file in output.iterdir() if file.is_file()}}
    (output / 'BUILD-PROVENANCE.json').write_text(json.dumps(provenance, indent=2) + '\n')
    (output / 'SHA256SUMS.txt').write_text(''.join(digest(file) + '  ' + file.name + '\n'
        for file in sorted(output.iterdir()) if file.is_file()))
    # SHA256SUMS was not present while computing its contents; never hash itself.
    notes = (f'## Preview release {tag}\n\nBuilt and tested in [GitHub Actions run {number}]({run["html_url"]}), source `{run["head_sha"]}`.\n\n'
        f'- Windows x64 ZIP: application/core only; extension code/packages removed from the Windows archive. Internal Windows/add-on version: {version}.\n'
        '- Chrome ZIP and Firefox XPI are separate downloads. Firefox permanent installation requires AMO signing.\n'
        f'- Owner-signed Android ARM64 and x86_64 APKs: internal version {android_versions["arm64-v8a"]["version_name"]}, code {android_versions["arm64-v8a"]["version_code"]}.\n'
        '- SHA256SUMS and source provenance included. Android signatures reverified against the owner certificate pin before publishing.\n\n'
        '**Pre-release:** the user reports successful functional testing. Independent DNS/IPv6 leakage, sustained quality and OS-lockdown verification remain; third-party libbox audit/provenance gate remains. Windows is not Authenticode signed.\n')
    context = {'tag': tag, 'source_sha': run['head_sha'], 'notes': notes}
    (temp / 'publish-context.json').write_text(json.dumps(context))
    write_output('ready', 'true')
    print('Prepared five separate installable packages with verified owner APK signatures.')


def publish(api, output):
    temp = Path(os.environ.get('RUNNER_TEMP', 'build')) / 'release-source'
    context = json.loads((temp / 'publish-context.json').read_text())
    tag = context['tag']
    try:
        release = api.request('/releases/tags/' + urllib.parse.quote(tag, safe=''))
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = api.request('/releases', 'POST', {'tag_name': tag, 'target_commitish': context['source_sha'],
            'name': 'RouteShield ' + tag.removeprefix('v'), 'body': context['notes'], 'draft': True,
            'prerelease': True, 'make_latest': 'false'})
    if release.get('target_commitish') != context['source_sha']:
        raise ValueError('Release target differs; refusing to overwrite')
    assets = {item['name']: item for item in api.request('/releases/' + str(release['id']) + '/assets?per_page=100')}
    for file in sorted(output.iterdir()):
        if not file.is_file():
            continue
        old = assets.get(file.name)
        if old:
            if old.get('digest') != 'sha256:' + digest(file) or old.get('size') != file.stat().st_size:
                raise ValueError('Existing asset differs; refusing to overwrite')
            continue
        if not release['draft']:
            raise ValueError('Published release is incomplete; refusing to modify')
        result = subprocess.run(['gh', 'release', 'upload', tag, str(file), '--repo', api.repository], capture_output=True, text=True)
        if result.returncode:
            raise ValueError('Release upload failed; draft retained for safe retry')
    final = {item['name']: item for item in api.request('/releases/' + str(release['id']) + '/assets?per_page=100')}
    expected = {file.name for file in output.iterdir() if file.is_file()}
    if set(final) != expected:
        raise ValueError('Release asset set differs; draft will not be published')
    for name, item in final.items():
        if item.get('digest') != 'sha256:' + digest(output / name) or item.get('size') != (output / name).stat().st_size:
            raise ValueError('Uploaded release asset digest/size mismatch')
    if release['draft']:
        release = api.request('/releases/' + str(release['id']), 'PATCH', {'draft': False, 'prerelease': True, 'make_latest': 'false'})
    print('Published verified pre-release: ' + release['html_url'])
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
            summary.write('## Published release\n' + release['html_url'] + '\n\nFive installable packages are separate; browser extensions are not bundled in the Windows ZIP.\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['prepare', 'publish'])
    parser.add_argument('--output', default='release-assets')
    args = parser.parse_args()
    api = GitHub(os.environ['GITHUB_REPOSITORY'], os.environ['GH_TOKEN'])
    try:
        (prepare if args.mode == 'prepare' else publish)(api, Path(args.output))
    except Exception as error:
        # No response bodies, authorization headers, signing material or raw native output in logs.
        print('::error::Release failed: ' + type(error).__name__ + ': ' + str(error).splitlines()[0][:300])
        raise SystemExit(1)
