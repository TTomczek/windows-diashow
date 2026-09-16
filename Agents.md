# Agents.md

This guidance applies to the Windows application and its validation projects:

- `diashow/`: WPF application code
- `tests/`: unit and integration tests
- `ui-tests/`: Windows UI Automation tests
- `installer/`: Windows installer project for the application
- `updater/`: Windows updater project for the application

## Working principles

### Preserve scalability

- Treat 100,000 media items as a supported workload.
- Keep scrolling, searching, selection, and rendering responsive at that scale.
- Prefer progressive loading, batching, virtualization, bounded concurrency, lazy
  work, and cancellation over loading or processing everything on the UI thread.
- Keep the existing performance budgets as regression requirements:
  - Adding 100,000 playlist items in batches must remain under 20 seconds.
  - Each 1,000-item playlist ordering variant must remain under 10 seconds.
- Do not replace a streaming or bounded pipeline with an unbounded collection or
  synchronous UI work without measuring and documenting the trade-off.

### Keep the UI responsive

- Keep file discovery, image decoding, sorting, and other expensive work off the
  WPF dispatcher.
- Marshal background results to the UI with the dispatcher and update in batches.
- Use cancellation tokens and version checks so stale folder loads or transitions
  cannot update the current view.
- Bound caches, channels, and parallel work to avoid memory pressure.
- Preserve graceful behavior for missing, corrupt, or unsupported media.

### Accessibility

- New controls must support basic keyboard operation and visible keyboard focus.
- Preserve meaningful automation names and polite live announcements where they
  already exist.
- Ensure keyboard users have an actionable alternative to mouse-only behavior.

### Internationalization

- Route user-facing text through the existing `Localization` service; do not add
  hard-coded UI strings.
- Add translations for every supported language when adding or changing a label,
  message, tooltip, shortcut description, or accessibility name.
- Use culture-aware formatting for user-visible values and preserve the English
  fallback behavior.

### Keyboard shortcuts

- Keep shortcut behavior discoverable in the README and localized UI text.
- Avoid conflicts with existing shortcuts and ordinary text/input behavior.
- Test shortcuts and provide an equivalent discoverable UI action where practical.
- The current shortcuts are documented in `README.md`; update that table and
  `Localization.cs` together when shortcuts change.

### Testing and regression protection

- Run the smallest relevant existing test project for each change; run both test
  projects for cross-cutting or user-visible behavior.
- Every bug fix should add a focused regression test when feasible.
- Preserve tests for playlist ordering, filtering, navigation, media discovery,
  cancellation, settings persistence, decoding failures, and scalability budgets.
- For UI changes, consider Windows UI Automation coverage and inspect screenshots
  when a UI test fails.
- Do not weaken a test threshold or remove coverage to make a change pass without
  documenting and reviewing the changed requirement.

When responsiveness, accessibility, internationalization, and feature scope
compete, evaluate the trade-off explicitly rather than silently dropping one of
the principles.

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
