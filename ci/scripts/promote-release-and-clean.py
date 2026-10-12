"""Owner-requested promotion and cleanup with pinned release assets and branch heads."""
import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def api(path, method='GET', data=None):
    request = urllib.request.Request('https://api.github.com/repos/' + os.environ['GITHUB_REPOSITORY'] + path,
        data=None if data is None else json.dumps(data).encode(), method=method,
        headers={'Authorization': 'Bearer ' + os.environ['GH_TOKEN'],
                 'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json',
                 'X-GitHub-Api-Version': '2022-11-28'})
    with urllib.request.urlopen(request, timeout=60) as response:
        return None if response.status == 204 else json.load(response)


def main():
    if os.environ['GITHUB_REF'] != 'refs/heads/main':
        raise ValueError('Maintenance only runs on main')
    plan = json.loads(Path('ci/repository-maintenance.json').read_text())
    release = api('/releases/tags/' + urllib.parse.quote(plan['tag'], safe=''))
    if release['id'] != plan['release_id'] or release['draft']:
        raise ValueError('Expected public release differs')
    assets = api('/releases/' + str(release['id']) + '/assets?per_page=100')
    snapshot = {a['name']: {'size': a['size'], 'digest': a['digest']} for a in assets}
    if snapshot != plan['assets']:
        raise ValueError('Release assets changed; no promotion or deletion performed')
    tag = api('/git/ref/tags/' + urllib.parse.quote(plan['tag'], safe=''))
    if tag['object']['sha'] != plan['source_sha']:
        raise ValueError('Release tag target changed')
    repository = api('')
    approved = []
    # Preflight every branch before changing release or deleting any ref.
    for branch, expected in plan['delete_merged_branches'].items():
        if branch in ('main', repository['default_branch']):
            raise ValueError('Cannot remove default/main branch')
        path = '/branches/' + urllib.parse.quote(branch, safe='')
        try:
            current = api(path)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                continue
            raise
        if current['protected'] or current['commit']['sha'] != expected:
            raise ValueError('Branch is protected or changed since approval')
        subprocess.run(['git', 'fetch', '--quiet', 'origin', expected], check=True)
        ancestor = subprocess.run(['git', 'merge-base', '--is-ancestor', expected, 'HEAD'])
        if ancestor.returncode:
            # Squash merges have different commit IDs; require every patch already present.
            cherry = subprocess.check_output(['git', 'cherry', 'HEAD', expected], text=True)
            if ancestor.returncode != 1 or any(not line.startswith('- ') for line in cherry.splitlines()):
                raise ValueError('Branch contains unmerged changes; refusing deletion')
        approved.append((branch, expected))
    body = release['body'].replace('## Preview release ', '## Release ').replace('**Pre-release:**', '**Verification status:**')
    api('/releases/' + str(release['id']), 'PATCH', {'prerelease': False, 'make_latest': 'true', 'body': body})
    for branch, expected in approved:
        encoded = urllib.parse.quote(branch, safe='')
        ref = api('/git/ref/heads/' + encoded)
        if ref['object']['sha'] != expected:
            raise ValueError('Branch moved; refusing deletion')
        api('/git/refs/heads/' + encoded, 'DELETE')
        print('Removed merged branch: ' + branch)
    final = api('/releases/tags/' + urllib.parse.quote(plan['tag'], safe=''))
    final_assets = api('/releases/' + str(release['id']) + '/assets?per_page=100')
    if final['prerelease'] or {a['name']: {'size': a['size'], 'digest': a['digest']} for a in final_assets} != snapshot:
        raise ValueError('Final release verification failed')
    print('Published normal release: ' + final['html_url'])
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
        summary.write('## Release promoted\n' + final['html_url'] + '\n\nAssets unchanged. Removed only pinned, already-merged branches.\n')


if __name__ == '__main__':
    main()
