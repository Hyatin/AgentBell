# Changelog

All notable changes follow Keep a Changelog conventions. This project uses semantic versions with prerelease suffixes.

## [0.7.0-beta.1] - Unreleased

### Added

- Content-free Codex `PermissionRequest` and `PostToolUse` Hooks that preserve Codex approval behavior and exit silently.
- Completion, permission, reply, confirmation, and attention-required event semantics for Windows and Android.
- Conservative English and Simplified Chinese Stop-response classification with local notification controls.
- A separate high-importance Android action-required notification channel and sanitized Windows event history.

### Changed

- Permission request notifications now use an explicit Off (default) / Always notify policy; Hook timing and PostToolUse arrival are no longer treated as evidence of who handled approval.
- Setup now maintains exactly one AgentBell Stop, PermissionRequest, and PostToolUse Hook while preserving all unrelated Hooks and `config.toml`.
- Android version code is 7; product metadata targets `0.7.0-beta.1` while protocol version 1 remains unchanged.

### Security

- Permission payloads are reduced to enumerated tool categories, project basenames, and irreversible identifier hashes before loopback forwarding.
- Raw commands, tool input, descriptions, full paths, identifiers, prompts, replies, and pairing credentials are excluded from synchronized action events.

## [0.6.0-beta.1] - 2026-08-06

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

[0.7.0-beta.1]: https://github.com/OWNER/AgentBell/compare/v0.6.0-beta.1...v0.7.0-beta.1
[0.6.0-beta.1]: https://github.com/OWNER/AgentBell/releases/tag/v0.6.0-beta.1
[0.5.0-beta.1]: https://github.com/OWNER/AgentBell/releases/tag/v0.5.0-beta.1
