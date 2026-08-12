# AgentBell for Codex：局域网任务完成提醒器开发规格

> 文档版本：0.1  
> 日期：2026-07-31  
> 状态：可直接用于 Codex 开发  
> 第一轮范围：Codex + Windows + Android + 同一局域网  
> 核心目标：简单、高效、低延迟、接近一键配置

> **M0 集成修订（2026-08-01）**：Windows Codex 客户端已经拥有自己的用户级
> `notify`，该命令指向会随客户端升级变化的版本化 runtime。AgentBell 不得修改、
> 删除、接管或串联这条 `notify`。M0 的主要集成改为用户级
> 当前用户 `.codex\hooks.json` 中的 `Stop` command Hook，正式命令为
> `AgentBell.Hook.exe --codex-stop-hook`，JSON 对象由 stdin 输入。已有单 JSON 命令行
> `notify` 模式只作为兼容能力保留。此修订优先于本文后续所有以 `notify`、
> `config.toml` 修改或 notify chaining 为主要集成方式的旧描述；这些旧描述不得用于
> 当前或后续实现，直至规格被正式重构。非托管 Hook 仍须由用户审核和信任。

> **M6 操作请求通知修订（2026-08-07）**：`0.7.0-beta.1` 在现有 Stop Hook
> 之外增加用户级 `PermissionRequest` 与 `PostToolUse` command Hook，并以向后兼容的协议 v1
> 增量字段支持 `completion` 与 `action_required`。此修订只发送通知，不支持
> 远程批准、远程回复、自动 allow/deny、权限策略变更或 Hook trust 绕过。
> Codex Hook 当前不会暴露 PermissionRequest 是由 Auto-review 自动处理，还是正在
> 等待用户批准。权限请求提醒因此改为显式的 `关闭` / `始终提醒` 策略，默认关闭；
> 不得再使用超时或 PostToolUse 到达时间推断审批者。

---

## 1. 项目定义

AgentBell 是一个本地优先的 Codex 任务完成提醒工具。

当 Codex 完成当前 Agent 回合后，Codex 调用电脑上的轻量通知程序。该程序把事件交给 Windows 常驻程序，常驻程序再通过同一局域网内的 WebSocket 将事件推送给 Android 手机，并触发顶部横幅通知。

第一轮只解决一个问题：

> 用户离开电脑后，Codex 当前回合完成时，手机应尽快收到通知。

第一轮不尝试显示“真实进度百分比”，也不尝试理解 Codex 是否完成了一个宏观项目。官方 `notify` 事件表达的是 `agent-turn-complete`，因此产品文案必须使用“当前回合完成”或“Codex 已完成”，不能宣称整个项目已经完成。

---

## 2. 第一轮硬性范围

### 2.1 支持范围

- Agent：Codex 本地客户端。
- 电脑系统：Windows 10/11 x64。
- 手机系统：Android。
- 网络：电脑和手机位于同一局域网。
- 事件：`agent-turn-complete`。
- 提醒方式：Android 高优先级系统通知。
- 电脑部署方式：最终提供一个 Windows 安装包。
- 手机部署方式：APK。
- 数据路径：仅局域网，不经过云服务器。
- 用户账户：无。
- 第三方推送：无。
- 数据库：第一轮不使用数据库。

### 2.2 明确不做

以下项目不得在第一轮实现：

- Claude Code 支持。
- Gemini CLI、Aider、OpenCode 等其他 Agent。
- iOS。
- 公网穿透。
- Firebase Cloud Messaging。
- 用户注册和登录。
- 云端消息中转。
- 远程操作 Codex。
- 真实任务进度检测。
- OCR、截图识别、终端日志轮询。
- 多电脑管理。
- 复杂 Dashboard。
- 统计分析。
- Codex 审批请求提醒。
- 正式 Codex Hooks 插件。
- 插件市场发布。
- 自动局域网发现或 mDNS。
- 强制 TLS/WSS。

除非主链路已经通过全部验收测试，否则不得扩大范围。

---

## 3. 已验证的 Codex 接口事实

截至 2026-07-31，Codex 官方文档说明：

1. 用户级配置位于 `CODEX_HOME/config.toml`；`CODEX_HOME` 默认是 `~/.codex`。
2. Windows 默认路径通常为 `%USERPROFILE%\.codex\config.toml`。
3. `notify` 是一个字符串数组，表示 Codex 要执行的外部命令。
4. `notify` 当前支持的外部事件是 `agent-turn-complete`。
5. Codex 将单个 JSON 字符串作为额外命令行参数传给通知程序。
6. 常见字段包括：
   - `type`
   - `thread-id`
   - `turn-id`
   - `cwd`
   - `input-messages`
   - `last-assistant-message`
7. `notify` 属于机器级通知配置，必须写入用户级配置；项目内 `.codex/config.toml` 中的 `notify` 会被忽略。
8. Codex 也有正式生命周期 Hooks，但非托管命令 Hook 需要用户审核和信任。为了让第一轮配置更接近一键完成，本项目先采用 `notify`。

官方参考：

- https://developers.openai.com/codex/config-advanced
- https://developers.openai.com/codex/config-reference
- https://developers.openai.com/codex/hooks

实现时不要假设未记录的字段必然存在。所有字段都要按可空值处理。

---

## 4. 成功标准

第一轮 MVP 同时满足以下条件才算完成：

