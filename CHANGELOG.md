# Changelog

All notable changes follow Keep a Changelog conventions. This project uses semantic versions with prerelease suffixes.

## [0.6.0-beta.1] - Unreleased

### Added

- English and Simplified Chinese resources for the Windows Tray, pairing page, Android UI, foreground/completion notifications, Setup, and uninstaller.
- Independent Follow system, English, and Simplified Chinese application language settings.
- Resource parity, placeholder, fallback, persistence, and hardcoded-user-interface auditing.

### Changed

- Default public repository documentation is now English with a matching Simplified Chinese README.
- Windows language is stored in the existing atomic local configuration; Android uses official per-app locales.

### Compatibility

- Protocol version 1, API/WebSocket contracts, Hook arguments, pairing, and Codex configuration behavior are unchanged.

## [0.5.0-beta.1] - 2026-08-03

### Added

- Public-beta README, privacy, security, threat-model, troubleshooting, contribution, release, and third-party notices.
- Reproducible Windows/Android release pipeline with audit, checksums, release manifest, and SPDX output.
- External long-term Android signing configuration and optional Windows Authenticode signing.
- GitHub CI and guarded draft-first prerelease workflow.

### Changed

- Unified Windows install/data/APK/diagnostic/startup paths on the LocalAppData Known Folder.
- Centralized product `0.5.0`, informational `0.5.0-beta.1`, Android version code 5, and protocol version 1 metadata.
- Public Android packaging now requires a release-signed APK and never falls back to debug signing.

### Security

- Expanded ignore rules and added current-tree/tracked/history release auditing.
- Documented unsigned Windows beta, SmartScreen, trusted-LAN, QR, Hook, and signing-key risks.

[0.5.0-beta.1]: https://github.com/OWNER/AgentBell/releases/tag/v0.5.0-beta.1
