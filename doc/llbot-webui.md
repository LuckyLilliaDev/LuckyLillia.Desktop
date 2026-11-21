# LLBot 实例与 WebUI

Desktop 只做三件事: 准备运行时 -> 按实例拉起 LLBot -> 把选中实例的 WebUI 嵌进控制面板. LLBot 自己的配置 (登录方式
直连/PMHQ 桥接, QQ 路径, 协议, WebUI 端口, WebUI 密码...) 全部在 WebUI 里改, Desktop 不读不写, 启动时也不传任何模式参数.
入口是 `HomeViewModel` (`ViewModels/HomeViewModel.cs`), 每个实例一个 `InstanceViewModel`.

## 实例从哪来

LLBot 是多实例的: `<LLBot 目录>/data/instances/<实例名>/` 一套配置 (`settings.json` / `plugins.json` / `email.json` /
`webui_token.txt` / `plugin-data/` / `database.db`, 见 LLBot `src/common/configPaths.ts`), 名字规则
`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` (`src/common/instance.ts`). LLBot 的 `DATA_DIR` 是 `path.resolve('data')`,
Desktop 起它时工作目录就是 `llbot.js` 所在目录, 所以 `Utils/LLBotWebUi.cs` 里的路径都按脚本目录拼.

- 实例列表 = `data/instances/` 下名字合法的子目录 + 永远存在的 `default` (`LLBotWebUi.ListInstances`); 正在运行的实例
  即使目录被外部删了也留在列表里. 菜单 "重新扫描实例目录" 就地增删 (不整表清空: 清空会让下拉框丢掉选中项, 之后同一个值推不回去), 保留已有实例对象的运行状态.
- 新建实例: 入口是实例下拉框末尾的 "新建实例..." 项 (`NewInstanceEntry`; `HomePage` 在 SelectionChanged 里把控件拨回当前实例再调 `RequestNewInstance`, 不能靠 ViewModel 重发同一个值, 两向绑定不推相同值). 只要名字合法且不重名, 就新建一个 `InstanceViewModel` 并立刻启动, LLBot 第一次以这个名字启动时自己按
  `default_config.json` 生成目录. Desktop 不写实例目录.
- 删除实例: 只允许停止态, 确认后删 `data/instances/<名>/` 和锁持有者文件 `data/state/instances/<名>.json`.
  QQ 账号自己的消息库在 `data/database/` 下, 不在实例目录里, 不会被删.
- 随桌面端启动: `app_settings.json` 的 `auto_start_instances` (字符串数组, 默认 `["default"]`), 菜单里勾选即写回.
  程序启动时按顺序逐个拉起 (串行, 端口分配要看前一个占了什么).

## 启动一个实例

1. 运行时准备 (`EnsureRuntimeAsync`, 所有实例共用, 只做一次): 系统时间校验 (偏差 > 1 分钟阻止, 网络不可用放行),
   Node.js >= 24 (配置 -> PATH -> `bin/llbot/node.exe` -> 下载), `bin/llbot/llbot.js` (缺则下载 `llonebot-dist`),
   LLBot 主版本 >= 8 (FFmpeg 已不是必需组件, 不再检测 / 下载). 下载改了路径就 read-modify-write 存回 `app_settings.json`.
2. 读该实例 `settings.json` 的 `webui` 段 (`ReadEndpoint`; 文件缺失 / 坏掉 / 字段缺失退回 `127.0.0.1:3080`).
   `webui.enable=false` 直接失败, 遮罩提示去改回来.
3. 分配端口 (`TcpPorts.FindFree`): 从配置的端口起往上找第一个空闲且没被别的实例占的端口, 以 `--webui-port <端口>` 传给
   LLBot (这个参数让 LLBot 在内存里覆盖 `webui.port`, WebUI 里该字段只读, 不写回文件). 端口和配置不同时只在日志里记警告, 界面不提示.
4. `IProcessManager.StartInstanceAsync(实例名, LLBotLaunchOptions)`: 参数只有 `--instance <名>` / `--webui-port <端口>` /
   `--parent-pid=<Desktop PID>` / `--cdn china` (配置 `server_region`) / `--dev` (配置 `dev`), 环境变量传代理. 进程输出以实例名为前缀
   进日志 (`[default] ...`).
