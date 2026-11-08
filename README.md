# YTMusic

A lightweight desktop wrapper around YouTube Music built with **.NET 8 WinForms + WebView2**.

Features:
- Custom frameless title bar (back / home / forward / mute / settings / min-max-close)
- Global hotkeys (Play/Pause, Next, Prev, Mute) — configurable in Settings
- Mutes YouTube Music's own audio only (never your system volume)
- Remembers the last played track and resumes on restart
- Discord-free, Edge-based media card ("YTMusic")
- 80% default zoom, dark theme
- Optional autorun at Windows startup

## Requirements (running the app)
- Windows 10/11 (x64)
- The app is **self-contained** (.NET 8 runtime bundled) — no .NET install needed
- **Microsoft Edge WebView2 Runtime** must be present. Most Windows 10/11 systems already have it (via Edge). If not, the installer (`YouTubeMusic_Setup.exe`) installs it automatically (offline bundle). For the portable build, install it from https://developer.microsoft.com/microsoft-edge/webview2/

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

## Project layout
```
App/        WinForms + WebView2 application
Shared/     Shared AppIdentity (AppUserModelId / media card registration)
logo.png    YTMusic logo
ytmusic.ico Application icon
version.txt Version used by builds
```

## License
MIT — see LICENSE.
