# M6 manual test: Codex action-required notifications

This procedure validates the `0.7.0-beta.1` source build. It covers the existing
Stop, PermissionRequest, and PostToolUse Hooks, the explicit permission
notification policy, Windows and Android behavior, lifecycle correlation,
upgrade, and uninstall safety. It does not authorize remote approval or replies.

PermissionRequest sample-file tests prove only Hook transport and sanitization.
They do not prove that Codex displayed an approval UI or waited for a person.

## 1. Build and automated gates

Run from `<REPOSITORY_ROOT>` in PowerShell:

```powershell
dotnet --info
dotnet --list-sdks
dotnet format .\AgentBell.sln --verify-no-changes
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build

Push-Location .\android\AgentBell
.\gradlew.bat testReleaseUnitTest
.\gradlew.bat lintRelease
Pop-Location
```

`assembleRelease` additionally requires all four long-term release-signing
variables. Never substitute a debug key:

```powershell
$m6SigningNames = @(
    'AGENTBELL_ANDROID_KEYSTORE',
    'AGENTBELL_ANDROID_KEYSTORE_PASSWORD',
    'AGENTBELL_ANDROID_KEY_ALIAS',
    'AGENTBELL_ANDROID_KEY_PASSWORD'
)
$m6MissingSigning = @(
    $m6SigningNames | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    }
)
if ($m6MissingSigning.Count -eq 0) {
    Push-Location .\android\AgentBell
    .\gradlew.bat assembleRelease
    Pop-Location
} else {
    Write-Host 'Release APK gate blocked: long-term signing variables are absent.'
}
```

## 2. Back up Codex files without changing `notify`

AgentBell must never modify `.codex\config.toml` or its existing `notify` value.

```powershell
$m6CodexHome = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
    Join-Path $env:USERPROFILE '.codex'
} else {
    [Environment]::ExpandEnvironmentVariables($env:CODEX_HOME)
}
$m6CodexHome = [IO.Path]::GetFullPath($m6CodexHome)
$m6Hooks = Join-Path $m6CodexHome 'hooks.json'
$m6ConfigToml = Join-Path $m6CodexHome 'config.toml'
$m6Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$m6ManualBackup = "$m6Hooks.manual-backup-$m6Stamp"

if (Test-Path -LiteralPath $m6Hooks -PathType Leaf) {
    Copy-Item -LiteralPath $m6Hooks -Destination $m6ManualBackup
}
$m6ConfigHashBefore = if (Test-Path -LiteralPath $m6ConfigToml -PathType Leaf) {
    (Get-FileHash -LiteralPath $m6ConfigToml -Algorithm SHA256).Hash
} else { $null }
```

Do not edit `hooks.json` by hand and do not overwrite it with an example.

## 3. Install or repair the three managed Hooks

Replace `<INSTALL_DIR>` with the installed AgentBell directory:

```powershell
$m6InstallDir = '<INSTALL_DIR>'
$m6Integration = Join-Path $m6InstallDir 'AgentBell.Integration.exe'
$m6Hook = Join-Path $m6InstallDir 'AgentBell.Hook.exe'

& $m6Integration repair --json --codex-home $m6CodexHome
if ($LASTEXITCODE -ne 0) { throw "Hook repair failed: $LASTEXITCODE" }
& $m6Integration verify --json --codex-home $m6CodexHome
if ($LASTEXITCODE -ne 0) { throw "Hook verification failed: $LASTEXITCODE" }
```

Verify strict JSON and exactly one AgentBell command in each event group without
printing command content:

```powershell
$m6Document = Get-Content -Raw -LiteralPath $m6Hooks | ConvertFrom-Json
$m6Managed = foreach ($m6EventName in @('Stop', 'PermissionRequest', 'PostToolUse')) {
    foreach ($m6Group in @($m6Document.hooks.$m6EventName)) {
        foreach ($m6Handler in @($m6Group.hooks)) {
            $m6Text = ([string]$m6Handler.command) + ' ' +
                ([string]$m6Handler.commandWindows)
            if ($m6Text -match 'AgentBell\.Hook\.exe' -and
                $m6Text -match '--codex-(?:stop|permission-request|post-tool-use)-hook') {
                [pscustomobject]@{ Event = $m6EventName; Timeout = $m6Handler.timeout }
            }
        }
    }
}
$m6Managed | Format-Table
foreach ($m6EventName in @('Stop', 'PermissionRequest', 'PostToolUse')) {
    if (@($m6Managed | Where-Object Event -eq $m6EventName).Count -ne 1) {
        throw "Expected exactly one managed $m6EventName Hook."
    }
}
```