1. Codex 每次产生 `agent-turn-complete` 时，电脑端只接收一次事件。
2. Codex 通知程序执行失败或桌面端未运行时，不得导致 Codex 崩溃或明显卡顿。
3. Hook 转发程序正常情况下应在 300 ms 内退出。
4. 在稳定局域网环境中，从 Codex 调用通知程序到手机显示通知，P95 延迟小于 1 秒。
5. 手机 App 在前台时可以稳定收到通知。
6. 手机 App 通过前台服务保持连接时，锁屏或切换到其他 App 后仍可收到通知。
7. 断网后可以自动重连，不要求用户重新打开 App。
8. 同一个 `turn-id` 不得产生重复系统通知。
9. 默认不发送 `input-messages`。
10. 默认只发送项目名、状态、时间和经过截断的最后回复摘要。
11. Windows 安装程序修改 Codex 配置前必须备份原文件。
12. 卸载时不能无条件覆盖用户后来修改过的 Codex 配置。
13. 所有核心功能可以在无互联网环境下运行。

---

## 5. 总体架构

```text
Codex
  │
  │ notify + 单个 JSON 参数
  ▼
AgentBell.Hook.exe
  │
  │ HTTP POST，仅回环地址
  ▼
AgentBell.Desktop.exe
  │
  │ WebSocket，同一局域网
  ▼
AgentBell Android
  │
  ▼
Android Heads-up Notification
```

### 5.1 为什么拆成两个 Windows 程序

`AgentBell.Hook.exe`：

- 被 Codex 直接调用。
- 必须极小、极快。
- 不显示 UI。
- 不直接维护手机连接。
- 不保存复杂状态。
- 即使桌面端不存在，也应快速失败并以退出码 0 结束，避免影响 Codex。

`AgentBell.Desktop.exe`：

- 作为系统托盘程序常驻。
- 接收本机 Hook 事件。
- 管理局域网服务。
- 管理配对 Token。
- 管理 Android WebSocket。
- 生成二维码。
- 推送事件。
- 保存最近少量事件。
- 提供测试通知。

这种拆分可以降低 Codex 调用链的耦合度，也方便后续增加 Claude 适配器。

---

## 6. 技术选型

### 6.1 Windows

使用：

- C#。
- .NET 10 LTS。
- `net10.0-windows`。
- ASP.NET Core Kestrel Minimal API。
- WinForms `NotifyIcon` 或等价的轻量托盘实现。
- `System.Text.Json`。
- xUnit。
- 自包含发布。
- `win-x64`。
- 单文件发布优先。

选择 .NET 10 的原因：截至本规格日期，.NET 10 是处于活动支持期的 LTS 版本。

不使用：

- Python 运行时。
- Electron。
- Node.js 常驻服务。
- Redis。
- SQLite。
- Docker。
- 重型 UI 框架。

### 6.2 Android

使用：

- Kotlin。
- Android 原生项目。
- Jetpack Compose，仅实现必要页面。
- OkHttp WebSocket 或其他单一成熟 WebSocket 客户端。
- Android Keystore 保存配对 Token。
- 高重要性通知频道。
- 前台服务维护局域网连接。
- 指数退避重连。

不使用：

- Flutter。
- React Native。
- Firebase。
- WebView 作为主应用。
- 云推送。

### 6.3 安装器

使用 Inno Setup，最终生成：

```text
AgentBell-Setup-x64.exe
```

安装器放在最后一个里程碑实现。主链路未跑通前，不投入时间优化安装 UI。

---

## 7. 推荐仓库结构

```text
AgentBell/
├─ AGENTS.md
├─ README.md
├─ AgentBell.sln
├─ Directory.Build.props
├─ src/
│  ├─ AgentBell.Contracts/
│  │  ├─ AgentBell.Contracts.csproj
│  │  ├─ CodexNotifyPayload.cs
│  │  ├─ AgentEvent.cs
│  │  └─ ProtocolMessages.cs
│  │
│  ├─ AgentBell.Hook/
│  │  ├─ AgentBell.Hook.csproj
│  │  ├─ Program.cs
│  │  └─ HookForwarder.cs
│  │
│  ├─ AgentBell.Desktop/
│  │  ├─ AgentBell.Desktop.csproj
│  │  ├─ Program.cs
│  │  ├─ AppHost.cs
│  │  ├─ Tray/
│  │  ├─ Api/
│  │  ├─ Pairing/
│  │  ├─ WebSockets/
│  │  ├─ Storage/
│  │  └─ Security/
│  │
│  └─ AgentBell.Configurator/
│     ├─ AgentBell.Configurator.csproj
│     ├─ CodexHomeResolver.cs
│     ├─ CodexConfigPatcher.cs
│     └─ ConfigBackupService.cs
│
├─ tests/
│  ├─ AgentBell.Contracts.Tests/
│  ├─ AgentBell.Hook.Tests/
│  ├─ AgentBell.Desktop.Tests/
│  └─ AgentBell.Configurator.Tests/
│
├─ android/
│  └─ AgentBell/
│     ├─ app/
│     ├─ build.gradle.kts
│     └─ settings.gradle.kts
│
├─ installer/
│  └─ AgentBell.iss
│
├─ docs/
│  ├─ DEVELOPMENT_SPEC.md
│  ├─ CODEX_PAYLOAD_SAMPLE.json
│  ├─ PROTOCOL.md
│  └─ TEST_PLAN.md
│
└─ scripts/
   ├─ publish-windows.ps1
   └─ package-installer.ps1
```

第一阶段可以暂时不创建所有空目录，但最终结构应保持清晰。

