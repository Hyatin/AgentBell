# Localization testing

Run from the repository root in PowerShell 7:

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

The .NET tests compare both `.resx` key sets, empty values, duplicates,
placeholders, English fallback, exact `zh-CN` system matching, manual overrides,
config restoration, localized status projection, action-required history and
notification text, and pairing-page output. Android tests compare both XML key
sets and format arguments, verify required accessibility/channel/service/action
strings, and exercise language parsing and unsupported-locale fallback. Installer
source tests compare `en` and `zhcn` custom-message key sets and reject
user-facing Pascal literals.

`scripts/audit-localization.ps1` scans the WinForms UI, Compose UI, Android notifications/service, and installer Pascal UI. It excludes only recognized resource keys, the AgentBell product name, language-neutral symbols, logs, tests, protocol constants, API/JSON fields, filenames, regexes, technical exceptions, docs, third-party code, and generated output. Its report is written as UTF-8 without BOM to `artifacts/localization/hardcoded-ui-strings.json`; the command fails when `remainingUserVisibleHardcodedCount` is nonzero.

## Manual matrix

Windows:

1. English Windows + Follow system: English UI.
2. Simplified Chinese Windows + Follow system: Simplified Chinese UI.
3. English Windows + 简体中文: Chinese main window, tray menu, tooltip, dialogs, and pairing page.
4. Chinese Windows + English: English UI.
5. Switch language while paired and connected; confirm the WebSocket remains connected and QR/token do not change.
6. Restart AgentBell; confirm the setting remains.
7. Inspect 125%, 150%, and 200% DPI for wrapping and clipping.

Android:

1. English Android + Follow system: English UI.
2. `zh-CN` Android + Follow system: Simplified Chinese UI.
3. `zh-TW`, `zh-HK`, Japanese, or German + Follow system: English UI.
4. Override English with 简体中文 and Chinese with English.
5. Confirm Activity text, scanner accessibility description, foreground notification, notification channel names/descriptions, completion notification, and all four action-required notification types.
6. Restart the app and confirm selection persistence.
7. Test a small screen, landscape, and large font/TalkBack.

Installer:

1. Compile and launch both English and Simplified Chinese flows.
2. Verify fresh install, in-place upgrade, rollback, English uninstall, and Chinese uninstall.
3. Confirm non-AgentBell Hooks remain, exactly one Stop, one PermissionRequest, and one PostToolUse AgentBell Hook exist, removal is idempotent, and exit code is 0 on success.

Cross-language pairing must also be tested in both directions. Payloads and protocol bytes must remain compatible with 0.5.
