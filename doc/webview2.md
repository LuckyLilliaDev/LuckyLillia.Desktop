# WebView2 嵌入 (NativeAOT)

控制面板就是一个 WebView2, 显示 Desktop 自己拉起的 LLBot 的 WebUI. 代码:

- `Utils/WebView2/WebView2Interop.cs` -- 手写的 COM 接口声明 + 回调实现 + `WebView2Loader` 导出
- `Utils/WebView2/WebView2Runtime.cs` -- 运行时探测, 进程内唯一的 `CoreWebView2Environment`, 用户数据目录
- `Controls/WebView2Host.cs` -- Avalonia `NativeControlHost` 子类, 自建子 HWND 挂 WebView2 控制器
- `Views/Pages/HomePage.axaml` -- 工具条 + `WebView2Host` + 遮罩; `HomeViewModel` 决定什么时候显示谁

## 为什么不用 Microsoft.Web.WebView2.Core

Release 是 NativeAOT (`PublishAot=true`). 官方托管包装 `Microsoft.Web.WebView2.Core.dll` 走的是运行时内置 COM 互操作
(`[ComImport]`), NativeAOT 不支持. 所以这里只从 NuGet 包取原生加载器 (`ExcludeAssets="compile;build;..."`),
COM 接口用 .NET 8+ 的源生成互操作 (`[GeneratedComInterface]` / `[GeneratedComClass]`) 手写最小集合.

## 接口声明的规矩

- **方法顺序 = `WebView2.h` 里的 vtable 顺序**, 来源是 NuGet 包 `build/native/include/WebView2.h` (当前 1.0.4314.35).
  propget / propput 也占槽位 (`get_IsVisible` / `put_IsVisible`), 漏一个后面全错位, 表现为随机的 E_NOINTERFACE / 崩溃.
- 用不到的方法只需要占位, 参数类型随便 (`nint`), 但必须存在. 要新用某个方法时把它的参数改成真实签名即可.
- `BOOL` 一律 `int`; `LPCWSTR` 用 `string` (`StringMarshalling.Utf16`); 原生返回的 `LPWSTR*` 用 `out string`,
  生成代码会 `CoTaskMemFree`, 与 WebView2 的分配约定一致. `RECT` / `COREWEBVIEW2_COLOR` 按值传, 实测封送正确.
- 派生接口 (`ICoreWebView2Controller2 : ICoreWebView2Controller`) 直接继承声明; 从控制器拿 Controller2 用
  `controller is ICoreWebView2Controller2 c2` (RCW 是 `IDynamicInterfaceCastable`, 会做 QueryInterface).
- 显式释放 RCW: `((object?)rcw as ComObject)?.FinalRelease()` -- `ComObject` 是 sealed 且不静态实现接口, 必须先转 object.
- 要提取某个接口的方法序, 对 `WebView2.h` 跑:
  `awk '/MIDL_INTERFACE\("/{getline; if($1=="ICoreWebView2Controller") b=1} b && /STDMETHODCALLTYPE/{print} b && /^    };/{b=0}'`

## 加载器: Debug 用 DLL, AOT 静态链接

- Debug / `dotnet run`: `[LibraryImport("WebView2Loader")]` 在运行时加载 NuGet 的 `runtimes/win-x64/native/WebView2Loader.dll`
  (SDK 按 deps.json 探测).
- NativeAOT (`-r win-x64`): csproj 里 `DirectPInvoke Include="WebView2Loader"` + `NativeLibrary` 指向包内
  `build/native/x64/WebView2LoaderStatic.lib`, 额外链 `version.lib`; 发布物仍是单 exe (发布目录里多出的
  `WebView2Loader.dll` 是 NuGet 原生资产的副本, AOT exe 不依赖它, CI 只打包 exe).
  静态库只声明 `uuid.lib` / `ole32.lib` 默认库, 不带 CRT 指令, 与 VC-LTL 不冲突.
- 本机做 AOT 发布要在 VS 开发者环境里 (`vswhere.exe` 要在 PATH 上, VC-LTL / YY-Thunks 的 targets 会调它; MSYS bash
  直接跑会因为丢了 `ProgramFiles(x86)` 变量而链接失败). CI 用 `setup-msvc-dev`.
- 升级 SDK 版本: 改 csproj 的包版本, 再按上面方法核对一遍用到的接口方法序没有变 (微软保证已发布接口不变, 核对是防手误).

## 运行时与回退

- `WebView2Runtime.IsAvailable` 调 `GetAvailableCoreWebView2BrowserVersionString`: 非 Windows / 没装 Evergreen 运行时 /
  加载器缺失都为 false. Win11 自带运行时; Win10 绝大多数随 Edge 装好; Win7/8 只有 109 版运行时.
