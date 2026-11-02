# Lucky Lillia Desktop - Avalonia 版本

Lucky Lillia Desktop 的 Avalonia/C# 重写版本。

## 它做什么

- 启动即自动拉起 LLBot（缺 Node.js / LLBot / FFmpeg 时自动下载），控制面板里直接嵌入 LLBot 的 WebUI（Windows 用 WebView2）。
- LLBot 的登录方式、QQ 路径、协议、WebUI 端口、WebUI 密码等全部在 WebUI 里配置；桌面端的「设置」页只有
  自己的选项：主题、托盘/关闭行为、开机自启、启动后命令、Node 与 LLBot 路径 / 服务器线路 / HTTP 代理。
- 支持 LLBot 多实例：控制面板工具条上选择实例（`data/instances/<实例>/`），可以新建 / 删除实例、同时运行多个，
  每个实例一个 LLBot 进程和自己的 WebUI 端口（桌面端分配空闲端口并以 `--webui-port` 传入），勾选「随桌面端启动」的实例
  在程序启动时自动拉起。
- 退出时先请求 LLBot 优雅关闭，再进程树兜底；Windows 还有 Job Object 兜底桌面端崩溃的情况。

细节见 [doc/llbot-webui.md](doc/llbot-webui.md)、[doc/webview2.md](doc/webview2.md)
和 [doc/job-object.md](doc/job-object.md)。
