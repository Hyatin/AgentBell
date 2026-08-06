# AgentBell

[English](README.md) | [简体中文](README.zh-CN.md)

AgentBell sends an Android system notification over your trusted LAN when a Codex turn ends on Windows.

> Current public release: `v0.5.0-beta.1` (pre-release). The source tree targets `0.6.0-beta.1`; protocol version 1 remains unchanged.

## Features and architecture

- User-level Codex `Stop` Hook without replacing an existing `notify` command.
- Lightweight Windows tray process, loopback ingestion, and authenticated LAN WebSocket.
- Android heads-up notifications, reconnect, resume, and deduplication.
- English, Simplified Chinese, and Follow system language options on Windows and Android.
- No account, cloud service, telemetry, or advertising SDK.
- Sanitized diagnostics: no prompts, full replies, full paths, raw IDs, or pairing tokens.

```text
Codex Stop Hook -> AgentBell.Hook.exe -> 127.0.0.1 Windows Tray
                                         -> authenticated LAN WebSocket -> Android notification
```

## Language

Windows and Android store language choices independently. The default is **Follow system**. An exact `zh-CN` system UI language selects Simplified Chinese; every other unsupported language, including `zh-TW` and `zh-HK`, falls back to English. Changing language does not remove pairing data or change the network protocol.

## Install

1. Download Setup, the signed release APK, and `SHA256SUMS.txt` from the `v0.5.0-beta.1` GitHub pre-release.
2. Verify each SHA-256 hash before running or sideloading it.
3. Run Setup and choose English or Simplified Chinese. An unsigned Windows beta may trigger Microsoft Defender SmartScreen; continue only after verifying the release source and hash.
4. When Codex first reviews the Stop Hook, confirm that it points to the Known Folder installation of `AgentBell.Hook.exe --codex-stop-hook`, then trust it manually. AgentBell does not replace or chain `notify`.
5. Install the signed release APK. A previously installed debug-signed build usually must be uninstalled first, which removes Android pairing data.
6. Scan the Tray pairing QR and grant notification permission.
7. On Xiaomi/Redmi/HyperOS, allow background activity and autostart when available, and set battery use to unrestricted. AgentBell does not bypass OS policy.

All future releases for `com.hyatin.agentbell` must use the same long-term release key. Losing it prevents in-place upgrades.

## Upgrade and uninstall

Run a newer Setup over the existing installation to preserve Windows settings, language, pairing, and event history. Android upgrades require the same signing key. Windows uninstall retains local data by default and offers an explicit delete-data option. The uninstaller uses the language selected during Setup.

## Security and privacy

Hook ingestion binds only to `127.0.0.1:17863`. LAN access is restricted to RFC1918 IPv4 and requires a random 256-bit token protected locally with Windows DPAPI and Android Keystore. The beta uses HTTP/WS on the trusted LAN: **it is not TLS and not end-to-end encrypted**. Do not use it on public, guest, or hostile networks, and never share a pairing QR or URL.

See [SECURITY.md](SECURITY.md), [PRIVACY.md](PRIVACY.md), and the [threat model](docs/THREAT_MODEL.md).

## Limits

- Windows 10/11 x64, Android, and Codex only.
- A notification means one Codex turn ended; it does not prove the overall task is complete or expose real progress.
- No cloud relay; both devices must be on the same trusted LAN.
- Unsigned Windows beta binaries may trigger SmartScreen. Checksums and provenance do not replace Authenticode.
- Vendor battery policies may suspend the Android foreground connection.

## Build and test

Building requires .NET 10, JDK 17, Android SDK 36, and the Gradle Wrapper. A public Android release additionally requires an external long-term signing key.

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

Follow [CONTRIBUTING.md](CONTRIBUTING.md), [troubleshooting](docs/TROUBLESHOOTING.md), the [localization testing guide](docs/localization/testing.md), and the [release checklist](docs/M5_RELEASE_CHECKLIST.md). A release dry run does not create a tag or GitHub Release.

Use repository Issue templates for bugs and submit only sanitized summaries. Security reports should follow [SECURITY.md](SECURITY.md).

## License

AgentBell is licensed under the [Apache License 2.0](LICENSE). Third-party notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