Completely exit Codex, including background and tray processes, then restart it.
Trust a changed unmanaged Hook only after verifying that the executable belongs
to AgentBell. AgentBell cannot bypass Codex Hook review.

## 4. Verify Windows settings and migration

Open AgentBell in English and Simplified Chinese. The settings must contain one
permission control, not both a checkbox and a selector:

- English: `Permission request notifications` with `Off` and `Always notify`;
- zh-CN: `权限请求提醒` with `关闭` and `始终提醒`.

The default and migration result must be Off. A legacy
`notifyPermissionRequests=true` value must not silently enable notifications.
The global “Codex needs attention” switch continues to control only input,
confirmation, and attention notifications; it does not override this policy.

Confirm the explanation is fully visible and wraps without clipping at 100%,
125%, and 150% DPI in both languages:

> Codex does not currently expose whether a permission request is being handled
> by Auto-review or is waiting for you. “Always notify” may therefore alert you
> for requests that Codex handles automatically.

> Codex 目前不会向 Hook 暴露权限请求是由 Auto-review 自动处理，还是正在等待你批准。
> 因此，“始终提醒”可能会提醒一些最终由 Codex 自动处理的请求。

## 5. Direct Hook process contracts

Start AgentBell Tray. Use file redirection so Windows PowerShell 5.1 does not
rewrite JSON quoting:

```powershell
$m6Sample = (Resolve-Path '.\docs\CODEX_PERMISSION_REQUEST_SAMPLE.json').Path
$m6PostSample = (Resolve-Path '.\docs\CODEX_POST_TOOL_USE_SAMPLE.json').Path
$m6Temp = Join-Path ([IO.Path]::GetTempPath()) (
    "AgentBell-M6-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $m6Temp -Force | Out-Null
$env:AGENTBELL_HOOK_DIAGNOSTICS = '1'
$env:AGENTBELL_HOOK_DIAGNOSTICS_PATH = Join-Path $m6Temp 'hook.ndjson'

function Invoke-M6SilentHook([string]$Option, [string]$InputPath, [string]$Name) {
    $stdout = Join-Path $m6Temp "$Name-stdout.txt"
    $stderr = Join-Path $m6Temp "$Name-stderr.txt"
    $process = Start-Process -FilePath $m6Hook `
        -ArgumentList $Option `
        -RedirectStandardInput $InputPath `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0 -or
        (Get-Item -LiteralPath $stdout).Length -ne 0 -or
        (Get-Item -LiteralPath $stderr).Length -ne 0) {
        throw "$Name Hook process contract failed."
    }
}

Invoke-M6SilentHook '--codex-permission-request-hook' $m6Sample 'permission'
Invoke-M6SilentHook '--codex-post-tool-use-hook' $m6PostSample 'post-tool-use'
Get-Content -LiteralPath $env:AGENTBELL_HOOK_DIAGNOSTICS_PATH |
    Select-Object -Last 2
```

Both Hooks must exit `0` with empty stdout and stderr. Diagnostics may contain
only sanitized hashes, stable categories, result codes, status, and timing. They
must not contain raw JSON, command text, tool input/response, descriptions, full
paths, original identifiers, prompts, replies, or credentials.

PostToolUse must parse and correlate safely, but it must never generate a
standalone user notification. Its arrival time is not evidence that Auto-review
or a person handled the request.

## 6. Policy Off acceptance

Select Off on Windows and Android, then send the PermissionRequest sample again.
Expected behavior:

- the Hook exits `0` and Desktop returns HTTP `202`;
- optional diagnostics report the sanitized request lifecycle;
- no Windows notification appears;
- no permission event is written to `%LOCALAPPDATA%\AgentBell\events.json`;
- no permission action appears in Windows history;
- no permission event or notification appears on Android;
- the Hook does not wait for a follow-up event or timer.

Sending matching PostToolUse afterward may close the sanitized lifecycle entry,
but it must not retroactively create a user event.

## 7. Policy Always notify acceptance

Select Always notify on Windows and Android, then send the PermissionRequest
sample once. Verify immediately:

- exactly one event is stored with `category=action_required` and
  `actionType=permission_required`;
- Windows displays exactly one generic permission notification;
- Android displays exactly one generic notification on
  `agentbell_action_required`;
- neither surface contains command text or other sensitive content.

Inspect only allow-listed fields:

```powershell
$m6LocalAppData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
$m6Events = Join-Path $m6LocalAppData 'AgentBell\events.json'
$m6EventDocument = Get-Content -Raw -LiteralPath $m6Events | ConvertFrom-Json
$m6Latest = @($m6EventDocument) | Sort-Object sequence | Select-Object -Last 1
$m6Latest | Select-Object category, actionType, toolCategory, project, sequence, occurredAt

