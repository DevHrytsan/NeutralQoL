"""Game path as the build sees it, so the tools never disagree with dotnet build.

Asks MSBuild for the GamePath property, which applies the lookup order in
Directory.Build.props (-p:GamePath, NO_GAME_PATH, GamePath.user.props, Steam).
"""
import os
import subprocess

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
PROJECT = os.path.join(REPO, 'src', 'NeutralQoL', 'NeutralQoL.csproj')


def build_game_path():
    """Return the game folder the build would use, or '' if it finds none."""
    try:
        out = subprocess.run(['dotnet', 'msbuild', PROJECT, '-nologo', '-getProperty:GamePath'],
                             cwd=REPO, check=True, capture_output=True, text=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return ''
    return os.path.normpath(out.strip()) if out.strip() else ''


def managed_dir(game):
    return os.path.join(game, 'NuclearOption_Data', 'Managed')