- 不可用时 `HomeViewModel.WebViewSupported=false`: 遮罩显示 "未安装 WebView2 运行时" + 安装按钮
  (`WebView2Runtime.InstallerUrl`, 微软固定的 Evergreen 引导包链接) + "在浏览器中打开". 非 Windows 直接只有浏览器打开.
- 初始化中途失败 (`InitializationFailed`) 同样回退到浏览器打开, 并把异常信息显示在遮罩上.
- 用户数据 (cookie / 缓存, 含 WebUI 的登录 cookie) 在 `data/webview2/` (工作目录下), 便携部署一起带走.

## 和 Avalonia 的配合 (三个踩过的坑)

1. **exe 必须带 `app.manifest` 的 supportedOS 列表** (csproj `ApplicationManifest`). 没有它 Windows 按旧系统兼容模式跑,
   子窗口不允许 `WS_EX_LAYERED`, Avalonia 的 `NativeControlHost` 建宿主窗口直接抛
   "Unable to create child window for native control host". 这个文件以前一直没被 csproj 引用, 之前没人用 NativeControlHost 所以没暴露.
2. **Avalonia 的宿主窗口是分层窗口时 WebView2 不显示**. 默认的 WinUI / DirectComposition 模式下
   `WindowImpl` 用 `new Win32NativeControlHost(this, !UseRedirectionBitmap)` 把宿主 (`AvaloniaDumbWindow-*`) 建成
   `WS_EX_LAYERED`, 为的是让 GDI 画的原生控件在没有重定向位图的顶层窗口里有自己的表面. WebView2 的像素来自它自己的
   DirectComposition 子窗口 (`Intermediate D3D Window`, `WS_EX_NOREDIRECTIONBITMAP`), 在分层祖先里不会被合成:
   页面导航成功、子窗口尺寸正确, 屏幕上却只剩创建时残留的父窗口画面. `WebView2Host.UnlayerHolder` 在建完自己的
   子窗口后把宿主的 `WS_EX_LAYERED` 去掉, 因为 Avalonia 的合成视觉树本来就在子窗口之下 (`CreateDesktopWindowTarget(hwnd, isTopmost: 0)`).
   这依赖 Avalonia 内部实现 (11.3.x), 升级 Avalonia 后要重新实测.
3. **不要换成 `Win32CompositionMode.RedirectionSurface` 来绕第 2 条**: 那样宿主不再分层, 但 ANGLE 会在顶层 HWND 上用
   DXGI flip 交换链呈现, DWM 把它合成在所有子窗口之上, WebView2 同样一片空白. 纯软件渲染 (`Win32RenderingMode.Software`,
   `LLD_LOW_PERF_RENDERING=1` 那条路) 是 GDI 重定向表面, 能正常显示. 实测矩阵:

   | 渲染 | 合成模式 | 宿主分层 | 结果 |
   |---|---|---|---|
   | AngleEgl | WinUIComposition (默认) | 是 (Avalonia 默认) | 空白 |
   | AngleEgl | WinUIComposition (默认) | 否 (`UnlayerHolder`) | **正常** (当前方案) |
   | AngleEgl | RedirectionSurface | 否 | 空白 |
   | Software | RedirectionSurface | 否 | 正常 |

其他约定:

- `NativeControlHost` 在挂到视觉树时就调 `CreateNativeControlCore` (不等可见); 之后 `IsVisible=false` 只是把宿主移出屏幕并隐藏,
  控制器一直活着, 切页面回来不用重建.
- **原生 HWND 永远盖在 Avalonia 内容之上**: 要显示遮罩 / 下载进度时必须把 `WebView2Host.IsVisible` 设成 false
  (`HomeViewModel.ShowWebView`), 不能靠 ZIndex. 主窗口页面切换因此也不做淡入淡出 (`MainWindow.SetVisiblePage`).
- 尺寸: 子 HWND 的 `WM_SIZE` -> `put_Bounds` (物理像素); Avalonia 侧 `Bounds` / `IsVisible` 变化时再按 `GetClientRect`
  对齐一次 (尺寸没变时收不到 WM_SIZE); 主窗口移动 -> `NotifyParentWindowPositionChanged`.
- 背景色: `put_DefaultBackgroundColor` 取当前主题的 `MainBackground`, 主题切换时重设, 避免深色主题闪白.
- WebUI 里的外链 (`NewWindowRequested`) 一律交给系统浏览器, 不在嵌入视图里开新窗口.
- Environment 在 `HomePage` 挂到视觉树时预热 (`WebView2Runtime.GetEnvironmentAsync`), 浏览器进程启动的几百毫秒
  和 LLBot 启动重叠.
