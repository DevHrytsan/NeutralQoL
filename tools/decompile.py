"""Decompile game assemblies into decomp/<name>/ for reading and scanning.

Uses ilspycmd from the repo's dotnet tool manifest, so the decompiler
version is pinned and the same on every machine.

    python tools/decompile.py [--game PATH] [Assembly-CSharp ...]

Without --game it uses the same game folder as dotnet build.
"""
import argparse
import os
import shutil
import subprocess
import sys

from gamepath import REPO, build_game_path, managed_dir


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('assemblies', nargs='*', default=['Assembly-CSharp'],
                    help='assembly names without .dll (default Assembly-CSharp)')
    ap.add_argument('--game', help='game install folder (default: the one dotnet build uses)')
    args = ap.parse_args()

    args.game = args.game or build_game_path()
    managed = managed_dir(args.game) if args.game else ''
    if not os.path.isdir(managed):
        sys.exit('game not found: run python tools/setup.py or pass --game')

    subprocess.run(['dotnet', 'tool', 'restore'], cwd=REPO, check=True)
    for name in args.assemblies:
        dll = os.path.join(managed, name + '.dll')
        out = os.path.join(REPO, 'decomp', name)
        if not os.path.isfile(dll):
            sys.exit(f'{dll} not found')
        # A stale tree from an older game build would mix old and new code.
        shutil.rmtree(out, ignore_errors=True)
        print(f'decompiling {name} -> {os.path.relpath(out, REPO)}')
        subprocess.run(['dotnet', 'ilspycmd', '-p', '-o', out, '-r', managed, dll], cwd=REPO, check=True)

    build_hash = os.path.join(args.game, 'build-hash.txt')
    if os.path.isfile(build_hash):
        shutil.copy(build_hash, os.path.join(REPO, 'decomp', 'build-hash.txt'))


if __name__ == '__main__':
    main()
