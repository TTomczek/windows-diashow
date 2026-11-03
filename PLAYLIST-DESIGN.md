# Playlist design

This document records the agreed scope for the persisted playlist feature. It
is a design baseline for implementation; it does not change application
behavior by itself.

## Scope and ownership

- A playlist is scoped to exactly one scanned folder.
- Only one playlist can be active at a time.
- Switching folders clears playlist activation but keeps persisted playlists.
- The built-in Favorite playlist always exists for the currently scanned
  folder, even when empty.
- Playlist display names are stored in plaintext. Folder and file names are
  not stored in plaintext in the playlist store.
- The existing `AppSettings.LastFolder` behavior remains unchanged.

## Persisted identity and data protection

- Playlists are stored in a separate, versioned playlist store behind a
  storage abstraction. They are not added to `settings.json`.
- The store must use atomic replacement and cross-process locking because the
  application and IPC clients can both write it.
- The store should use current-user-only Windows ACLs where supported.
- The canonical root-folder identity is a versioned cryptographic hash.
- Each membership entry uses a versioned cryptographic hash of the canonical
  folder-relative path.
- Canonicalization follows Windows path semantics: case-insensitive matching,
  normalized `\` separators, and an explicit algorithm/version prefix.
- No absolute paths are persisted in the playlist store.
- File metadata needed for stale detection may be persisted, but must not
  expose folder or file names.
- A renamed or moved file becomes stale; it is not automatically reattached.
- Missing entries remain persisted as stale entries, are excluded from
  playback, and can be cleaned up explicitly.

The protection goal is to prevent casual disclosure from the store. Hashes
alone do not prevent offline dictionary matching when an attacker already
knows candidate paths.

## Plugin model

- Define a small, versioned public playlist-plugin contract.
- Plugins run in-process for the first release.
- Only built-in plugins are loaded initially; arbitrary third-party DLL
  loading is out of scope.
- The first providers are:
  - **Favorite**: a built-in, undeletable playlist for the current folder.
  - **External**: a built-in provider for named custom playlists created and
    managed through the application or IPC.
- Playlist membership supports both images and videos.
- Favorite can mark both images and videos.
- Custom playlists can be created, renamed, and deleted. They use stable
  internal IDs, while Favorite has a fixed identity and localized display
  name.

## Membership and ordering

- Add and remove operations are idempotent.
- Malformed or out-of-scope paths are validation errors.
- Playlist items use the scanned-folder order by default.
- The plugin contract should leave room for a future plugin-defined ordering
  hook without requiring custom ordering in the first release.
- A playlist may be managed from both the in-app UI and the external API.

## Playback behavior

- Activating a playlist limits playback to that playlist, subject to the
  global media filter.
- If the filter removes all members, the playlist remains active and the UI
  shows an explicit empty state. There is no silent fallback to the full
  folder.
- Activation during an incremental scan starts with currently discovered
  members and incorporates later matching items in batches.
- Each playlist persists an end-of-playlist mode:
  - **Loop playlist**: repeat playlist members only.
  - **Append non-playlist items**: after playlist exhaustion, play the
    remaining scanned-folder items once, then follow the application's
    existing normal end-of-playback behavior.
- A global default may be used for newly created playlists; the effective
  setting is persisted per playlist.

## User interface

- Provide a dedicated playlist panel or menu and a compact active-playlist
  indicator in the main playback controls.
- The Favorite action is a toggle available for the current media and
  thumbnails.
- The toggle exposes checked/unchecked state and a meaningful localized
  accessible name.
- Add `Shift+F` as the Favorite toggle outside text inputs.
- Preserve plain `F` as fullscreen. Shortcut handling must check modifiers and
  must not intercept `Shift+F` while an editable text control has focus.
- Document the new shortcut in the README and add English, German, and
  Spanish localization entries.

## External interface

- Provide a local named-pipe IPC service.
- The service is limited to the currently loaded folder.
- External clients can create, rename, delete, list, inspect, and update
  named External playlists, subject to ownership and sharing rules.
- The service supports reads and mutations. Clients use polling initially;
  push notifications are out of scope for the first release.
- Clients submit folder-relative paths. Diashow canonicalizes and hashes them;
  clients do not need to implement the hash contract.
- Use versioned JSON request/response messages with length framing.
- Add and remove operations are idempotent.
- Access requires the current Windows user plus an app-issued capability.
- The capability is generated and revoked through an explicit user action.
- Playlist mutation is owner-scoped by default; sharing with another local
  client requires an explicit app action.
- The API must report malformed, unauthorized, stale, and out-of-scope
  requests explicitly rather than silently ignoring them.

## Compatibility and validation requirements

- Existing playlist navigation, filtering, preview, preloading, incremental
  loading, and 100,000-item scalability behavior must remain intact.
- Persisted data needs a schema version and a migration path.
- Implement focused tests for hashing/canonicalization, serialization,
  locking/atomic writes, stale entries, duplicate membership, folder changes,
  scan-time updates, ordering, end-of-playlist modes, and API authorization.
- Add UI automation coverage for Favorite toggling, playlist activation,
  empty-state behavior, and the active-playlist indicator where practical.
- Keep all user-facing text localized in English, German, and Spanish.
