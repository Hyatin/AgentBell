# M3 manual test: Android foreground WebSocket and Codex notifications

This procedure validates only M3:

```text
Codex Stop Hook
  -> AgentBell.Hook.exe
  -> http://127.0.0.1:17863/api/v1/events/codex
  -> AgentBell.Desktop.exe
  -> ws://<PRIVATE_IPV4>:<PORT>/ws/v1/events
  -> AgentBell Android foreground service
  -> Android heads-up notification
```

M3 still uses ordinary HTTP and WS. It is **not end-to-end encrypted** and must
only be used on a trusted private LAN. It uses no cloud service, Internet relay,
Firebase, Google account, or GMS-only scanner. Android and OEM firmware can limit
background work; AgentBell uses a foreground service but does not bypass Android
security or battery policy.

Android network-security XML cannot enumerate a private address learned at runtime,
so cleartext capability is enabled at the manifest/XML layer. The app compensates
with two application-layer gates: pairing accepts only numeric RFC1918 IPv4 and
ports 17864-17874, and the shared OkHttp interceptor permits only those addresses,
ports and `/api/v1/status` or `/ws/v1/events` with no query. No arbitrary URL is
passed to the network client. This is a scope restriction, not transport encryption.

Do not edit the current user's `.codex\hooks.json`, its trusted Hook command,
`config.toml`, or the existing Codex `notify` command. M2's browser client remains
supported.

## 1. Toolchain requirements

Windows requires the .NET 10 SDK. Android builds require:

- a 64-bit JDK 17;
- Android SDK Platform 36 and Build Tools 36.0.0;
- Android SDK Platform-Tools (`adb`);
- network access on the first Gradle run to resolve pinned dependencies.

Check the environment from PowerShell:

```powershell
dotnet --info
dotnet --list-sdks
java -version
javac -version

Get-Command adb -ErrorAction SilentlyContinue
Get-Command sdkmanager -ErrorAction SilentlyContinue
Get-ChildItem Env:JAVA_HOME,Env:ANDROID_HOME,Env:ANDROID_SDK_ROOT -ErrorAction SilentlyContinue
```

If `java` is missing, install a JDK 17 distribution and set `JAVA_HOME`. If the
SDK is missing, install Android Studio or the Android command-line tools, then
install Platform 36, Build Tools 36.0.0 and Platform-Tools. Set
`ANDROID_SDK_ROOT` to the SDK directory and add `platform-tools` to `PATH`.

## 2. Format, restore, Release build, and all Windows tests

Run at the repository root:

```powershell
dotnet format .\AgentBell.sln --verify-no-changes
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
```

## 3. Publish Hook and Desktop

Publish the Hook to its already trusted location. Do not change `hooks.json`:

```powershell
dotnet publish .\src\AgentBell.Hook\AgentBell.Hook.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m0-hook

dotnet publish .\src\AgentBell.Desktop\AgentBell.Desktop.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m3-desktop

$m3Hook = (Resolve-Path -LiteralPath '.\artifacts\m0-hook\AgentBell.Hook.exe').Path
$m3Desktop = (Resolve-Path -LiteralPath '.\artifacts\m3-desktop\AgentBell.Desktop.exe').Path
$m3Sample = (Resolve-Path -LiteralPath '.\docs\CODEX_STOP_HOOK_SAMPLE.json').Path
$m3LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$m3EventsPath = Join-Path $m3LocalAppData 'AgentBell\events.json'
$m3ConfigPath = Join-Path $m3LocalAppData 'AgentBell\config.json'
$m3QrPath = Join-Path $m3LocalAppData 'AgentBell\pairing\agentbell-pairing.png'
$m3HookLog = Join-Path $m3LocalAppData 'AgentBell\logs\m3-hook.ndjson'
$m3DesktopLog = Join-Path $m3LocalAppData 'AgentBell\logs\m3-desktop.ndjson'
```

## 4. Build and test Android

Run from the Android project directory:

```powershell
Push-Location .\android\AgentBell
try {
    .\gradlew.bat testDebugUnitTest
    if ($LASTEXITCODE -ne 0) { throw 'Android unit tests failed.' }

    .\gradlew.bat assembleDebug
    if ($LASTEXITCODE -ne 0) { throw 'Android debug assembly failed.' }
}
finally {
    Pop-Location
}
```

With a connected emulator or device, also run the Keystore, notification and
foreground-service instrumentation tests:

