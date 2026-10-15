# SyncCue

社交对拍提示器（Windows 原生）：局域网／公网房间对时、置顶倒计时、就绪与关房踢人。

**SyncCue** is a Windows-native cue/countdown tool for synced takes over LAN or a public tunnel.

作者／Author：**南千和（MinamiChiwa）** · [爱发电](https://afdian.com/a/minamichiwa) · [Patreon](https://patreon.com/chiwaminami)  
赞助自愿，不影响功能。Sponsorship is voluntary and does not affect features. 本工具由 AI 辅助开发。

当前可用包／Current build：**v0.2.14**

---

## 中文

### 这是什么

给一起对拍／倒计时的人用：房主开房，成员加入，到点一起响。支持同一 Wi‑Fi 局域网，或开 Cloudflare 临时隧道给外网朋友。

### 下载与启动

1. 到 [Releases](https://github.com/MinamiChiwa/SyncCue/releases) 下载 `SyncCue-native-0.2.14-win-x64.zip`
2. 解压后双击顶层的 `SyncCue.exe`（其余文件在子目录，不用翻）
3. 若本机 **3131** 端口还挂着旧的房间服务，请先关掉再开本包，否则可能复用旧进程

包内另有 `使用说明.txt`（中文）与 `README.txt`／`USAGE.en.txt`（英文）。

### 常用用法

- **房主**：填昵称与倒计时秒数 → 开房 → 复制局域网邀请；需要外网时先「开隧道」，等「已开通」再复制外网邀请
- **成员**：把整段邀请贴进加入区 → 连接并加入 → 点「我已就绪」（**只有参与者需要就绪，房主不用**）
- **开始**：房主（或协作者）点「开始倒计时」；同一房可连开多轮
- **关房**：房主关房后，所有参与者会立刻被清出房间
- **提前量**：正数＝自己更早响（补偿设备延迟）；负数＝更晚响

### 仓库结构

| 路径 | 说明 |
|------|------|
| `native/` | .NET 8 WPF 客户端（SyncCue.exe） |
| `server/` | Node 房间服务（默认 `ws://127.0.0.1:3131`） |
| `shared/` | 与客户端对齐的时钟等共享逻辑 |

### 自行编译（开发者）

- 客户端：安装 .NET 8 SDK 后，在 `native/` 执行 `dotnet build -c Release`
- 服务：需要 Node.js；绿包会自带可运行的 server 侧车。从源码跑可参考 `package.json` 的 `npm` 脚本

### 赞助

如果 SyncCue 帮到你，自愿请杯奶茶即可，不影响任何功能：

- 爱发电：https://afdian.com/a/minamichiwa
- Patreon：https://patreon.com/chiwaminami

---

## English

### What it is

A small Windows app for **synced countdowns / take cues** with friends: host a room, join on LAN or via a temporary public tunnel, ready up, and hit start together.

### Download & run

1. Get `SyncCue-native-0.2.14-win-x64.zip` from [Releases](https://github.com/MinamiChiwa/SyncCue/releases)
2. Unzip and double-click top-level `SyncCue.exe`
3. If something already listens on port **3131**, stop that old room server before launching this build

### Everyday use

- **Host**: set nickname + duration → create room → copy LAN invite; for remote friends, start the tunnel, wait until Ready, then copy the public invite
- **Guest**: paste the whole invite → join → tap **I'm ready** (hosts do not need ready)
- **Start**: host/cohost starts the countdown; you can run multiple rounds in the same room
- **Close room**: when the host closes the room, every participant is cleared out immediately
- **Lead time**: positive = fire earlier on your machine; negative = later

### Sponsors

Voluntary support (never paywalled):

- Afdian: https://afdian.com/a/minamichiwa
- Patreon: https://patreon.com/chiwaminami

---

## License

Source is provided for personal / community use with the project. See repository files for details.
