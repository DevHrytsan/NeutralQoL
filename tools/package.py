"""Build a release zip for the Nuclear Option Mod Manager (NOMM).

NOMM unpacks a mod archive into BepInEx/disabledPlugins/<id>/ and moves it to
plugins/ when enabled, so NeutralQoL.dll sits at the root of the zip. Also writes
the NOMNOM registry manifest for that release (version, download URL, hash).

    python tools/package.py --game-version 0.34.2 [--owner NAME] [--repo NAME]

Upload dist/NeutralQoL.zip to a GitHub release tagged v<version>. The manifest in
dist/NeutralQoL.json goes into NOMNOM's modManifests/ folder by pull request; once
listed with autoUpdateArtifacts, NOMNOM picks up later releases by itself.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import zipfile

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
PROJECT = os.path.join(REPO, 'src', 'NeutralQoL', 'NeutralQoL.csproj')
DIST = os.path.join(REPO, 'dist')
MOD_ID = 'NeutralQoL'


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('--game-version', required=True, help='latest game version the release supports, e.g. 0.34.2')
    ap.add_argument('--owner', default='DevHrytsan', help='GitHub owner of the release repo')
    ap.add_argument('--repo', default='NeutralQoL', help='GitHub repo that hosts releases')
    args = ap.parse_args()

    with open(PROJECT, encoding='utf-8') as f:
        match = re.search(r'<Version>([^<]+)</Version>', f.read())
    if not match:
        sys.exit(f'no <Version> in {PROJECT}')
    version = match.group(1)

    subprocess.run(['dotnet', 'build', PROJECT, '-c', 'Release', '-p:DeployToGame=false'], cwd=REPO, check=True)
    dll = os.path.join(REPO, 'src', 'NeutralQoL', 'bin', 'Release', MOD_ID + '.dll')

    os.makedirs(DIST, exist_ok=True)
    archive = os.path.join(DIST, MOD_ID + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        z.write(dll, MOD_ID + '.dll')
        # The repo README is for developers and carries private notes; the license is what a download needs.
        z.write(os.path.join(REPO, 'LICENSE'), 'LICENSE')
    with open(archive, 'rb') as f:
        digest = hashlib.sha256(f.read()).hexdigest()

    url = f'https://github.com/{args.owner}/{args.repo}'
    manifest = {
        'id': MOD_ID,
        'displayName': 'NeutralQoL',
        'description': 'Small fixes and useful extras for menus, the HUD, the map and controls. '
                       'Client side only.',
        'tags': ['QoL'],
        'urls': [{'name': 'info', 'url': url}],
        'authors': [args.owner],
        'isClientOrServer': 'Client',
        'githubOwner': args.owner,
        'githubRepoName': args.repo,
        'autoUpdateArtifacts': 'True',
        'releaseAssetName': MOD_ID + '.zip',
        'artifacts': [{
            'fileName': MOD_ID + '.zip',
            'version': version,
            'category': 'Release',
            'type': 'plugin',
            'gameVersion': args.game_version,
            'downloadUrl': f'{url}/releases/download/v{version}/{MOD_ID}.zip',
            'hash': 'sha256:' + digest,
        }],
    }
    with open(os.path.join(DIST, MOD_ID + '.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(manifest, f, indent=2)
        f.write('\n')

    print(f'{os.path.relpath(archive, REPO)}  v{version}  sha256:{digest}')
    print(f'tag the GitHub release v{version} and upload {MOD_ID}.zip unchanged, or the hash will not match')


if __name__ == '__main__':
    main()
