# M2 manual test: private-LAN pairing and WebSocket

This procedure validates only M2:

```text
Codex Stop Hook
  -> AgentBell.Hook.exe
  -> http://127.0.0.1:17863
  -> AgentBell.Desktop.exe
  -> one RFC1918 IPv4 address on port 17864-17874
  -> authenticated WebSocket
  -> Android Chrome pairing page
```

M2 uses ordinary HTTP and WS. It is not end-to-end encrypted and must be used only on a trusted private LAN. AgentBell creates no firewall rule. If Windows Firewall asks, allow the Desktop executable only on **Private networks**, never Public networks. A browser page can receive in real time only while it is open and Android has not frozen it; reliable lock-screen notification belongs to M3 and is not part of this test.

Do not edit the current user's `.codex\hooks.json`, its trusted command, `config.toml`, or the existing Codex `notify` command.

## 1. Restore, Release build, and run every test

From the repository root:

```powershell
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
```

## 2. Publish Hook and Desktop

Publish Hook to the already trusted path without changing `hooks.json`:

```powershell
dotnet publish .\src\AgentBell.Hook\AgentBell.Hook.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m0-hook

dotnet publish .\src\AgentBell.Desktop\AgentBell.Desktop.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m2-desktop

$m2Hook = (Resolve-Path -LiteralPath '.\artifacts\m0-hook\AgentBell.Hook.exe').Path
$m2Desktop = (Resolve-Path -LiteralPath '.\artifacts\m2-desktop\AgentBell.Desktop.exe').Path
$m2Sample = (Resolve-Path -LiteralPath '.\docs\CODEX_STOP_HOOK_SAMPLE.json').Path
$m2LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$m2EventsPath = Join-Path $m2LocalAppData 'AgentBell\events.json'
$m2ConfigPath = Join-Path $m2LocalAppData 'AgentBell\config.json'
$m2QrPath = Join-Path $m2LocalAppData 'AgentBell\pairing\agentbell-pairing.png'
$m2HookLog = Join-Path $m2LocalAppData 'AgentBell\logs\m2-hook.ndjson'
$m2DesktopLog = Join-Path $m2LocalAppData 'AgentBell\logs\m2-desktop.ndjson'
$m2RealHookLog = [Environment]::GetEnvironmentVariable(
    'AGENTBELL_HOOK_DIAGNOSTICS_PATH', 'User')
if ([string]::IsNullOrWhiteSpace($m2RealHookLog)) {
    $m2RealHookLog = Join-Path $m2LocalAppData 'AgentBell\logs\m0-hook.ndjson'
}
```

## 3. Start Desktop in a foreground PowerShell

Open a second Windows PowerShell window at the repository root:

```powershell
$env:AGENTBELL_DESKTOP_DIAGNOSTICS = '1'
$m2LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH = Join-Path $m2LocalAppData 'AgentBell\logs\m2-desktop.ndjson'
$m2Desktop = (Resolve-Path -LiteralPath '.\artifacts\m2-desktop\AgentBell.Desktop.exe').Path
& $m2Desktop
```

Expected startup output includes:

```text
M1 listener: http://127.0.0.1:17863
LAN status: Available (<PRIVATE_IPV4>:<PORT>)
Pairing URL (contains credential; do not share publicly):
http://<PRIVATE_IPV4>:<PORT>/pair#token=<TOKEN>&device=<REDACTED>&v=1
Pairing QR: <LOCAL_APP_DATA>\AgentBell\pairing\agentbell-pairing.png
```

The pairing URL and QR contain the credential. Show them only for explicit pairing and do not paste them into logs, tickets, or public chat. If no valid private IPv4 exists, expect `LAN status: Unavailable`; the M1 listener must remain active.

## 4. Verify both listeners and forbidden wildcard bindings

In the first PowerShell window:

