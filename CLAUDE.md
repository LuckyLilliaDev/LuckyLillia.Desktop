# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Lucky Lillia Desktop (幸运莉莉娅桌面端) is the desktop shell for LLBot (QQ robot framework). Built with Avalonia UI + ReactiveUI on .NET 10, it prepares the runtime (Node.js / LLBot downloads), runs one LLBot Node process per **instance** (`<LLBot>/data/instances/<name>/`), and embeds the selected instance's WebUI in a WebView2 as the control panel. Everything LLBot-specific (login method, QQ path, protocol, WebUI password) is configured inside that WebUI; the desktop app only keeps its own few settings plus the instance list / auto-start choice.

The codebase is primarily in Chinese (comments, UI strings, log messages).

## Build & Run Commands

```bash
# Restore and build
dotnet restore
dotnet build

# Run in development
dotnet run

# Publish for Windows (NativeAOT single-file exe). Needs an MSVC developer environment
# (vswhere.exe on PATH; CI uses setup-msvc-dev, locally use a VS Developer PowerShell)
dotnet publish -c Release -r win-x64

# Publish for macOS
./build-macos-app.sh arm64

# Run tests (test project is standalone, not referenced by main .sln)
dotnet test LuckyLilliaDesktop.Tests/LuckyLilliaDesktop.Tests.csproj

# Run a single test
dotnet test LuckyLilliaDesktop.Tests/LuckyLilliaDesktop.Tests.csproj --filter "FullyQualifiedName~TestMethodName"
```

## Architecture

### MVVM with Reactive Extensions

