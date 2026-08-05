# M1 manual test: Windows local bridge

This procedure validates only the local M1 chain:

```text
Codex Stop Hook
  -> AgentBell.Hook.exe
  -> POST http://127.0.0.1:17863/api/v1/events/codex
  -> AgentBell.Desktop.exe
  -> sanitized conversion, deduplication, and events.json
```

M2 LAN listening, WebSocket, Android, pairing, QR codes, firewall rules, an installer, a Windows service, startup registration, Claude, and other agents are not part of M1.

## Safety and fixed contracts

- Do not edit the current user's `.codex\config.toml` or its existing Codex `notify` command.
- Do not edit the current user's `.codex\hooks.json`. The trusted AgentBell command remains unchanged.
- Desktop listens only on `127.0.0.1:17863`; `0.0.0.0`, LAN addresses, and IPv6 wildcard addresses are forbidden.
- M1 creates no firewall rule.
- The Hook sends `POST /api/v1/events/codex`, `Content-Type: application/json; charset=utf-8`, and the validated Stop JSON body.
- The request limit is 1 MiB.
- The Hook still exits `0` and emits exactly `{"continue":true}` in Stop mode.
- Hook forwarding success is recorded as `result = success` with `httpStatus = 202`.
- `events.json` never contains raw JSON, prompts, a complete assistant reply, a complete `cwd`, or raw session/turn identifiers.

Stable Desktop HTTP behavior:

| Request | Status |
|---|---:|
| New valid Stop event | 202 |
| Duplicate valid Stop event | 202 |
| Non-Stop or missing `hook_event_name` | 204 |
| Empty, malformed, or type-invalid JSON | 400 |
| Body over 1 MiB | 413 |
| Non-JSON Content-Type | 415 |
| Persistence failure after acceptance | 202 |

## 1. Restore, build, and test

From the repository root:

```powershell
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
```

## 2. Publish Hook and Desktop

Publish the Hook to the existing trusted command path. Do not change the Hook command in `hooks.json`:

```powershell
dotnet publish .\src\AgentBell.Hook\AgentBell.Hook.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m0-hook

dotnet publish .\src\AgentBell.Desktop\AgentBell.Desktop.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\artifacts\m1-desktop

$m1Hook = (Resolve-Path -LiteralPath '.\artifacts\m0-hook\AgentBell.Hook.exe').Path
$m1Desktop = (Resolve-Path -LiteralPath '.\artifacts\m1-desktop\AgentBell.Desktop.exe').Path
$m1Sample = (Resolve-Path -LiteralPath '.\docs\CODEX_STOP_HOOK_SAMPLE.json').Path
$m1LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$m1Events = Join-Path $m1LocalAppData 'AgentBell\events.json'
$m1HookLog = Join-Path $m1LocalAppData 'AgentBell\logs\m1-hook.ndjson'
$m1DesktopLog = Join-Path $m1LocalAppData 'AgentBell\logs\m1-desktop.ndjson'
$m1RealHookLog = [Environment]::GetEnvironmentVariable(
    'AGENTBELL_HOOK_DIAGNOSTICS_PATH', 'User')
if ([string]::IsNullOrWhiteSpace($m1RealHookLog)) {
    $m1RealHookLog = Join-Path $m1LocalAppData 'AgentBell\logs\m0-hook.ndjson'
}
```

Windows PowerShell 5.1 must not create test JSON with its BOM-producing default encodings. If a JSON copy is ever needed, write it explicitly as UTF-8 without BOM:

```powershell
$m1Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$m1Json = [System.IO.File]::ReadAllText($m1Sample, [System.Text.Encoding]::UTF8)
$m1JsonCopy = Join-Path $env:TEMP 'agentbell-m1-payload.json'
[System.IO.File]::WriteAllText($m1JsonCopy, $m1Json, $m1Utf8NoBom)
```

The remaining steps read the checked-in sample bytes directly and do not need that copy.

## 3. Start Desktop in a foreground PowerShell

Open a second PowerShell window at the repository root. Enable only sanitized Desktop diagnostics for that process, then run Desktop in the foreground:

