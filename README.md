# Knightfall

An in-game menu for Batman: Arkham Knight, built on BmSDK.

Each option in the menu says what it does when you move onto it.

## Install

1. Install [BmSDK-AK v0.24.1](https://github.com/XeTrinityz/BmSDK-AK/releases/tag/v0.24.1) or newer. Run its installer, or copy the `Binaries` and `BmGame` folders from its zip into your game folder. Older BmSDK builds don't run Knightfall.
2. Put the `Knightfall` folder from the zip in `BmGame/Mods`, so you end up with `BmGame/Mods/Knightfall/Knightfall.dll`. When you update, copy it over the old one. Your settings stay. You can delete an old `scripts` folder in there. It isn't used any more.

The folder only holds `Knightfall.dll` and `mod.toml`. Everything the mod writes goes in `Knightfall/data`. Files from older versions are moved there on the first start.
3. Start the game and press F1.

## Controls

| | Keyboard | Xbox | PlayStation |
|---|---|---|---|
| Open or close | F1 | Click both sticks | L3 and R3 |
| Move | Arrow keys | D-pad | D-pad |
| Change a value | Left and right | D-pad | D-pad |
| Select | Enter | A | Cross |
| Close | Escape | B | Circle |
| Switch tab | Q and E | LB and RB | L1 and R1 |
| Switch page | Z and C | LT and RT | L2 and R2 |

DualShock 4 and DualSense pads work over a cable or Bluetooth, with or without Steam Input.

The menu hides while the game is paused.

## Settings

Your choices are saved in `Knightfall/data/settings.toml`. The mod writes it on first start. You can change the keys there, including the keys some options have, like `jump_key` and `jump_pad`. Close the game before you edit it.

## Other mods

Knightfall changes no game files. Texture, skin and other package mods work beside it.

Another BmSDK script mod can clash if it redirects the same game functions. The ones most likely to matter are the camera update, the gadget buttons and special moves.

## Something broke

Send me `Knightfall.log` from `Knightfall/data/logs`. `Knightfall.prev.log` next to it is the log from the session before.
