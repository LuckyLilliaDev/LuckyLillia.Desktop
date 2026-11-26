# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Lucky Lillia Desktop (幸运莉莉娅桌面端) is the desktop shell for LLBot (QQ robot framework). Built with Avalonia UI + ReactiveUI on .NET 10, it prepares the runtime (Node.js / LLBot downloads), runs a single LLBot Node process (`llbot.js` -> `llbot-manager.js`, which manages all instances centrally), and embeds the unified gateway WebUI in a WebView2 as the control panel. Everything LLBot-specific (instances, login method, QQ path, protocol, unified WebUI password) is configured inside that WebUI; the desktop app only keeps its own few settings.

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

Three pages, switched by `MainWindowViewModel.SelectedIndex` (0 = 控制面板 `HomePage`, 1 = 设置 `SettingsPage`, 2 = 关于 `AboutPage`). There is no log page: the terminal output (all instances + desktop logs) lives inside the control panel (toolbar `终端日志` toggle, `HomeViewModel.ShowTerminal`, embedded `LogPage` bound to `HomeViewModel.TerminalLog`). The navigation is a row of tabs in the custom title bar (`ListBox.nav` in `MainWindow.axaml`) next to the theme toggle -- there is deliberately **no desktop sidebar**, the only sidebar on screen is the WebUI's own. `TitleBar_PointerPressed` skips window dragging when the press lands on the tabs. `MainWindow.SetVisiblePage` toggles `IsVisible` without animation because the control panel holds a native WebView2 window (see Platform-Specific Behavior). Dialogs are injected into ViewModels as `Func<T>` delegates (`ConfirmDialog`, `ShowAlertDialog`, `ShowLoadingDialog`, `ShowTextInputDialog`).

### Key Responsibilities

| Component | Role |
|---------|------|
| `HomeViewModel` | Single LLBot manager process lifecycle (`ServicePhase`: Stopped / Starting / Running / Stopping / Failed), runtime preparation (`RuntimePhase`), gateway port allocation + `--webui-port`, WebUI readiness, unified WebUI URL and overlay state, plus one-time migration of dev `auto_start_instances` to `data/manager.json`. See [doc/llbot-webui.md](doc/llbot-webui.md). |
| `Controls/WebView2Host` + `Utils/WebView2/*` | WebView2 hosted via `NativeControlHost` with hand-written, NativeAOT-safe COM interop. See [doc/webview2.md](doc/webview2.md). |
| `Utils/LLBotWebUi` | Reading unified gateway host/port (`data/manager.json` with fallback to `data/instances/default/settings.json`), unified login cookie (`data/webui_token.txt` with legacy fallback), one-time `auto_start_instances` migration, `IsWebUiAsync` signature probe (unit-tested). |
| `Utils/TcpPorts` + `Utils/TcpListenerTable` | Free-port allocation (system TCP listener table first, bind probe second) and listener-port-to-PID lookup via `GetExtendedTcpTable` (Windows only, null elsewhere). |
| `ProcessManager` | Single LLBot Node process (`StartAsync` / `StopAsync` / `ForceKill`, `StatusChanged` events). Graceful stop = console Ctrl+C (Windows, `AttachConsole` + `GenerateConsoleCtrlEvent`) / SIGTERM (Unix), 35 s (manager stops all child instances within 25 s), then process-tree kill; clears the inherited ignore-Ctrl+C flag before spawning. Windows Job Objects cover parent crashes. |
| `ConfigManager` | JSON config I/O (`app_settings.json`) with caching and raw JSON node preservation. |
| `DownloadService` | Node.js / LLBot / app-update downloads with progress tracking and multi-mirror npm registry support. |
| `UpdateChecker` / `UpdateStateService` | Version checks for the app and LLBot; the About page performs updates. |
| `SystemTimeChecker` | UTC Date-header clock check before every LLBot start. |
| `SettingsViewModel` | Desktop-only settings page: theme / tray / close behavior / autostart / startup command (switches apply immediately, single-key writes) and the LLBot runtime fields `node_path`, `llbot_path`, `server_region`, `http_proxy` (text fields are drafts saved by the bottom "保存" button; take effect on the next instance start). Instances themselves are managed on the control panel, not here. Theme changes round-trip through `MainWindowViewModel` (`ThemeModeChanged` / `SyncThemeMode`) so the title-bar toggle and the page agree. |
| `LogViewModel` / `LogPage` | 终端日志: every instance's stdout/stderr plus the desktop's own Serilog output, unfiltered, each line prefixed with its source (`[default]`, `[Desktop]`; desktop WRN/ERR levels colored; ANSI colors via `Utils/AnsiParser`): auto-scroll, multi-select copy, clear (all). Owned by `HomeViewModel.TerminalLog`, available even with no instance; UI updates pause while the window is hidden (`MainWindow.SetBackgroundMode`). |
| `AboutViewModel` | Versions / updates / links. |