```powershell
$env:AGENTBELL_DESKTOP_DIAGNOSTICS = '1'
$m1LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH = Join-Path $m1LocalAppData 'AgentBell\logs\m1-desktop.ndjson'
$m1Desktop = (Resolve-Path -LiteralPath '.\artifacts\m1-desktop\AgentBell.Desktop.exe').Path
& $m1Desktop
```

Leave this window open. The M1 console is intentionally minimal and may print nothing.

In the first PowerShell window, verify the listener:

```powershell
$m1Listener = Get-NetTCPConnection -State Listen -LocalPort 17863 -ErrorAction Stop
$m1Listener | Select-Object LocalAddress,LocalPort,OwningProcess | Format-Table

if (@($m1Listener | Where-Object LocalAddress -ne '127.0.0.1').Count -ne 0) {
    throw 'M1 listener is not restricted to 127.0.0.1.'
}
```

Expected: the only local address is `127.0.0.1` and the port is `17863`.

## 4. Define the PowerShell 5.1-safe local Hook test

This helper sends the sample's exact UTF-8 bytes through stdin and captures the complete Hook protocol output:

```powershell
$env:AGENTBELL_HOOK_DIAGNOSTICS = '1'
$env:AGENTBELL_HOOK_DIAGNOSTICS_PATH = $m1HookLog

function Invoke-M1AgentBellSample {
    $m1StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $m1StartInfo.FileName = $m1Hook
    $m1StartInfo.Arguments = '--codex-stop-hook'
    $m1StartInfo.UseShellExecute = $false
    $m1StartInfo.RedirectStandardInput = $true
    $m1StartInfo.RedirectStandardOutput = $true
    $m1StartInfo.RedirectStandardError = $true
    $m1StartInfo.CreateNoWindow = $true

    $m1Process = New-Object System.Diagnostics.Process
    $m1Process.StartInfo = $m1StartInfo
    if (-not $m1Process.Start()) { throw 'Failed to start AgentBell.Hook.' }

    $m1Bytes = [System.IO.File]::ReadAllBytes($m1Sample)
    $m1Process.StandardInput.BaseStream.Write($m1Bytes, 0, $m1Bytes.Length)
    $m1Process.StandardInput.BaseStream.Flush()
    $m1Process.StandardInput.Close()
    $m1Stdout = $m1Process.StandardOutput.ReadToEnd()
    $m1Stderr = $m1Process.StandardError.ReadToEnd()
    $m1Process.WaitForExit()

    [pscustomobject]@{
        ExitCode       = $m1Process.ExitCode
        Stdout         = $m1Stdout
        StdoutIsExact  = $m1Stdout -ceq '{"continue":true}'
        StderrChars    = $m1Stderr.Length
    }
}
```

## 5. Send the local sample and inspect sanitized results

Record the current event count, invoke the sample, and verify Hook success:

```powershell
$m1BeforeSampleCount = if (Test-Path -LiteralPath $m1Events) {
    @((Get-Content -Raw -LiteralPath $m1Events | ConvertFrom-Json)).Count
} else { 0 }

$m1SampleResult = Invoke-M1AgentBellSample
$m1SampleResult | Format-List

$m1LastHookEvent = Get-Content -LiteralPath $m1HookLog -Last 1 |
    ConvertFrom-Json -ErrorAction Stop
$m1LastHookEvent |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,httpStatus,elapsedMs |
    Format-List

if ($m1SampleResult.ExitCode -ne 0 -or
    -not $m1SampleResult.StdoutIsExact -or
    $m1SampleResult.StderrChars -ne 0) {
    throw 'Hook protocol validation failed.'
}

if ($m1LastHookEvent.result -ne 'success' -or $m1LastHookEvent.httpStatus -ne 202) {
    throw 'Hook did not receive HTTP 202 from Desktop.'
}
```

Inspect `events.json` without displaying any raw source payload:

```powershell
$m1StoredEvents = @(
    Get-Content -Raw -LiteralPath $m1Events |
        ConvertFrom-Json -ErrorAction Stop
)

$m1StoredEvents |
    Select-Object eventId,agent,status,title,project,summary,
        threadIdHash,turnIdHash,occurredAt,sequence |
    Format-Table -Wrap

$m1AfterSampleCount = $m1StoredEvents.Count
Write-Host "Before sample: $m1BeforeSampleCount; after sample: $m1AfterSampleCount"
```

