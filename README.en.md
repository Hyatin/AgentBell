# AgentBell

[简体中文](README.md)

AgentBell sends an Android system notification over your trusted LAN when a Codex turn ends on Windows.

> Current release: `v0.5.0-beta.1` Public Beta (pre-release), protocol version 1.

## Features and architecture

- User-level Codex `Stop` Hook without replacing an existing `notify` command.
- Lightweight Windows tray process, loopback ingestion, and LAN WebSocket.
- Android heads-up notifications, reconnect, resume, and deduplication.
- No account, cloud service, telemetry, or advertising SDK.
- Sanitized diagnostics: no prompts, full replies, full paths, raw IDs, or pairing tokens.

```text
Codex Stop Hook -> AgentBell.Hook.exe -> 127.0.0.1 Windows Tray
                                         -> authenticated LAN WebSocket -> Android notification
```

## Install

1. Download the Setup, release APK, and `SHA256SUMS.txt` from the `v0.5.0-beta.1` GitHub pre-release.
2. Verify each SHA-256 hash before running or sideloading it.
3. Run Setup. An unsigned Windows beta may trigger Microsoft Defender SmartScreen; continue only after verifying the release source and hash.
4. When Codex first reviews the Stop Hook, confirm that it points to the Known Folder installation of `AgentBell.Hook.exe --codex-stop-hook`, then trust it manually. AgentBell does not replace or chain `notify`.
5. Install the signed release APK. A previously installed debug-signed build usually must be uninstalled first, which removes Android pairing data.
6. Scan the Tray pairing QR and grant notification permission.
7. On Xiaomi/Redmi/HyperOS, allow background activity and autostart when available, and set battery use to unrestricted. AgentBell does not bypass OS policy.

All future releases for `com.hyatin.agentbell` must use the same long-term release key. Losing it prevents in-place upgrades.

## Upgrade and uninstall

Run a newer Setup over the existing installation to preserve Windows data. Android upgrades require the same signing key. Windows uninstall retains local data by default and offers an explicit delete-data option. Android system settings can clear or remove app data.

## Security and privacy

Hook ingestion binds only to `127.0.0.1:17863`. LAN access is restricted to RFC1918 IPv4 and requires a random 256-bit token protected locally with Windows DPAPI and Android Keystore. The beta uses HTTP/WS on the trusted LAN: **it is not TLS and not end-to-end encrypted**. Do not use it on public, guest, or hostile networks, and never share a pairing QR or URL.

See [SECURITY.md](SECURITY.md), [PRIVACY.md](PRIVACY.md), and the [threat model](docs/THREAT_MODEL.md).

## Limits

- Windows 10/11 x64, Android, and Codex only.
- A notification means one Codex turn ended; it does not prove the overall task is complete or expose real progress.
- No cloud relay; both devices must be on the same trusted LAN.
- Unsigned Windows beta binaries may trigger SmartScreen. Checksums and provenance do not replace Authenticode.
- Vendor battery policies may suspend the Android foreground connection.

## Build, FAQ, and bugs

Building requires .NET 10, JDK 17, Android SDK 36, and the Gradle Wrapper. A public Android release additionally requires an external long-term signing key. Follow [CONTRIBUTING.md](CONTRIBUTING.md), [troubleshooting](docs/TROUBLESHOOTING.md), and the [release checklist](docs/M5_RELEASE_CHECKLIST.md).

Use the repository Issue templates for bugs, and submit only sanitized summaries. Do not upload tokens, pairing URLs or QR codes, private configuration, event history, raw logs, prompts, responses, or a full diagnostic archive. Security reports should follow [SECURITY.md](SECURITY.md). Safe screenshot placeholders are documented in [docs/images](docs/images/README.md).

## License

AgentBell is licensed under the [Apache License 2.0](LICENSE). Third-party notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
