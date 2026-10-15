# SyncCue native 0.2.14

缺陷：关掉主窗口后，房间服务（node / 3131）、cloudflared 等子进程仍留在后台。

根因：`Closed += async` 在 WPF 进程撕毁时可能跑不完，`StopAsync` / `DisposeAsync` 来不及 Kill。

改动：
1. **Closing 取消直到清干净**：关窗先停隧道 → Dispose 客户端 → Dispose 本机房间服务（Kill entireProcessTree），再真正关闭。
2. **Windows Job Object**（`ChildProcessJob`，`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）：启动 node / cloudflared 时挂进 Job；关窗或 `App.OnExit` 关掉 Job 句柄，残留子进程一并带走。
3. 关房路径不变：先广播 `room-closed`，再关隧道。

## 克莉斯特验收
1. 开房（可顺带开隧道）→ 直接关软件 → 任务管理器里不应再留本实例的 SyncCue / node / cloudflared。
2. 再开一次，确认能正常拉起房间服务与隧道。
3. （可选）关房：成员应收到离开；隧道应关掉。

备注：若 3131 上已有**非本实例启动**的服务，仍不会杀（`Owned=false`）。只清本实例拉起的子进程。

绿包：`D:\Working\SyncCue\sync-cue-native-release\SyncCue-native-0.2.14-win-x64.zip`