If the checked-in sample had never been accepted by this event history, the count increases by one. If it was already present, it remains unchanged because the EventId is deterministic.

Optional sanitized Desktop diagnostics:

```powershell
Get-Content -LiteralPath $m1DesktopLog -Last 5 |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,duplicate,
        httpStatus,elapsedMs,persistenceSucceeded,eventCount |
    Format-Table
```

## 6. Verify duplicate suppression

Capture the count, submit the exact same sample twice, and confirm the file count does not increase:

```powershell
$m1BeforeDuplicateCount = @(
    Get-Content -Raw -LiteralPath $m1Events | ConvertFrom-Json
).Count

Invoke-M1AgentBellSample | Format-List
Invoke-M1AgentBellSample | Format-List

$m1AfterDuplicateEvents = @(
    Get-Content -Raw -LiteralPath $m1Events | ConvertFrom-Json
)
$m1AfterDuplicateCount = $m1AfterDuplicateEvents.Count

[pscustomobject]@{
    BeforeDuplicate = $m1BeforeDuplicateCount
    AfterDuplicate  = $m1AfterDuplicateCount
    DidNotGrow      = $m1BeforeDuplicateCount -eq $m1AfterDuplicateCount
} | Format-List
```

Expected: `DidNotGrow = True`, while both Hook invocations still receive HTTP 202 and output `{"continue":true}`.

## 7. Complete one real Codex turn

Keep Desktop running. Record the event count and current time:

```powershell
$m1RealStart = [DateTimeOffset]::Now
$m1BeforeRealCount = @(
    Get-Content -Raw -LiteralPath $m1Events | ConvertFrom-Json
).Count
$m1RealStart
```

In the already trusted Windows Codex client, complete one new real turn. Do not edit `hooks.json` or `config.toml`. After the turn completes:

```powershell
$m1RealEnd = [DateTimeOffset]::Now
$m1RealHookEvents = @(
    Get-Content -LiteralPath $m1RealHookLog | ForEach-Object {
        try {
            $m1Event = $_ | ConvertFrom-Json -ErrorAction Stop
            $m1Timestamp = [DateTimeOffset]::Parse([string]$m1Event.timestamp)
            if ($m1Event.eventType -eq 'codex-stop' -and
                $m1Timestamp -ge $m1RealStart -and
                $m1Timestamp -le $m1RealEnd) {
                $m1Event
            }
        } catch { }
    }
)

$m1RealHookEvents |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,httpStatus,elapsedMs |
    Format-Table

$m1AfterRealEvents = @(
    Get-Content -Raw -LiteralPath $m1Events | ConvertFrom-Json
)

[pscustomobject]@{
    BeforeRealTurn = $m1BeforeRealCount
    AfterRealTurn  = $m1AfterRealEvents.Count
    AddedEvents    = $m1AfterRealEvents.Count - $m1BeforeRealCount
} | Format-List

$m1AfterRealEvents |
    Sort-Object sequence -Descending |
    Select-Object -First 1 eventId,agent,status,title,project,summary,
        threadIdHash,turnIdHash,occurredAt,sequence |
    Format-List
```

Acceptance:

- At least one Hook record inside the real-turn window has `result = success` and `httpStatus = 202`, not `forward_timeout`.
- `AddedEvents` is `1` for a new turn.
- The newest event has increasing `sequence` and non-empty identifier hashes when Codex supplied IDs.
- Neither Hook diagnostics nor `events.json` contains raw JSON, complete paths, raw IDs, prompts, or a complete assistant reply.

The deferred 20-turn stability test is not required to start M1. Run the complete end-to-end repetition test after the later milestones requested by the project owner.

## 8. Stop Desktop normally

Return to the foreground Desktop PowerShell and press `Ctrl+C`. Wait for the process to exit. Verify the listener is gone:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 17863 -ErrorAction SilentlyContinue
```

Remove only the process-level diagnostic variables used by these PowerShell windows:

```powershell
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_DESKTOP_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
if ($m1JsonCopy -and (Test-Path -LiteralPath $m1JsonCopy)) {
    Remove-Item -LiteralPath $m1JsonCopy -Force
}
```

Do not remove or rewrite the already trusted Hook definition, and do not touch the existing Codex `notify` configuration.