5. 等 WebUI 就绪 (`WaitForWebUiAsync`, 最多 90 秒, 每 500ms 一轮):
   - Windows: 查系统 TCP 监听表 (`TcpListenerTable`, `GetExtendedTcpTable`), 分配的端口被**这个实例的 PID** 监听即就绪.
     端口能连上不算数 -- 别的程序 / 别的实例占着同一个端口时连通探测会被骗.
   - LLBot 没用分配的端口 (端口在它启动前一瞬被占, 或旧版本不认 `--webui-port`) 时, 在该 PID 监听的其他端口里找
     带 WebUI 特征的 (`IsWebUiAsync`: GET `/api/login-info` 响应带 `x-webui-auth` 头), 找到就改用 (日志里有警告).
   - 非 Windows 没有监听表, 或 Windows 读取失败时, 用 `IsWebUiAsync` 探分配的端口. HTTP 探测直接连接本机, 不走系统代理、不跟随重定向、不携带 cookie; 普通 HTTP 200 不算就绪, 必须有 WebUI 的鉴权特征头.
   - `LLBotWebUi.FindReadyPortAsync` 共用上述判断; 能读到完整端口表时仍只认可自己的 PID, 不会连到其他实例占用的端口. 开始等待时记录 PID / 地址, 端口表不可用时警告一次, 超时记录该 PID 的监听端口和目标端口的实际拥有者.
   - 期间进程退出 -> Stopped (显示退出码); 被用户停掉 -> 停止流程接管.
6. 就绪后 `Url` 赋值, 选中该实例时 `WebView2Host` 导航. 切换下拉框只是换 `WebUiUrl`, 不动进程.

## 端口探测的坑

Windows 上 Node 的监听 socket 允许别的进程再绑同一个端口 (SO_REUSEADDR 语义), `TcpListener` 试绑定会把被占的端口当成
空闲 -- 本机 `tsx watch` 跑着的 LLBot 占着 3080, 试绑定 `0.0.0.0:3080` / `127.0.0.1:3080` 全部成功. 所以
`TcpPorts.FindFree` 先查 TCP 监听表 (IPv4 + IPv6), 没有监听表的平台才只靠试绑定. 实测: 3080 被占时分配 3081,
LLBot 日志 `Webui 服务器已启动 127.0.0.1:3081`, 0.5 秒内就绪.

2026-11-09 修复: `GetExtendedTcpTable` 读取出错曾被当成成功取得空表或部分表, 导致浏览器能打开 WebUI,
Desktop 却不执行 HTTP 兜底并一直等到超时. 现在任一地址族读取失败都返回 `null`, 成功取得空表才返回空列表;
查询大小后监听表增长时按新大小重试 (最多 3 次). `TcpListenerTableTests` 覆盖 API 失败、增长和实际 IPv4 / IPv6
监听 PID; `LLBotWebUiProbeTests` 覆盖从表读取失败到本机 HTTP 就绪的完整判断, 以及不同 PID、端口顺延、代理和重定向.
这是已复现的错误处理漏洞; 没有发生机器的 Desktop 日志时, 不能仅凭“等待就绪”截图认定它是唯一原因.

## 停止 / 重启

- 停止 (`StopInstanceAsync`): `IProcessManager.StopInstanceAsync` 先请求优雅退出 (Windows: `AttachConsole` 到子进程的隐藏
  控制台发 Ctrl+C, LLBot 当 SIGINT 处理, `src/main/hostLifecycle.ts`; Unix: SIGTERM), 最多等 35 秒, 超时杀进程树.
  实测 8.3.3 收到 Ctrl+C 后 0.2 秒退出.
- 子进程会继承父进程的 "忽略 Ctrl+C" 标志 (`SetConsoleCtrlHandler(NULL, TRUE)`; 某些终端 / 启动器拉起 Desktop 时会带上),
  带着它的 LLBot 收不到 Ctrl+C, 只能等 35 秒超时强杀. `ProcessManager` 起进程前先 `SetConsoleCtrlHandler(NULL, FALSE)`
  清掉. 实验: 不清标志 node 收不到 SIGINT, 清了立刻收到.
