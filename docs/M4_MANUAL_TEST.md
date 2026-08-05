# M4 manual test: per-user Tray, installer, upgrade, and uninstall

This procedure validates only M4. It assumes the M0-M3 Stop Hook, loopback,
private-LAN WebSocket, browser, and Android acceptance tests have already passed.

AgentBell M4 still uses ordinary HTTP and WebSocket traffic on one trusted private
LAN. It is **not end-to-end encrypted**. If Windows Firewall asks, allow AgentBell
only on **Private networks**. M4 has no cloud relay or automatic online updater.

The Setup executable is not formally code-signed, so Windows SmartScreen may show
a warning. Run only a Setup that you built yourself or received from a source you
trust, and compare its SHA-256 with `artifacts\m4-installer\SHA256SUMS.txt`.
Codex Hook trust review, Windows Firewall consent, Android permissions, and OEM
battery policy cannot and must not be bypassed by AgentBell. The included Android
APK is development-signed and Android may require explicit unknown-source consent.

Do not perform upgrade or uninstall tests until the backups in step 3 exist. Do
not edit or delete the entire `.codex` directory. Do not expose `config.json`, the
pairing QR, a pairing URL, or diagnostic source data in screenshots or bug reports.

## 1. Check the development environment

Open PowerShell at the repository root and verify the required toolchains:

```powershell
dotnet --version
dotnet --list-sdks

$m4LocalAppData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($m4LocalAppData)) {
    throw 'The Windows LocalApplicationData Known Folder is unavailable.'
}

$m4Java = @(
    $env:JAVA_HOME,
    (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr')
) | Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\java.exe')) } |
    Select-Object -First 1
& (Join-Path $m4Java 'bin\java.exe') -version

$m4Sdk = if ($env:ANDROID_SDK_ROOT) {
    $env:ANDROID_SDK_ROOT
} elseif ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} else {
    Join-Path $m4LocalAppData 'Android\Sdk'
}
Get-Item -LiteralPath (Join-Path $m4Sdk 'platforms\android-36')
Get-Item -LiteralPath (Join-Path $m4Sdk 'build-tools\36.0.0')
Get-Item -LiteralPath (Join-Path $m4Sdk 'platform-tools')

$m4Iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$m4Iscc
```

The build requires .NET SDK 10, a compatible 64-bit JDK 17 or newer, Android SDK
Platform 36, Build Tools 36.0.0, Platform-Tools, and a real Inno Setup compiler.
If `ISCC.exe` is not on `PATH`, set `ISCC_PATH` to the compiler's actual installed
path. `scripts\build-m4.ps1` also discovers registered Inno Setup installations.

## 2. Run the complete M4 build

The automated build and test suite may run while a production Desktop/Tray is
active: every child Hook and test Host uses explicit test mode, temporary data and
Codex homes, random non-production ports, and an isolated Tray instance identity.
For the later interactive production installation steps, first inspect running
development processes so the expected stable installation is unambiguous:

```powershell
Get-Process AgentBell.Desktop,AgentBell.Tray -ErrorAction SilentlyContinue |
    Select-Object Id,ProcessName,Path

.\scripts\build-m4.ps1 -Clean
```

The command must format-check, restore, Release-build, run all Windows tests,
publish three isolated self-contained executables, run Android unit tests, build a
real APK, assemble staging, compile Inno Setup, and emit hashes. Any failing step
must terminate the script.

The expected final files are:

```powershell
$m4Setup = (Resolve-Path '.\artifacts\m4-installer\AgentBell-Setup-0.4.0.exe').Path
$m4Apk = (Resolve-Path '.\artifacts\m4-package\android\AgentBell-debug.apk').Path
Get-Item $m4Setup,$m4Apk | Select-Object FullName,Length,LastWriteTime
Get-FileHash -Algorithm SHA256 -LiteralPath $m4Setup,$m4Apk
Get-Content '.\artifacts\m4-installer\SHA256SUMS.txt'
```

