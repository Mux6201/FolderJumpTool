# FolderJumpTool

> [中文](README.md) | **English** · Inspired by [Listary](https://www.listary.com/)

A Windows tray utility that docks a tiny non-activating overlay next to system file open/save dialogs. Click a target and the dialog jumps straight there — no more typing paths by hand.

## Features

- **Overlay beside the dialog**: appears automatically with open/save dialogs; dock left / center / right; stays on screen at screen edges
- **Candidates**: open Explorer windows (including tabs, Z-ordered with the active one on top) + recent folders + favorites
- **Click to jump**: folder → dialog navigates in; file → dialog selects it
- **Favorites**: star a row to pin it; tray menu → **Favorites…** to add / remove
- **Ctrl+G**: while the overlay is visible, jump straight to the top candidate
- **Everything search (optional)**: configure es.exe once, then type to search the whole disk from the overlay
- **Polish**: live light/dark theming, Chinese / English UI, Win11-style flat card

## Usage

Download the portable exe from **Releases** — no install needed. Or build it yourself:

```bash
git clone https://github.com/Mux6201/FolderJumpTool.git
cd FolderJumpTool
dotnet build          # Windows + .NET 10 SDK required
```

After launch there is **no window and no console** — a blue arrow tray icon means it is running. Press `Ctrl+O` / `Ctrl+S` in any app to see the overlay.

### Everything search (optional)

1. Install [Everything](https://www.voidtools.com/) (keep it running)
2. Download the official CLI tool [es.exe](https://www.voidtools.com/downloads/)
3. Tray right-click → **Choose es.exe…**, or drop it on `PATH` / in the Everything install dir (auto-detected)

A search box then appears on the overlay when a dialog opens: type to search, **Enter** for the top hit, **Esc** to clear. Hidden when not configured.

## Compatibility

Windows 10/11, x64. Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the self-contained Release build does not).

**Known limitations**: dialogs from elevated apps need this tool running elevated too (UIPI); self-drawn dialogs (Qt / Electron / some Java) don't use the common dialog template and won't trigger the overlay; positioning may drift on multi-monitor setups with mixed DPI scaling.

## Tech stack

WPF on `.NET 10` (C#) + Win32 (a `SetWinEventHook` for dialog events, `WM_SETTEXT` to write paths, DWM for visual bounds). No third-party NuGet packages.

## License

[MIT](LICENSE)
