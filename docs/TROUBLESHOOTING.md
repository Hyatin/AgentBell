# Troubleshooting

## Windows Tray does not start

Resolve the installation from the uninstall registry or the running process path; do not assume `C:` or trust `%LOCALAPPDATA%` alone. The fallback is `Environment.GetFolderPath(LocalApplicationData)\Programs\AgentBell`. Check that only one Tray instance runs and that the HKCU Run value points to the same stable path.

## Hook remains pending or times out

Confirm Codex trusts the exact installed `AgentBell.Hook.exe --codex-stop-hook` command. Do not modify or chain an existing `notify`. Start Tray and verify `127.0.0.1:17863` is listening. Export only the sanitized diagnostic summary; never post complete `hooks.json` or raw Hook input.

## Phone cannot pair

Both devices must use the same trusted RFC1918 network. Disable guest isolation/VPN temporarily for diagnosis, keep Windows network profile private, and generate a new QR if the old one may have leaked. Never paste the QR or pairing URL into an Issue.

## Notifications stop in background

Grant notification permission, keep continuous receiving enabled, and allow foreground/background operation. On Xiaomi/Redmi/HyperOS, set battery use to unrestricted and enable autostart if the OS offers it. Reboot-specific behavior must be verified manually.

## APK will not upgrade

A debug-signed build and the public release use different certificates. Uninstalling the debug build removes pairing data. Every public release must use the same long-term release key; compare the APK certificate SHA-256 before upgrading.

## SmartScreen warning

The first beta may be unsigned. Verify the download is from the intended GitHub pre-release and compare SHA-256. Checksums and GitHub provenance do not create a trusted Windows publisher. If verification is uncertain, cancel installation.