- 重启 = 停止 + 启动 (先确认, 会断开登录会话). 关于页更新 LLBot 前 `StopAllForUpdateAsync` 停掉全部并记住哪些在跑,
  更新后 `ResumeAfterUpdateAsync` 重新过一遍运行时检查 (版本变了) 再把它们拉起来.
- 进程自己退出 (`IProcessManager.StatusChanged` -> Stopped / Error) 进入 Stopped, 遮罩显示退出码和 "启动实例" 按钮.
- Desktop 退出时 `StopAllAsync` 并行停所有实例; Desktop 崩溃由 Job Object 兜底 (见 [job-object.md](job-object.md)),
  Unix 上 LLBot 自己盯 `--parent-pid` 传入的 PID.
- 不要去碰 QQ 进程: QQ 归 LLBot 的原生模块管.

## 页面状态

工具条: `实例 [下拉框: 状态点 + 实例名 + :端口, 末尾 "新建实例..."]` + 红字页面加载错误; 右侧 有更新 / 终端日志 (开关) /
刷新 / 启动|停止 / 菜单 (重启当前实例, 随桌面端启动, 删除当前实例, 重新扫描实例目录). "终端日志" 打开后内容区换成
选中实例的 stdout / stderr (`LogPage`, `HomeViewModel.TerminalLog`, 按 `LogEntry.ProcessName` = 实例名过滤, 切实例跟着切),
再点一次回到 WebUI; 桌面端没有单独的日志页. 状态只靠颜色点 (绿 运行中, 黄 启动 / 停止中,
灰 未运行, 红 失败), 不显示状态文字、URL 或端口被占的提示; "在浏览器中打开" 只在内嵌 WebView 不可用的遮罩上有.

| 状态 | 内容区 |
|---|---|
| 运行时 Preparing / Downloading | 遮罩: 转圈 / 下载进度 (可取消) |
| 运行时 Failed | 遮罩: 原因 + 重试 + 终端日志 / 打开日志目录 |
| 没有实例 | 遮罩: 说明 + 新建实例 |
| 选中实例 Starting / Stopping | 遮罩: 转圈 + `正在启动实例 x (端口 N)` |
| 选中实例 Running, WebView2 可用 | WebView2 |
| 选中实例 Running, WebView2 不可用 | 遮罩: 安装 WebView2 / 在浏览器中打开 (见 [webview2.md](webview2.md)) |
| 选中实例 Stopped / Failed | 遮罩: 原因 + 启动实例 (重试) + 终端日志 / 打开日志目录 |

页面导航失败 (`NavigationCompleted` 不成功) 不换遮罩, 只在工具条红字提示并保留 WebView2 自己的错误页, 点刷新重试.

## 账号信息

Desktop 不再拿 QQ 账号信息 (IPC 已删除): 侧边栏 / 标题 / 托盘都不显示昵称和 QQ 号, 账号状态看各实例的 WebUI.
以后要显示的话可以用 WebUI 的 `GET /api/login-info` (`X-Webui-Token` = `webui_token.txt` 明文密码的 sha256).

## 用源码构建的 LLBot 验证

`../LuckyLillia.Bot` 里 `npm run build && npm run build-webui` (先 build 再 build-webui, 后者输出到 `dist/webui`, 反过来
会被 vite 清掉), 产物在 `dist/` (`llbot.js` + `package.json` + `node_modules` + `default_config.json` + `webui/`).
把 `app_settings.json` 的 `llbot_path` 指到 `dist/llbot.js` 即可, 数据目录随之在 `dist/data/`.
**再次 build 前先把 `dist/data` 挪走**: vite 会清空整个 `dist/` (实例配置 / WebUI 密码 / 插件库都在里面), 而且实例在跑时
文件被占着, build 直接 EPERM 失败. 顺序: 退出桌面端 -> `mv dist/data /tmp/keep` -> build -> 挪回来 -> 再启动.
已发布的 `llonebot-dist` 8.3.2 不认 `--webui-port`, 走的是上面 "LLBot 没用分配的端口" 的兜底路径.