`-SkipAndroid` uses an already existing non-empty debug APK. `-SkipInstaller`
creates only the verified package and makes no Setup-success claim. Neither switch
is a substitute for the final default-build acceptance.

### Automated test-isolation contract

Automated child processes set all of the following together:

```text
AGENTBELL_TEST_MODE=1
AGENTBELL_TEST_LOOPBACK_PORT=<PORT>
AGENTBELL_TEST_LAN_PORT=<PORT>
AGENTBELL_TEST_INSTANCE_ID=<REDACTED>
AGENTBELL_DATA_HOME=<LOCAL_APP_DATA>
CODEX_HOME=<CODEX_HOME>
```

Test mode never falls back to 17863 when its port is missing or invalid. Core
rejects listener overrides outside explicit test mode. Test LAN/WebSocket binds
only loopback, uses a separate Token and event pipeline, and cannot be reached by
the real Android pairing. Tests stop every Host and delete their temporary roots in
cleanup. `scripts\test-m4-install.ps1` uses the same isolation and tracks only the
Tray PIDs it starts; it never addresses a production Tray Mutex or pipe.

The synthetic summary `中文 🔔` belongs to the Integration child-process quoting
test. It remains as Unicode test data, but now reaches only that test's random port
and temporary `events.json`.

## 3. Back up Codex and AgentBell data

Resolve the same Codex home that Integration uses, make a timestamped backup, and
record hashes without printing file content:

```powershell
$m4CodexHome = if ($env:CODEX_HOME) {
    $env:CODEX_HOME
} else {
    Join-Path $env:USERPROFILE '.codex'
}
$m4Hooks = Join-Path $m4CodexHome 'hooks.json'
$m4ConfigToml = Join-Path $m4CodexHome 'config.toml'
$m4Data = Join-Path $m4LocalAppData 'AgentBell'
$m4Backup = Join-Path $env:TEMP (
    'AgentBell-M4-Backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $m4Backup -Force | Out-Null

if (Test-Path $m4Hooks) {
    Copy-Item -LiteralPath $m4Hooks -Destination (Join-Path $m4Backup 'hooks.json')
}
if (Test-Path $m4ConfigToml) {
    Copy-Item -LiteralPath $m4ConfigToml -Destination (Join-Path $m4Backup 'config.toml')
}
if (Test-Path $m4Data) {
    Copy-Item -LiteralPath $m4Data -Destination (Join-Path $m4Backup 'AgentBell-data') -Recurse
}

$m4HooksBefore = if (Test-Path $m4Hooks) {
    (Get-FileHash -LiteralPath $m4Hooks -Algorithm SHA256).Hash
}
$m4TomlBefore = if (Test-Path $m4ConfigToml) {
    (Get-FileHash -LiteralPath $m4ConfigToml -Algorithm SHA256).Hash
}
$m4ConfigBefore = if (Test-Path (Join-Path $m4Data 'config.json')) {
    (Get-FileHash -LiteralPath (Join-Path $m4Data 'config.json') -Algorithm SHA256).Hash
}
$m4EventsBefore = if (Test-Path (Join-Path $m4Data 'events.json')) {
    (Get-FileHash -LiteralPath (Join-Path $m4Data 'events.json') -Algorithm SHA256).Hash
}

[pscustomobject]@{
    Backup = $m4Backup
    HooksHash = $m4HooksBefore
    ConfigTomlHash = $m4TomlBefore
    AgentBellConfigHash = $m4ConfigBefore
    EventsHash = $m4EventsBefore
} | Format-List
```

Keep this PowerShell open so the recorded values remain available.

## 4. Run Setup

Verify the Setup hash against `SHA256SUMS.txt`, then launch it normally:

```powershell
Get-FileHash -LiteralPath $m4Setup -Algorithm SHA256
& $m4Setup
```

Keep **Start AgentBell when I sign in** selected for the first pass. The desktop
shortcut is optional. Setup must remain per-user and must not request elevation.
It may report that Codex needs to review the new stable Hook path. If integration
fails because `hooks.json` is invalid or ambiguous, Setup must stop with a clear
error instead of overwriting it.