```powershell
$m2DesktopProcess = Get-Process 'AgentBell.Desktop' |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if ($null -eq $m2DesktopProcess) { throw 'Desktop process was not found.' }

$m2Listeners = @(
    Get-NetTCPConnection -State Listen -OwningProcess $m2DesktopProcess.Id -ErrorAction Stop
)
$m2Listeners |
    Select-Object LocalAddress,LocalPort,OwningProcess |
    Sort-Object LocalPort |
    Format-Table

$m2HookListener = @($m2Listeners | Where-Object LocalPort -eq 17863)
if ($m2HookListener.Count -ne 1 -or $m2HookListener[0].LocalAddress -ne '127.0.0.1') {
    throw 'Hook listener is not exactly 127.0.0.1:17863.'
}

$m2LanListener = @($m2Listeners | Where-Object {
    $_.LocalPort -ge 17864 -and $_.LocalPort -le 17874
})
if ($m2LanListener.Count -ne 1) { throw 'Expected exactly one M2 LAN listener.' }
if ($m2LanListener[0].LocalAddress -in @('0.0.0.0', '::', '[::]')) {
    throw 'Wildcard LAN binding is forbidden.'
}

$m2LanAddress = [Net.IPAddress]::Parse($m2LanListener[0].LocalAddress)
$m2Bytes = $m2LanAddress.GetAddressBytes()
$m2IsPrivate = $m2Bytes.Length -eq 4 -and (
    $m2Bytes[0] -eq 10 -or
    ($m2Bytes[0] -eq 172 -and $m2Bytes[1] -ge 16 -and $m2Bytes[1] -le 31) -or
    ($m2Bytes[0] -eq 192 -and $m2Bytes[1] -eq 168)
)
if (-not $m2IsPrivate) { throw 'LAN address is not RFC1918 private IPv4.' }

$m2LanPort = $m2LanListener[0].LocalPort
$m2LanOrigin = 'http://{0}:{1}' -f $m2LanAddress, $m2LanPort
```

Expected: exactly one loopback listener on 17863 and one RFC1918 listener on the actual LAN port. There must be no `0.0.0.0`, IPv6 wildcard, public, or second-interface listener.

## 5. Obtain the explicit pairing output and inspect the QR file

Copy the pairing URL from the Desktop foreground window into a hidden prompt variable:

```powershell
$m2PairingUrl = Read-Host 'Paste the pairing URL shown by Desktop'
$m2PairingUri = [Uri]$m2PairingUrl
Add-Type -AssemblyName System.Web
$m2Fragment = [System.Web.HttpUtility]::ParseQueryString(
    $m2PairingUri.Fragment.TrimStart('#'))
$m2Token = $m2Fragment['token']
if ([string]::IsNullOrWhiteSpace($m2Token)) { throw 'Pairing token was not found in fragment.' }
if ($m2PairingUri.Query) { throw 'Pairing token must not be in the HTTP query.' }

Get-Item -LiteralPath $m2QrPath |
    Select-Object FullName,Length,LastWriteTime |
    Format-List
```

The initial HTTP portion must be exactly `http://<PRIVATE_IPV4>:<PORT>/pair`; the Token appears only after `#`.

Check the unauthenticated health response and authenticated status response:

```powershell
$m2Health = Invoke-WebRequest -UseBasicParsing -Uri "$m2LanOrigin/health"
if ($m2Health.Content -cne '{"status":"ok"}') { throw 'Health response leaked extra state.' }

$m2Status = Invoke-RestMethod -Uri "$m2LanOrigin/api/v1/status" -Headers @{
    Authorization = "Bearer $m2Token"
}
$m2Status | Select-Object protocolVersion,serverVersion,deviceName,deviceId,
    lanAddress,lanPort,webSocketPath,latestSequence,eventCount | Format-List
```

The response must not contain the Token, paths, logs, or encrypted configuration fields.

## 6. Pair Android Chrome on the same trusted Wi-Fi

1. Connect the phone and PC to the same trusted Wi-Fi/private LAN.
2. Scan the QR under the Windows LocalAppData Known Folder at `AgentBell\pairing\agentbell-pairing.png`, or open the explicit pairing URL.
3. Confirm the page says `Connected` and shows the computer name and protocol version 1.
4. Keep the page open for the real-time tests.

If Windows Firewall prompts, allow only Private networks. Do not enable Public networks and do not add a manual broad firewall rule.

## 7. Define a PowerShell 5.1-safe Hook sender

This sends exact UTF-8 bytes through stdin. It does not place JSON on the native command line:

