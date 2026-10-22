"""Find Nuclear Option and write GamePath.user.props for this clone.

Only needed when the build cannot find the game by itself, which happens when
it is in a second Steam library or outside Steam. Searches every Steam library
listed in libraryfolders.vdf, checks that BepInEx 5 is installed and writes
the gitignored GamePath.user.props that Directory.Build.props imports.

    python tools/setup.py                  find the game, write the file
    python tools/setup.py --game "E:\\Games\\Nuclear Option"
    python tools/setup.py --deploy         also copy every build into the game
    python tools/setup.py --show           print what the build uses now
"""
import argparse
import os
import re
import sys
from xml.sax.saxutils import escape

import gamepath

APP_ID = '2168680'
GAME_DIR = 'Nuclear Option'
USER_PROPS = os.path.join(gamepath.REPO, 'GamePath.user.props')


def steam_roots():
    roots = []
    if sys.platform == 'win32':
        import winreg
        for hive, key, value in [(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
                                 (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath')]:
            try:
                with winreg.OpenKey(hive, key) as k:
                    roots.append(winreg.QueryValueEx(k, value)[0])
            except OSError:
                pass
    else:
        home = os.path.expanduser('~')
        roots += [os.path.join(home, '.steam', 'steam'), os.path.join(home, '.local', 'share', 'Steam'),
                  os.path.join(home, '.var', 'app', 'com.valvesoftware.Steam', 'data', 'Steam')]
    return [os.path.normpath(r) for r in roots if os.path.isdir(r)]


def library_folders(root):
    libs = [root]
    vdf = os.path.join(root, 'steamapps', 'libraryfolders.vdf')
    if os.path.isfile(vdf):
        with open(vdf, encoding='utf-8', errors='replace') as f:
            for path in re.findall(r'"path"\s+"([^"]+)"', f.read()):
                libs.append(os.path.normpath(path.replace('\\\\', '\\')))
    return libs


def find_game():
    seen = set()
    for root in steam_roots():
        for lib in library_folders(root):
            if lib.lower() in seen:
                continue
            seen.add(lib.lower())
            game = os.path.join(lib, 'steamapps', 'common', GAME_DIR)
            if os.path.isfile(os.path.join(lib, 'steamapps', f'appmanifest_{APP_ID}.acf')) and os.path.isdir(game):
                return game
    return ''


def check_game(game):
    """Return (ok, notes). ok is False when the build cannot use the folder."""
    if not os.path.isfile(os.path.join(gamepath.managed_dir(game), 'Assembly-CSharp.dll')):
        return False, [f'no NuclearOption_Data/Managed/Assembly-CSharp.dll in {game}']
    notes = []
    if not os.path.isfile(os.path.join(game, 'BepInEx', 'core', 'BepInEx.dll')):
        notes.append('BepInEx 5 is not installed in the game folder: the mod builds, but the game will not load it. '
                     'Get BepInEx 5.4.x x64 from https://github.com/BepInEx/BepInEx/releases and unzip it into the game folder.')
    elif sys.platform == 'win32' and not os.path.isfile(os.path.join(game, 'winhttp.dll')):
        notes.append('BepInEx/core is there but winhttp.dll is missing, so BepInEx will not start. Reinstall BepInEx 5 x64.')
    return True, notes


def write_props(game, deploy):
    lines = ['<Project>',
             '  <!-- Written by tools/setup.py for this machine only; gitignored. -->',
             '  <PropertyGroup>',
             f'    <GamePath Condition="\'$(GamePath)\' == \'\'">{escape(game)}</GamePath>']
    if deploy is not None:
        lines.append(f'    <DeployToGame Condition="\'$(DeployToGame)\' == \'\'">{str(deploy).lower()}</DeployToGame>')
    lines += ['  </PropertyGroup>', '</Project>', '']
    with open(USER_PROPS, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))


def existing_deploy():
    if not os.path.isfile(USER_PROPS):
        return None
    with open(USER_PROPS, encoding='utf-8') as f:
        match = re.search(r'<DeployToGame[^>]*>(\w+)</DeployToGame>', f.read())
    return None if not match else match.group(1) == 'true'


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__.split('\n\n', 1)[1])
    ap.add_argument('--game', help='game folder, skips the Steam search')
    deploy = ap.add_mutually_exclusive_group()
    deploy.add_argument('--deploy', dest='deploy', action='store_true', default=None,
                        help='copy the plugin into BepInEx/plugins after every build')
    deploy.add_argument('--no-deploy', dest='deploy', action='store_false', help='keep builds in the repo (default)')
    ap.add_argument('--show', action='store_true', help='print the game path the build uses and exit')
    args = ap.parse_args()

    if args.show:
        game = gamepath.build_game_path()
        print(game or 'the build finds no game; run python tools/setup.py')
        return

    game = os.path.normpath(os.path.abspath(args.game)) if args.game else find_game()
    if not game:
        sys.exit('Nuclear Option not found in any Steam library. Pass --game "<folder that holds NuclearOption.exe>".')
    ok, notes = check_game(game)
    if not ok:
        sys.exit(notes[0])

    write_props(game, args.deploy if args.deploy is not None else existing_deploy())
    print(f'game: {game}')
    print(f'wrote {os.path.relpath(USER_PROPS, gamepath.REPO)}')
    for note in notes:
        print('note: ' + note)


if __name__ == '__main__':
    main()