---

## 8. Windows 进程设计

## 8.1 AgentBell.Hook

### 职责

1. 读取命令行参数。
2. 找到 Codex 传入的 JSON 参数。
3. 验证 JSON 是否可解析。
4. 验证 `type == "agent-turn-complete"`。
5. 将原始 JSON POST 到桌面端回环接口。
6. 设置很短的超时。
7. 无论桌面端是否运行，都快速退出。
8. 不输出敏感内容。
9. 不弹窗。

### 命令调用形式

Codex 用户配置最终类似：

```toml
notify = ["C:\\Program Files\\AgentBell\\AgentBell.Hook.exe"]
```

Codex 会追加 JSON 参数。因此程序不能假设 JSON 固定处于 `args[0]`，应采用以下兼容策略：

- 首先检查 `args.Length == 1`。
- 如果大于 1，从后向前寻找第一个可解析为 JSON 对象、并含有 `type` 字段的参数。
- 没找到则静默退出。
- 不把所有参数直接拼接，否则路径或其他参数可能破坏 JSON。

### 本机转发接口

```http
POST http://127.0.0.1:17863/api/v1/events/codex
Content-Type: application/json
```

请求体为 Codex 原始 JSON。

### 超时

- 总超时：500 ms。
- 连接失败：静默。
- 非 2xx：写入可选的诊断日志，但仍返回退出码 0。
- 最终进程退出码默认使用 0，避免影响 Codex 工作流。

### 日志

默认不写日志。开启诊断模式后只记录：

- 时间。
- 事件类型。
- `turn-id` 的哈希或后 8 位。
- HTTP 状态。
- 耗时。
- 错误类型。

不得记录完整 Prompt、完整回复或原始 JSON。

---

## 8.2 AgentBell.Desktop

### 职责

- 托盘驻留。
- 启动两个监听端点：
  - 回环事件接收端点。
  - 局域网配对和 WebSocket 端点。
- 解析 Codex 事件。
- 去重。
- 生成 AgentBell 事件。
- 向已配对 Android 设备推送。
- 显示二维码。
- 发送测试事件。
- 保存配置和最近事件。
- 管理开机启动。
- 提供退出功能。

### 监听端口

固定默认端口：

```text
127.0.0.1:17863   Hook 事件接收
0.0.0.0:17864     手机配对和 WebSocket
```

两个端口分开，理由：

- Codex 事件接收接口不暴露到局域网。
- 手机服务不能向本机事件接收接口伪造 Codex 原始事件。
- 防火墙规则只需开放 17864 的专用网络访问。

端口冲突时：

- 17863 冲突：桌面端启动失败并明确提示。
- 17864 冲突：从 17864 至 17874 依次尝试，成功后二维码携带实际端口。
- 第一轮不做动态端口持久化以外的复杂服务发现。

### 托盘菜单

第一轮仅保留：

```text
AgentBell
状态：正在运行
手机：已连接 / 未连接
Codex：已配置 / 未配置

显示配对二维码
发送测试通知
打开诊断信息
设置
退出
```

设置仅包含：

- 开机启动。
- 显示回复摘要。
- 摘要最大字符数，默认 160。
- 通知声音，由手机决定。
- 诊断日志开关。

---

## 9. 数据模型

## 9.1 Codex 原始载荷

所有字段可空，不要使用不可空断言。

```csharp
public sealed record CodexNotifyPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("thread-id")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("turn-id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("input-messages")]
    public IReadOnlyList<string>? InputMessages { get; init; }

    [JsonPropertyName("last-assistant-message")]
    public string? LastAssistantMessage { get; init; }
}
```

### 注意

- `input-messages` 只为兼容解析，不发送给手机。
- 不要依赖字段顺序。
- 未知字段应忽略。
- `type` 不是 `agent-turn-complete` 时直接忽略。
- `turn-id` 为空时生成本地 UUID，但去重能力会降低。

## 9.2 统一 AgentBell 事件

```csharp
public sealed record AgentEvent
{
    public required string EventId { get; init; }
    public required string Agent { get; init; }
    public required string Status { get; init; }
    public required string Title { get; init; }
    public string? Project { get; init; }
    public string? Summary { get; init; }
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required long Sequence { get; init; }
}
```

固定值：

```text
Agent  = "codex"
Status = "completed"
Title  = "Codex 已完成当前回合"
```

`Project`：

- 从 `cwd` 取最后一个有效目录名。
- 解析失败则为 `null`。
- 不把完整绝对路径发送给手机。

`Summary`：

- 来源为 `last-assistant-message`。
- 将连续空白压缩成单个空格。
- 删除首尾空白。
- 默认截断为 160 个 Unicode 文本元素。
- 不应粗暴按 UTF-16 `char` 截断表情符号或代理对。
- 用户关闭摘要时为 `null`。

`EventId`：

优先级：

1. `codex:{thread-id}:{turn-id}`
2. `codex:{turn-id}`
3. 本地 UUID

---

## 10. 去重和顺序

桌面端维护内存中的 LRU 去重集合：

- 最大 1000 个 EventId。
- 进程重启后可以从最近事件文件恢复。
- 相同 EventId 再次到达时返回 HTTP 202，但不再次推送。

每个新事件分配单调递增的 `Sequence`。

第一轮保存最近 100 条事件到：

```text
<LOCAL_APP_DATA>\AgentBell\events.json
```

写入要求：