```powershell
adb devices
Push-Location .\android\AgentBell
try {
    .\gradlew.bat connectedDebugAndroidTest
    if ($LASTEXITCODE -ne 0) { throw 'Android instrumentation tests failed.' }
}
finally {
    Pop-Location
}
```

The debug APK must really exist before it is copied:

```powershell
$m3Apk = (Resolve-Path -LiteralPath `
    '.\android\AgentBell\app\build\outputs\apk\debug\app-debug.apk').Path
Get-Item -LiteralPath $m3Apk | Select-Object FullName,Length,LastWriteTime | Format-List

New-Item -ItemType Directory -Path '.\artifacts\m3-android' -Force | Out-Null
Copy-Item -LiteralPath $m3Apk `
    -Destination '.\artifacts\m3-android\AgentBell-debug.apk' -Force
```

Do not claim an APK exists if `assembleDebug` failed or the path is absent.

## 5. Start Desktop and verify both listeners

Open a second PowerShell at the repository root:

```powershell
$env:AGENTBELL_DESKTOP_DIAGNOSTICS = '1'
$env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH = `
    Join-Path $m3LocalAppData 'AgentBell\logs\m3-desktop.ndjson'
$m3Desktop = (Resolve-Path -LiteralPath `
    '.\artifacts\m3-desktop\AgentBell.Desktop.exe').Path
& $m3Desktop
```

Expected output includes the loopback listener, exactly one private-LAN listener,
the fragment-only pairing URL and the QR path. In the first PowerShell:

```powershell
$m3DesktopProcess = Get-Process 'AgentBell.Desktop' |
    Sort-Object StartTime -Descending | Select-Object -First 1
if ($null -eq $m3DesktopProcess) { throw 'Desktop process was not found.' }

$m3Listeners = @(Get-NetTCPConnection -State Listen `
    -OwningProcess $m3DesktopProcess.Id -ErrorAction Stop)
$m3Listeners | Select-Object LocalAddress,LocalPort,OwningProcess |
    Sort-Object LocalPort | Format-Table

$m3Loopback = @($m3Listeners | Where-Object LocalPort -eq 17863)
if ($m3Loopback.Count -ne 1 -or $m3Loopback[0].LocalAddress -ne '127.0.0.1') {
    throw 'M1 listener is not exactly 127.0.0.1:17863.'
}
$m3Lan = @($m3Listeners | Where-Object {
    $_.LocalPort -ge 17864 -and $_.LocalPort -le 17874
})
if ($m3Lan.Count -ne 1 -or $m3Lan[0].LocalAddress -in @('0.0.0.0','::','[::]')) {
    throw 'Expected exactly one non-wildcard M2 LAN listener.'
}

Get-Item -LiteralPath $m3QrPath |
    Select-Object FullName,Length,LastWriteTime | Format-List
Start-Process -FilePath $m3QrPath
```

If Windows Firewall prompts, allow only **Private networks**. AgentBell does not
create a firewall rule.

## 6. Install, launch, and pair Android

With `adb`:

```powershell
adb install -r $m3Apk
adb shell am start -n com.hyatin.agentbell/.MainActivity
```

Without `adb`, copy `app-debug.apk` to the phone over USB or another trusted local
method, enable installation from that file manager when Android asks, install it,
then revoke that one-time installation permission if desired.

On first launch:

1. Connect phone and PC to the same trusted Wi-Fi.
2. Tap **扫码配对** and grant Camera permission only when requested.
3. Scan `agentbell-pairing.png`. The image is decoded locally and is not saved.
4. Confirm the app validates `/api/v1/status` before saving the credential.
5. Grant notification permission on Android 13+.
6. Enable **持续接收** if it is not already enabled.
7. Confirm the low-importance `AgentBell连接服务` foreground notification appears.
8. Confirm the app shows the computer name, masked host, port, protocol 1 and
   `Connected`.

The manual URL field is diagnostic fallback only. Never paste the URL into logs,
issues or public chat because its fragment contains the Token.

## 7. Prepare a PowerShell 5.1-safe unique Stop payload

PowerShell 5.1 must not use its BOM-producing default file encodings. The following
creates UTF-8 **without BOM** and sends exact stdin to the production Stop-Hook mode:

```powershell
$m3Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$m3PayloadObject = Get-Content -Raw -LiteralPath $m3Sample | ConvertFrom-Json
$m3PayloadObject.session_id = 'm3-session-' + [Guid]::NewGuid().ToString('N')
$m3PayloadObject.turn_id = 'm3-turn-' + [Guid]::NewGuid().ToString('N')
$m3PayloadObject.cwd = '<REPOSITORY_ROOT>'
$m3PayloadObject.last_assistant_message = 'M3 中文和 emoji 已完成 🔔'
$m3PayloadJson = $m3PayloadObject | ConvertTo-Json -Compress
$m3PayloadPath = Join-Path $env:TEMP 'agentbell-m3-stop.json'
[System.IO.File]::WriteAllText($m3PayloadPath, $m3PayloadJson, $m3Utf8NoBom)

