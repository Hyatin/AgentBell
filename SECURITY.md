# Security policy

## Supported versions

Security fixes are currently provided only for the latest `0.5.x` public beta. Pre-beta builds and development artifacts are unsupported.

## Reporting a vulnerability

Please report vulnerabilities privately through the repository's GitHub Security Advisory feature once the public repository enables it. Until that channel exists, do not publish exploit details; the private contact address is intentionally pending repository setup.

Never include a pairing Token, pairing URL or QR code, `config.json`, `events.json`, complete `hooks.json`, raw logs, prompts, assistant replies, keystores, or a full diagnostic ZIP. Provide a minimal reproduction with synthetic values and a manually reviewed, sanitized summary.

We will acknowledge reports when maintainers are available, but this volunteer beta makes no guaranteed response or remediation time. There is currently no bug bounty.

## Release trust

Verify `SHA256SUMS.txt`, the Android signing certificate fingerprint, and any available GitHub artifact attestation. These do not substitute for Android APK signing or Windows Authenticode. An explicitly unsigned Windows beta may trigger SmartScreen.