- 先写临时文件。
- `Flush`。
- 原子替换正式文件。
- 文件损坏时重命名为 `.corrupt-时间戳`，然后重新创建。
- 不允许因为历史记录写入失败而阻塞实时推送。

---

## 11. 局域网配对

## 11.1 配对原则

- 不使用账号。
- 不使用云端。
- 不要求用户输入电脑 IP。
- Windows 端显示二维码。
- Android 扫描二维码后保存连接信息。
- 第一轮 IP 改变后允许用户重新扫码，不实现自动发现。

## 11.2 配对 Token

桌面端首次启动时生成：

- 32 字节安全随机数。
- Base64URL 编码。
- 至少 256 bit 熵。
- 不记录到普通日志。

Windows 存储：

```text
<LOCAL_APP_DATA>\AgentBell\config.json
```

Token 字段应使用 Windows DPAPI 加密后保存，或保存在仅当前用户可读的受保护文件中。优先使用 DPAPI。

Android 使用 Keystore 或基于 Keystore 的加密存储。

## 11.3 二维码内容

```text
agentbell://pair?v=1&host=<PRIVATE_IPV4>&port=<PORT>&token=<TOKEN>&device=<REDACTED>
```

要求：

- `host` 必须选择当前有效的私有 IPv4 地址。
- 排除：
  - Loopback。
  - APIPA `169.254.0.0/16`。
  - 虚拟机和不活跃网卡。
- 如果检测到多个候选地址，在二维码窗口允许用户选择。
- `device` 只用于显示，不能作为身份凭证。

## 11.4 配对验证接口

```http
GET /api/v1/status
Authorization: Bearer <TOKEN>
```

成功：

```json
{
  "protocolVersion": 1,
  "deviceName": "HYATIN-PC",
  "serverVersion": "0.1.0",
  "webSocketPath": "/ws/v1/events"
}
```

失败：

- 无 Token：401。
- Token 错误：403。
- 服务不可用：连接失败。

---

## 12. WebSocket 协议

连接：

```text
ws://<PRIVATE_IPV4>:<PORT>/ws/v1/events
Authorization: Bearer <TOKEN>
```

Android 某些 WebSocket 客户端不便设置自定义 Header 时，可以允许：

```text
ws://<PRIVATE_IPV4>:<PORT>/ws/v1/events?token=<TOKEN>
```

优先使用 Header；查询参数只用于兼容，且不得写入访问日志。

### 12.1 Hello

服务器连接成功后发送：

```json
{
  "type": "hello",
  "protocolVersion": 1,
  "serverVersion": "0.1.0",
  "deviceName": "HYATIN-PC",
  "serverTime": "2026-07-31T22:19:00+08:00"
}
```

### 12.2 事件

```json
{
  "type": "event",
  "payload": {
    "eventId": "codex:thread-123:turn-456",
    "agent": "codex",
    "status": "completed",
    "title": "Codex 已完成当前回合",
    "project": "AgentBell",
    "summary": "Implemented the WebSocket endpoint and added tests.",
    "threadId": "thread-123",
    "turnId": "turn-456",
    "occurredAt": "2026-07-31T22:19:00+08:00",
    "sequence": 42
  }
}
```

### 12.3 心跳

- WebSocket 库层 Ping/Pong 优先。
- 如果客户端库不暴露 Ping，应用层每 20 秒发送：

```json
{"type":"ping","timestamp":1785517140000}
```

响应：

```json
{"type":"pong","timestamp":1785517140000}
```

连续两次心跳失败则断开并重连。

### 12.4 重连

Android 退避顺序：

```text
1s → 2s → 5s → 10s → 30s
```

连接恢复后重置为 1 秒。

第一轮允许不实现离线补发，但必须保留 `sequence` 字段，为后续补发协议预留兼容性。推荐在 MVP 后半程实现最近 100 条事件补发。

---

## 13. Android 应用

## 13.1 页面

第一轮只需要三个状态页面。

### 未配对

- “扫描电脑上的 AgentBell 二维码”。
- 扫码按钮。
- 手动输入作为隐藏诊断入口，不作为主要流程。

### 已配对、已连接

- 电脑名称。
- 连接状态。
- 最近一次事件时间。
- 发送测试请求。
- 断开配对。
- 打开系统通知设置。

### 已配对、未连接

- 电脑名称。
- “正在重连”。
- 当前退避时间。
- 重新扫码。
- 系统后台权限说明。

不做复杂任务列表。最多显示最近 20 条本地收到的事件。

## 13.2 Android 通知

创建高重要性通知频道：

```text
Channel ID: agentbell_codex_completed
Name: Codex 任务完成
Importance: HIGH
```

通知格式：

```text
标题：Codex 已完成 · AgentBell
正文：Implemented the WebSocket endpoint and added tests.
```

若无项目名：

```text
标题：Codex 已完成
```

若无摘要：

```text
正文：当前回合已经结束。
```

要求：

- Android 13 及以上请求通知权限。
- 通知使用稳定的事件哈希作为 notification ID，避免重复。
- 点击通知打开 App。
- 第一轮不做操作按钮。
- 不申请悬浮窗权限。
- 使用系统 Heads-up Notification，而不是真正 Overlay。

## 13.3 后台连接

为了保证低延迟：

- 用户启用“持续接收”后启动前台服务。
- 常驻通知使用低重要性独立频道。
- 服务负责维护 WebSocket。
- App UI 只观察服务状态。
- 连接和通知逻辑不得依赖 Activity 存活。

