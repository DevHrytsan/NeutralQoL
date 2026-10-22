# Contributing to NeutralQoL

Thanks for helping. This page gets you from a fresh clone to a feature in
game, and lists the rules every change has to follow. The rules matter more
than the code: a feature that gives an advantage or touches networking will
not be merged, however well it is written.

## Setup

You need:

- Nuclear Option installed (the build compiles against the game's own DLLs)
- [.NET SDK 9.0.200 or newer](https://dotnet.microsoft.com/download)
- [BepInEx 5.4.x x64](https://github.com/BepInEx/BepInEx/releases) unzipped
  into the game folder, to run the mod
- Python 3, only for the helper tools in `tools/`
- Any C# IDE: Rider, Visual Studio or VS Code with the C# extension

```
git clone https://github.com/DevHrytsan/NeutralQoL
cd NeutralQoL
dotnet build
```

The build finds the game in the default Steam library by itself. If it says
"Nuclear Option not found", run:

```
python tools/setup.py                      searches every Steam library
python tools/setup.py --game "<game folder>"
```

This writes `GamePath.user.props` (gitignored, yours only). The build looks
for the game in this order: `-p:GamePath=...`, the `NO_GAME_PATH`
environment variable, `GamePath.user.props`, the default Steam library.
`python tools/setup.py --show` prints the folder the build uses.

## Build, deploy, run

| What | Command |
| --- | --- |
| Debug build (adds the Dev sample feature) | `dotnet build` |
| Release build | `dotnet build -c Release` |
| Build and copy into `BepInEx/plugins/NeutralQoL/` | `dotnet build -p:DeployToGame=true` |
| Always copy after a build | `python tools/setup.py --deploy` (once) |
| Build into `Build/` | `build.bat [debug] [deploy]` or `./build.sh [debug] [deploy]` |

CI needs the game's DLLs, which are not public, so pull requests from forks
skip the CI build. Build Release locally before opening one.

In Visual Studio(or Rider) on Windows, the `Nuclear Option` launch profile
(`src/NeutralQoL/Properties/launchSettings.json`) makes the run button
build and start the game through Steam. With deploy on, the fresh build is
copied first. Without it, the game loads whatever plugin is already
installed. Close the game before building, because Windows locks the
loaded DLL.

The mod's log lines go to `BepInEx/LogOutput.log` in the game folder. At
startup the mod logs one line per feature: on, off, or broken with the
reason. Settings are in `BepInEx/config/com.devhrytsan.neutralqol.cfg` and
in the game under Settings, QoL tab.

## Reading the game code

Open `NuclearOption_Data/Managed/Assembly-CSharp.dll` in
[dnSpy](https://github.com/dnSpyEx/dnSpy) or ILSpy for targeted reading,
including "Analyze" to see who calls a method. For grep across everything:

```
python tools/decompile.py                  game code -> decomp/Assembly-CSharp
python tools/decompile.py Assembly-CSharp Rewired_Core
```

`decomp/` is gitignored. Never commit decompiled game code; quoting the few
lines an explanation needs is fine.

## Adding a feature

1. Copy `src/NeutralQoL/Features/DevSample/` to `Features/<YourFeature>/`,
   rename the class to `<YourFeature>Feature` and the namespace to match,
   and remove the `#if DEBUG` lines and the `ShowInMenu` override.
2. Set `Section` (the area: `Map`, `HUD`, `Menus`, `Controls`, `Camera`,
   `Chat`, ...), `Name` and a `Description` a player understands.
3. Find every game member you use in `Resolve()` through `Core/Targets.cs`,
   so a game update turns your feature into one clear "broken" log line
   instead of an exception mid-flight.
4. Put Harmony patches in nested classes and list them in `PatchClasses`.
   Undo anything else you changed in `OnDisable()`.
5. In the pull request, describe what it does, the config key and default,
   the methods patched, who calls them, and why it is no advantage.

The registry finds feature classes by reflection; there is nothing to
register. `Features/ZoomLookSensitivity/` is a small real example.

## Rules

### Fair play

- No competitive advantage. A feature may only show, remember or make
  easier to reach what an unmodded player can already see or do in the same
  situation.
- Never reveal hidden information: undetected units, enemy positions, fog
  of war, or exact values the vanilla HUD rounds or hides.
- Never aim, fly, fire, dodge or react for the player, and never automate
  input faster or more precisely than a person could.
- When in doubt, it is out. Open an issue and ask before writing code.

### Compatibility

- A modded client must stay fully compatible with unmodded servers and
  players. Never touch networking (`[SyncVar]`, RPCs, message handlers,
  serialization, send rates) and never send anything the vanilla client
  would not send.
- Never change simulation: flight model, weapons, damage, detection,
  `NuclearOption.Jobs`. `Core/PatchSet.cs` refuses some of these patches.
- Before patching a method, check who calls it and when it runs (local UI
  only, owner, every peer, server), and write that down in the pull request.

### Features

- Every feature has its own config entry with a description a player
  understands. Features that change what the player sees ship off; pure
  conveniences (remembering a setting, fixing an annoyance) may ship on.
- Turning a feature off at runtime restores vanilla behaviour without a
  restart where the game allows it.
- Keybinds are BepInEx `KeyboardShortcut` config entries, never hard coded,
  and must not collide with the game's default bindings.
- Settings live in the BepInEx config, never in the game's own settings or
  PlayerPrefs.
- New UI matches the game's look (its fonts, sprites and prefabs) and
  scales with the game's UI scale.

### Patch code

- Prefer prefix and postfix over transpilers. A transpiler checks that the
  IL it expects is there and, if not, skips the patch with a warning.
- Tolerate a missing target method or field: log once, disable that
  feature, keep the rest running. `Resolve()` plus `Targets` does this.
- No allocations per frame in code that runs every frame; cache in fields.
- Start each feature file with a short comment on why the change is fair
  and safe.

### Code style

`.editorconfig` sets formatting and naming, and your IDE shows violations.
Naming follows Unity's own source:

| Kind | Style |
| --- | --- |
| private and protected instance fields | `m_PascalCase` |
| private static fields | `s_PascalCase` |
| private constants | `k_PascalCase` |
| public and internal members, types, methods | `PascalCase` |
| locals and parameters | `camelCase` |

Check naming from the command line before you open a pull request:

```
dotnet build -p:EnforceCodeStyleInBuild=true
```

Parameters are not checked, because Harmony needs names like `__instance`
and `__state`.

Comments explain why, not what. No em dashes or emojis in code, comments or
docs.

## Pull requests

- One feature or fix per pull request.
- Bump `<Version>` in `src/NeutralQoL/NeutralQoL.csproj` for any change a
  player would notice (patch for fixes, minor for new features).
- Commit messages use [Conventional Commits](https://www.conventionalcommits.org):
  `feat(hud): show flight hud in chase view`, imperative, lowercase, at most
  72 characters.
- Say how you tested it in game. The pull request template has the
  checklist.
