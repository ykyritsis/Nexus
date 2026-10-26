# Nexus plan

Goal: a console-quality overlay that opens with the Guide button over any game, matches the Xbox
Game Bar's compact mode on day one, looks far better, and grows from there.

## Design
- The window covers the game's monitor. Windows' acrylic blur frosts the game behind it, and a
  vignette darkens the edges.
- A centred, horizontal liquid-glass panel holds the content: translucent gradient fills, a
  bright gradient rim as the specular edge, very round corners.
- A pill tab bar along the top has a glass "lens" that slides to the selected tab. A clock chip sits
  on the left and the controller battery on the right.
- Tabs lay out horizontally:
  - **Home:** game cards, with a hero card for the last game played.
  - **Settings:** a Control Center tile grid.
  - **Audio:** the mixer as channel strips.

## Phases

| Phase | Contents | Status |
|---|---|---|
| **0. Spike** | Guide button through XInputGetStateEx, overlay pre-created and hidden, fast show plus focus handover to the game, Game Bar and Steam Guide checks | Done |
| **1. First version** | Shell, theme, controller navigation, **Home, Settings, Audio** | Done (needs testing with a controller in a real game) |
| **2. Core widgets** | Apps (window switcher with live thumbnails), Performance (Intel PresentMon service for FPS; Windows performance counters for GPU and VRAM), Display (borderless sizes and placement, HDR, refresh rate, Lossless Scaling mode), Capture (screenshot through Windows' screen capture API; recording through Game Bar's `ms-gamebar://` links) | Next |
| **3. Library and social** | Gallery, Resources, SteamGridDB art, Steam achievements and friends | |
| **4. Beyond Game Bar** | Own recorder with AMD hardware encoding and a replay buffer, pinnable HUD widgets, plugin interface, controller isolation (HidHide plus a virtual controller), signed install for Windows' top-most layer | |
| **5. Optional** | Xbox friends, chat and achievements, if Microsoft sign-in ever becomes possible | |

## Known limits and risks
1. **Games still see the controller while Nexus is open.** XInput doesn't care which window has focus.
   Most games ignore input when they're not in front, and Phase 4's controller isolation fixes the rest.
2. **Exclusive fullscreen games** minimise when Nexus takes focus. Switch them to borderless.
3. **Windows' Game Bar sits in a private top-most layer.** Nexus is a normal top-most window, so
   occasionally a notification can appear above it.
4. **Steam and Game Bar also want the Guide button.** More settings checks both.
5. **Undocumented pieces** (XInputGetStateEx, the default-device interface, the touch keyboard
   interface) have been stable for years, but need watching across Windows updates.
6. **Recent games come from Steam only.** EA, Battle.net and Ubisoft don't store a readable
   last-played time.

## Decisions taken (change any time)
- Name: Nexus. Stack: C# with WinUI 3 (Windows App SDK 1.8), unpackaged and self-contained.
- Home button opens OmniDash if installed, otherwise the Xbox app. Library opens the Xbox app.
  Both can be changed in `settings.json`.