常驻通知文案：

```text
AgentBell 已连接到 HYATIN-PC
```

小米、Redmi 等系统可能限制后台活动。应用首次配对成功后显示一次引导：

- 允许通知。
- 电池策略设为不限制。
- 允许后台活动。
- 允许自启动，若系统提供该选项。

不要尝试使用隐蔽方式规避 Android 后台策略。

---

## 14. 安全和隐私

第一轮虽然只在局域网运行，仍须满足：

1. Codex 事件接收端点只绑定 `127.0.0.1`。
2. 手机端接口全部要求随机 Token。
3. Token 至少 256 bit。
4. 不发送 `input-messages`。
5. 不发送完整 `cwd`。
6. 默认最多发送 160 字摘要。
7. 不上传数据。
8. 不包含第三方分析 SDK。
9. 不包含广告 SDK。
10. 日志不得包含原始 Prompt、完整回复和 Token。
11. WebSocket 查询参数中的 Token 不得写入访问日志。
12. 局域网第一轮使用 HTTP/WS 是明确的工程折中，不应宣传为端到端加密。
13. 后续版本可以增加配对公钥和加密通道，但不能阻塞第一轮。

威胁模型主要考虑：

- 同一 Wi-Fi 内的其他设备扫描端口。
- 未授权客户端尝试连接。
- 日志泄漏。
- 用户电脑 IP 变化。
- 防火墙误配置。
- 重复事件。
- 配置文件被错误覆盖。

---

## 15. Codex 配置管理

## 15.1 路径解析

依次检查：

1. 环境变量 `CODEX_HOME`。
2. `%USERPROFILE%\.codex`。
3. 如果目录不存在，可以创建，但安装器必须明确记录创建行为。

目标文件：

```text
<CODEX_HOME>\config.toml
```

## 15.2 修改原则

- 修改前创建字节级备份。
- 保留 UTF-8 BOM 状态。
- 保留原始换行符风格。
- 不重排用户配置。
- 不格式化整个 TOML。
- 修改必须幂等。
- 已经配置 AgentBell 时不得重复添加。
- 配置无法安全解析时停止修改并提示，不得猜测。

## 15.3 第一轮冲突策略

### 没有 `notify`

追加：

```toml

# AgentBell managed setting
notify = ["C:\\Program Files\\AgentBell\\AgentBell.Hook.exe"]
```

### 已经是 AgentBell

不修改。

### 已存在其他 `notify`

第一轮正式安装包应当：

- 不覆盖。
- 显示“检测到现有 Codex notify 配置”。
- 保留备份。
- 给出两个选项：
  - 暂不配置 Codex。
  - 允许 AgentBell 接管并启用兼容代理。

兼容代理可以安排在安装器里程碑后半程实现。其行为：

```text
Codex
  ↓
AgentBell.Hook
  ├─ 转发给 AgentBell Desktop
  └─ 使用同一个原始 JSON 参数调用用户原来的 notify 命令
```

在个人开发原型阶段，可以只实现“发现冲突后安全停止”，不得直接覆盖。

## 15.4 卸载

卸载器不能直接用最初备份覆盖当前文件，因为用户可能在安装后修改过配置。

安全规则：

- 如果当前 `notify` 正好仍指向 AgentBell，只删除该项。
- 其他配置保持不变。
- 如果无法安全定位，只提示用户并保留配置。
- 保存备份，不自动删除用户备份目录。
- 删除防火墙规则和开机启动项。

---

## 16. 防火墙和启动

### 防火墙

只为：

```text
AgentBell.Desktop.exe
TCP 17864
Private profile only
Inbound
```

创建规则。

不得开放：

- Public profile。
- 17863。
- 任意程序。
- 任意端口范围。

### 开机启动

第一轮使用“用户登录后启动”，不使用 Windows Service。

优先方案：

- Windows Startup Task 或注册表当前用户 Run 项。
- 不要求管理员权限，除非安装到 Program Files 或添加防火墙规则确实需要提升。

桌面程序启动后最小化到托盘，不显示主窗口。

---

## 17. 错误处理

## 17.1 Hook 端

所有异常都必须捕获：

- 参数缺失。
- JSON 无效。
- 事件类型不支持。
- HTTP 连接失败。
- 超时。
- 桌面端返回错误。
- 本地日志目录不可写。

默认退出码为 0。

## 17.2 Desktop 端

不得因为以下问题退出：

- 手机断开。
- JSON 字段缺失。
- 历史文件损坏。
- 二维码生成失败。
- 防火墙规则不存在。
- Android 发送未知消息。
- 同一事件重复到达。

只有以下问题可以阻止完整启动：

- 回环事件端口无法绑定。
- 配置目录完全不可写。
- 核心依赖缺失或二进制损坏。

## 17.3 Android 端

- Token 无效：停止快速重连并提示重新配对。
- Wi-Fi 不可用：等待网络变化。
- 连接被拒绝：指数退避。
- 通知权限被拒绝：保持连接，但明确显示“已收到事件，系统通知未授权”。
- 前台服务权限或系统限制失败：退回前台接收模式并显示说明。

---

## 18. 测试要求

## 18.1 单元测试

### Payload

- 完整载荷。
- 缺少所有可选字段。
- 未知字段。
- 中文内容。
- Emoji。
- 超长摘要。
- 错误事件类型。
- 无效 JSON。

### Project 名提取

- Windows 路径。
- 路径末尾带分隔符。
- 根目录。
- UNC 路径。
- 空字符串。
- 非法路径字符。