$m6StoredText = Get-Content -Raw -LiteralPath $m6Events
if ($m6StoredText -match 'tool_input|toolInput|tool_response|description|REDACTED_TEST_COMMAND') {
    throw 'Sensitive PermissionRequest content reached events.json.'
}
```

Send the same sample again. The deterministic EventId must deduplicate it: no
second history entry, Windows alert, or Android alert. Send matching PostToolUse
and confirm it creates no standalone notification. Any resolution update may
only clear the already-active notification.

“Always notify” means a PermissionRequest occurred. It does not mean Codex is
currently waiting for human approval.

## 8. Android channel and history checks

Use only the release APK signed with the established long-term key. Verify:

- `agentbell_action_required` remains separate from completion;
- importance is High, sound and vibration are available;
- no full-screen intent or Do Not Disturb bypass is used;
- Off suppresses permission notification and permission action history;
- Always notify shows a unique request once;
- changing the general action switch does not silently change permission policy;
- English and zh-CN texts are complete on Activity foreground/background, lock
  screen, reconnect, and foreground-service operation.

## 9. Real Codex desktop checks

Use the final supported Windows Codex Desktop client. Trigger real operations
that cause PermissionRequest with Auto-review enabled and disabled. In both
cases validate Hook delivery and privacy, but do not label either case from Hook
timing: command Hooks cannot distinguish Auto-review from a human wait.

- With Off, neither case may produce a permission user event.
- With Always notify, every unique PermissionRequest in either case produces one
  generic Windows and Android notification.
- Codex remains the only approval UI; AgentBell never returns allow/deny.
- PostToolUse remains lifecycle correlation only.

For Stop-based input/confirmation/attention notifications, use an explicit task
that asks the user to choose. Verify generic notification text and no question
body on the phone. This classifier is a conservative heuristic, not an exact
structured signal and not guaranteed to detect every request.

## 10. App Server capability boundary

Record that Codex App Server has precise signals unavailable to command Hooks:

- `item/commandExecution/requestApproval`;
- `item/fileChange/requestApproval`;
- `item/permissions/requestApproval`;
- `thread/status/changed` with `waitingOnApproval`;
- `serverRequest/resolved`.

A second App Server client cannot observe the Windows Codex Desktop client's
private connection. Do not use OCR, UI automation, process injection, private
connection takeover, SQLite scraping, or undocumented `approvalsReviewer` in
M6. `approvalsReviewer` is an investigation fact only, not a supported contract.

## 11. Upgrade, uninstall, and cleanup

Upgrade in English and zh-CN, then run repair again. Exactly one Stop, one
PermissionRequest, and one PostToolUse AgentBell Hook must remain. Unrelated
Hooks and `config.toml` must remain unchanged. Uninstall must remove only the
three strictly recognized AgentBell handlers.

```powershell
$m6ConfigHashAfter = if (Test-Path -LiteralPath $m6ConfigToml -PathType Leaf) {
    (Get-FileHash -LiteralPath $m6ConfigToml -Algorithm SHA256).Hash
} else { $null }
if ($m6ConfigHashAfter -cne $m6ConfigHashBefore) {
    throw 'config.toml changed during M6 integration testing.'
}

Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS -ErrorAction SilentlyContinue
Remove-Item Env:AGENTBELL_HOOK_DIAGNOSTICS_PATH -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $m6Temp) {
    Remove-Item -LiteralPath $m6Temp -Recurse -Force
}
```

Keep the manual backup until install, upgrade, and uninstall have been reviewed.
Restore it only as an explicit recovery action, never by overwriting later user
Hook changes automatically.
