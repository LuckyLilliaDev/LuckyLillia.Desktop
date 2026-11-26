# LLBot 统一管理进程与 WebUI

Desktop 只做三件事: 准备运行时 -> 拉起唯一的 LLBot 管理进程 (`llbot.js` -> `llbot-manager.js`) -> 把统一网关的 WebUI 嵌进控制面板.
多实例增删改启停、登录方式、QQ 路径、协议、WebUI 监听地址与密码全部在嵌入的 WebUI 里管理, Desktop 不再维护多实例状态, 启动时也不传 `--instance`.
入口是 `HomeViewModel` (`ViewModels/HomeViewModel.cs`).

## 统一网关配置与开发版迁移

LLBot 管理器的数据目录在 `<LLBot 目录>/data/` (`LLBotWebUi.DataDir`):
- `data/manager.json`: 统一网关监听地址 (`gateway.host` / `gateway.port`) 和各实例的自启设置 (`instances.<名>.autostart`).
- `data/webui_token.txt`: 所有实例共用的 WebUI 登录密码 (明文).
- `data/instances/<实例名>/`: 各实例独立的配置与数据, 由 `llbot-manager.js` 分别以子进程 (`llbot-core.js`) 管理.

对于使用过早期开发版多实例 Desktop 的用户, `HomeViewModel.MigrateLegacyDesktopMultiInstanceAsync` 会在首次运行时一次性把 `app_settings.json` 中的 `auto_start_instances` 迁到 `data/manager.json` (仅当 `manager.json` 尚不存在且列表非纯 `["default"]` 时写入), 并把 `instance_ports["default"]` 迁为 `webui_fallback_port`, 随后清理这两个旧字段.

## 启动流程

1. 运行时准备 (`EnsureRuntimeAsync`, 只做一次): 系统时间校验 (偏差 > 1 分钟阻止, 网络不可用放行),
   Node.js >= 24 (配置 -> PATH -> `bin/llbot/node.exe` -> 下载), `bin/llbot/llbot.js` (缺则下载 `@luckylillia-dev/luckylillia`),
   LLBot 主版本 >= 8. 下载改了路径就 read-modify-write 存回 `app_settings.json`.
   就绪后每次启动 / 重启仍会重读 `app_settings.json` (`RefreshReadyConfigAsync`), 设置页保存的代理 / 区域等直接生效;
   只有 `node_path` / `llbot_path` 改变时才重新走运行时检查.
2. 读网关监听地址 (`LLBotWebUi.ReadGatewayEndpoint`): 优先读 `data/manager.json` 的 `gateway` 段; 还没有 `manager.json` 时沿用
   `data/instances/default/settings.json` 的 `webui` 段; 否则退回 `127.0.0.1:3080`.
3. 确认端口空闲 (`TcpPorts`): 首选配置端口; 被占时先试 `app_settings.json` 的 `webui_fallback_port` (上次备用端口, 保持 origin 与 cookie / localStorage 稳定),
   再不行用 `TcpPorts.FindFree` 顺延或系统分配, 以 `--webui-port <端口>` 传给 `llbot.js`.
4. `IProcessManager.StartAsync(LLBotLaunchOptions)`: 启动参数为 `--parent-pid=<Desktop PID>`、`--webui-port <端口>`、
   `--cdn china` (配置 `server_region`)、`--dev` (配置 `dev`), 环境变量传代理. 进程输出归到日志源 `LLBot` (`llbot-manager.js` 在多实例时自己加 `[实例名] ` 前缀).
5. 等统一网关就绪 (`WaitForWebUiAsync`, 最多 90 秒, 每 500ms 一轮):
   - Windows: 查系统 TCP 监听表 (`TcpListenerTable`, `GetExtendedTcpTable`), 分配的端口被 **LLBot 进程树** (`ProcessTree`: `llbot.js` -> `llbot-manager.js` -> 子实例) 监听即就绪.
   - 若实际端口不同, 在进程树监听的其他端口里找带 `X-Webui-Auth` 头的 (`IsWebUiAsync`: GET `/api/login-info`). 各子实例内部端口受网关密钥保护, 拒绝请求时不带 `X-Webui-Auth` 头, 不会被误认成网关.
   - 非 Windows 或 Windows 端口表读取失败时, 直接用 `IsWebUiAsync` 探测分配端口 (直连本机, 不走代理/重定向/cookie).
   - 进程状态事件 (`IProcessManager.StatusChanged`) 带 `Requested=true` 时表示主动停止, 首页不当成异常退出.