- 排查显示问题的办法: 日志里 "WebView2 控制器已就绪 / 导航 / 导航完成" 三条都在却看不到页面, 用 Spy++ 或
  `EnumChildWindows` 看宿主链 `AvaloniaDumbWindow-* -> LuckyLilliaWebView2Host -> Chrome_WidgetWin_0 -> ... -> Intermediate D3D Window`
  的样式 / 尺寸 / 可见性, 再对照上面的矩阵.

## 主题同步

WebUI 自己的主题 (`stores/themeStore.ts`) 有 light / dark / auto 三档, 默认 auto = 跟随 `prefers-color-scheme`, 并监听它的变化;
选择按 origin 存在 localStorage (`llbot-theme`, zustand persist 只存 `mode`; `main.tsx` 首屏也读这个键), 所以每个端口各存一份.
嵌在桌面端里时主题以桌面端为准, 两步 (`Controls/WebView2Host.cs`):

1. `ApplyColorScheme`: 把页面看到的 `prefers-color-scheme` 钉成桌面端当前主题 -- 初始化后、每次导航完成、`ActualThemeVariant`
   变化时调 CDP `Emulation.setEmulatedMedia` (`{"features":[{"name":"prefers-color-scheme","value":"dark"}]}`).
   auto 档的页面会立刻跟着切 (store 监听了媒体查询的 change 事件), 含桌面端 "跟随系统" 时的系统切换.
2. `InstallThemeFollowScript`: `AddScriptToExecuteOnDocumentCreated` 注入一段脚本, 每个文档的脚本跑起来之前把 `llbot-theme` 里的
   `mode` 改回 `auto`. 不然用户在 WebUI 里手动选过亮 / 暗的实例 (或者端口漂移后继承了别的实例存的选择) 会无视第 1 步.
   代价: 嵌入时 WebUI 自己的主题开关只在当前页面有效, 刷新就回到跟随桌面端.

实测: 系统浅色、桌面端深色时, 新 origin 的实例 (没存过选择) 直接渲染成深色; 存过 dark 的 default 在桌面端切浅色后也跟着变浅.

- 为什么用 CDP: `CallDevToolsProtocolMethod` 在 `ICoreWebView2` 基接口上就有. 正规做法 `ICoreWebView2Profile.PreferredColorScheme`
  要先 QI 到 `ICoreWebView2_13` 拿 `get_Profile`, 手写 COM 得把 `ICoreWebView2_2` ~ `_13` 的全部方法按顺序声明出来才能占对 vtable 槽位,
  目前没这个必要.
- 模拟的媒体特性由 DevTools 会话保存, 跨导航保留; 导航完成后再设一次只是兜底.

## 后台挂起与退出收尾

窗口最小化 / 收进托盘时 (`MainWindow.SetBackgroundMode`) 调 `WebView2Host.SetBackgroundMode(true)`: 先 `put_IsVisible(0)`,
再 `ICoreWebView2_3.TrySuspend` (要求先不可见); 显示时 `Resume` + `put_IsVisible(1)`. 实测 (Debug 构建, WebUI 仪表盘页):

| 状态 | WebView2 6 个进程私有内存 |
|---|---:|
| 可见 | 388 ~ 641 MB (GPU 进程波动大) |
| 只 IsVisible=false | 315 MB |
| IsVisible=false + TrySuspend | 286 MB |

大头是 IsVisible=false 本身 (合成器 / GPU 资源释放, 停止渲染); TrySuspend 再多省几十 MB 并冻结页面脚本 (零 CPU).
两者本来就要一起用. 恢复后 GPU 进程会重新分配, 私有内存回到可见水平.

退出时 (`App.ExitCoreAsync` -> `MainWindow.ShutdownWebViewAsync`): 关控制器、放掉 environment、等浏览器进程自己退出 (最多 3 秒).
原因: 本进程在 KILL_ON_JOB_CLOSE 的 Job Object 里 (见 job-object.md), 进程一退 WebView2 的几个进程会被直接杀掉, Chromium 的
cookie 库每 30 秒才提交一次, 登录后 30 秒内退出就会丢 WebUI 的登录态 cookie (`webui_token`).

## WebUI 登录态与多实例

WebUI 把登录 token 存在 cookie `webui_token` (FE `utils/api.ts`), cookie 只按主机名隔离不按端口, 所以同一台机器上的几个实例共用一个
cookie, 而每个实例的密码 (`webui_token.txt`) 各不相同: 登录了 bot2 就把 default 的 token 覆盖掉, 切回 default 又要输一次.
这是 WebUI 侧的设计, 桌面端绕不开; 要修得在 WebUI 里按端口命名 cookie 或改用 localStorage (按 origin 隔离) + `X-Webui-Token` 头.
另一条路是桌面端每个实例一个 WebView2 profile (cookie 彼此隔离), 代价是每个实例一个渲染进程.
