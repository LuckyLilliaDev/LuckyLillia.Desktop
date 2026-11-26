# app_settings.json 持久化约定

`app_settings.json` 由 `ConfigManager` 读写, 只放 Desktop 自己的设置 (LLBot 的配置都在它的 WebUI 里, 见
[llbot-webui.md](llbot-webui.md)). 有多个写入方各自维护其中不同的字段, 而 `ConfigManager.SaveConfigAsync` 是
**整文件覆盖写** (`JsonSerializer.Serialize(config)` + `File.WriteAllTextAsync`, 不做任何 merge), 任何持有部分字段的
写入方如果 `new AppConfig { ... }` 只填自己那几项再存回, 就会把**别的写入方维护的字段冲成默认值**.

## 字段归属

| 字段 | JSON key | 维护方 |
|------|----------|--------|
| 运行时路径 | `node_path` `llbot_path` | 设置页 `SettingsViewModel` (保存按钮, `SetSettingAsync` 单 key); `HomeViewModel.EnsureRuntimeAsync` 下载后也会改 (read-modify-write 整体存回) |
| LLBot 运行环境 | `http_proxy` `startup_command` | 设置页 (保存按钮, 单 key) |
| 备用 WebUI 端口 | `webui_fallback_port` (整数或 `null`) | 网关配置端口被占时 `HomeViewModel.RememberFallbackPortAsync` (`SetSettingAsync` 单 key) |
| 桌面端开关 | `minimize_to_tray_on_start` `close_to_tray` `startup_command_enabled` `server_region` | 设置页 (改动即时 `SetSettingAsync` 单 key) |
| 关闭窗口"记住选择" | `close_to_tray` | `MainWindow.OnWindowClosing` 关闭对话框 (`SetSettingAsync` 单 key) |
| 主题 | `theme_mode` | `MainWindowViewModel` (标题栏按钮和设置页下拉框都经它写, `SetSettingAsync` 单 key) |
| 窗口位置 | `window_left` `window_top` `window_width` `window_height` | `MainWindow` (`SetSettingAsync` 单 key) |
| 没有 UI 的字段 | `dev` | **无写入方**, 只能手改文件 (见下) |

`close_to_tray` 有两个写入方 (设置页下拉框和关闭对话框), 写的是同一个 key, 最后写入者生效; 设置页只在加载时读一次,
关闭对话框改过之后要重新打开程序才会显示新值.

`HomeViewModel` 的整体存回与设置页的单 key 写之间有个小竞争: 启动流程开头 `LoadConfigAsync` 拿到的对象在下载完成后整体存回,
会把这期间设置页改过的文本字段冲回旧值. 窗口很短 (只在首次下载 Node / LLBot 时), 接受.

开机自启不在 `app_settings.json` 里, 是 HKCU Run 注册表项 (`Utils/StartupManager.cs`), 设置页的开关直接读写注册表.

## 硬性约定：整体存回必须 read-modify-write

任何要通过 `ConfigManager.SaveConfigAsync(config)` 整体存回的写入方, **必须先 `LoadConfigAsync()` 拿到现有配置对象,
再只覆盖自己拥有的字段**, 绝不能 `new AppConfig { ... }` 从零构造. `LoadConfigAsync()` 返回的是 `ConfigManager` 的
缓存实例, 直接改字段再存回即可, 这样本方不涉及的字段 (含日后新增的) 都原样保留.

- 正例: `HomeViewModel.EnsureRuntimeAsync` -- `var config = await _configManager.LoadConfigAsync(); config.NodePath = ...;`
- 反例 (历史 bug): 系统配置页曾 `new AppConfig { ...本页字段... }`, 每次保存都把其他页维护的字段冲成默认值.

只改**单个 key** 时用 `ConfigManager.SetSettingAsync(key, value)` -- 它基于 `_rawJson` 合并写, 天然安全, 不受此约定约束.
值为 `null` 时写出 JSON `null` (三态字段用得上).

## 没有 UI 的字段不要写回

没有对应控件的字段用户只能手改文件; 若某个页面整体存回时把这些字段一起写回去 (写的是页面加载时读到的旧值), 用户手改的值
就会被冲掉而且在界面上看不出来. 所以: 整体存回只覆盖自己的字段 (上面的约定已经保证); 设置页全部走单 key 写, 不整体存回, 所以它没加载到的字段
(`dev`, `window_*`) 不会被它冲掉.

`AppConfig` 里已删除的旧字段 (`qq_path` `pmhq_*` `headless` `protocol` `auto_login_qq` `auto_start_bot`
`auto_start_frameworks` `log_*` `llbot_instance` `auto_start_instances` `instance_ports` 等) 反序列化时被忽略;
其中开发版的 `auto_start_instances` 与 `instance_ports["default"]` 会在首次运行时一次性迁出并从 `app_settings.json` 移除.

## 三态字段 close_to_tray

`AppConfig.CloseToTray` 是 `bool?`, 三态语义 (见 `MainWindow.axaml.cs:OnWindowClosing`):

- `null` -- 每次关闭都弹 `CloseDialog` 询问
- `true` -- 收进托盘
- `false` -- 直接退出

设置页用三选项 ComboBox 表达, `SettingsViewModel.CloseBehaviorIndex` 负责 `int`(0/1/2) <-> `bool?`(null/true/false) 映射.
