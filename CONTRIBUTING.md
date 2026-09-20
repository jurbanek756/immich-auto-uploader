# Contributing to Immich Auto Uploader

Thank you for your interest in contributing to **Immich Auto Uploader**! This document provides guidelines for setting up your development environment, understanding the project structure, running tests, and submitting pull requests.

---

## Table of Contents
1. [Development Prerequisites](#development-prerequisites)
2. [Getting Started](#getting-started)
3. [Project Architecture](#project-architecture)
4. [Building and Running](#building-and-running)
5. [Running Unit Tests](#running-unit-tests)
6. [Coding Guidelines & Standards](#coding-guidelines--standards)
7. [Security Standards](#security-standards)
8. [Submitting Pull Requests](#submitting-pull-requests)

---

## Development Prerequisites

To develop, build, and test Immich Auto Uploader, you will need:
- **Operating System**: Windows 10 or Windows 11 (required for WPF desktop application and DPAPI credential storage; the Core class library is standard `net10.0`).
- **.NET SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.
- **PowerShell**: PowerShell 7+ (`pwsh`) or Windows PowerShell 5.1 (used by [build.ps1](file:///c:/Users/Joe%20Urbanek/Documents/Antigravity/immich-auto-uploader/build.ps1)).
- **Git**: [Git for Windows](https://git-scm.com/).
- **IDE**: Visual Studio 2022 / 2026, JetBrains Rider, or Visual Studio Code with C# Dev Kit.

---

## Getting Started

1. **Clone the repository**:
   ```powershell
   git clone https://github.com/jurbanek756/immich-auto-uploader.git
   cd immich-auto-uploader
   ```

2. **Fetch and pin `immich-go`**:
   The upload engine relies on a bundled, pinned binary of `immich-go`. Run the setup script to download and extract the latest binary:
   ```powershell
   .\build.ps1
   ```
   This populates the `tools/immich-go/` directory with `immich-go.exe` and `pinned-version.txt`.

3. **Restore and Build**:
   ```powershell
   dotnet build ImmichAutoUploader.sln
   ```

---

## Project Architecture

The solution is divided into three focused projects:

```
immich-auto-uploader/
├── build.ps1                              # Downloads & pins latest immich-go binary
├── ImmichAutoUploader.sln
├── tools/immich-go/                       # Bundled immich-go binaries (git-ignored)
├── src/
│   ├── ImmichAutoUploader.Core/           # Headless class library (net10.0)
│   │   ├── Models/                        # Data contracts (AppSettings, QueuedFile, etc.)
│   │   ├── Security/                      # DPAPI credential store & interfaces
│   │   └── Services/                      # Watcher, Queue, Engine, Runner, Tailscale, Jellyfin
│   └── ImmichAutoUploader.App/            # WPF desktop system tray application (net10.0-windows)
│       ├── App.xaml / App.xaml.cs         # Single-instance mutex, lifecycle orchestration
│       ├── MainWindow.xaml / .cs          # Settings UI, connection test dialog
│       └── TrayIconManager.cs             # System tray icon, notifications, context menu
└── tests/
    └── ImmichAutoUploader.Core.Tests/     # xUnit test suite
```

For an in-depth architectural breakdown, please read [ARCHITECTURE.md](file:///c:/Users/Joe%20Urbanek/Documents/Antigravity/immich-auto-uploader/ARCHITECTURE.md).

---

## Building and Running

### Running in Debug Mode
To launch the application with the settings UI visible:
```powershell
dotnet run --project src/ImmichAutoUploader.App
```

To simulate running directly into the system tray (without displaying the settings window):
```powershell
dotnet run --project src/ImmichAutoUploader.App -- --tray
```

### Publishing a Single-File Release
To generate the production single-file self-contained executable:
```powershell
dotnet publish src/ImmichAutoUploader.App/ImmichAutoUploader.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o publish
```

---

## Running Unit Tests

We use **xUnit** and **FluentAssertions** for automated testing. Unit tests do not require external services or network access; database and file system operations are isolated via temporary directories and in-memory test databases.

Run the test suite from the repository root:
```powershell
dotnet test
```

To run with detailed test output:
```powershell
dotnet test --logger "console;verbosity=detailed"
```

When contributing new features or bug fixes, ensure relevant tests are added to `tests/ImmichAutoUploader.Core.Tests/`.

---

## Coding Guidelines & Standards

- **Language Version**: C# 13 with .NET 10 features (nullable reference types enabled, file-scoped namespaces, pattern matching).
- **XML Documentation**: All public and internal types, records, methods, and properties must include standard C# XML doc comments (`/// <summary>`, `<param>`, `<returns>`, `<exception>`).
- **Thread Safety**:
  - `UploadQueue` serializes database access via a private lock object (`_lock`). Never perform long-running I/O or hashing inside `_lock`.
  - `FileWatcherService` uses bounded channels (`Channel<string>`) to buffer incoming file events and decouple intake from queueing.
  - `UploadEngine` uses `SemaphoreSlim` to throttle concurrent uploads up to `AppSettings.ConcurrentTasks`.
- **Error Handling**:
  - The application must never crash due to external file locks, transient network drops, or corrupt user files.
  - Suppress and log transient I/O exceptions when appropriate. Use [AppLogger](file:///c:/Users/Joe%20Urbanek/Documents/Antigravity/immich-auto-uploader/src/ImmichAutoUploader.Core/Services/AppLogger.cs) to record errors.

---

## Security Standards

- **Zero Plaintext Secrets**: API keys must never be stored in `settings.json`, output in console logs, written to log files, or displayed in exception traces.
- **Windows DPAPI**: Always persist sensitive credentials using [DpapiCredentialStore](file:///c:/Users/Joe%20Urbanek/Documents/Antigravity/immich-auto-uploader/src/ImmichAutoUploader.Core/Security/DpapiCredentialStore.cs) (`DataProtectionScope.CurrentUser`).
- **Process Security**: Never pass API keys as command-line arguments to child processes. Child processes (such as `immich-go`) receive API keys exclusively via environment variables (`IMMICH_API_KEY`, `IMMICH_ADMIN_API_KEY`) to prevent secrets from leaking into process tables or event audit logs.
- **Log Redaction**: When logging error output from external processes, always pass error strings through `UploadEngine.RedactSecrets`.

---

## Submitting Pull Requests

1. **Create a branch**:
   ```powershell
   git checkout -b feature/my-feature-name
   ```
2. **Commit your changes**:
   Write concise, imperative commit messages:
   ```powershell
   git commit -m "feat(watcher): improve handling for transient network locks"
   ```
3. **Validate**:
   Ensure the code builds cleanly and all tests pass:
   ```powershell
   dotnet build ImmichAutoUploader.sln -c Release
   dotnet test
   ```
4. **Open a Pull Request**:
   Push your branch to your fork and submit a PR against `main`. Provide a clear summary of what changed and reference any related issues.
