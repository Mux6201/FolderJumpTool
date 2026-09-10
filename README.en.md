# FolderJumpTool

> [中文](README.md) | **English** · Inspired by [Listary](https://www.listary.com/)

A Windows tray utility that docks a tiny non-activating overlay next to system file open/save dialogs. Click a target and the dialog jumps straight there — no more typing paths by hand.

## Features

- **Overlay beside the dialog**: appears automatically with open/save dialogs; dock left / center / right; stays on screen at screen edges
- **Manual dismiss (×)**: a × fades in on the title row when you hover the card — click it to suppress the overlay for a misfired dialog (no overlay for it until it closes, then it auto-recovers)
- **Candidates**: open Explorer windows (including tabs, Z-ordered with the active one on top) + recent folders; each row shows the name plus its parent folder (hover for the full path)
- **Three tabs**: Recent (open windows + recently used) / Favorites / History (automatically recorded browsing history — folders you already closed are still there); the latter two appear when they have data
- **Browsing history**: recorded in the background (opening Explorer windows, navigating into subfolders, and every jump you make from the overlay); closed folders stay recoverable on the History tab. The tray menu can hide the tab — recording continues either way
- **Start with Windows**: one tray toggle (writes the per-user startup entry, also visible/manageable in Task Manager and Settings; the path is re-synced if the app is moved or updated)
- **Click to jump**: folder → dialog navigates in; file → dialog selects it
- **Keyboard**: ↑/↓ to move the selection, **Enter** to jump to it (or the top hit)
- **Favorites**: star a row to pin it; tray menu → **Favorites…** to add / remove / rename inline / drag-reorder
- **Ctrl+G**: while the overlay is visible, jump straight to the top candidate
- **Everything search (optional)**: configure es.exe once, then type to search the whole disk from the overlay (smart ranking puts target folders on top); the tray menu can toggle the search box off at any time
- **Tray menu**: theme (system / light / dark), window material (solid / acrylic), overlay position, Everything search, browsing history, language, start with Windows — all switch live and persist
- **Polish**: live light/dark theming, **acrylic material** for the overlay (Win10 1607+), native Windows file-type icons, Chinese / English UI, Win11-style flat card, a matching app icon family

## Usage

Download the zip from **Releases** — no install needed.

After launch there is **no window and no console** — a blue rounded-square arrow tray icon means it is running. The tray right-click menu switches theme, window material, overlay position and language. Press `Ctrl+O` / `Ctrl+S` in any app to see the overlay.

### Everything search (optional)

1. Install [Everything](https://www.voidtools.com/) (keep it running)
2. Download the official CLI tool [es.exe](https://www.voidtools.com/downloads/)
3. Tray right-click → **Choose es.exe…**, or drop it on `PATH` / in the Everything install dir (auto-detected)

A search box then appears at the top of the overlay when a dialog opens: type to search (smart ranking: folders first, exact name matches and shallow paths on top), **↑/↓** to select, **Enter** to jump to the selection (or the top hit), **Esc** to clear. The whole row hides when not configured; the tray "Everything search" menu can turn the box off anytime.

## Compatibility

Windows 10/11, x64. Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the self-contained Release build does not).
The acrylic material needs Windows 10 1607+ (ACCENT API).

**Known limitations**: dialogs from elevated apps need this tool running elevated too (UIPI); self-drawn dialogs (Qt / Electron / some Java) don't use the common dialog template and won't trigger the overlay; positioning may drift on multi-monitor setups with mixed DPI scaling.

## Tech stack

WPF on `.NET 10` (C#) + Win32 (a `SetWinEventHook` for dialog events, `WM_SETTEXT` to write paths, DWM for visual bounds). No third-party NuGet packages.

## Building from source

Requires Windows + the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
git clone https://github.com/Mux6201/FolderJumpTool.git
cd FolderJumpTool
dotnet run                # run directly (tray-resident, no main window)
dotnet build -c Release   # compile only
```

### Packaging a release build

These match what CI produces; pick the form you need:

```bash
# Framework-dependent: smaller, needs the .NET 10 Desktop Runtime
dotnet publish FolderJumpTool.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/portable

# Self-contained: no runtime needed, larger
dotnet publish FolderJumpTool.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/selfcontained
```

The output lands in `publish/`. CI workflows and the release process are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
