# WinDockIt

A clean, modern dock for Windows: frosted glass, smooth magnification, and it sits on any edge of your screen.

WinDockIt is a small native app (.NET 9 / WPF). It starts instantly, uses each app's real Windows icon, and stays out of your way.

## Features

- **Frosted glass.** Real blur of whatever is behind the dock, with smooth rounded corners. It follows your Windows light/dark theme and the *Transparency effects* setting.
- **Magnification.** Icons swell smoothly as you move across them. The icon under your cursor stays put.
- **Any edge.** Bottom, top, left or right, switchable from the dock's menu.
- **Running indicators.** A dot under every app with an open window.
- **Click to switch.** Clicking a running app brings it forward. Click again to minimize it. If the app has several windows, clicking cycles through them.
- **Launch bounce.** Icons hop when an app starts.
- **Pin anything.** Drag apps, shortcuts, files or folders onto the dock, or use **Add app…**.
- **Reorder and remove.** Drag icons to reorder. Drag one off the dock and let go to remove it.
- **Recycle Bin.** Shows whether it's full or empty, and can be emptied from its menu.
- **Auto-hide.** Optional. The dock slides away and returns when you touch the screen edge.
- **Stays out of fullscreen.** The dock hides while a game or video is fullscreen.
- **Start with Windows.** Optional, from the menu.

## Using it

| Action | How |
| --- | --- |
| Open or switch to an app | Click its icon |
| Open a new window | Shift+click or middle-click |
| App options | Right-click an icon |
| Dock settings (position, size, magnification, auto-hide) | Right-click an empty part of the dock |
| Pin something | Drag it onto the dock |
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
  "autoHide": false,
  "items": [
    { "name": "Terminal", "location": "shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "processName": "WindowsTerminal" }
  ]
}
```

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
| `Shell.cs` | High-res shell icons, shortcut targets, Recycle Bin |
| `WindowList.cs` | Finds open app windows (running dots, click-to-switch) |
| `DockConfig.cs` | Settings and default pinned apps |
| `Theme.cs` | Light/dark colors and menu styling |

## Contributing

Issues and pull requests are welcome. Some things that would be great to have:

- Multi-monitor support (choose the screen, or one dock per screen)
- Showing running apps that aren't pinned
- Folder "stacks" that fan out
- Windows taskbar-style jump lists on right-click
- An app icon and installer

## License

[MIT](LICENSE)
