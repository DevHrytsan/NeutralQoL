# NeutralQoL

Client side quality of life mod for Nuclear Option.

- Works on any server, modded or not. The mod never touches networking and
  never sends anything an unmodded game would not send.
- Never changes the simulation
- Never shows hidden information and never aims, flies or fires for you.

![Flight HUD in third person with fuel time and range](promo/hud_qol_01.png)

## Installation

**With the [Nuclear Option Mod Manager (NOMM)](https://github.com/Combat787/NOMM)
(easiest):** find NeutralQoL in the mod list and install it. NOMM keeps it
up to date.

**Manual:**

1. If you don't have it yet, install
   [BepInEx 5.4.x x64](https://github.com/BepInEx/BepInEx/releases) into
   the game folder and start the game once.
2. Download `NeutralQoL.zip` from the
   [latest release](https://github.com/DevHrytsan/NeutralQoL/releases/latest).
3. Extract it (right click, **Extract All**) into
   `BepInEx/plugins/NeutralQoL/` in the game folder, so the file ends up at
   `BepInEx/plugins/NeutralQoL/NeutralQoL.dll`. Windows opens a zip like a
   normal folder, which is why it may not look zipped. BepInEx loads only
   `.dll` files, so a `.zip` placed in `plugins` does nothing.
4. Start the game. The Settings window now has a **QoL** tab. If it
   doesn't, open `BepInEx/LogOutput.log` and search for `NeutralQoL`.

## Settings

In game: **Settings, QoL tab**. Every feature has its own switch and options
there, and changes apply right away. Turning a feature off restores the
vanilla behaviour without a restart.

![Settings window](promo/menu_qol_01.png)

The same settings are stored in
`BepInEx/config/com.devhrytsan.neutralqol.cfg`. The mod never writes to the
game's own settings.

## Features

### Menus

![Loadout weapon preview](promo/loadout_qol_01.png)

| Feature | Default | What it does |
| --- | --- | --- |
| Loadout weapon preview | on | While a hardpoint's weapon list is open in the aircraft selection menu, shows what that hardpoint carries. Each part below can be turned off on its own. |
| Open bay doors | on | Opens the hardpoint's bay doors or cargo ramp so you can see what is loaded inside. |
| Move camera to weapon | on | Frames and zooms in on the hardpoint's weapon. Hold Free Look and move the mouse to turn round it. |
| Show weapon through aircraft | on | Fades in a see-through copy of the weapons, so ones in bays or under the fuselage show from any angle. |

### HUD

| Feature | Default | What it does |
| --- | --- | --- |
| Flight HUD in third person | off | Shows your flight HUD and minimap in the chase and orbit views. |
| Fuel time and range | on | Flight time left at the current fuel burn under the fuel gauge, optionally the range, yellow or red when fuel runs low. |
| Cargo drop aid | on | While cargo is selected: an `[UNSAFE DROP]` warning and a ground marker where it would land, worked out with the game's own parachute physics and wind. |
| Auto Hover reminder | off | Shows `[AUTO HOVER OFF]` when you fly an Auto Hover aircraft slowly near the ground with it switched off. It only reminds you. |

### Camera

| Feature | Default | What it does |
| --- | --- | --- |
| Zoom-consistent look | on | Free look slows down while zoomed in, so a mouse move turns the view by the same share of the screen at every zoom. |
| Raised orbit camera | on | Lifts the orbit camera so the aircraft sits lower on screen and the aim marker shows above it. |
| World fixed orbit camera | on | Keeps the orbit camera pointing the same compass direction when the aircraft turns. |

### Weapons

| Feature | Default | What it does |
| --- | --- | --- |
| Bomb arming reminder | on | While a bomb with an arming delay is selected (nuclear bombs and some regular ones), shows `[TOO LOW TO ARM]` when a bomb dropped now would break on hitting the ground or water before it arms, and `[LOW BURST]` when it would go off below its normal height, including on the ground or under water. |
| Jamming pod as a switch | off | Tap Fire to start jamming and again to stop, instead of holding the button. |
| Open bay doors on weapon select | off | Keeps the selected weapon's bay doors open while it is selected. Only you see it; flight and launch timing are unchanged. |

### Always on

These fixes have no setting:
- Loadout weapon lists refresh: fixes vanilla issue that nukes don't show up in the loadout menu on your first spawn, without going back to the map.

## Problems

The mod logs one line per feature at startup to `BepInEx/LogOutput.log`:
on, off, or broken with the reason. After a game update a feature may show
as unavailable in the QoL tab while the rest keep working. Please
[open an issue](https://github.com/DevHrytsan/NeutralQoL/issues) with that
log attached.

## Building and contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, the build commands and the
rules every feature follows. To build a release zip:

```
python tools/package.py --game-version 0.34.1
```

## License

Mozilla Public License 2.0, see [LICENSE](LICENSE).