6. 就绪后 `WebUiUrl` 赋值, `WebView2Host` 导航到统一网关根路径 `/` (实例总览管理页), 并在导航前预置来自 `data/webui_token.txt` 的免登录 cookie.

## 端口探测的坑

Windows 上 Node 的监听 socket 允许别的进程再绑同一个端口 (SO_REUSEADDR 语义), `TcpListener` 试绑定会把被占的端口当成空闲.
所以 `TcpPorts.FindFree` 先查 TCP 监听表 (IPv4 + IPv6), 没有监听表的平台才只靠试绑定.
`GetExtendedTcpTable` 任一地址族读取失败都返回 `null` (触发 HTTP 兜底), 查询大小后监听表增长时最多重试 3 次.

## 停止 / 重启

- 停止 (`StopAsync`): `IProcessManager.StopAsync` 先请求优雅退出 (Windows: `AttachConsole` 到隐藏控制台发 Ctrl+C; Unix: SIGTERM).
  `llbot-manager.js` 收到信号后通过 IPC 在 25 秒内逐一停止所有实例并退出, Desktop 最多等 35 秒, 超时再强杀进程树.
- 子进程会继承父进程的 "忽略 Ctrl+C" 标志 (`SetConsoleCtrlHandler(NULL, TRUE)`), `ProcessManager` 起进程前先清掉.
- 重启 = 停止 + 启动 (先确认, 会停止所有运行中的实例). 关于页更新 LLBot 前 `StopForUpdateAsync` 停进程, 更新后 `ResumeAfterUpdateAsync` 重新检查运行时并拉起.
- Desktop 退出时 `StopAsync` 优雅停机; Desktop 崩溃由 Job Object 兜底 (见 [job-object.md](job-object.md)),
  Unix 上由 `llbot.js` / `llbot-manager.js` 监控 `--parent-pid` 自行退出.
- 不要去碰 QQ 进程: QQ 归 LLBot 原生模块管.

## 页面状态

工具条: 左侧 `[状态点] LLBot :端口` + 红字页面加载错误; 右侧 `有更新` / `主页` + `终端日志` / `刷新` / `关闭页面|打开页面` / `重启` / `启动|停止`.
"终端日志" 显示 LLBot 输出与 Desktop 自身日志 (`LogPage`, `HomeViewModel.TerminalLog`), 点 "主页" 回到 WebUI.

| 状态 | 内容区 |
|---|---|
| 运行时 Preparing / Downloading | 遮罩: 转圈 / 下载进度 (可取消) |
| 运行时 Failed | 遮罩: 原因 + 重试 + 终端日志 / 打开日志目录 |
| LLBot Starting / Stopping | 遮罩: 转圈 + `正在启动 LLBot (端口 N)` |
| LLBot Running, WebView2 可用 | WebView2 (统一网关 WebUI) |
| LLBot Running, WebView2 不可用 | 遮罩: 安装 WebView2 / 在浏览器中打开 (见 [webview2.md](webview2.md)) |
| LLBot Stopped / Failed | 遮罩: 原因 + 启动 (重试) + 终端日志 / 打开日志目录 |

## 用源码构建的 LLBot 验证

`../LuckyLillia.Bot` 里运行 `npm run build` (会自动构建 SDK、内置插件、WebUI 前端和 `dist/llbot.js` / `llbot-manager.js` / `llbot-core.js` / `webui/`).
把 `app_settings.json` 的 `llbot_path` 指到 `dist/llbot.js` 即可, 数据目录随之在 `dist/data/`.
**再次 build 前先把 `dist/data` 挪走**: vite 会清空整个 `dist/`, 而且进程在跑时文件被占着会导致 EPERM.