## 5. Verify the stable installation directory

```powershell
function Get-M4AgentBellInstallLocation {
    $m4AppId = 'A17863B4-7E64-4D74-A0B4-004000000001'
    $m4UninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($m4UninstallRoot in $m4UninstallRoots) {
        if (-not (Test-Path -LiteralPath $m4UninstallRoot)) {
            continue
        }

        foreach ($m4Entry in Get-ChildItem -LiteralPath $m4UninstallRoot) {
            if ($m4Entry.PSChildName.IndexOf(
                $m4AppId,
                [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            $m4Properties = Get-ItemProperty -LiteralPath $m4Entry.PSPath
            $m4InstallProperty = $m4Properties.PSObject.Properties['InstallLocation']
            if ($null -ne $m4InstallProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$m4InstallProperty.Value)) {
                return [IO.Path]::GetFullPath(
                    ([string]$m4InstallProperty.Value).Trim().Trim('"'))
            }
        }
    }

    $m4KnownLocalAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($m4KnownLocalAppData)) {
        throw 'The Windows LocalApplicationData Known Folder is unavailable.'
    }

    return [IO.Path]::GetFullPath(
        (Join-Path $m4KnownLocalAppData 'Programs\AgentBell'))
}

$m4Install = Get-M4AgentBellInstallLocation
[pscustomobject]@{
    EnvironmentLocalAppData = $env:LOCALAPPDATA
    KnownFolderLocalAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    RegisteredOrFallbackInstall = $m4Install
} | Format-List

$m4Required = @(
    'AgentBell.Tray.exe',
    'AgentBell.Hook.exe',
    'AgentBell.Integration.exe',
    'android\AgentBell-debug.apk',
    'unins000.exe'
)
$m4Required | ForEach-Object {
    $path = Join-Path $m4Install $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing installed file: $path"
    }
    Get-Item -LiteralPath $path | Select-Object FullName,Length,LastWriteTime
}
```

The program directory contains replaceable binaries. Persistent `config.json`,
`events.json`, logs, and pairing assets remain under `$m4Data`, which is resolved
from the Windows LocalApplicationData Known Folder rather than the environment
variable of the same name.

## 6. Verify that hooks.json gained only AgentBell

Run the installed Integration status command; its JSON must contain no Token or
other Hook command body:

```powershell
$m4Integration = Join-Path $m4Install 'AgentBell.Integration.exe'
& $m4Integration status --json
$m4HookBackups = @(Get-ChildItem -LiteralPath $m4CodexHome `
    -Filter 'hooks.json.agentbell-backup-*' -File |
    Sort-Object LastWriteTime -Descending)
