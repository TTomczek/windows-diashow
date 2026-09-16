# Agents.md

This guidance applies to the Windows application, release components, and
validation projects:

- `diashow/`: WPF application code
- `tests/`: unit and integration tests
- `ui-tests/`: Windows UI Automation tests
- `installer/`: Windows installer project for the application
- `updater/`: Windows updater project for the application

## Working principles

### Preserve scalability

- Treat 100,000 media items as a supported workload.
- Keep scrolling, discovery, filtering, selection, and rendering responsive at
  that scale.
- Changes MUST prefer progressive loading, batching, virtualization, bounded
  concurrency, lazy work, and cancellation over loading or processing everything
  on the UI thread.
- Keep the existing performance budgets as regression requirements:
  - Adding 100,000 playlist items in batches must remain under 20 seconds.
  - Each 1,000-item playlist ordering variant must remain under 10 seconds.
- Do not replace a streaming or bounded pipeline with an unbounded collection or
  synchronous UI work without measuring and documenting the trade-off.

### Keep the UI responsive

- File discovery, image decoding, sorting, and other expensive work MUST stay off
  the WPF dispatcher.
- Background results MUST be marshaled to the UI with the dispatcher and updated
  in batches.
- Async work MUST use cancellation tokens and version checks so stale folder loads
  or transitions cannot update the current view.
- Caches, channels, and parallel work MUST be bounded to avoid memory pressure.
- File-system watcher events SHOULD be debounced or coalesced before they trigger
  discovery or UI updates, because one file operation can produce event bursts.
- Missing, corrupt, or unsupported media MUST fail gracefully.

### Accessibility

- New controls MUST support basic keyboard operation and visible keyboard focus.
- Changes MUST preserve meaningful automation names and polite live announcements
  where they already exist.
- Keyboard users MUST have an actionable alternative to mouse-only behavior.

### Internationalization

- User-facing text MUST go through the existing `Localization` service; do not add
  hard-coded UI strings.
- Changes MUST add translations for every supported language when adding or
  changing a label, message, tooltip, shortcut description, or accessibility name.
- User-visible values MUST use culture-aware formatting and preserve the English
  fallback behavior.

### Keyboard shortcuts

- Shortcut behavior MUST remain discoverable in the README and localized UI text.
- New shortcuts MUST avoid conflicts with existing shortcuts and ordinary
  text/input behavior.
- Shortcuts SHOULD be tested and provide an equivalent discoverable UI action where
  practical.
- The current shortcuts are documented in `README.md`; update that table and
  `Localization.cs` together when shortcuts change.

### Testing and regression protection

- Contributors MUST run the smallest relevant existing test project for each
  change; both test projects MUST be run for cross-cutting or user-visible
  behavior.
- Every bug fix MUST add a focused regression test when feasible.
- Changes MUST preserve tests for playlist ordering, filtering, navigation, media
  discovery, cancellation, settings persistence, decoding failures, and
  scalability budgets.
- UI changes SHOULD add or update Windows UI Automation coverage where practical,
  and failures SHOULD be investigated using the captured screenshots.
- Do not weaken a test threshold or remove coverage to make a change pass without
  documenting and reviewing the changed requirement.

When responsiveness, accessibility, internationalization, and feature scope
compete, evaluate the trade-off explicitly rather than silently dropping one of
the principles.

## Change checklist

- **UI change:** check keyboard focus, automation names, localization, and UI
  test coverage.
- **Playlist or discovery change:** check scalability budgets, cancellation, event
  bursts, and background-thread behavior.
- **Bug fix:** add a focused regression test when feasible.
- **Shortcut change:** update `README.md`, `Localization.cs`, and shortcut tests.
- **Installer or updater change:** preserve versioning, atomic operations, and
  update signature and checksum verification.

## Architecture landmarks

- `MediaDiscovery.cs` streams discovered media through a bounded channel.
- `ImagePreloader.cs` owns bounded concurrent decoding and the image LRU cache.
- `Playlist.cs` owns playback ordering, filtering, navigation, and preload
  candidates.
- `MainWindow.xaml.cs` coordinates UI state, dispatcher updates, cancellation,
  playback timers, and folder loading.
- `Localization.cs` owns the supported language dictionaries.
- `Settings.cs` persists user settings in
  `%APPDATA%\diashow\settings.json`; keep existing settings backward compatible.
- `UpdateManager.cs` verifies signed updates; do not bypass signature validation.

Prefer the existing dependency-light Windows/WPF patterns and immutable data
models before introducing a new framework or global state.

## Development commands

Run these commands from the repository root in PowerShell on Windows:

```powershell
dotnet restore diashow.sln --runtime win-x64
dotnet build diashow.sln --configuration Debug --no-restore
dotnet test tests\diashow.Tests.csproj --configuration Release --no-restore
dotnet test ui-tests\diashow.UiTests.csproj --configuration Release --no-restore
```

UI tests require Windows and a usable desktop session. CI restores the solution
for `win-x64`, runs both test projects in Release, publishes TRX summaries, and
uploads UI-test screenshots. Keep local validation consistent with those commands
when the change affects the corresponding project.

Update this file in the same change whenever the application architecture,
commands, shortcuts, performance budgets, or quality conventions change.
