# Diashow

[![Build and release](https://github.com/TTomczek/windows-diashow/actions/workflows/build.yml/badge.svg)](https://github.com/TTomczek/windows-diashow/actions/workflows/build.yml)

A focused, keyboard-friendly slideshow for Windows. Diashow plays images and videos from a folder, including nested folders, while keeping controls out of the way until they are needed.

## Highlights

- Displays images and videos in a single, distraction-free viewer
- Discovers media recursively and updates the playlist when files are added, removed, or renamed
- Saves folder-specific Favorites and named playlists, with playlist-only or append-folder playback
- Supports filename, creation-date, or random playback order with ascending or descending sorting
- Filters playback to images, videos, or both
- Plays animated GIFs and supports common image formats such as JPEG, PNG, BMP, TIFF, WebP, and HEIC
- Configurable image duration, instant, fade, slide, Ken Burns, crossfade, zoom, cover, or blur dissolve transitions, and image preloading
- Fullscreen mode with automatic control hiding
- Previous/next navigation, video seeking, mute/unmute, and reveal-in-Explorer actions
- English, German, and Spanish interface translations
- Remembers the last folder and playback settings
- Offers a local, capability-protected named-pipe API for custom playlist management
- Optional Windows Explorer context-menu and Start-menu integration through the installer

## Installation

### Installer (recommended)

1. Download `Diashow.Installer.exe` from the [latest GitHub release](https://github.com/TTomczek/windows-diashow/releases/latest).
2. Run the installer.
3. Choose an installation directory.
4. Optionally enable the File Explorer context-menu and Start-menu shortcuts.
5. Select **Install**.

The installer downloads the matching latest `Diashow.exe` and `Diashow.Updater.exe` release assets before installing them. The published application is self-contained, so a separate .NET runtime installation is not required.

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
- Filename, creation-date, or random playback order
- Ascending or descending sort direction
- Instant switching, fade, slide-left, Ken Burns, crossfade, zoom, cover, or blur dissolve transitions
- Fade duration
- Image preloading
- Interface language

The media filter button cycles between images only, videos only, and both. Settings are stored in `%APPDATA%\diashow\settings.json`.

### Persisted playlists

Open **Playlists** in the playback controls to manage the currently scanned folder. **Favorite** is always available, and the star toggle on the current item and queue thumbnails adds or removes it. Custom playlists can be created, renamed, deleted, and activated; each can loop its members or append the remaining folder items once. Active playlists continue to honor the global media filter and show an explicit empty state when no members match.

Playlist data is kept separately in `%LOCALAPPDATA%\Diashow\playlists.json`. The versioned store keeps playlist display names in plaintext, but stores folder and relative-file identities as version-prefixed SHA-256 hashes, along with file metadata for stale detection. Missing or changed files remain as stale entries until **Remove stale entries** is used. The store uses atomic replacement, a cross-process mutex, and current-user-only Windows ACLs.

### Local playlist API

The app exposes `\\.\pipe\Diashow.Playlists.v1` to processes running as the current Windows user. Open **Playlists → Local API access → Generate API access key** to issue a capability and copy it to the clipboard. Keys are held only for the current app run; generate a new key after restarting, or revoke all issued keys from the same panel. By default, API clients can manage only playlists created using their own capability. The app's **Allow other local API clients to manage this playlist** checkbox explicitly shares the selected playlist with other issued clients.

Each connection sends one versioned JSON request and receives one JSON response. A four-byte length prefix in big-endian byte order precedes each JSON message; messages are limited to 64 KiB. Requests use protocol `version: 1`, the issued `capability`, and an operation:

| Operation | Additional fields | Purpose |
| --- | --- | --- |
| `list` | — | List playlists visible to this capability |
| `inspect` | `playlistId` | Inspect a visible playlist and its current-folder-relative members |
| `create` | `name`, optional `endMode` | Create an owned External playlist |
| `rename` | `playlistId`, `name` | Rename an owned or explicitly shared playlist |
| `delete` | `playlistId` | Delete an owned or explicitly shared custom playlist |
| `add` / `remove` | `playlistId`, `paths` | Idempotently update folder-relative membership |
| `set_end_mode` | `playlistId`, `endMode` | Set `LoopPlaylist` or `AppendNonPlaylistItems` |
| `cleanup_stale` | `playlistId` | Explicitly remove stale membership records |

Malformed, unauthorized, stale-file, and out-of-scope requests return explicit error codes. The API only operates on the currently loaded folder; clients never need to compute hashes. No push notifications are sent; clients can poll with `list` or `inspect`.

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
| `Q` | Show or hide the queue preview |
| `Shift` + `F` | Add or remove the current item from Favorites (not in text inputs) |
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

The tests cover playlist ordering and navigation, persisted playlist isolation and stale detection, named-pipe authorization, media discovery, settings persistence, image decoding, and Explorer integration.

UI tests use FlaUI to launch the application and interact with its Windows UI Automation tree:

```powershell
dotnet test ui-tests\diashow.UiTests.csproj --configuration Release --no-restore
```

UI tests require Windows because they launch the WPF application.

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

dotnet publish updater\Diashow.Updater.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  --no-restore `
  --output publish\updater
```

The resulting files are `publish\app\Diashow.exe`, `publish\installer\Diashow.Installer.exe`, and `publish\updater\Diashow.Updater.exe`.

## Project structure

| Path | Purpose |
| --- | --- |
| `diashow\` | Main WPF application |
| `installer\` | Release installer that downloads and installs the latest app |
| `updater\` | Replacement helper used after the app exits |
| `tests\` | xUnit tests |
| `.github\workflows\build.yml` | CI, test, publish, and release automation |
| `diashow.sln` | Solution containing the app, installer, and tests |

The application is intentionally dependency-light: the viewer is implemented with .NET/WPF, `MediaElement`, Windows Explorer integration, and the built-in file-system APIs.

## Releases and CI

Pushes to `develop` build and upload development executables as GitHub Actions artifacts. Pushes to `master` run the same tests and publish steps, then create a GitHub release using the `<Version>` value from `diashow\diashow.csproj`.

### Automatic updates

At startup, an installed copy checks the GitHub latest stable release without blocking the UI. A newer semantic version is downloaded in the background with `Diashow.exe`, `Diashow.exe.sha256`, and `Diashow.exe.sig`. The checksum uses `<lowercase SHA-256>␠␠Diashow.exe`; the detached Ed25519 signature is base64 encoded and covers the checksum file bytes. Both checks must pass before staging under `%LOCALAPPDATA%\Diashow\updates`.

On close, `Diashow.Updater.exe` waits for the app to exit and atomically replaces the installed executable with rollback on errors. It never relaunches the app. Master releases require the `DIASHOW_ED25519_PRIVATE_KEY` GitHub secret in PEM format; the corresponding public key is committed in `diashow\UpdateManager.cs`.

## License

Diashow is licensed under the [Apache License 2.0](LICENSE).
