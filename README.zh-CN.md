# AgentBell

[English](README.md) | [简体中文](README.zh-CN.md)

AgentBell 在 Codex 当前回合结束后，通过可信局域网把完成通知从 Windows 电脑发送到 Android 系统通知栏。

> 当前公开版本：`v0.5.0-beta.1`（预发布）。源码树的下一目标版本为 `0.6.0-beta.1`，协议版本仍为 1。

## 功能与架构

- 用户级 Codex `Stop` Hook，不覆盖现有 `notify`。
- 轻量 Windows Tray、本机回环入口与已认证的 LAN WebSocket。
- Android heads-up 通知、自动重连、resume 和事件去重。
- Windows 和 Android 均支持 English、简体中文和跟随系统。
- 无账号、无云服务、无遥测、无广告 SDK。
- 默认脱敏日志；不记录 Prompt、完整回复、完整路径、原始 ID 或配对 Token。

```text
Codex Stop Hook -> AgentBell.Hook.exe -> 127.0.0.1 Windows Tray
                                         -> 已认证的 LAN WebSocket -> Android 通知
```

## 语言

Windows 和 Android 分别保存语言选择，首次启动默认“跟随系统”。系统界面语言精确为 `zh-CN` 时显示简体中文；其他未支持语言（包括 `zh-TW` 和 `zh-HK`）均回退到英文。切换语言不会删除配对数据，也不会修改网络协议。

## 安装

1. 从 `v0.5.0-beta.1` GitHub Pre-release 下载 Setup、已签名 release APK 和 `SHA256SUMS.txt`。
2. 运行或安装前验证每个文件的 SHA-256。
3. 运行 Setup 并选择 English 或简体中文。未使用可信代码签名证书的 Windows Beta 可能触发 SmartScreen；只应在核对发布来源和哈希后继续。
4. Codex 首次审核 Stop Hook 时，确认其指向 Known Folder 安装目录中的 `AgentBell.Hook.exe --codex-stop-hook`，再手工信任。AgentBell 不替换或链接 `notify`。
5. 安装已签名的 release APK。若设备已有 debug 签名版本，通常必须先卸载，这会删除 Android 配对数据。
6. 扫描 Tray 中的配对二维码并授予通知权限。
7. Xiaomi/Redmi/HyperOS 建议允许后台活动和自启动（如系统提供），并将电池策略设为不限制。AgentBell 不绕过系统策略。

`com.hyatin.agentbell` 后续版本必须继续使用同一长期 release 密钥，否则无法覆盖升级。

## 升级与卸载

覆盖运行新版 Setup 可保留 Windows 设置、语言、配对和事件历史。Android 覆盖升级必须使用同一签名密钥。Windows 卸载默认保留本地数据，并提供明确的删除数据选项。卸载器沿用安装时选择的语言。

## 安全与隐私

本机 Hook 入口仅绑定 `127.0.0.1:17863`。手机连接只接受 RFC1918 IPv4，并要求 256-bit 随机 Token；Token 由 Windows DPAPI 和 Android Keystore 在本地保护。Beta 版在可信局域网中使用 HTTP/WS，**不是 TLS，也不是端到端加密**。不要在公共、访客或不可信网络中使用，也不要分享配对二维码或 URL。

详见 [SECURITY.md](SECURITY.md)、[PRIVACY.md](PRIVACY.md) 和 [威胁模型](docs/THREAT_MODEL.md)。

## 已知限制

- 仅支持 Windows 10/11 x64、Android 和 Codex。
- 通知只表示一个 Codex 回合已结束，不表示整个任务完成，也不提供真实进度。
- 不使用云中继；电脑与手机必须位于同一可信局域网。
- 未签名 Windows Beta 可能触发 SmartScreen；校验和及 provenance 不能替代 Authenticode。
- Android 厂商的电池策略可能暂停前台连接。

## 构建与测试

需要 .NET 10、JDK 17、Android SDK 36 和 Gradle Wrapper。公开 Android release 还需要仓库外的长期签名密钥。

```powershell
dotnet format .\AgentBell.sln --verify-no-changes
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
.\scripts\audit-localization.ps1
Push-Location .\android\AgentBell
.\gradlew.bat testReleaseUnitTest lintRelease assembleRelease
Pop-Location
```

参见 [贡献指南](CONTRIBUTING.md)、[故障排除](docs/TROUBLESHOOTING.md)、[国际化测试指南](docs/localization/testing.md) 和 [发布清单](docs/M5_RELEASE_CHECKLIST.md)。Release Dry Run 不会创建 tag 或 GitHub Release。

Bug 请使用仓库 Issue 模板，并只提交脱敏摘要。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 许可证

AgentBell 使用 [Apache License 2.0](LICENSE)。第三方许可证详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
