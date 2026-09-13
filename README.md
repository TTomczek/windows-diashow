# Diashow

[![Build and release](https://github.com/TTomczek/windows-diashow/actions/workflows/build.yml/badge.svg)](https://github.com/TTomczek/windows-diashow/actions/workflows/build.yml)

A focused, keyboard-friendly slideshow for Windows. Diashow plays images and videos from a folder, including nested folders, while keeping controls out of the way until they are needed.

## Highlights

- Displays images and videos in a single, distraction-free viewer
- Discovers media recursively and updates the playlist when files are added, removed, or renamed
- Supports filename order or random playback
- Filters playback to images, videos, or both
- Plays animated GIFs and supports common image formats such as JPEG, PNG, BMP, TIFF, WebP, and HEIC
- Configurable image duration, instant or fade transitions, and image preloading
- Fullscreen mode with automatic control hiding
- Previous/next navigation, video seeking, mute/unmute, and reveal-in-Explorer actions
- English, German, and Spanish interface translations
- Remembers the last folder and playback settings
- Optional Windows Explorer context-menu and Start-menu integration through the installer

## Installation

### Installer (recommended)

1. Download `Diashow.Installer.exe` from the [latest GitHub release](https://github.com/TTomczek/windows-diashow/releases/latest).
2. Run the installer.
3. Choose an installation directory.
4. Optionally enable the File Explorer context-menu and Start-menu shortcuts.
5. Select **Install**.

The installer downloads the matching latest `Diashow.exe` release asset before installing it. The published application is self-contained, so a separate .NET runtime installation is not required.

### Portable application

For a portable setup, download `Diashow.exe` from the latest release and run it directly. The application accepts either a folder or a media-file path as an optional command-line argument:

```powershell
.\Diashow.exe "C:\Users\you\Pictures\Holiday"
.\Diashow.exe "C:\Users\you\Pictures\Holiday\cover.jpg"
```

If no path is supplied, Diashow reopens the last folder used. If no folder has been selected yet, use **Choose folder** in the empty state.

## Using Diashow

Move the mouse to reveal the playback controls. The settings panel lets you configure:

- Image duration from 1 second to 1 hour
- Filename or random playback order
- Instant switching or fade transitions
- Fade duration
- Image preloading
- Interface language

The media filter button cycles between images only, videos only, and both. Settings are stored in `%APPDATA%\diashow\settings.json`.

### Keyboard shortcuts

| Key | Action |
| --- | --- |
| `Space` | Pause or resume playback |
| `Left` / `Right` | Previous or next item |
| `Left` / `Right` on the video slider | Seek by 10 seconds |
| `F` | Toggle fullscreen |
| `V` | Cycle images, videos, or both |
| `M` | Mute or unmute the current video |
| `E` | Reveal the current file in File Explorer |
| `Escape` | Close settings or exit fullscreen |

Video playback uses the Windows media stack. The formats available to play therefore depend on the codecs supported by the Windows installation.

## Development

### Requirements

- Windows 10 or later
- .NET SDK 10.0 or later
- Git
- Visual Studio 2022, JetBrains Rider, or another editor with .NET/WPF support (optional)

Diashow uses WPF and Windows Forms integration, so it cannot be built or run as a native application on Linux or macOS.

### Clone and restore

```powershell
git clone https://github.com/TTomczek/windows-diashow.git
cd windows-diashow
dotnet restore diashow.sln --runtime win-x64
```

### Build

Build the complete solution in Debug configuration:

```powershell
dotnet build diashow.sln --configuration Debug --no-restore
```

Run the application with an optional folder or file:

```powershell
dotnet run --project diashow\diashow.csproj -- "C:\Users\you\Pictures"
```

### Test

Run the xUnit test project:

```powershell
dotnet test tests\diashow.Tests.csproj --configuration Release --no-restore
```

The tests cover playlist ordering and navigation, media discovery, settings persistence, image decoding, and Explorer integration.

### Publish locally

The release workflow publishes self-contained, single-file Windows executables for `win-x64`. To reproduce that output locally:

```powershell
dotnet publish diashow\diashow.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  --no-restore `
  --output publish\app

dotnet publish installer\Diashow.Installer.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  --no-restore `
  --output publish\installer
```

The resulting files are `publish\app\Diashow.exe` and `publish\installer\Diashow.Installer.exe`.

## Project structure

| Path | Purpose |
| --- | --- |
| `diashow\` | Main WPF application |
| `installer\` | Release installer that downloads and installs the latest app |
| `tests\` | xUnit tests |
| `.github\workflows\build.yml` | CI, test, publish, and release automation |
| `diashow.sln` | Solution containing the app, installer, and tests |

The application is intentionally dependency-light: the viewer is implemented with .NET/WPF, `MediaElement`, Windows Explorer integration, and the built-in file-system APIs.

## Releases and CI

Pushes to `develop` build and upload development executables as GitHub Actions artifacts. Pushes to `master` run the same tests and publish steps, then create a GitHub release using the `<Version>` value from `diashow\diashow.csproj`.

## License

Diashow is licensed under the [Apache License 2.0](LICENSE).
