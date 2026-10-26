# Nexus

A console-style overlay for Windows PCs, opened with the controller's **Guide button**. It covers
everything the Xbox Game Bar's compact mode does, with a liquid-glass design: the game blurs behind a
centred, horizontal panel.

> Status: all six tabs work (see [docs/PLAN.md](docs/PLAN.md) for what's next).

## What it does today

| Tab | |
|---|---|
| **Home** | Your last four games as square tiles: scenic art with the game's logo centred (Steam, falling back to Xbox/PlayStation/IGN art), Home and Library buttons, launchers |
| **Settings** | Control Center tiles: mic, Bluetooth, airplane mode, Xbox mode, screenshot, network, touch keyboard, gamepad cursor, compact mode, Game Bar; volume and mic levels; More settings |
| **Audio** | Output and input device, mute and level; per-app mixer as channel strips with favourites |
| **Social** | Steam friends with online and in-game status (no API key needed); chat and profile |
| **Apps** | Open apps with live thumbnails; switch, close, end a frozen app |
| **Widgets** | Performance (CPU, GPU, RAM, VRAM), Capture (screenshot, last 30 s, record), Display (borderless sizes and placement, Lossless Scaling, HDR), Gallery, Resources |

A soft glow behind the panel takes its colour from the focused game.

**Lightweight:** idle in the background it uses 0% CPU and about 13 MB of working memory; every
widget, timer and network lookup runs only while its tab is open.

**Controller:** Guide opens and closes it. Hold Guide jumps to Settings. D-pad or left stick moves,
A selects, B goes back, LB/RB switch tabs, Y favourites an app in the mixer.
**Keyboard:** Win + Shift + G, Esc to go back. **Command line:** `Nexus.exe --toggle`, `--background`, `--quit`.

## Setup

1. Turn off Windows' own Guide binding: Settings → Gaming → Game Bar → "Allow your controller to
   open Game Bar" (More settings in Nexus has a shortcut to that page). Changing the registry value
   alone isn't enough while Game Bar is running.
2. If Steam opens on Guide too, turn off "Guide button focuses Steam" in Steam's controller settings.
3. Optional: More settings → Start with Windows.

## Build

Needs the .NET 9 SDK. The Windows App SDK comes from NuGet and is bundled into the output
(self-contained), so nothing else needs installing.

```
dotnet build src/Nexus/Nexus.csproj -c Release -p:Platform=x64
```

Output: `src/Nexus/bin/x64/Release/net9.0-windows10.0.22621.0/win-x64/Nexus.exe`.
The icon is drawn by `tools/make-icon.py` (needs Pillow).

## How it works

- **Guide button:** XInput's hidden `XInputGetStateEx` (xinput1_4.dll ordinal 100) reports the Guide
  button. A background thread polls at about 125 Hz while a controller is connected
  (`Input/GamepadService.cs`).
- **Fast open:** the overlay window is created and laid out at startup, then hidden, so a Guide press
  only shows it. Open time is logged to `%LOCALAPPDATA%\Nexus\nexus.log`.
- **Glass:** the window covers the monitor and uses Windows' acrylic blur as its backdrop
  (`Views/GlassBackdrop.cs`). Panels layer a light gradient fill and a gradient specular rim on top
  (`App.xaml`).
- **Controller navigation:** WinUI desktop apps don't get gamepad focus navigation on their own, so
  `Input/PadNavigator.cs` maps the pad to focus moves and control actions.

## Anti-cheat

Nexus never injects into games, hooks rendering or reads game memory. It only opens game processes with
the most limited access, to read an exe path for the icon. It's an always-on-top window like any other.
Exclusive fullscreen games can't be drawn over safely; switch those to borderless.