### 摘要处理

- 合并换行和多空格。
- Unicode 文本元素截断。
- Emoji 不被切断。
- 空回复。
- 关闭摘要。

### 去重

- 同一 thread/turn。
- 不同 thread、相同 turn。
- 无 thread。
- 无 turn。
- LRU 超过容量。

### Codex 配置

- 文件不存在。
- 空文件。
- 已有普通配置。
- 已有 AgentBell notify。
- 已有其他 notify。
- UTF-8 BOM。
- CRLF 和 LF。
- 只读文件。
- 非法 TOML。
- `CODEX_HOME` 自定义路径。
- 幂等执行两次。

## 18.2 集成测试

1. 手工运行 Hook，并传入样例 JSON。
2. Desktop 接收并转换事件。
3. WebSocket 测试客户端收到事件。
4. Android 前台收到事件。
5. Android 后台收到系统通知。
6. Desktop 未启动时 Hook 在 500 ms 左右退出。
7. 手机断开不影响 Hook。
8. 同一事件发送两次只显示一次通知。
9. 防火墙仅允许专用网络。
10. Token 错误无法建立 WebSocket。

## 18.3 延迟测试

在 Desktop 和 Android 中记录单调时钟时间点：

- Hook 进程开始。
- Desktop 收到 HTTP。
- Desktop 发出 WebSocket。
- Android 收到 WebSocket。
- Android 请求显示通知。

不要在正常产品日志中记录内容，只记录耗时。

测试至少执行 100 次，输出：

- P50。
- P95。
- P99。
- 最大值。
- 丢失率。
- 重复率。

目标：

```text
P95 < 1000 ms
丢失率 = 0%
重复率 = 0%
```

---

## 19. 开发里程碑

## M0：Codex 事件探针

目标：证明官方 `notify` 可以稳定触发。

实现：

- 创建 `AgentBell.Hook`。
- 接收单个 JSON 参数。
- 验证 `agent-turn-complete`。
- 诊断模式下写入经过脱敏的事件元数据。
- 手动在用户 `config.toml` 中配置 Hook。
- 连续执行 20 个 Codex 回合。

验收：

- 20 次全部触发。
- 无重复。
- 字段解析正确。
- Hook 不明显阻塞 Codex。

禁止实现：

- Android。
- WebSocket。
- 安装器。
- 托盘 UI。

## M1：Windows 本地桥接

目标：完成 Codex → Hook → Desktop。

实现：

- `AgentBell.Desktop`。
- 回环 Minimal API。
- AgentEvent 转换。
- 去重。
- 最近 100 条 JSON 历史。
- 托盘“发送测试事件”。

验收：

- 手工 Hook 和真实 Codex 均可进入 Desktop。
- 无 Desktop 时 Hook 快速退出。
- 相同 EventId 不重复处理。

## M2：局域网 WebSocket

目标：完成 Desktop → 任意测试客户端。

实现：

- Token。
- 状态接口。
- WebSocket。
- 二维码内容生成。
- 测试网页或命令行 WebSocket 客户端，仅作调试。

验收：

- 同一局域网设备可以认证并收到事件。
- 错误 Token 被拒绝。
- 断开后 Desktop 稳定运行。
- 事件延迟满足目标。

禁止提前美化 Android UI。

## M3：Android MVP

目标：完成系统通知。

实现：

- 扫码。
- 配对验证。
- WebSocket。
- 前台服务。
- 高重要性通知频道。
- 断线重连。
- 去重。
- 三个极简状态页。

验收：

- App 前台、后台、锁屏均可收到。
- 100 次测试无丢失、无重复。
- P95 小于 1 秒。
- 通知权限被拒绝时 App 不崩溃。

## M4：一键配置和安装器

目标：电脑端接近一键部署。

实现：

- Codex 路径检测。
- 配置备份。
- 安全写入 `notify`。
- 幂等安装。
- 托盘自启动。
- 专用网络防火墙规则。
- Inno Setup。
- 卸载安全清理。
- 安装结束自动打开配对二维码。

验收：

用户流程最多为：

```text
双击安装包
→ 接受 Windows 必要权限
→ 手机安装 APK
→ 扫描二维码
→ 允许通知
→ 收到测试提醒
```

## M5：稳定性和发布候选

实现：

- 诊断信息导出。
- 配置冲突代理。
- 升级兼容。
- 安装/卸载回归测试。
- 不同 Windows 用户目录测试。
- 不同局域网和 Android 设备测试。
- README 和隐私说明。

完成 M5 后才能称为公开测试版。

---

## 20. Codex 开发规则

Codex 在实现本项目时必须遵守：

1. 一次只处理一个里程碑。
2. 开始编码前先读取本文件和仓库根目录 `AGENTS.md`。
3. 每次修改前说明：
   - 本次目标。
   - 将修改的文件。
   - 不会修改的范围。
4. 不得擅自增加 Claude 支持。
5. 不得增加云端依赖。
6. 不得增加用户账户。
7. 不得为了“未来扩展”引入复杂框架。
8. 核心协议必须有测试。
9. 所有外部输入均按不可信处理。
10. 不得在日志中输出 Token、Prompt、完整回复。
11. 不得覆盖用户 Codex 配置。
12. 不得在 M0 到 M2 阶段实现安装器。
13. 每个里程碑完成后运行：
    - 格式化。
    - 构建。
    - 单元测试。
    - 对应的手工测试。