### Process Management Rules

- Single process: Desktop starts one `llbot.js` process (`llbot.js` -> `llbot-manager.js` -> per-instance `llbot-core.js`). Multi-instance management (create/start/stop/rename/delete/autostart) happens inside the embedded WebUI. The toolbar shows `[status dot] LLBot :port` + page actions (`主页` / `终端日志` / `刷新` / `关闭页面|打开页面` / `重启` / `启动|停止`).
- Launch flags: `--parent-pid=<Desktop PID>`, `--webui-port <port>`, `--cdn china` when `server_region` is `china`, `--dev` in dev mode, plus the HTTP proxy environment variables. **No** `--instance`, login-mode, protocol, or QQ-path flags.
- Port: preferred port comes from `data/manager.json` (fallback: legacy `data/instances/default/settings.json`, then `127.0.0.1:3080`); if busy, falls back to `webui_fallback_port` then `TcpPorts.FindFree` and passes `--webui-port`. Readiness = the port is listened on by LLBot's process tree (`TcpListenerTable` + `ProcessTree`); if LLBot bound another port, desktop scans the tree's listeners for `x-webui-auth` (internal child instance ports refuse without `x-webui-auth`, so they are never mistaken for the gateway). Details in [doc/llbot-webui.md](doc/llbot-webui.md).
- Runtime gate before every start: system time check (offset > 1 min blocks; check failures log and continue), Node.js >= 24 (config -> PATH -> `bin/llbot/node.exe` -> download), LLBot script (download `@luckylillia-dev/luckylillia` if missing), LLBot major version >= 8. Downloaded paths are written back with read-modify-write (see [doc/config-persistence.md](doc/config-persistence.md)).
- Stop: console Ctrl+C to `llbot.js` (manager handles SIGINT and stops all child instances via IPC within 25 s; SIGTERM on Unix) -> wait up to 35 s -> process-tree kill fallback. `ProcessManager` calls `SetConsoleCtrlHandler(NULL, FALSE)` before every spawn. Never terminate a QQ process. Windows Job Objects cover desktop crashes; on Unix LLBot watches `--parent-pid`. See [doc/job-object.md](doc/job-object.md).
- Exiting on its own flips `ServicePhase` to `Stopped` with the exit code and a start button; "重启" confirms first. The About page updates LLBot through `HomeViewModel.StopForUpdateAsync` / `ResumeAfterUpdateAsync` (stop process, re-run runtime gate, restart if it was running).
- There is no desktop <-> LLBot IPC and the desktop shows no account info (nickname / uin).

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

Serilog with console + file sinks. Log files are per-session (`logs/yyyyMMdd_HHmmss.log`), capped at 10MB with auto-roll, and cleaned up on startup (7-day retention, max 50 files). Each instance's stdout/stderr is attached by `LogCollector` and written into the same log with the instance name as prefix (`[default] ...`). The 终端日志 view shows all of it live together with the desktop's own log events: `Services/LogCollectorSink` is a Serilog sink feeding `ILogCollector.Add` (it buffers until DI attaches the collector, and skips the instance-output events carrying the `InstanceOutput` property so lines are not duplicated). The 设置 page / the Stopped overlay offer "打开日志目录".

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
