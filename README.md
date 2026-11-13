# WinDockIt

A clean, modern dock for Windows: frosted glass, smooth magnification, and it sits on any edge of your screen.

WinDockIt is a small native app (.NET 9 / WPF). It starts instantly, uses each app's real Windows icon, and stays out of your way.

WinDockIt is free. If you find it useful, you can [support the developer ♥](https://flossafrica.com/m/stephengade).

## Features

- **Frosted glass.** Real blur of whatever is behind the dock, with smooth rounded corners. It follows your Windows light/dark theme and the *Transparency effects* setting.
- **Six dock styles.** *Floating* (rounded glass lifted off the edge), *Full width* (edge to edge, like the taskbar), *Attached* (centred, resting on the edge), *Compact pill* (smaller icons in a capsule), *Split* (pinned apps in one piece of glass, open apps and the Recycle Bin in another) and *Minimal* (no glass until you point at the dock). Pick one in Settings › Appearance.
- **Colour themes.** Tint the glass of the dock, status bar and Launchpad (Graphite, Ocean, Plum, Rose, Forest or Sunset) and choose how strong the tint is. Pick an accent colour for the running dots, Launchpad's letter index and Settings, or follow your Windows accent.
- **Magnification.** Icons swell smoothly as you move across them. The icon under your cursor stays put.
- **Any edge.** Bottom, top, left or right, switchable from the dock's menu.
- **Smart hide.** Windows get the whole screen. The dock stays in view and only slides away while a window overlaps it. Touch the screen edge to bring it back. You can instead keep a strip of the screen for the dock, like the taskbar, or have it always auto-hide.
- **Always fits.** No scrolling: when there are too many items, every icon shrinks evenly until the whole dock fits. Icons never get smaller than 32px: past that, the last pinned apps move into a **+N** tile that opens Launchpad.
- **Import from taskbar.** Brings over your pinned taskbar apps, in order, including Store apps. On first run the dock starts with them.
- **Running indicators.** A dot under every app with an open window, in your accent colour.
- **Every open app.** Running apps that aren't pinned appear after a divider, and leave when you close them. Right-click › **Keep in Dock** pins one. Windows are grouped by app the same way the taskbar groups them, so web apps and Store apps get their own icons.
- **Asks for attention.** When an app flashes for attention, its icon bounces and its dot turns orange until you look.
- **Launchpad.** The WinDockIt icon at the start of the dock (and at the top left of the status bar) fills the screen with all your apps in large icons over a blurred wallpaper, like the Mac. It shows pinned apps that didn't fit on the dock, Suggestions (apps that are open, then the ones you use most), and every app grouped A–Z, Store apps included. Hover or drag along the letter index on the right to jump through the alphabet, or use Page Up and Page Down. The dock stays on top while it's open. Start typing to search; arrow keys and Enter open an app. Esc or a click on an empty area closes it. Drag an app onto the dock to pin it, or right-click › **Keep in Dock**. Right-click the WinDockIt icon for the Windows Start menu.
- **Status bar.** A thin frosted bar along the top of the screen, like the Mac menu bar. On the left: Launchpad and the name of the app you're in. Click the name to see that app's windows, or to hide or quit it. On the right: Wi-Fi, Bluetooth, volume, battery, quick settings, and the date and time. Each opens the matching Windows panel, and you can scroll over the volume icon to change it. By default the bar keeps its own strip of the screen. In Settings you can make it hide like the dock instead: *Smart* steps aside while a window covers it, and *Always* keeps it out of sight. Either way, rest the cursor at the top edge to bring it back. Turn it off with **Show status bar** in the dock's menu.
- **Replace the taskbar.** Optional: **Hide Windows taskbar** in the dock's menu. The taskbar comes back when WinDockIt quits, and a small watchdog brings it back even if WinDockIt crashes. While it's hidden, the status bar shows the clock and system status.
- **Click to switch.** Clicking a running app brings it forward. Click again to minimize it. If the app has several windows, clicking cycles through them.
- **Launch bounce.** Icons hop when an app starts.
- **Pin anything.** Drag apps, shortcuts, files or folders onto the dock, or use **Add app…**.
- **Reorder and remove.** Drag icons to reorder. Drag one off the dock and let go to remove it.
- **Recycle Bin.** Shows whether it's full or empty, and can be emptied from its menu.
- **Settings window.** Appearance (position, icon size, space between icons, magnification, dock style, light or dark theme, glass tint, accent colour, frosted glass), Behaviour (how the dock and status bar hide, status bar, taskbar, running apps, Launchpad button, start with Windows), and your pinned apps in order. Every change applies to the dock straight away. Open it from the dock's menu, the status bar's menu, or by launching WinDockIt again while it's running.
- **Stays out of fullscreen.** The dock hides while a game or video is fullscreen.
- **Start with Windows.** Optional, from the menu.

## Using it

| Action | How |
| --- | --- |
| Open or switch to an app | Click its icon |
| Find any app | Click the WinDockIt icon to open Launchpad, then type |
| Windows Start menu | Right-click the WinDockIt icon › Windows Start menu |
| Open a new window | Shift+click or middle-click |
| App options, its open windows, Keep in Dock, Close | Right-click an icon |
| Settings | Right-click any dock icon › Dock settings…, right-click the status bar › WinDockIt settings…, or launch WinDockIt again |
| Pin something | Drag it onto the dock, from File Explorer or from Launchpad |
| Change the volume | Scroll over the volume icon in the status bar |
| Bring over your taskbar apps | Right-click an empty part of the dock › Import from taskbar |
| Reorder | Drag an icon along the dock |
| Unpin | Drag an icon off the dock, or right-click › Remove from Dock |

## Build and run

Requires Windows 10 1903 or later (Windows 11 recommended) and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet run --project src/WinDockIt -c Release
```

To produce a single self-contained `WinDockIt.exe`:

```bash
dotnet publish src/WinDockIt -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Settings

Settings and pinned items live in `%APPDATA%\WinDockIt\config.json`. You can edit it by hand while the dock is closed. For example, set `processName` on an item if its running dot doesn't light up (the dock matches apps by process name).

```json
{
  "position": "Bottom",
  "iconSize": 46,
  "magnification": 1.75,
  "smartHide": true,
  "style": "Floating",
  "tint": "Ocean",
  "tintStrength": 35,
  "accent": "Windows",
  "items": [
    { "name": "Terminal", "location": "shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "processName": "WindowsTerminal" }
  ]
}
```

To open Launchpad from a keyboard shortcut, point a shortcut at `WinDockIt.exe --launchpad` and give it a shortcut key in its Properties.

Errors are logged to `%APPDATA%\WinDockIt\error.log`.

## How it works

The dock is made of two windows stacked together:

- **The glass** (`GlassBackdrop.cs`) is a bare window hosting a Windows Composition visual. It's painted with a host-backdrop brush (the blurred desktop behind it) and clipped to an anti-aliased rounded rectangle. A plain "blur behind" window can't do this: Windows ignores window regions when blurring, so you always get square corners.
- **The icons** (`DockWindow.cs`) sit in a transparent window on top, owned by the glass window so they always stay above it. Keeping the icons in their own window is what lets magnified icons grow past the glass.

All layout happens by hand each frame, in "dock space": distance *along* the dock and distance *from* the screen edge. One mapping function turns that into window coordinates for whichever edge is selected, so every position shares the same code.

| File | What it does |
| --- | --- |
| `DockWindow.cs` | Layout, magnification, animation, input, menus |
| `GlassBackdrop.cs` | Frosted glass via Windows.UI.Composition |
| `AppBar.cs` | Reserves the dock's screen space, like the taskbar does |
| `TaskbarPins.cs` | Reads the taskbar's pinned apps for import |
| `LaunchpadWindow.cs` | Launchpad: search, suggestions, all apps A–Z with a letter index |
| `AppCatalog.cs` | Lists every app in the Start menu (desktop and Store) with icons |
| `SettingsWindow.cs` | The Settings window |
| `StatusBar.cs` | The bar along the top: app name, status icons, clock |
| `SystemStatus.cs` | Reads network, Bluetooth, volume and battery state |
| `TaskbarHider.cs` | Hides and restores the Windows taskbar, plus the crash watchdog |
| `Shell.cs` | High-res shell icons, shortcut targets, Recycle Bin |
| `WindowList.cs` | Finds open app windows and works out which app each belongs to |
| `DockConfig.cs` | Settings and default pinned apps |
| `Theme.cs` | Light/dark colors and menu styling |

## Contributing

Issues and pull requests are welcome. Some things that would be great to have:

- Multi-monitor support (choose the screen, or one dock per screen)
- Folder "stacks" that fan out
- Windows taskbar-style jump lists on right-click
- An installer

## Support

WinDockIt is built and maintained in spare time. If it's made your desktop nicer, you can [support the developer](https://flossafrica.com/m/stephengade). It's also linked from Settings → About and the dock's right-click menu.

## License

[MIT](LICENSE)
