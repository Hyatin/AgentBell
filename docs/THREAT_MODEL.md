# AgentBell threat model

## Boundaries and assets

Codex and the loopback Hook endpoint share the Windows user trust boundary. The LAN is a separate, only conditionally trusted boundary. Pairing Tokens, the Android release key, Hook integrity, Setup/APK integrity, and sanitized event content are protected assets. Cloud services and hostile-network operation are outside the design.

## Threats and mitigations

| Threat | Current mitigation | Residual risk |
|---|---|---|
| Pairing Token or QR leak | 256-bit random token, fragment-based QR, DPAPI and Android Keystore storage, redacted logs | Anyone with the token on the LAN can authenticate until re-pairing rotates it |
| Malicious LAN device | RFC1918-only binding, mandatory token, bounded parsers and queues | HTTP/WS traffic is not encrypted; a hostile LAN can observe or disrupt traffic |
| `hooks.json` tampering | Exact managed command, backup, merge-only integration, Codex trust review | Malware running as the same user can modify the Hook or binaries |
| APK sideload replacement | Release signing and published certificate SHA-256 | Users may install an unverified APK; losing the release key prevents upgrades |
| Installer replacement | SHA-256, optional Authenticode, optional GitHub provenance | Unsigned beta has no trusted publisher identity and can trigger SmartScreen |
| Keystore loss or theft | Repository-external generation and separate secure backups | Loss blocks same-package upgrades; theft enables malicious signed updates |
| Oversized or malicious JSON | 1 MB limits, strict event checks, unknown-field ignore, stable errors | Resource exhaustion remains possible from a compromised local user process |
| Sensitive diagnostics | Default-off Hook log, bounded sanitized fields, export scanner | A user can still share unrelated private files manually |

## Not solved

AgentBell does not protect a compromised Windows account or Android device, encrypt LAN traffic, authenticate the human operating Codex, provide remote control, secure public/guest Wi-Fi, or establish a commercial code-signing reputation. GitHub attestation, checksums, Authenticode, and APK signing are distinct controls and must not be conflated.