14. 失败时修复根因，不以跳过测试作为解决方案。
15. 不确定 Codex 官方行为时，优先查阅官方文档，不根据记忆猜测。

---

## 21. 第一条给 Codex 的任务

将本文件保存为：

```text
docs/DEVELOPMENT_SPEC.md
```

将配套 `AGENTS.md` 放在仓库根目录，然后向 Codex 发送：

```text
阅读仓库根目录的 AGENTS.md 和 docs/DEVELOPMENT_SPEC.md。

现在只执行 M0：Codex 事件探针。不要实现 Desktop、WebSocket、Android、安装器或 Claude 支持。

要求：
1. 创建最小的 .NET 10 solution。
2. 创建 AgentBell.Contracts、AgentBell.Hook 和对应测试项目。
3. 实现对 Codex notify 单个 JSON 参数的健壮解析。
4. 只处理 type=agent-turn-complete。
5. 默认不写入原始 JSON或消息正文。
6. 实现可通过环境变量启用的脱敏诊断日志。
7. HTTP 转发接口可以先定义，但 M0 使用可替换接口和测试替身；不要实现 Desktop。
8. 添加完整单元测试。
9. 添加 docs/M0_MANUAL_TEST.md，写明如何手动配置 ~/.codex/config.toml、如何测试、如何恢复配置。
10. 完成后运行 build 和 test，并报告结果、剩余风险以及下一步，但不要自动开始 M1。
```

---

## 22. M0 建议接口

```csharp
public interface ICodexPayloadParser
{
    bool TryParse(
        IReadOnlyList<string> arguments,
        out CodexNotifyPayload? payload,
        out string? errorCode);
}

public interface IEventForwarder
{
    Task<ForwardResult> ForwardAsync(
        string rawJson,
        CancellationToken cancellationToken);
}

public interface IDiagnosticLogger
{
    void Record(HookDiagnosticEvent diagnosticEvent);
}
```

错误代码使用稳定枚举或常量：

```text
no_arguments
json_not_found
invalid_json
missing_type
unsupported_type
forward_timeout
forward_unavailable
forward_rejected
unexpected_error
```

不要把异常消息直接作为公开错误代码。

---

## 23. 样例 Codex 载荷

```json
{
  "type": "agent-turn-complete",
  "thread-id": "thread-123",
  "turn-id": "turn-456",
  "cwd": "C:\\Projects\\AgentBell",
  "input-messages": [
    "Implement the Codex event probe."
  ],
  "last-assistant-message": "Implemented the event probe and all tests pass."
}
```

M0 诊断日志允许记录：

```json
{
  "timestamp": "2026-07-31T22:19:00+08:00",
  "eventType": "agent-turn-complete",
  "threadSuffix": "ead-123",
  "turnSuffix": "urn-456",
  "hasWorkingDirectory": true,
  "hasAssistantMessage": true,
  "forwardResult": "success",
  "elapsedMs": 37
}
```

不得记录：

- `input-messages` 内容。
- `last-assistant-message` 内容。
- 完整工作目录。
- 完整 thread-id。
- 完整 turn-id。
- 配对 Token。

---

## 24. 发布定义

### Prototype

- 手动修改 Codex 配置。
- Windows 到 Android 主链路打通。
- 可用于开发者本人测试。

### MVP

- Windows 安装包。
- Android APK。
- 自动配置 Codex。
- 二维码配对。
- 后台通知。
- 安全卸载。

### Beta

- 配置冲突代理。
- 多设备兼容测试。
- 诊断导出。
- 升级策略。
- 隐私声明。
- 基础签名和发布流程。

第一轮开发只承诺 MVP，不承诺 Beta 之外的能力。

---

## 25. 最终产品文案边界

允许：

> Codex 当前回合完成后，AgentBell 会通过局域网立即在手机上显示通知。

允许：

> 无需账号，不经过云端，不上传代码。

不允许：

> AgentBell 可以准确监测 Codex 的真实完成百分比。

不允许：

> AgentBell 能判断整个开发任务是否彻底完成。

不允许：

> 消息经过端到端加密。

第一轮 HTTP/WS 仅通过局域网隔离和随机 Token 控制访问，应如实描述。

---

## 26. 决策摘要

- 第一适配器：Codex。
- 第一事件来源：官方 `notify`。
- 第一事件类型：`agent-turn-complete`。
- Windows：C# + .NET 10。
- Android：Kotlin 原生。
- 电脑进程：Hook + Desktop 两个进程。
- 实时通信：WebSocket。
- 配对：二维码 + 256 bit Token。
- 局域网事件入口和回环事件入口分端口。
- 默认不发送 Prompt。
- 默认摘要 160 字。
- 无账号。
- 无云端。
- 无数据库。
- 第一轮不做 Claude。
- 第一轮不做正式 Hooks 插件。
- 先完成 M0，再逐步推进，禁止跨里程碑扩张。

---

## 27. M6：Codex Action Required Notifications

M6 目标版本为 `0.7.0-beta.1`，Android `versionCode` 为 7，协议版本保持 1。

### 27.1 事件来源

- `AgentBell.Hook.exe --codex-stop-hook` 保持既有 stdin 与
  `{"continue":true}` stdout 契约。
- `AgentBell.Hook.exe --codex-permission-request-hook` 从 stdin 接收官方
  `PermissionRequest` JSON；所有结果均 exit 0，stdout/stderr 均为空，不返回
  `allow`、`deny` 或 `updatedInput`，硬超时不超过 3 秒。