$env:AGENTBELL_HOOK_DIAGNOSTICS = '1'
$env:AGENTBELL_HOOK_DIAGNOSTICS_PATH = $m3HookLog

function Invoke-M3AgentBellStopPayload {
    param([Parameter(Mandatory=$true)][string]$PayloadPath)

    $m3StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $m3StartInfo.FileName = $m3Hook
    $m3StartInfo.Arguments = '--codex-stop-hook'
    $m3StartInfo.UseShellExecute = $false
    $m3StartInfo.RedirectStandardInput = $true
    $m3StartInfo.RedirectStandardOutput = $true
    $m3StartInfo.RedirectStandardError = $true
    $m3StartInfo.CreateNoWindow = $true
    $m3StartInfo.StandardInputEncoding = $m3Utf8NoBom
    $m3Process = New-Object System.Diagnostics.Process
    $m3Process.StartInfo = $m3StartInfo
    [void]$m3Process.Start()
    $m3Json = [System.IO.File]::ReadAllText($PayloadPath, [Text.Encoding]::UTF8)
    $m3Process.StandardInput.Write($m3Json)
    $m3Process.StandardInput.Close()
    $m3Stdout = $m3Process.StandardOutput.ReadToEnd().TrimEnd("`r", "`n")
    $m3Stderr = $m3Process.StandardError.ReadToEnd()
    $m3Process.WaitForExit()
    [pscustomobject]@{
        ExitCode = $m3Process.ExitCode
        Stdout = $m3Stdout
        StderrLength = $m3Stderr.Length
    }
}

$m3BeforeCount = @((Get-Content -Raw -LiteralPath $m3EventsPath |
    ConvertFrom-Json).events).Count
$m3SendResult = Invoke-M3AgentBellStopPayload -PayloadPath $m3PayloadPath
$m3SendResult | Format-List
if ($m3SendResult.ExitCode -ne 0 -or
    $m3SendResult.Stdout -cne '{"continue":true}' -or
    $m3SendResult.StderrLength -ne 0) {
    throw 'Stop Hook contract failed.'
}
```

The phone should receive one high-importance `Codex任务完成` notification. Inspect
only sanitized Hook/Desktop diagnostics:

```powershell
Get-Content -LiteralPath $m3HookLog -Tail 5 |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,httpStatus,elapsedMs |
    Format-Table

Get-Content -LiteralPath $m3DesktopLog -Tail 20 |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.result -in @('broadcast_queued','success') } |
    Select-Object timestamp,eventType,result,sequence,activeConnections | Format-Table
```

Expected Hook result is `success` with HTTP 202; Desktop should include
`broadcast_queued`. Neither log may contain the Token, full IP, summary, raw JSON,
raw EventId, raw session ID or raw turn ID.

## 8. Foreground, background, and lock-screen acceptance

Use a fresh real Codex turn for every case so that every EventId is unique.

1. **Foreground:** leave AgentBell visible, complete a real Codex turn, verify a
   heads-up notification in about one second and one new history item.
2. **Background:** press Home or switch to another app, complete a real Codex turn
   without reopening AgentBell, and verify the top notification appears.
3. **Lock screen:** lock the phone, complete a real Codex turn, and verify the
   notification appears according to the phone's lock-screen privacy setting.
4. Tap a completion notification and verify AgentBell opens the matching event
   detail without displaying a raw identifier, path, Token or JSON.

The foreground service, not `MainActivity`, owns the WebSocket. Swiping the Activity
away must not create a second socket; OEM behavior should be recorded if the system
also explicitly kills the foreground service.

## 9. Duplicate EventId acceptance

Send the exact same file again twice:

```powershell
$m3DuplicateOne = Invoke-M3AgentBellStopPayload -PayloadPath $m3PayloadPath
$m3DuplicateTwo = Invoke-M3AgentBellStopPayload -PayloadPath $m3PayloadPath
$m3AfterCount = @((Get-Content -Raw -LiteralPath $m3EventsPath |
    ConvertFrom-Json).events).Count
