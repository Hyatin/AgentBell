# M5 `v0.6.0-beta.1` public beta release checklist

This checklist is manual. Completing it does not authorize scripts or Codex to push, tag, create a repository, publish a Release, upload assets, or change GitHub settings.

- [ ] 1. Install on a new Windows user or clean Windows 10/11 x64 VM.
- [ ] 2. Confirm self-contained Setup works without a preinstalled .NET runtime.
- [ ] 3. Review and trust the exact stable Codex Stop Hook; confirm existing `notify` is unchanged.
- [ ] 4. Confirm Tray single-instance and graceful shutdown behavior.
- [ ] 5. Confirm only the intended private-network firewall/LAN behavior and loopback `127.0.0.1:17863`.
- [ ] 6. Install the long-term-key-signed Android release APK for the first time.
- [ ] 7. Pair by scanning a newly generated private QR.
- [ ] 8. Verify a foreground Android notification.
- [ ] 9. Verify a background Android notification.
- [ ] 10. Verify a lock-screen notification without exposing unintended content.
- [ ] 11. Disconnect/reconnect Wi-Fi and verify bounded automatic recovery.
- [ ] 12. Restart Tray and verify configuration, sequence, and resume recovery.
- [ ] 13. Run a Windows Setup in-place upgrade and confirm data/Hook/startup preservation.
- [ ] 14. Install a newer release APK signed by the same key over the current release.
- [ ] 15. Uninstall Windows with default data retention and verify retained data.
- [ ] 16. Reinstall, then perform complete uninstall with explicit data deletion.
- [ ] 17. Follow both README languages from a clean state without developer knowledge.
- [ ] 18. Verify every listed SHA-256 against every intended public asset.
- [ ] 19. Run `apksigner verify --verbose --print-certs` and compare the certificate SHA-256.
- [ ] 20. Record actual SmartScreen behavior and confirm unsigned-beta wording is accurate.
- [ ] 21. Run `scripts/audit-public-release.ps1`; manually review its sanitized report and Git history.
- [ ] 22. Confirm root `LICENSE` is the standard Apache License 2.0 and all project metadata says Apache-2.0.
- [ ] 23. Compare resolved dependencies with `THIRD_PARTY_NOTICES.md` and include required notices.
- [ ] 24. Review a GitHub Draft Release: Pre-release flag, notes, exact asset whitelist, hashes, optional SBOM/provenance, no debug/PDB/key/log.
- [ ] 25. Obtain final human approval before tag creation and publication.

Also verify that a deliberately wrong `LOCALAPPDATA` environment value is untouched while install/data/startup/APK/diagnostic paths resolve through the Windows LocalAppData Known Folder. If Authenticode is absent, record `Signed: false`; do not represent checksums or attestation as code signing.

## Local release preparation commands

Create the long-term Android key outside the repository and confirm its destination interactively:

```powershell
.\scripts\create-android-release-key.ps1
```

Set the four Android variables only in the private release shell; do not save their values in this repository or paste them into logs:

```powershell
$env:AGENTBELL_ANDROID_KEYSTORE = '<REDACTED>'
$env:AGENTBELL_ANDROID_KEYSTORE_PASSWORD = '<REDACTED>'
$env:AGENTBELL_ANDROID_KEY_ALIAS = 'agentbell-release'
$env:AGENTBELL_ANDROID_KEY_PASSWORD = '<REDACTED>'
```

For optional trusted Windows signing, set the external PFX path/password and a trusted RFC 3161 timestamp URL through `AGENTBELL_WINDOWS_SIGN_CERTIFICATE`, `AGENTBELL_WINDOWS_SIGN_CERTIFICATE_PASSWORD`, and `AGENTBELL_WINDOWS_TIMESTAMP_URL`. Without them, the report must say `Signed: false` and `UNSIGNED BETA`.

```powershell
.\scripts\audit-public-release.ps1
.\scripts\audit-localization.ps1
.\scripts\build-release.ps1 -Version 0.6.0-beta.1 -Clean -DryRun
```

Remove secret variables from the release shell after use. A non-dry-run local build still does not push, tag, upload, or create a GitHub Release.

Verify release assets with `Get-FileHash -Algorithm SHA256`, `apksigner verify --verbose --print-certs`, and, when signed, `Get-AuthenticodeSignature`. Optional GitHub provenance can later be checked with `gh attestation verify <asset> --repo <REDACTED>/<REDACTED>`; provenance is not Authenticode or APK signing.