- `AgentBell.Hook.exe --codex-post-tool-use-hook` 从 stdin 接收官方
  `PostToolUse` JSON；只转发 session/turn/tool-use 的确定性截断哈希和工具类别，
  不保存 `tool_input`、`tool_response` 或命令。所有结果均 exit 0 且
  stdout/stderr 为空。
- 当前官方 Hooks schema 明确支持 `PostToolUse`。其安全关联字段为
  `session_id`、`turn_id`、`tool_use_id` 和 `tool_name`；PermissionRequest 的
  官方字段不保证包含 `tool_use_id`，缺失时仅在相同 session、turn 和工具类别内
  使用最早生命周期项进行保守关联。该关联只用于去重、清理与未来兼容，不用于
  判断请求是否曾等待用户。
- 未观察到稳定的结构化 user-input `tool_name` 时，不得安装猜测的
  PreToolUse matcher。回复和确认请求使用本机 Stop 高置信度分类作为回退。

### 27.2 事件语义

协议 v1 增量字段：

- `category`: `completion` 或 `action_required`；
- `actionType`: `none`、`permission_required`、`input_required`、
  `confirmation_required`、`attention_required`；
- `toolCategory`: `none`、`command`、`file_change`、`network_access`、
  `external_tool`、`computer_control`、`other`。

PermissionRequest 在 Hook 进程内先清理，再发送到 Desktop。Desktop 对唯一 EventId
维护有界、线程安全的脱敏生命周期。策略为 `关闭` 时仅记录可选脱敏诊断并参与
进程内去重，不写入 `events.json`，不进入 Windows/Android 操作历史，不弹通知，
也不通过 WebSocket 广播。策略为 `始终提醒` 时，每个唯一请求立即生成一个
`category=action_required`、`actionType=permission_required` 事件，持久化并分别交给
Windows 与 Android；不存在 grace period、等待计时器或“超时即需人工处理”的语义。

PostToolUse 优先以 tool-use 哈希关联对应生命周期项；Stop 关联同一 session/turn 的
全部权限项。已发布项可使用同一 EventId 和更高 Sequence 写入 `resolvedAt` 更新，
供客户端清除活动通知；仅观察项不生成用户事件。PostToolUse 的出现时间、延迟或
缺失均不得用于推断 Auto-review、人工审批或真实等待状态。

### 27.3 隐私边界

LAN 与 Android 不得接收 raw Hook JSON、command、tool input/response、
description、prompt、用户问题、`last_assistant_message`、完整 cwd、原始 ID、
transcript 或 Token。只允许稳定枚举、project basename、不可逆截断哈希、
event ID、sequence 和时间。Windows/Android 通知只显示本地化通用文案与
project basename。

### 27.4 本地分类

Stop 文本分类只匹配明确多词短语，优先级为 permission、input、confirmation、
attention、completion。不得以问号或“确认”“需要”等单词单独判断。无法高
置信度分类时必须作为 completion。分类后 action-required 事件不保存摘要；
诊断最多记录 `classifiedAs`、规则 ID 与置信度，不记录命中文本。

### 27.5 展示与设置

Windows 与 Android 分别保存完成通知、一般需要操作、回复与确认等现有显示设置。
权限请求使用独立策略：`关闭`（默认）或 `始终提醒`。一般“需要操作”开关只控制
input/confirmation/attention，不控制 permission。旧版 `notifyPermissionRequests`
布尔值（包括 `true`）必须安全迁移为 `关闭`，防止升级后继续误提醒。

设置页必须明确说明：Codex 不会向 Hook 暴露权限请求由 Auto-review 自动处理还是
等待用户批准，因此“始终提醒”也可能提醒最终由 Codex 自动处理的请求。不得提供
Auto、Smart 等暗示可精确判断的选项。

Android 保留 completion channel，并增加
`agentbell_action_required`（High importance、声音、震动；无 full-screen
intent，不绕过勿扰）。所有新增文本必须保持英文/简体中文 key 与占位符一致。

### 27.6 安装与卸载

Setup 在 `hooks.json` 中维护恰好一个 Stop、一个 PermissionRequest 和一个
PostToolUse AgentBell Hook，写入前备份，保留全部非 AgentBell Hook，不修改 `config.toml`、`notify`
或权限策略。定义更新后由 Codex 正常发起 Review/Trust。歧义时停止自动修改。
卸载只删除严格识别的三个 AgentBell Hook。

人工验收步骤和模拟输入边界见 `docs/M6_MANUAL_TEST.md`。模拟
PermissionRequest 不能宣称为真实 Codex 权限弹窗验收；自然语言分类不能宣称
百分之百准确。

### 27.7 能力边界调查结论

Codex App Server 协议存在比 Hooks 更精确的审批信号，包括：

- `item/commandExecution/requestApproval`；
- `item/fileChange/requestApproval`；
- `item/permissions/requestApproval`；
- `thread/status/changed` 的 `waitingOnApproval`；
- `serverRequest/resolved`。

这些信号不在当前 command Hook 输入中。另起一个 App Server 客户端也不能观察
Windows Codex Desktop 已建立的私有连接，因此 M6 不接管或代理 Desktop 的 App
Server 会话。禁止以 OCR、UI 自动化、进程注入、私有连接接管、SQLite 抓取或未文档化
`approvalsReviewer` 作为产品实现；`approvalsReviewer` 只保留为能力调查事实，不构成
受支持契约。