```powershell
$env:AGENTBELL_HOOK_DIAGNOSTICS = '1'
$env:AGENTBELL_HOOK_DIAGNOSTICS_PATH = $m2HookLog

function Invoke-M2AgentBellPayload {
    param([Parameter(Mandatory=$true)][string]$PayloadPath)

    $m2StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $m2StartInfo.FileName = $m2Hook
    $m2StartInfo.Arguments = '--codex-stop-hook'
    $m2StartInfo.UseShellExecute = $false
    $m2StartInfo.RedirectStandardInput = $true
    $m2StartInfo.RedirectStandardOutput = $true
    $m2StartInfo.RedirectStandardError = $true
    $m2StartInfo.CreateNoWindow = $true

    $m2Process = New-Object System.Diagnostics.Process
    $m2Process.StartInfo = $m2StartInfo
    if (-not $m2Process.Start()) { throw 'Failed to start AgentBell.Hook.' }

    $m2PayloadBytes = [IO.File]::ReadAllBytes($PayloadPath)
    $m2Process.StandardInput.BaseStream.Write($m2PayloadBytes, 0, $m2PayloadBytes.Length)
    $m2Process.StandardInput.BaseStream.Flush()
    $m2Process.StandardInput.Close()
    $m2Stdout = $m2Process.StandardOutput.ReadToEnd()
    $m2Stderr = $m2Process.StandardError.ReadToEnd()
    $m2Process.WaitForExit()

    [pscustomobject]@{
        ExitCode      = $m2Process.ExitCode
        Stdout        = $m2Stdout
        StdoutIsExact = $m2Stdout -ceq '{"continue":true}'
        StderrChars   = $m2Stderr.Length
    }
}

function Get-M2StoredEvents {
    if (-not (Test-Path -LiteralPath $m2EventsPath)) { return @() }
    $m2Parsed = Get-Content -Raw -LiteralPath $m2EventsPath |
        ConvertFrom-Json -ErrorAction Stop
    if ($m2Parsed -is [Array]) { return @($m2Parsed) }
    if ($m2Parsed.PSObject.Properties.Name -contains 'events') {
        return @($m2Parsed.events)
    }
    return @()
}
```

## 8. Send the local sample and verify immediate browser delivery

```powershell
$m2LocalStart = [DateTimeOffset]::Now
$m2BeforeCount = @(Get-M2StoredEvents).Count
$m2LocalResult = Invoke-M2AgentBellPayload -PayloadPath $m2Sample
$m2LocalResult | Format-List

$m2LastHook = Get-Content -LiteralPath $m2HookLog -Last 1 |
    ConvertFrom-Json -ErrorAction Stop
$m2LastHook |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,httpStatus,elapsedMs |
    Format-List

if ($m2LocalResult.ExitCode -ne 0 -or
    -not $m2LocalResult.StdoutIsExact -or
    $m2LocalResult.StderrChars -ne 0) {
    throw 'Stop Hook protocol changed.'
}
if ($m2LastHook.result -ne 'success' -or $m2LastHook.httpStatus -ne 202) {
    throw 'M1 Hook forwarding did not remain success/202.'
}

$m2AfterEvents = @(Get-M2StoredEvents)
[pscustomobject]@{
    Before = $m2BeforeCount
    After  = $m2AfterEvents.Count
    Delta  = $m2AfterEvents.Count - $m2BeforeCount
} | Format-List
```

The Android Chrome page should immediately show the title, project, truncated summary, time, and sequence. If this exact deterministic sample already exists, it is a duplicate and will not display again; use the unique payload in step 12.

## 9. Measure Hook, Desktop broadcast, and browser receipt times

Desktop diagnostics contain no event body or Token. Find broadcasts inside the test window:

```powershell
$m2LocalEnd = [DateTimeOffset]::Now
$m2Broadcasts = @(
    Get-Content -LiteralPath $m2DesktopLog | ForEach-Object {
        try {
            $m2Diagnostic = $_ | ConvertFrom-Json -ErrorAction Stop
            $m2Timestamp = [DateTimeOffset]::Parse([string]$m2Diagnostic.timestamp)
            if ($m2Diagnostic.result -eq 'broadcast_queued' -and
                $m2Timestamp -ge $m2LocalStart -and
                $m2Timestamp -le $m2LocalEnd) {
                $m2Diagnostic
            }
        } catch { }
    }
)
$m2Broadcasts |
    Select-Object timestamp,sequence,result,activeConnections |
    Format-Table
```

Use these three points:

- Hook invocation/diagnostic time: `$m2LocalStart` and `$m2LastHook.timestamp`.
- Desktop queue time: the matching `broadcast_queued.timestamp`.
- Browser receive time: displayed on the event card as `浏览器收到 <ISO timestamp>`.

On a stable private LAN, the intended end-to-end result is approximately one second or less.

## 10. Complete one real Codex turn

Keep Desktop and the phone page open. Record a window, complete one real turn in the already trusted Windows Codex client, then inspect only sanitized data:

```powershell
$m2RealStart = [DateTimeOffset]::Now
# Complete one new Codex turn now.
$m2RealEnd = [DateTimeOffset]::Now

$m2RealHookEvents = @(
    Get-Content -LiteralPath $m2RealHookLog | ForEach-Object {
        try {
            $m2Event = $_ | ConvertFrom-Json -ErrorAction Stop
            $m2Timestamp = [DateTimeOffset]::Parse([string]$m2Event.timestamp)
            if ($m2Event.eventType -eq 'codex-stop' -and
                $m2Timestamp -ge $m2RealStart -and
                $m2Timestamp -le $m2RealEnd) {
                $m2Event
            }
        } catch { }
    }
)
$m2RealHookEvents |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,httpStatus,elapsedMs |
    Format-Table

@(Get-M2StoredEvents) |
    Sort-Object sequence -Descending |
    Select-Object -First 1 eventId,agent,status,title,project,summary,
        threadIdHash,turnIdHash,occurredAt,sequence |
    Format-List
```

