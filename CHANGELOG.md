# Changelog

All notable changes to the **Immich Auto Uploader** project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [v1.0.1] - 2026-09-20

### Added
- **Bundled Windows Installer**:
  - Inno Setup script (`installer.iss`) creating a unified `ImmichAutoUploader-<version>-Setup.exe` installer with Start Menu shortcuts, optional Desktop shortcut, and Windows uninstaller.
  - Automated installer compilation in GitHub Actions release workflow publishing both the `.exe` installer and `.zip` archive.

## [v1.0.0] - 2026-09-20

### Added
- **System Tray Desktop Application (.NET 10 / WPF)**:
  - Background system tray presence with double-click settings restore, "Upload now" action, and clean exit handling.
  - Single-instance enforcement via Windows named mutex (`Local\ImmichAutoUploader_SingleInstance`) to avoid concurrent folder watchers.
  - Windows startup toggle writing to `Software\Microsoft\Windows\CurrentVersion\Run`.
  - Comprehensive Settings UI with interactive Immich and Jellyfin connection testing.
- **Reliable Folder Intake (`FileWatcherService`)**:
  - Recursive folder monitoring via `FileSystemWatcher` with 64 KB internal buffer.
  - 5-second settle loop debounce tracking file size stability across timer ticks before intake.
  - File availability verification (`FileStream` read check) to prevent reading files actively locked by copying processes.
  - Automatic filtering for temporary files (`~*`, `$*`, `.tmp`, `.part`, `.crdownload`, `thumbs.db`, `desktop.ini`, `.ds_store`) and non-media extensions.
  - Safe zero-byte file detection and skipping to prevent server rejection.
  - Buffer overflow recovery via automatic asynchronous directory rescan.
  - Initial startup scanning using an asynchronous depth-first traversal to queue files added while offline.
- **SQLite-Backed Persistent Queue (`UploadQueue`)**:
  - Full persistence across application restarts and system crashes stored in `%AppData%\ImmichAutoUploader\upload-queue.db`.
  - WAL (Write-Ahead Logging) journal mode and `NORMAL` synchronous mode for high-performance concurrent reads and writes.
  - SHA-256 content deduplication ensuring files re-added under different names or paths are never re-uploaded.
  - Separate permanent `uploaded` table recording completed file hashes and timestamps.
  - Crash recovery routine (`ResetStuckUploading`) resetting orphaned `Uploading` rows back to `Pending` on startup.
  - Exponential backoff retry mechanism for failed items ($5 \times 2^{\text{attempts}-1}$ minutes, capped at 240 minutes) with indexed `next_retry_at` timestamps.
- **Robust Upload Engine (`UploadEngine` & `ImmichGoRunner`)**:
  - Strict per-file process isolation using bundled `immich-go`: one corrupt or unsupported file never aborts the rest of the batch.
  - Controlled batch processing (`MaxFilesPerBatch`) and parallel execution throttling (`ConcurrentTasks` bounded by `SemaphoreSlim`).
  - Automatic move-to-done semantics preserving relative folder hierarchies, with collision handling (` (2)`, ` (3)`) and cross-volume copy-delete retry loop with orphan cleanup.
  - Clean batch cancellation and shutdown with automatic reversion of unprocessed items to `Pending`.
- **Ecosystem Integration Hooks**:
  - **Tailscale Hook (`TailscaleService`)**: Automatic on-demand connection (`tailscale up`) when queue has due items; tracks connection ownership (`ConnectedByUs`) to ensure manual user connections are never disconnected; supports alternate Tailscale Immich URLs (`ImmichUrlViaTailscale`); surfaces login URLs when machine authentication is needed.
  - **Jellyfin Hook (`JellyfinService`)**: Post-upload library refresh trigger using `Authorization: MediaBrowser Token="..."` header; resolves targeted library IDs via `/Library/MediaFolders` or triggers full library refresh.
- **Security & Secret Management**:
  - Windows DPAPI encryption (`DataProtectionScope.CurrentUser`) for all secrets stored in `%AppData%\ImmichAutoUploader\secrets\`.
  - Subprocess security: `immich-go` receives API keys via `IMMICH_API_KEY` and `IMMICH_ADMIN_API_KEY` environment variables, keeping secrets off command-line arguments and out of process listings and event log audits.
  - In-memory redaction (`RedactSecrets`) ensuring API keys never appear in log files or UI error messages.
- **Build & Distribution Automation**:
  - `build.ps1` PowerShell script to dynamically fetch, verify, and bundle the latest pinned release of `sweepies/immich-go`.
  - GitHub Actions release workflow producing self-contained single-file `win-x64` executables packaged in ZIP archives.