$m4HookBackups | Select-Object -First 3 FullName,Length,LastWriteTime
```

Parse both JSON files and compare all non-AgentBell Hooks against the backup. There
must be exactly one AgentBell command Hook under `Stop`, and all other events,
groups, Hooks, and top-level fields must remain semantically present. The installed
command must reference the stable installed path under `$m4Install`, not the old
`<REPOSITORY_ROOT>\artifacts\m0-hook` development path. The newest timestamped
backup must be byte-for-byte equal to the pre-install `hooks.json`:

```powershell
if ($m4HooksBefore) {
    $m4BackupHash = (Get-FileHash -LiteralPath $m4HookBackups[0].FullName `
        -Algorithm SHA256).Hash
    if ($m4BackupHash -cne $m4HooksBefore) {
        throw 'AgentBell hooks backup is not byte-for-byte identical.'
    }
}
```

If multiple ambiguous AgentBell-like commands existed, the installer should have
refused automatic migration; resolve them manually and rerun **repair**.

## 7. Verify config.toml and notify are unchanged

```powershell
if ($m4TomlBefore) {
    $m4TomlAfterInstall = (Get-FileHash -LiteralPath $m4ConfigToml `
        -Algorithm SHA256).Hash
    if ($m4TomlAfterInstall -cne $m4TomlBefore) {
        throw 'config.toml changed during installation.'
    }
}
```

Do not print `config.toml` into a public log. Byte equality proves the existing
`notify` entry and every other setting were retained.

## 8. Start Codex and review the stable Hook path

Start a new Codex session. Codex may ask you to review the exact changed Hook
definition because the executable moved to its stable installed path. Confirm that
the displayed path is inside the registry-resolved `$m4Install` directory,
then trust it through Codex's own UI. AgentBell must not click, edit, or bypass this
review. Until it can reliably observe trust state, the Tray should show Unknown or
waiting-for-review, never a fabricated Trusted state.

## 9. Verify the tray icon and single instance

The Setup completion page should start one Tray instance. Verify the icon and
dynamic menu entries, then launch the executable again:

```powershell
$m4Tray = Join-Path $m4Install 'AgentBell.Tray.exe'
& $m4Tray
Start-Sleep -Seconds 2
Get-Process AgentBell.Tray | Select-Object Id,ProcessName,Path
```

Only one Tray process, one NotifyIcon, one local listener, and one LAN listener may
remain. The second invocation should exit with the documented secondary-instance
code and cause the existing main/pairing window to appear.

## 10. Verify current-user startup

```powershell
$m4RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$m4Startup = Get-ItemPropertyValue -LiteralPath $m4RunKey -Name AgentBell
$m4Startup
$m4ExpectedStartup = '"' + $m4Tray + '" --startup'
if ($m4Startup -cne $m4ExpectedStartup) {
    throw 'AgentBell startup command is missing or quoted incorrectly.'
}
```

Toggle startup off and on from the Tray menu. Each operation must be idempotent and
must not create a scheduled task, service, or machine-wide registry entry.

## 11. Display the pairing QR

Open **Show pairing QR**. Verify the computer name, service status, one private LAN
address and a port in 17864-17874. The full Token must not appear as text. Pressing
**Copy pairing URL** must first show a warning that the URL contains a credential,
must not be shared, and is only for a trusted LAN. Cancel once and confirm nothing
is copied; then continue only if a diagnostic URL copy is actually necessary.

The window must also say that the bundled APK is development-signed and may require
unknown-source installation consent.

## 12. Reuse or establish the Android connection

If this machine was already paired during M3, do not scan again: the phone should
reuse its Keystore credential because M4 retained the Windows Token and deviceId.
Otherwise, open the installed APK folder from the Tray, transfer
`android\AgentBell-debug.apk` to the phone over a trusted local method, install it,
grant Camera and notification permission when prompted, and scan the QR.

Do not require Android Studio or `adb` for an end user. On Redmi/HyperOS, retain the
M3 notification, autostart, background activity, and unrestricted-battery settings.

## 13. Complete a real Codex turn

With Tray running and the phone connected, complete a fresh real Codex turn. Verify:

- Codex invokes the stable installed `AgentBell.Hook.exe` in Stop-Hook mode;
- the Hook reads one JSON object from stdin;
- its stdout remains exactly `{"continue":true}`;
- local ingestion returns HTTP 202;
- the newest Desktop event has a new EventId and sequence;
- the browser client, if open, remains compatible.

Do not replace the real Hook with a test `notify` command and do not modify the
existing `config.toml` notify value.

## 14. Verify the Android heads-up notification

Keep another app in the foreground or lock the phone, complete another unique real
Codex turn, and confirm the `Codex任务完成` heads-up notification appears. Open it and
verify event history updates once. No Token, path, raw session/turn ID, or JSON may
appear. Repeat once with the AgentBell Activity closed to verify the foreground
service still owns the WebSocket.

## 15. Exit Tray and verify fast Hook failure

Choose **Exit** from the Tray menu. Confirm the icon disappears, the process exits,
WebSockets close, and ports are released:

```powershell
Get-Process AgentBell.Tray -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -LocalPort 17863 -ErrorAction SilentlyContinue
```

Complete a fresh real Codex turn while Tray is stopped. Codex must still complete
normally and the Hook must quickly return exactly `{"continue":true}` even though
local forwarding cannot reach 17863. No Android notification is expected for that
event because the Desktop service was unavailable.

## 16. Restart Tray and verify recovery

```powershell
Start-Process -FilePath $m4Tray
Start-Sleep -Seconds 3
Get-NetTCPConnection -State Listen -OwningProcess `
    (Get-Process AgentBell.Tray).Id |
    Select-Object LocalAddress,LocalPort,State
```