Acceptance: Hook reports `success`/202, `events.json` gains the new sequence, and the phone page shows that same sanitized event.

## 11. Verify wrong credentials fail

```powershell
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$m2LanOrigin/api/v1/status" -Headers @{
        Authorization = 'Bearer <REDACTED>'
    } -ErrorAction Stop | Out-Null
    throw 'Wrong Token unexpectedly succeeded.'
} catch {
    $m2StatusCode = [int]$_.Exception.Response.StatusCode
    if ($m2StatusCode -ne 403) { throw }
    Write-Host 'Wrong status Token rejected with 403.'
}
```

For WebSocket, edit only the fragment in a temporary browser tab to use an incorrect Token. The page must not reach `Connected`. Do not put a real Token in an HTTP query parameter.

## 12. Verify duplicate suppression with a unique UTF-8-no-BOM payload

Windows PowerShell 5.1 must use an explicit UTF-8 encoding without BOM:

```powershell
$m2Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$m2UniquePayloadPath = Join-Path $env:TEMP 'agentbell-m2-unique-stop.json'
$m2UniquePayload = Get-Content -Raw -LiteralPath $m2Sample |
    ConvertFrom-Json -ErrorAction Stop
$m2UniquePayload.turn_id = 'm2-manual-' + [Guid]::NewGuid().ToString('N')
$m2UniqueJson = $m2UniquePayload | ConvertTo-Json -Depth 10 -Compress
[IO.File]::WriteAllText($m2UniquePayloadPath, $m2UniqueJson, $m2Utf8NoBom)

$m2BeforeUnique = @(Get-M2StoredEvents).Count
Invoke-M2AgentBellPayload -PayloadPath $m2UniquePayloadPath | Format-List
$m2AfterFirstUnique = @(Get-M2StoredEvents).Count
Invoke-M2AgentBellPayload -PayloadPath $m2UniquePayloadPath | Format-List
$m2AfterDuplicate = @(Get-M2StoredEvents).Count

[pscustomobject]@{
    Before          = $m2BeforeUnique
    AfterFirst      = $m2AfterFirstUnique
    AfterDuplicate  = $m2AfterDuplicate
    FirstAddedOne   = $m2AfterFirstUnique -eq ($m2BeforeUnique + 1)
    DuplicateNoGrow = $m2AfterDuplicate -eq $m2AfterFirstUnique
} | Format-List
```

The phone page displays the unique event once. Both Hook calls still return HTTP 202 and exact `{"continue":true}`.

## 13. Verify offline resume

1. Record the last sequence shown on the phone.
2. Close the page or turn off phone Wi-Fi.
3. Change `turn_id` again and write a new UTF-8-no-BOM payload:

```powershell
$m2OfflinePayloadPath = Join-Path $env:TEMP 'agentbell-m2-offline-stop.json'
$m2OfflinePayload = Get-Content -Raw -LiteralPath $m2Sample |
    ConvertFrom-Json -ErrorAction Stop
$m2OfflinePayload.turn_id = 'm2-offline-' + [Guid]::NewGuid().ToString('N')
$m2OfflineJson = $m2OfflinePayload | ConvertTo-Json -Depth 10 -Compress
[IO.File]::WriteAllText($m2OfflinePayloadPath, $m2OfflineJson, $m2Utf8NoBom)
Invoke-M2AgentBellPayload -PayloadPath $m2OfflinePayloadPath | Format-List
```

4. Re-enable Wi-Fi or reopen the same fragment pairing URL.
5. The page reads `lastSequence` from localStorage, sends `resume`, and displays the missed event once in ascending sequence order.

Only the most recent 100 locally retained events can be replayed. An accepted event whose disk persistence failed can still be delivered live, but it cannot be recovered after a Desktop restart.

## 14. Stop Desktop normally and clean only test process state

Return to the foreground Desktop PowerShell and press `Ctrl+C`. Verify both listeners disappeared:

```powershell
Start-Sleep -Milliseconds 300
Get-NetTCPConnection -State Listen -OwningProcess $m2DesktopProcess.Id -ErrorAction SilentlyContinue
```

Clean only temporary payloads and process-level variables:

```powershell
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $m2UniquePayloadPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $m2OfflinePayloadPath -Force -ErrorAction SilentlyContinue
$m2Token = $null
$m2PairingUrl = $null
```

Do not delete or rewrite `hooks.json`, `config.toml`, the existing `notify`, `events.json`, or `config.json` as part of this test.
