# YTMusic

A lightweight desktop wrapper around YouTube Music built with **.NET 8 WinForms + WebView2**.

## Features

- Custom frameless title bar (back / home / forward / mute / settings / min / max / close)
- Global hotkeys (Play/Pause, Next, Previous, Mute) — configurable in Settings
- Mutes YouTube Music's own audio only (never your system volume)
- Remembers the last played track and resumes on restart
- Edge-based media card ("YTMusic")
- 80% default zoom, dark theme (including the title bar)
- Optional autorun at Windows startup
- Built-in updater check (placeholder URL — set your own in `SettingsForm.cs`)

## Default hotkeys

| Action | Hotkey |
|---|---|
| Play / Pause | Alt + Space |
| Next track | Alt + E |
| Previous track | Alt + Q |
| Mute / Unmute | Alt + M |

All hotkeys can be changed in Settings.

## Requirements (running the app)

- Windows 10/11 (x64)
- Self-contained (.NET 8 runtime bundled) — no .NET install needed
- Microsoft Edge WebView2 Runtime must be present. Most Windows 10/11 systems already have it (via Edge). If not, the installer (`YouTubeMusic_Setup.exe`) installs it automatically from a bundled offline copy.

## Installation

### Option A — Installer (recommended)

Download `YouTubeMusic_Setup.exe` from the [Releases](../../releases) page and run it. It will:

1. Extract the app to `%LocalAppData%\YTMDesktop`
2. Create Desktop and Start Menu shortcuts
3. Register the app identity / media card
4. Enable autorun at Windows startup (can be turned off in Settings)
5. Install the WebView2 Runtime if it is missing

### Option B — Portable

Download the portable `YouTube Music.exe` from the [Releases](../../releases) page and run it directly (WebView2 Runtime must already be installed).

## Build (portable app)

```
build-app.bat
```

Output: `publish\YouTube Music.exe` (self-contained, single file).

Or manually:

```
dotnet publish App\App.csproj -c Release -r win-x64 --self-contained true ^
  -p:Version=1.0.0 -p:PublishSingleFile=true -p:PublishReadyToRun=true ^
  -p:DebugType=none -p:DebugSymbols=false -o publish
```

## Build (full installer)

Requires the WebView2 Runtime offline installer in `deps/` (downloaded automatically by `build.bat`):

```
build.bat
```

Output: `YouTubeMusic_Setup.exe`.

## Project layout

```
App/        WinForms + WebView2 application
Shared/     Shared AppIdentity (AppUserModelId / media card registration)
logo.png    YTMusic logo
ytmusic.ico Application icon
version.txt Version used by builds
```

## Settings

Open Settings from the title bar (gear icon). Available options:

- Theme (dark)
- Autorun at Windows startup
- Hotkey capture boxes
- Version label
- Check for updates (URL is a placeholder — set your own in `SettingsForm.cs`)

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 Vexrion.
