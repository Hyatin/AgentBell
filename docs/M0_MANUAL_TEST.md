# M0 manual test: Codex user-level Stop Hook

This procedure validates only the M0 AgentBell Hook. The primary integration is a user-level Codex `Stop` Hook. The legacy Codex `notify` command-line JSON mode and the manual `--payload-file` mode remain compatibility/test capabilities, but they are not used to install AgentBell.

AgentBell Desktop, WebSocket, Android, the installer, Claude support, and a formal plugin wrapper are not part of this test.

## Safety and privacy

- Do not edit the current user's `.codex\config.toml` for this integration.
- In particular, do not modify, remove, replace, or take over the existing Codex Windows client `notify` entry that invokes the versioned `codex-computer-use.exe` runtime.
- Do not chain that `notify` entry. Its versioned runtime path can change when Codex updates.
- The Hook never edits Codex configuration or `hooks.json`; the steps below are manual.
- Back up the current user's `.codex\hooks.json` before changing it.
- If `hooks.json` already exists, preserve every existing top-level property, event, matcher group, and command Hook. Append only the AgentBell `Stop` Hook shown below.
- Diagnostics are disabled by default. When enabled, they contain only allowlisted metadata: event type, deterministic identifier hashes, field-presence booleans, forwarding result/status, timestamp, and elapsed time.
- The Hook does not record raw JSON, `input-messages`, the assistant-message body, the complete `cwd`, or complete session/thread/turn identifiers.
- `threadIdHash` and `turnIdHash` are the first 6 bytes of SHA-256 over the UTF-8 identifier, encoded as 12 lowercase hexadecimal characters. There is no per-process random salt, so hashes for the same identifier are comparable across Hook processes without recording the original identifier.
- Stop Hook input is bounded to 1 MiB and is never used to read `transcript_path`.
- The Hook always exits with code `0`. Only `--codex-stop-hook` mode writes stdout, and it writes exactly `{"continue":true}` with no other text. Invalid input and forwarding errors can appear only as stable codes in the enabled, sanitized diagnostic log. Stderr is empty by default.

## 1. Restore, build, test, and publish

Run from the repository root in PowerShell:

```powershell
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
dotnet publish .\src\AgentBell.Hook\AgentBell.Hook.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\m0-hook
```

The Stop Hook command is:

```text
<REPOSITORY_ROOT>\artifacts\m0-hook\AgentBell.Hook.exe --codex-stop-hook
```

Resolve the actual executable path before editing `hooks.json`:

```powershell
$m0Hook = (Resolve-Path -LiteralPath '.\artifacts\m0-hook\AgentBell.Hook.exe').Path
$m0Hook
```

## 2. Back up and inspect the user Hook file

Completely exit Codex first, including every background or system-tray process. Confirm in Task Manager that no Codex process remains.

This M0 test uses only the current user's Hook file:

```powershell
$m0Hooks = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.codex\hooks.json'
$m0HooksDirectory = Split-Path -Parent $m0Hooks
New-Item -ItemType Directory -Path $m0HooksDirectory -Force | Out-Null
$m0HooksExisted = Test-Path -LiteralPath $m0Hooks
$m0HooksBackup = "$m0Hooks.m0-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

if ($m0HooksExisted) {
    Copy-Item -LiteralPath $m0Hooks -Destination $m0HooksBackup
    Get-Content -Raw -LiteralPath $m0Hooks |
        ConvertFrom-Json -ErrorAction Stop | Out-Null
}
```

If parsing fails, restore nothing automatically and do not edit the file. Investigate the existing file first.

## 3. Safely add only the AgentBell Stop Hook

If `hooks.json` does not exist, create it with this complete, strict JSON document:

```json
{
  "description": "User-level Codex hooks including AgentBell M0.",
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\To\\AgentBell.Hook.exe --codex-stop-hook",
            "timeout": 3
          }
        ]
      }
    ]
  }
}
```

If `hooks.json` already exists, do not replace it with that example. In a JSON-aware editor, append the following matcher group to the existing `hooks.Stop` array, preserving all existing content:

```json
{
  "hooks": [
    {
      "type": "command",
      "command": "C:\\Path\\To\\AgentBell.Hook.exe --codex-stop-hook",
      "timeout": 3
    }
  ]
}
```

If `hooks.Stop` does not exist, add a `Stop` array under the existing `hooks` object and place that matcher group in it. Do not add a `matcher` for `Stop`. Do not add a second copy if the exact AgentBell command is already present.

Validate the completed file without printing its contents:

```powershell
$m0HooksDocument = Get-Content -Raw -LiteralPath $m0Hooks |
    ConvertFrom-Json -ErrorAction Stop

$m0AgentBellCommands = @(
    $m0HooksDocument.hooks.Stop |
        ForEach-Object { $_.hooks } |
        Where-Object {
            $_.type -eq 'command' -and
            $_.command -eq "$m0Hook --codex-stop-hook"
        }
)

if ($m0AgentBellCommands.Count -ne 1) {
    throw "Expected exactly one AgentBell Stop Hook; found $($m0AgentBellCommands.Count)."
}
```

## 4. Enable sanitized diagnostics and restart Codex

Set the diagnostic log path at user scope:

```powershell
$m0LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$m0Log = Join-Path $m0LocalAppData 'AgentBell\logs\m0-hook.ndjson'
[Environment]::SetEnvironmentVariable('AGENTBELL_HOOK_DIAGNOSTICS', '1', 'User')
[Environment]::SetEnvironmentVariable('AGENTBELL_HOOK_DIAGNOSTICS_PATH', $m0Log, 'User')
```

After setting user environment variables, completely exit Codex again. Closing only the visible window is not enough: exit background and system-tray processes and verify in Task Manager that no Codex process remains. Only a newly started process inherits the user variables.

On the first start after adding a non-managed Hook, review the Codex Hook trust prompt. Approve only the user-level Hook whose exact command is `$m0Hook --codex-stop-hook`. If the client exposes the `/hooks` command, use it to inspect the merged Hook sources and approve this exact definition. Do not bypass the review, and review it again whenever its definition changes. Multiple Hook sources remain independent, and matching command Hooks may run concurrently.

## 5. Direct UTF-8 stdin smoke test

The sample is `docs\CODEX_STOP_HOOK_SAMPLE.json`. Validate it, then send its exact UTF-8 bytes to stdin. This works in Windows PowerShell 5.1 without placing quote-heavy JSON on a native command line:

```powershell
$env:AGENTBELL_HOOK_DIAGNOSTICS = '1'
$env:AGENTBELL_HOOK_DIAGNOSTICS_PATH = $m0Log
$m0Sample = (Resolve-Path -LiteralPath '.\docs\CODEX_STOP_HOOK_SAMPLE.json').Path
Get-Content -Raw -LiteralPath $m0Sample |
    ConvertFrom-Json -ErrorAction Stop | Out-Null

$m0StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$m0StartInfo.FileName = $m0Hook
$m0StartInfo.Arguments = '--codex-stop-hook'
$m0StartInfo.UseShellExecute = $false
$m0StartInfo.RedirectStandardInput = $true
$m0StartInfo.RedirectStandardOutput = $true
$m0StartInfo.RedirectStandardError = $true
$m0StartInfo.CreateNoWindow = $true

$m0Process = New-Object System.Diagnostics.Process
$m0Process.StartInfo = $m0StartInfo
if (-not $m0Process.Start()) { throw 'Failed to start AgentBell.Hook.' }

$m0Bytes = [System.IO.File]::ReadAllBytes($m0Sample)
$m0Process.StandardInput.BaseStream.Write($m0Bytes, 0, $m0Bytes.Length)
$m0Process.StandardInput.BaseStream.Flush()
$m0Process.StandardInput.Close()
$m0Stdout = $m0Process.StandardOutput.ReadToEnd()
$m0Stderr = $m0Process.StandardError.ReadToEnd()
$m0Process.WaitForExit()
$m0ExpectedStdout = '{"continue":true}'

[pscustomobject]@{
    ExitCode      = $m0Process.ExitCode
    Stdout         = $m0Stdout
    StdoutIsExact  = $m0Stdout -ceq $m0ExpectedStdout
    StderrChars    = $m0Stderr.Length
} | Format-List

Get-Content -LiteralPath $m0Log | Select-Object -Last 1
```

Expected results:

- `ExitCode` is `0`.
- `Stdout` is exactly `{"continue":true}` and `StdoutIsExact` is `True`; there is no other stdout text.
- `StderrChars` is `0`.
- The last diagnostic event has `eventType = codex-stop`.
- Until M1 exists, `result` is normally `forward_unavailable` or `forward_timeout`.
- No sample assistant text, raw JSON, full path, or full identifier appears in the log.

Immediately after the direct smoke test, record the start of the 20-turn desktop test. Keep this PowerShell session open:

```powershell
$m0Start = [DateTimeOffset]::Now
$m0Start
```

## 6. Real Codex first-turn check and 20-turn acceptance test

The primary acceptance test must run in the final Windows Codex desktop client AgentBell is intended to support. Codex CLI cannot replace this test. CLI may be run afterward only as an additional compatibility check.

Start a fresh Codex Windows desktop client, approve the Hook as described above, and complete one short turn. That first turn is turn 1 of the required 20 turns. Immediately record its end time:

```powershell
$m0OneEnd = [DateTimeOffset]::Now
$m0OneTurnEvents = @(
    Get-Content -LiteralPath $m0Log | ForEach-Object {
        try {
            $m0Event = $_ | ConvertFrom-Json -ErrorAction Stop
            $m0Timestamp = [DateTimeOffset]::Parse(
                [string]$m0Event.timestamp,
                [Globalization.CultureInfo]::InvariantCulture)

            if ($m0Event.eventType -eq 'codex-stop' -and
                $m0Timestamp -ge $m0Start -and
                $m0Timestamp -le $m0OneEnd) {
                $m0Event
            }
        }
        catch {
            Write-Warning 'Skipped one malformed diagnostic line.'
        }
    }
)
$m0OneTurnEvents |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,elapsedMs |
    Format-Table
```

Confirm that exactly one `codex-stop` event is present and that both hashes are non-empty. Then complete 19 more separate, short desktop-client turns. After turn 20 has fully completed, record the end time:

```powershell
$m0End = [DateTimeOffset]::Now
$m0End
```

Completely exit Codex, including its background or system-tray process, before analyzing the log. Parse NDJSON and filter by both timestamp and `eventType = codex-stop`:

```powershell
$m0WindowEvents = @(
    Get-Content -LiteralPath $m0Log | ForEach-Object {
        try {
            $m0Event = $_ | ConvertFrom-Json -ErrorAction Stop
            $m0Timestamp = [DateTimeOffset]::Parse(
                [string]$m0Event.timestamp,
                [Globalization.CultureInfo]::InvariantCulture)

            if ($m0Event.eventType -eq 'codex-stop' -and
                $m0Timestamp -ge $m0Start -and
                $m0Timestamp -le $m0End) {
                $m0Event
            }
        }
        catch {
            Write-Warning 'Skipped one malformed diagnostic line.'
        }
    }
)

$m0TurnHashes = @($m0WindowEvents | ForEach-Object { [string]$_.turnIdHash })
$m0MissingTurnHashes = @(
    $m0TurnHashes | Where-Object { [string]::IsNullOrWhiteSpace($_) }
)
$m0ComparableTurnHashes = @(
    $m0TurnHashes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$m0DuplicateTurnHashes = @(
    $m0ComparableTurnHashes | Group-Object | Where-Object Count -gt 1
)

[pscustomobject]@{
    TestWindowStart                 = $m0Start.ToString('o')
    TestWindowEnd                   = $m0End.ToString('o')
    EventCount                      = $m0WindowEvents.Count
    DistinctTurnIdHashCount         = @($m0ComparableTurnHashes | Sort-Object -Unique).Count
    MissingOrEmptyTurnIdHashCount   = $m0MissingTurnHashes.Count
} | Format-List

Write-Host 'Duplicate turnIdHash groups:'
if ($m0DuplicateTurnHashes.Count -eq 0) {
    Write-Host '  (none)'
}
else {
    $m0DuplicateTurnHashes |
        Select-Object @{ Name = 'turnIdHash'; Expression = { $_.Name } }, Count |
        Format-Table
}

$m0WindowEvents |
    Select-Object timestamp,eventType,threadIdHash,turnIdHash,result,elapsedMs |
    Format-Table
```

Acceptance evidence:

- The test-window event total is exactly `20`.
- The number of distinct, non-empty `turnIdHash` values is exactly `20`.
- The missing-or-empty `turnIdHash` count is `0`.
- Duplicate `turnIdHash` groups show `(none)`.
- Every selected event has `eventType = codex-stop` and falls inside the captured window.
- No raw JSON, `input-messages`, assistant message, complete `cwd`, or complete identifier appears in the diagnostic log.
- Hook invocation does not visibly delay Codex; `elapsedMs` should normally remain well below the 500 ms forwarding deadline.

## 7. Remove only AgentBell's Hook and diagnostics

Completely exit Codex, including background and system-tray processes. In a JSON-aware editor, remove only the matcher group containing this exact command:

```text
<REPOSITORY_ROOT>\artifacts\m0-hook\AgentBell.Hook.exe --codex-stop-hook
```

Preserve every other Hook source, event, matcher group, command, and top-level property. If M0 created a new `hooks.json` and the file still contains only the AgentBell test definition, the whole file may be removed. Do not restore the backup wholesale if anything else changed during the test. Keep the backup until the cleaned file has been validated.

Do not touch `config.toml` or its existing Codex `notify` entry.

Delete both the user-level variables and their values in the current PowerShell process:

```powershell
[Environment]::SetEnvironmentVariable('AGENTBELL_HOOK_DIAGNOSTICS', $null, 'User')
[Environment]::SetEnvironmentVariable('AGENTBELL_HOOK_DIAGNOSTICS_PATH', $null, 'User')
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
```

Start Codex again only after cleanup is complete. The diagnostic log and timestamped backup are retained for review and must be deleted manually when no longer needed.