[pscustomobject]@{
    Before = $m3BeforeCount
    After = $m3AfterCount
    Hook1 = $m3DuplicateOne.Stdout
    Hook2 = $m3DuplicateTwo.Stdout
} | Format-List
```

All Hook calls remain successful. Desktop and Android history retain the event once,
and Android must not show a second notification for the same EventId. Repeat after
force-closing and reopening the App to confirm the 100-ID persistent dedupe cache.

## 10. Network loss and resume replay

1. Note Android's latest sequence.
2. Turn off Wi-Fi on the phone. The UI should become `NoNetwork`; no rapid loop
   should occur.
3. While Wi-Fi is off, complete a fresh real Codex turn. Desktop retains it among
   its recent 100 events.
4. Turn Wi-Fi back on to the same trusted LAN.
5. Verify an immediate reconnect, `Connected`, and exactly one notification for the
   missed event.
6. Verify history is ordered by descending sequence and latest sequence advanced.

Desktop diagnostics should show a successful `resume` and replay count. Android
sends the saved watermark in `{"type":"resume","lastSequence":...}` after each
validated hello; replay and live events share the same EventId dedupe path.

## 11. Desktop restart recovery

1. Stop Desktop with Ctrl+C in its foreground PowerShell.
2. Confirm Android changes to `Reconnecting` and backs off through
   1, 2, 5, 10, then at most 30 seconds.
3. Start the same `m3-desktop` command again.
4. Confirm Android returns to `Connected`, sends resume, and does not duplicate
   prior notifications.

## 12. Controlled invalid-Token check

The automated MockWebServer test covers HTTP 403 as a terminal `Unauthorized` state
without retry. For a manual pairing-validation check, alter one character of the
Token in a copied pairing URL and paste it into the diagnostic field. Expected:
the stable `Unauthorized`/`unauthorized` state is shown; the invalid credential is
not saved and no WebSocket retry starts.

Do not edit Android DataStore, Windows `config.json`, the QR file, `hooks.json`, or
Codex `notify` merely to force this test. A live post-pairing Token-rotation test is
deferred unless a separately backed-up, disposable Windows profile is available.

## 13. Notification permission refusal

1. Open Android system notification settings from AgentBell and disable its
   notifications.
2. Complete a fresh Codex turn.
3. Verify Android history still increases and sequence advances.
4. Verify the UI says the event was received but notification permission is off.
5. Re-enable notifications for the remaining tests.

Permission refusal must never terminate the WebSocket or crash the service.

## 14. Xiaomi/Redmi background check

On Xiaomi/Redmi, follow the in-app guidance using public system settings:

1. Allow notifications.
2. Allow background activity.
3. Set battery policy to **Unrestricted**.
4. Enable autostart if the device exposes that option.
5. Lock the phone for 10 minutes, then complete a real Codex turn and record the
   delivery result and latency.

AgentBell does not use hidden OEM intents as its only settings path and cannot
guarantee that every firmware build will preserve a background process.

## 15. Cleanup

On Android, turn off **持续接收** and verify the foreground notification disappears.
Optionally uninstall the debug APK:

```powershell
adb uninstall com.hyatin.agentbell
```

Stop Desktop with Ctrl+C. Remove only the temporary test payload and process-local
diagnostic variables:

```powershell
Remove-Item -LiteralPath $m3PayloadPath -Force -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
```

Do not delete Windows pairing `config.json`, `events.json`, the trusted Stop Hook,
or the existing Codex `notify` configuration unless performing a separately agreed
full reset.

## Language verification for 0.6

Open AgentBell Settings > Language and test Follow system, English, and 简体中文.
Follow system uses Chinese only for an exact `zh-CN` device locale; `zh-TW`,
`zh-HK`, and every other unsupported locale must show English. Confirm scanner
instructions, accessibility description, connection states, validation errors,
foreground notification, notification channel name/description, completion
notification, and event details. Switch language while connected and confirm the
WebSocket remains connected and pairing data remains intact. Restart the app and
confirm the selection persists.