There must be exactly one `127.0.0.1:17863` listener and one non-wildcard RFC1918
listener on 17864-17874. LAN failure should be reported without disabling the local
listener. Complete another turn and verify notification delivery has recovered.

## 17. Test sign-out/sign-in startup

Close unrelated work, sign out of Windows, and sign in again. Within a reasonable
startup interval verify one Tray icon, one process, local and LAN listeners, and an
automatic phone reconnect. If Windows Firewall prompts, allow only Private networks.
Disable startup from the menu and repeat once if practical; AgentBell must not start.
Re-enable it for the upgrade test.

## 18. Test an in-place upgrade

Build a newer 0.4.0 commit or rebuild with a distinguishable file timestamp. Before
running Setup, record the persistent file hashes and startup choice:

```powershell
$m4ConfigPreUpgrade = (Get-FileHash `
    (Join-Path $m4Data 'config.json') -Algorithm SHA256).Hash
$m4EventsPreUpgrade = (Get-FileHash `
    (Join-Path $m4Data 'events.json') -Algorithm SHA256).Hash
$m4StartupPreUpgrade = Get-ItemPropertyValue `
    -LiteralPath $m4RunKey -Name AgentBell
& $m4Setup
```

Leave Tray running when upgrade starts. Setup should request a graceful shutdown,
replace only program files, retain its stable directory and prior task selection,
then restart Tray. Confirm there is still exactly one AgentBell Hook and no old
G-drive development command. A forced close, if ever needed after timeout, must be
visible rather than silently corrupting the data files.

## 19. Verify Token, deviceId, events, and pairing were retained

Do not print `config.json`. Compare hashes:

```powershell
$m4ConfigPostUpgrade = (Get-FileHash `
    (Join-Path $m4Data 'config.json') -Algorithm SHA256).Hash
$m4EventsPostUpgrade = (Get-FileHash `
    (Join-Path $m4Data 'events.json') -Algorithm SHA256).Hash
$m4StartupPostUpgrade = Get-ItemPropertyValue `
    -LiteralPath $m4RunKey -Name AgentBell

if ($m4ConfigPostUpgrade -cne $m4ConfigPreUpgrade) {
    throw 'Upgrade changed config.json, Token, or deviceId.'
}
if ($m4EventsPostUpgrade -cne $m4EventsPreUpgrade) {
    throw 'Upgrade changed events.json.'
}
if ($m4StartupPostUpgrade -cne $m4StartupPreUpgrade) {
    throw 'Upgrade changed the startup selection.'
}
```

The same Android pairing must remain usable without rescanning.

## 20. Verify phone reconnect and resume after upgrade

Keep Wi-Fi on and watch the Android connection state during upgrade. It should move
to Reconnecting and back to Connected after Tray restarts. To exercise resume:

1. Turn off phone Wi-Fi.
2. Complete one fresh Codex turn while Tray is running.
3. Turn Wi-Fi back on to the same trusted private LAN.
4. Verify exactly one replayed notification and an advanced sequence.
5. Confirm no older EventId is duplicated.

This must continue to use protocol version 1 and the same persisted credential.

## 21. Test uninstall

With Tray running, open **Installed apps > AgentBell > Uninstall** or run the stable
uninstaller:

```powershell
$m4Uninstaller = Join-Path $m4Install 'unins000.exe'
& $m4Uninstaller
```

For the first pass, leave **also delete AgentBell configuration, pairing, and event
history** unchecked. Uninstall must request Tray shutdown, remove program files and
the HKCU startup value, invoke Integration uninstall, and retain the data directory.

## 22. Verify other Codex Hooks were preserved

Parse `hooks.json` and compare it with the step-3 backup. Exactly AgentBell's own
strictly identified Stop command should be absent. All other Stop Hooks, other event
Hooks, groups, and top-level fields must remain. The `.codex` directory itself must
still exist. If safe Hook removal failed, the uninstaller must have warned and left
the file for manual review rather than guessing.

```powershell
Get-Item -LiteralPath $m4Hooks -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $m4CodexHome `
    -Filter 'hooks.json.agentbell-backup-*' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 5 FullName,Length,LastWriteTime
```

## 23. Verify config.toml and notify were preserved

```powershell
if ($m4TomlBefore) {
    $m4TomlAfterUninstall = (Get-FileHash -LiteralPath $m4ConfigToml `
        -Algorithm SHA256).Hash
    if ($m4TomlAfterUninstall -cne $m4TomlBefore) {
        throw 'Uninstall changed config.toml or notify.'
    }
}
```

No installer or Integration operation may write `config.toml`.

## 24. Verify default data retention

```powershell
if (-not (Test-Path -LiteralPath $m4Data -PathType Container)) {
    throw 'Default uninstall removed the AgentBell data directory.'
}
Get-Item -LiteralPath `
    (Join-Path $m4Data 'config.json'),
    (Join-Path $m4Data 'events.json') |
    Select-Object FullName,Length,LastWriteTime
```

Reinstall the same Setup and verify the prior pairing and history return without
scanning a new QR. This is the recovery path that proves uninstall retained data.

## 25. Test optional complete AgentBell-data cleanup

Only after the default-retention and reinstall checks pass, uninstall a second time
and explicitly select **also delete AgentBell configuration, pairing, and event
history**. Confirm only the Known-Folder-resolved `$m4Data` directory is deleted. The following must
remain untouched:

- the entire `.codex` directory;
- `config.toml` and its `notify` setting;
- every non-AgentBell Hook;
- unrelated LocalApplicationData Known Folder content.

Restore AgentBell data only from the step-3 backup if you intentionally want the
old pairing back. Never use a broad recursive delete command for this test.

## 26. Export and inspect a redacted diagnostic package

Reinstall/start Tray if needed, select **Export redacted diagnostics**, and save the
ZIP to a temporary location. The export must fail and delete the ZIP if its own
sensitive-value scan finds a Token or another known secret.

Inspect a successful archive without publishing it:

```powershell
$m4DiagnosticZip = Get-ChildItem -LiteralPath $m4Data `
    -Filter 'AgentBell-Diagnostics-*.zip' -Recurse -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$m4DiagnosticExtract = Join-Path $env:TEMP `
    ('AgentBell-M4-Diagnostics-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $m4DiagnosticZip.FullName `
    -DestinationPath $m4DiagnosticExtract
Get-ChildItem -LiteralPath $m4DiagnosticExtract -Recurse |
    Select-Object FullName,Length
rg -n -i 'token|authorization|access_token|pairingUrl|summary|session_id|turn_id|cwd' `
    $m4DiagnosticExtract
```

Review every match. Structure field names may be harmless, but the ZIP must not
contain any pairing Token or encrypted Token value, pairing URL/QR, Authorization
header, WebSocket query, event summary, prompt, raw JSON, full cwd, raw session or
turn ID, Android credential, full phone IP, or full `events.json`. It may contain
only version/runtime data, status, path hashes, private-address category, ports,
counts, sequence, sanitized rolling logs, and structure summaries.

After inspection, delete only the uniquely named extraction directory:

```powershell
Remove-Item -LiteralPath $m4DiagnosticExtract -Recurse -Force
```

Record results for all 26 steps, any Codex trust prompt, the private-only Firewall
choice, Setup/APK hashes, Android device/OEM version, notification latency, upgrade
reconnect behavior, and any known limitation. Stop after M4; do not begin M5 from
this procedure.
