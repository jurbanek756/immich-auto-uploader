# Immich Auto Uploader

A Windows system-tray app that watches a folder and automatically uploads new photos/videos
to Immich using a bundled, version-pinned `immich-go`.

## Decisions baked in (from planning)

1. **Move to done** — files are *moved* to the Done folder after a successful upload, never deleted.
   Failed files stay in the watch folder for retry.
2. **Admin API key** — passed to immich-go as `--admin-api-key` so it can pause/resume Immich
   server jobs during upload (`--pause-immich-jobs`, on by default). Optional.
3. **Pinned immich-go** — `build.ps1` downloads the latest release and records the version.
   Re-run it any time to update the pin.
4. **Optional Jellyfin hook** — set a Jellyfin server URL (+ API key, DPAPI-encrypted) and the
   app triggers a library refresh after each successful upload batch, so new photos show up
   in Jellyfin without a manual scan. Set an exact library name to refresh just that
   library (`POST /Items/{id}/Refresh?Recursive=true`); leave it blank for a full scan
   (`POST /Library/Refresh`). Uses the `Authorization: MediaBrowser Token="…"` header,
   which is what this Jellyfin instance requires. Only fires when a batch actually
   uploaded at least one file.
5. **Optional Tailscale hook** — when enabled, the app runs `tailscale up` before each
   upload batch (and waits for it to connect) so uploads work away from home. An
   optional Tailscale-specific server URL (e.g. `http://100.x.y.z:2283`) is used for
   immich-go while Tailscale is active; otherwise the primary URL is used. The app
   never runs `tailscale down` — your VPN state is left alone. If the device isn't
   logged into Tailscale, the login URL is surfaced to you instead of hanging.

## Project layout

```
immich-auto-uploader/
├── build.ps1                          # pins + bundles latest immich-go
├── ImmichAutoUploader.sln
├── tools/immich-go/                   # filled by build.ps1 (git-ignored)
└── src/
    ├── ImmichAutoUploader.Core/       # net10.0 class library (no UI)
    │   ├── Models/AppSettings.cs      # non-secret settings
    │   ├── Services/SettingsService.cs# JSON in %AppData%\ImmichAutoUploader
    │   └── Security/                  # ICredentialStore + Windows DPAPI impl
    └── ImmichAutoUploader.App/        # net10.0-windows WPF tray app
        ├── App.xaml(.cs)              # single instance, tray bootstrap
        ├── MainWindow.xaml(.cs)       # settings UI
        └── TrayIconManager.cs         # NotifyIcon, hide-to-tray
```

## Security notes

- API keys are encrypted with Windows DPAPI (CurrentUser scope) and stored under
  `%AppData%\ImmichAutoUploader\secrets\`. They never appear in `settings.json`,
  logs, or command-line previews.
- Only the same Windows user on the same machine can decrypt them.

## Build (on Windows, .NET 10 SDK required)

```powershell
# 1. Pin + bundle the latest immich-go
.\build.ps1

# 2. Build
dotnet build ImmichAutoUploader.sln -c Release

# 3. Run (or publish a single file)
dotnet publish src\ImmichAutoUploader.App -c Release -r win-x64 --self-contained
```

> Note: the WPF project only builds on Windows. The Core library is plain `net10.0`
> and builds anywhere.

## Roadmap

- **Phase 1** (this scaffold): solution, settings model, DPAPI credential storage,
  settings UI, tray icon, immich-go pinning script. ✅
- **Phase 2**: FileSystemWatcher + debounce/settle detection, persistent SQLite
  upload queue (hash-tracked, no duplicates, survives restarts). ✅
  - Watcher: 64 KB buffer, recursive, debounced; a file must sit quiet 5 s *and*
    have a stable size before it's touched (no grabbing half-copied videos).
    Temp files (`~*`, `.tmp`, `.part`, `thumbs.db`…), hidden/system files, and
    non-photo/video extensions are ignored. Zero-byte files are skipped with a
    warning instead of killing runs. Buffer overflow triggers a full rescan.
    Startup does an initial scan for files added while the app was closed.
  - Queue: `%AppData%\ImmichAutoUploader\upload-queue.db`; dedup by SHA-256 —
    re-adding the same bytes (any name, any time) never re-uploads. Interrupted
    "Uploading" rows reset to Pending on startup. Log file:
    `%AppData%\ImmichAutoUploader\logs\app-YYYYMMDD.log`.
- **Phase 3**: upload engine ✅
  - One immich-go process **per file** (never one run over a staging dir): exit 0
    means *this* file is in Immich (uploaded or already-exists), anything else is
    a failure for this file only. One bad file can never take down the batch —
    the original pain that started this project.
  - Internally always runs with `--on-errors stop` so failures surface as
    non-zero exits; the app-level **On errors** setting ("continue"/"stop")
    applies *across* files instead.
  - Flags passed: `--server`, `--api-key`, optional `--admin-api-key`,
    `--pause-immich-jobs` (per your setting), `--concurrent-tasks 1` (parallelism
    is handled at the process level), `--device-uuid` (stable per-PC id,
    auto-generated), `--log-level WARN`. Secrets go on the command line — this
    immich-go fork documents no env-var alternative.
  - Success → row marked uploaded, original **moved** to the Done folder with
    its relative path preserved (`watch\2024\trip\img.jpg` →
    `done\2024\trip\img.jpg`); name collisions get ` (2)`, ` (3)`… If the move
    fails the file stays put and is skipped as already-uploaded next scan —
    a photo is never lost over a move error.
  - Failure → row stays queued with exponential backoff (5 → 10 → 20 → …
    capped at 240 min) via a `next_retry_at` column (auto-migrated on older
    DBs). Files that vanished are dropped silently. "Upload now" button + tray
    menu item run a batch on demand (no-op if one is already running).
  - `TailscaleService`: if enabled, `tailscale status --json` → `tailscale up`
    if needed; surfaces the login URL if the device isn't authenticated; picks
    the alternate Immich URL when set. The app only ever brings Tailscale *up* —
    it never disconnects it.
  - `JellyfinService`: after a batch with ≥1 upload, resolves the exact library
    name via `/Library/MediaFolders` and calls
    `POST /Items/{id}/Refresh?Recursive=true` (blank name → full
    `POST /Library/Refresh`). Auth uses `Authorization: MediaBrowser Token="…"`
    (the `?api_key=` / `X-Emby-Token` variants 401 on this instance). Jellyfin
    failures are logged, never fail uploads.
- **Phase 4**: polish — toast notifications, log viewer, "check for updates"
  (re-pin immich-go), installer/startup entry verification.