- **ViewModels** extend `ViewModelBase` (which extends ReactiveUI's `ReactiveObject`). Properties use `RaiseAndSetIfChanged`; derived properties are re-raised by hand (`HomeViewModel.RaiseDerived`). Commands use `ReactiveCommand` with `AvaloniaUiScheduler` and gate inside the handler instead of `canExecute` observables (their `CanExecuteChanged` fires cross-thread).
- **Views** are Avalonia AXAML files. Pages live under `Views/Pages/`, dialogs under `Views/`.
- **Services** are interface-based, all registered as **singletons** in DI. ViewModels are **transient**.

### Dependency Injection

Configured in `App.axaml.cs:ConfigureServices()` using `Microsoft.Extensions.DependencyInjection`. The `IServiceProvider` is stored as `App.Services` and accessed by views to resolve dependencies.

### Navigation

Three pages, switched by `MainWindowViewModel.SelectedIndex` (0 = 控制面板 `HomePage`, 1 = 设置 `SettingsPage`, 2 = 关于 `AboutPage`). There is no log page: each instance's terminal output lives inside the control panel (toolbar `终端日志` toggle, `HomeViewModel.ShowTerminal`, embedded `LogPage` bound to `HomeViewModel.TerminalLog`). The navigation is a row of tabs in the custom title bar (`ListBox.nav` in `MainWindow.axaml`) next to the theme toggle -- there is deliberately **no desktop sidebar**, the only sidebar on screen is the WebUI's own. `TitleBar_PointerPressed` skips window dragging when the press lands on the tabs. `MainWindow.SetVisiblePage` toggles `IsVisible` without animation because the control panel holds a native WebView2 window (see Platform-Specific Behavior). Dialogs are injected into ViewModels as `Func<T>` delegates (`ConfirmDialog`, `ShowAlertDialog`, `ShowLoadingDialog`, `ShowTextInputDialog`).

### Key Responsibilities

| Component | Role |
|---------|------|
| `HomeViewModel` + `InstanceViewModel` | Instance list (`data/instances/*` + `default`), per-instance lifecycle (`InstancePhase`: Stopped / Starting / Running / Stopping / Failed), shared runtime preparation (`RuntimePhase`), port allocation + `--webui-port`, WebUI readiness, new / delete instance, auto-start set, the selected instance's URL and overlay state. See [doc/llbot-webui.md](doc/llbot-webui.md). |
| `Controls/WebView2Host` + `Utils/WebView2/*` | WebView2 hosted via `NativeControlHost` with hand-written, NativeAOT-safe COM interop. See [doc/webview2.md](doc/webview2.md). |
| `Utils/LLBotWebUi` | Instance name rules, instance / lock-holder paths, `ListInstances`, reading the WebUI host/port from the instance `settings.json`, `IsWebUiAsync` signature probe (unit-tested). |
| `Utils/TcpPorts` + `Utils/TcpListenerTable` | Free-port allocation (system TCP listener table first, bind probe second) and listener-port-to-PID lookup via `GetExtendedTcpTable` (Windows only, null elsewhere). |
| `ProcessManager` | One LLBot Node process per instance (`StartInstanceAsync` / `StopInstanceAsync` / `StopAllAsync`, `StatusChanged` events). Graceful stop = console Ctrl+C (Windows, `AttachConsole` + `GenerateConsoleCtrlEvent`) / SIGTERM (Unix), 35 s, then process-tree kill; clears the inherited ignore-Ctrl+C flag before spawning. Windows Job Objects cover parent crashes. |
| `ConfigManager` | JSON config I/O (`app_settings.json`) with caching and raw JSON node preservation. |
| `DownloadService` | Node.js / LLBot / app-update downloads with progress tracking and multi-mirror npm registry support. |
| `UpdateChecker` / `UpdateStateService` | Version checks for the app and LLBot; the About page performs updates. |
| `SystemTimeChecker` | UTC Date-header clock check before every LLBot start. |
| `SettingsViewModel` | Desktop-only settings page: theme / tray / close behavior / autostart / startup command (switches apply immediately, single-key writes) and the LLBot runtime fields `node_path`, `llbot_path`, `server_region`, `http_proxy` (text fields are drafts saved by the bottom "保存" button; take effect on the next instance start). Instances themselves are managed on the control panel, not here. Theme changes round-trip through `MainWindowViewModel` (`ThemeModeChanged` / `SyncThemeMode`) so the title-bar toggle and the page agree. |
| `LogViewModel` / `LogPage` | Terminal output of one instance (`LogViewModel.Instance` filters `ILogCollector.LogStream` by process name; ANSI colors via `Utils/AnsiParser`): auto-scroll, multi-select copy, per-instance clear. Owned by `HomeViewModel.TerminalLog`, follows the selected instance; UI updates pause while the window is hidden (`MainWindow.SetBackgroundMode`). |
| `AboutViewModel` | Versions / updates / links. |

### Process Management Rules

- Instances: every directory under `<LLBot>/data/instances/` with a valid name (`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`) plus `default`; one Node process each, several may run at once. Instances listed in `app_settings.json` `auto_start_instances` (default `["default"]`) are started on launch, sequentially. The toolbar shows only a ComboBox of `status dot + name + :port` (green running / yellow starting or stopping / gray stopped / red failed) with a trailing `新建实例...` entry (`NewInstanceEntry` sentinel handled by `HomeViewModel.PickerSelection`) -- no status text, URL, port-conflict notice or open-in-browser button (that one exists only on the no-WebView overlay).
- Launch flags per instance: `--instance <name>`, `--webui-port <port>` and `--parent-pid=<Desktop PID>` (always explicit), `--cdn china` when `server_region` is `china`, `--dev` in dev mode, plus the HTTP proxy environment variables. **No** login-mode / protocol / QQ-path flags: those are chosen in LLBot's WebUI, and LLBot marks them as pinned by `desktop` only when a flag is passed.
- Port: preferred port is `webui.port` from that instance's `settings.json` (default `127.0.0.1:3080`); `TcpPorts.FindFree` walks upward past ports that are busy or already given to another instance and the result is passed as `--webui-port` (LLBot keeps it in memory only, the WebUI shows the field read-only). Readiness = the port is listened on by the instance's own PID (`TcpListenerTable`); if LLBot ended up elsewhere (port grabbed in between, or an old LLBot that ignores `--webui-port`) the desktop scans that PID's listeners for the WebUI signature (`x-webui-auth` header). Do not trust a plain connect probe or a bind probe: on Windows a Node listener lets other processes bind the same port. Details in [doc/llbot-webui.md](doc/llbot-webui.md).
- Runtime gate before every start: system time check (offset > 1 min blocks; check failures log and continue), Node.js >= 24 (config -> PATH -> `bin/llbot/node.exe` -> download), LLBot script (download `@luckylillia-dev/luckylillia` if missing), LLBot major version >= 8. Downloaded paths are written back with read-modify-write (see [doc/config-persistence.md](doc/config-persistence.md)).
- Stop: console Ctrl+C to the instance process (LLBot handles SIGINT in `src/main/hostLifecycle.ts`; SIGTERM on Unix) -> wait up to 35 s -> process-tree kill fallback. `ProcessManager` calls `SetConsoleCtrlHandler(NULL, FALSE)` before every spawn because the ignore-Ctrl+C flag is inherited and a child carrying it never sees the Ctrl+C (learned the hard way: launched from a terminal tool, stops took the full 35 s). Never terminate a QQ process: native code inside LLBot owns a spawned QQ and an attached QQ belongs to its owner. Windows Job Objects cover desktop crashes; on Unix LLBot watches the PID passed with `--parent-pid` itself. See [doc/job-object.md](doc/job-object.md).
- An instance exiting on its own flips it to `Stopped` with the exit code and a start button; "重启当前实例" confirms first because it drops the login session. The About page updates LLBot through `HomeViewModel.StopAllForUpdateAsync` / `ResumeAfterUpdateAsync` (stop everything, re-run the runtime gate, restart what was running).
- Deleting an instance (stopped only, confirmed) removes `data/instances/<name>/` and the lock-holder file `data/state/instances/<name>.json`; the QQ account's own database under `data/database/` stays.
- There is no desktop <-> LLBot IPC any more and the desktop shows no account info (nickname / uin); QR / headless login, auth tokens, QQ download and PMHQ config all live in LLBot's WebUI.

### Platform-Specific Behavior

Cross-platform logic uses `PlatformHelper` (in `Utils/`). Key differences:
- **Windows**: WebView2 embedded via `NativeControlHost`. Three hard requirements, each learned the hard way: the exe must embed `app.manifest` (supportedOS list, csproj `ApplicationManifest`), the Avalonia holder window must have `WS_EX_LAYERED` removed (`WebView2Host.UnlayerHolder`), and the Win32 composition mode must stay at the default (RedirectionSurface + ANGLE blanks the WebView). Details and the tested matrix in [doc/webview2.md](doc/webview2.md); the same doc covers background suspend (`SetBackgroundMode`), the graceful WebView shutdown on exit (the Job Object would otherwise hard-kill the browser before it flushes cookies) and why the WebUI login cookie is shared across instances. Windows Job Objects for process tree cleanup; HKCU Run entry for autostart (`StartupManager`).
- **macOS / Linux**: no embedded view -- the control panel shows the WebUI URL with an "open in browser" button. `.app` bundle handling in `Program.cs`, App Translocation detection/recovery, `~/Library/Application Support/LuckyLilliaDesktop` as working directory when installed to `/Applications`.
- Working directory is set in `Program.cs` before the Avalonia app starts -- this is critical because all relative paths (config, logs, binaries, `data/webview2`) depend on it.

### UI Theme Rules

All UI must support both light and dark themes. **Never hardcode colors** -- use `DynamicResource` bindings:

| Purpose | Resource Key |
|---------|-------------|
| Primary background | `SystemControlBackgroundAltHighBrush` |
| Secondary background | `SystemControlBackgroundBaseLowBrush` |
| Card background | `CardBackground` |
| Primary text | `SystemControlForegroundBaseHighBrush` or `TextPrimary` |
| Secondary text | `SystemControlForegroundBaseMediumBrush` or `TextSecondary` |
| Border | `SystemControlForegroundBaseLowBrush` or `BorderColor` |
| Accent/primary button | `PrimaryBrush` |
| Danger action | `DangerColor` |

Exceptions: brand colors (e.g. `#6C7BFF`) and other functionally-fixed colors are acceptable with justification. Always verify contrast in both themes. The WebView's default background follows `MainBackground` so dark theme does not flash white, and the embedded WebUI follows the desktop theme because `WebView2Host.ApplyColorScheme` pins the page's `prefers-color-scheme` to `ActualThemeVariant` and an injected document-created script resets the WebUI's stored theme mode (`localStorage` `llbot-theme`) to `auto` (details in [doc/webview2.md](doc/webview2.md)).

### Configuration

`app_settings.json` stores only desktop-side settings with snake_case JSON property names (`llbot_path`, `node_path`, `auto_start_instances`, `dev`, `minimize_to_tray_on_start`, `startup_command(_enabled)`, `http_proxy`, `server_region`, `close_to_tray`, `theme_mode`, `window_*`). All of them except `dev`, `window_*` and `auto_start_instances` (control panel instance menu) are editable on the 设置 page. The `ConfigManager` preserves unknown JSON properties on single-key writes (`SetSettingAsync`), while `SaveConfigAsync` rewrites the whole file from `AppConfig`; field ownership and the read-modify-write rule are in [doc/config-persistence.md](doc/config-persistence.md).

### Logging

Serilog with console + file sinks. Log files are per-session (`logs/yyyyMMdd_HHmmss.log`), capped at 10MB with auto-roll, and cleaned up on startup (7-day retention, max 50 files). Each instance's stdout/stderr is attached by `LogCollector` and written into the same log with the instance name as prefix (`[default] ...`); it is also shown live per instance in the control panel's 终端日志 view, and the 设置 page / the Stopped overlay offer "打开日志目录".

## Topic Docs

| Doc | When to read |
|-----|--------------|
| [doc/llbot-webui.md](doc/llbot-webui.md) | Anything in the instance start/stop flow, instance listing / new / delete / auto-start, port allocation and WebUI readiness, or the control panel overlay states (`HomeViewModel`, `InstanceViewModel`, `LLBotWebUi`, `TcpPorts`, `ProcessManager` stop path). Also how to run against a from-source LLBot build. |
| [doc/webview2.md](doc/webview2.md) | Touching `Controls/WebView2Host`, `Utils/WebView2/*`, the csproj WebView2 package / AOT link settings, `Program.cs` Win32 options, or debugging a blank WebView. |
| [doc/config-persistence.md](doc/config-persistence.md) | 任何写 `app_settings.json` 的代码：整体存回必须 read-modify-write，别覆盖其他写入方维护的字段；`close_to_tray` 三态语义。 |
| [doc/job-object.md](doc/job-object.md) | Touching Windows Job Object subprocess cleanup in `ProcessManager`. |

## Release Process

Tags matching `v*` trigger the GitHub Actions workflow (`.github/workflows/release.yml`):
- Windows: `dotnet publish -c Release -r win-x64` -> NativeAOT single-file exe (WebView2 loader statically linked) -> zip + npm package
- macOS: `build-macos-app.sh` -> `.app` bundle -> tar.gz + npm package
- Both publish to GitHub Releases (**draft** -- must be published manually on the Releases page) and npm registry with provenance

npm publish gotchas:
- The npm steps require the `NPM_TOKEN` repo secret (`HAS_NPM_TOKEN` gate). If it is missing, they are **silently skipped** -- the run still shows success. If a release run succeeded but the version never appeared on npm, check this first.
- Manual `workflow_dispatch` runs never publish npm or create a release (`event_name == 'push'` gate; `ref_name` would be `main`, not the tag). Only a `v*` tag push publishes.
- To re-publish a version whose npm steps were skipped: delete the remote tag and push it again (`git push origin :refs/tags/vX.Y.Z && git push origin vX.Y.Z`) -- same commit is fine, the re-push re-fires the workflow.

## Testing

Tests use xUnit and live in `LuckyLilliaDesktop.Tests/` (separate net8.0 project, not in the solution file; it compiles selected `Utils/*.cs` files via `<Compile Include>`). Covered: `LLBotWebUi` (instance names, instance directory listing / paths, settings parsing, host normalization), `StartupCommandLine`. UI / WebView2 behavior is verified by running the app (`doc/webview2.md` lists what to look for in the log).
