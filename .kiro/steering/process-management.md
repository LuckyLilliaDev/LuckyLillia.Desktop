# 进程管理规范

## Desktop 只管 LLBot 实例进程

- 实例 = `<LLBot>/data/instances/<名>/`, 每个实例一个 LLBot Node 进程 (`IProcessManager.StartInstanceAsync`),
  `HomeViewModel` 管生命周期; 不传登录方式 / 协议 / QQ 路径参数, 这些都在 LLBot 的 WebUI 里配; 控制面板就是嵌入的 WebUI
- 端口由 Desktop 分配 (先查 TCP 监听表再试绑定, 试绑定在 Windows 上会被 Node 的 socket 骗) 并以 `--webui-port` 传入;
  就绪判定看端口是不是被这个实例的 PID 监听, 不要只靠能不能连上
- 停止顺序: 向子进程控制台发 Ctrl+C (LLBot 当 SIGINT) -> 最多等 35 秒 -> 进程树兜底; 起进程前先清掉本进程的
  "忽略 Ctrl+C" 标志, 否则子进程继承后收不到; Windows 还有 Job Object 兜底 Desktop 崩溃的情况
- 不要去碰 QQ 进程: QQ 归 LLBot 的原生模块管
- IPC 已删除: Desktop 不拿账号信息, 不显示昵称 / QQ 号

详见 `doc/llbot-webui.md`.
