#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$IntegrationExe,

    [string]$SetupPath,

    [switch]$SkipSetup,

    [string]$TestRoot,

    [string]$InstallerAppId = 'A17863B4-7E64-4D74-A0B4-004000000001',

    [string]$CriticalFailureSetupPath,

    [ValidateSet('All', 'NormalUninstall')]
    [string]$Scenario = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$IntegrationExe = [System.IO.Path]::GetFullPath($IntegrationExe)
if (-not (Test-Path -LiteralPath $IntegrationExe -PathType Leaf)) {
    throw "Integration executable not found: $IntegrationExe"
}

$integrationDirectory = Split-Path -Parent $IntegrationExe
$hookExe = Join-Path $integrationDirectory 'AgentBell.Hook.exe'
if (-not (Test-Path -LiteralPath $hookExe -PathType Leaf)) {
    throw "The Integration executable has no sibling AgentBell.Hook.exe: $integrationDirectory"
}

$appId = $InstallerAppId
$uninstallRegistryRoots = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)

function Get-AgentBellRegistration {
    foreach ($registryRoot in $uninstallRegistryRoots) {
        if (-not (Test-Path -LiteralPath $registryRoot)) {
            continue
        }

        $registration = Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName.Contains($appId, [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -ne $registration) {
            return $registration
        }
    }

    return $null
}

if (-not $SkipSetup) {
    if ([string]::IsNullOrWhiteSpace($SetupPath)) {
        throw 'Pass -SetupPath or use -SkipSetup.'
    }

    $SetupPath = [System.IO.Path]::GetFullPath($SetupPath)
    if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) {
        throw "Setup executable not found: $SetupPath"
    }

    if (-not [string]::IsNullOrWhiteSpace($CriticalFailureSetupPath)) {
        $CriticalFailureSetupPath = [System.IO.Path]::GetFullPath($CriticalFailureSetupPath)
        if (-not (Test-Path -LiteralPath $CriticalFailureSetupPath -PathType Leaf)) {
            throw "Critical-failure Setup executable not found: $CriticalFailureSetupPath"
        }
    }

    if ($null -ne (Get-AgentBellRegistration)) {
        throw 'Refusing to run the Setup integration test while AgentBell is already registered for this user.'
    }
}

if ($Scenario -ne 'All' -and $SkipSetup) {
    throw 'The NormalUninstall scenario requires -SetupPath.'
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][hashtable]$Environment
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $name = [string]$entry.Key
        if ($null -eq $entry.Value) {
            [void]$startInfo.Environment.Remove($name)
        }
        else {
            $startInfo.Environment[$name] = [string]$entry.Value
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The child process did not start.'
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-Integration {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$CodexHome
    )

    $process = Invoke-CapturedProcess -FilePath $IntegrationExe `
        -Arguments @($Operation, '--json') `
        -Environment @{ CODEX_HOME = $CodexHome }
    $result = $null
    if (-not [string]::IsNullOrWhiteSpace($process.StandardOutput)) {
        $result = $process.StandardOutput | ConvertFrom-Json
    }
    return [pscustomobject]@{
        Process = $process
        Result = $result
    }
}

function Get-RequiredDiagnosticProperty {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "The uninstall Integration diagnostic is missing required field '$Name'."
    }
    return $property.Value
}

function Get-UninstallIntegrationDiagnostic {
    param([Parameter(Mandatory = $true)][string]$LogContent)

    $marker = 'AgentBell Integration stdout:'
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($line in [System.Text.RegularExpressions.Regex]::Split($LogContent, '\r\n|\n|\r')) {
        $markerIndex = $line.IndexOf($marker, [StringComparison]::Ordinal)
        if ($markerIndex -lt 0) {
            continue
        }

        $json = $line.Substring($markerIndex + $marker.Length).Trim()
        if ([string]::IsNullOrWhiteSpace($json)) {
            throw 'The uninstall Integration diagnostic record is empty.'
        }

        try {
            $record = $json | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
        }
        catch {
            throw 'The uninstall Integration diagnostic contains malformed JSON.'
        }
        if ($null -eq $record -or
            $record -isnot [System.Management.Automation.PSCustomObject]) {
            throw 'The uninstall Integration diagnostic is not a JSON object.'
        }
        $records.Add($record)
    }

    $uninstallRecords = @($records | Where-Object {
        $code = $_.PSObject.Properties['code']
        $null -ne $code -and [string]$code.Value -ceq 'uninstalled'
    })
    if ($uninstallRecords.Count -eq 0) {
        throw 'The exact uninstall log contains no completed uninstall Integration diagnostic.'
    }
    if ($uninstallRecords.Count -ne 1) {
        throw 'The exact uninstall log contains duplicate uninstall Integration diagnostics.'
    }
    return $uninstallRecords[0]
}

function Assert-NormalUninstallDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string]$LogContent,
        [Parameter(Mandatory = $true)][int]$ExpectedBeforeCount
    )

    $record = Get-UninstallIntegrationDiagnostic -LogContent $LogContent
    $success = Get-RequiredDiagnosticProperty -Record $record -Name 'success'
    $code = Get-RequiredDiagnosticProperty -Record $record -Name 'code'
    $stage = Get-RequiredDiagnosticProperty -Record $record -Name 'stage'
    $beforeCount = Get-RequiredDiagnosticProperty -Record $record -Name 'agentBellHookCountBefore'
    $afterCount = Get-RequiredDiagnosticProperty -Record $record -Name 'agentBellHookCount'
    if ($success -ne $true -or
        [string]$code -cne 'uninstalled' -or
        [string]$stage -cne 'completed' -or
        [int]$beforeCount -ne $ExpectedBeforeCount -or
        [int]$afterCount -ne 0) {
        throw "The uninstall Integration diagnostic did not report the expected before/after state ($ExpectedBeforeCount/0)."
    }
}

function Assert-DiagnosticParserThrows {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Description
    )

    try {
        $null = & $Action
    }
    catch {
        return
    }
    throw "Diagnostic parser test did not fail closed: $Description"
}

function Test-UninstallDiagnosticParser {
    $prefix = '2026-08-12 12:00:00.000   AgentBell Integration stdout: '
    $compact = '{"success":true,"code":"uninstalled","stage":"completed","agentBellHookCountBefore":1,"agentBellHookCount":0}'
    $spaced = '{ "success": true, "code": "uninstalled", "stage": "completed", "agentBellHookCountBefore": 1, "agentBellHookCount": 0 }'
    $beforeThree = '{"success":true,"code":"uninstalled","stage":"completed","agentBellHookCountBefore":3,"agentBellHookCount":0}'
    $repair = '{"success":true,"code":"installed","stage":"completed","agentBellHookCountBefore":1,"agentBellHookCount":3}'

    [void](Assert-NormalUninstallDiagnostic -LogContent ($prefix + $compact) -ExpectedBeforeCount 1)
    [void](Assert-NormalUninstallDiagnostic -LogContent ($prefix + $spaced) -ExpectedBeforeCount 1)
    [void](Assert-NormalUninstallDiagnostic -LogContent ("header`n" + $prefix + $compact + "`nfooter") -ExpectedBeforeCount 1)
    [void](Assert-NormalUninstallDiagnostic -LogContent ("header`r`n" + $prefix + $compact + "`r`nfooter") -ExpectedBeforeCount 1)
    [void](Assert-NormalUninstallDiagnostic -LogContent (([char]0xFEFF) + $prefix + $compact) -ExpectedBeforeCount 1)
    [void](Assert-NormalUninstallDiagnostic -LogContent ($prefix + $repair + "`r`n" + $prefix + $beforeThree) -ExpectedBeforeCount 3)

    Assert-DiagnosticParserThrows -Description 'missing before-count field' -Action {
        Assert-NormalUninstallDiagnostic -LogContent ($prefix + '{"success":true,"code":"uninstalled","stage":"completed","agentBellHookCount":0}') -ExpectedBeforeCount 1
    }
    Assert-DiagnosticParserThrows -Description 'wrong before-count value' -Action {
        Assert-NormalUninstallDiagnostic -LogContent ($prefix + $beforeThree) -ExpectedBeforeCount 1
    }
    Assert-DiagnosticParserThrows -Description 'malformed JSON' -Action {
        Get-UninstallIntegrationDiagnostic -LogContent ($prefix + '{"code":"uninstalled"')
    }
    Assert-DiagnosticParserThrows -Description 'contradictory duplicate uninstall records' -Action {
        Get-UninstallIntegrationDiagnostic -LogContent ($prefix + $compact + "`n" + $prefix + $beforeThree)
    }
}

function Get-AgentBellHandlers {
    param([Parameter(Mandatory = $true)]$Root)

    $handlers = @()
    $definitions = @(
        [pscustomobject]@{ EventName = 'Stop'; Option = '--codex-stop-hook' },
        [pscustomobject]@{
            EventName = 'PermissionRequest'
            Option = '--codex-permission-request-hook'
        },
        [pscustomobject]@{
            EventName = 'PostToolUse'
            Option = '--codex-post-tool-use-hook'
        }
    )
    foreach ($definition in $definitions) {
        $eventProperty = $Root.hooks.PSObject.Properties[$definition.EventName]
        if ($null -eq $eventProperty) {
            continue
        }
        foreach ($group in @($eventProperty.Value)) {
            foreach ($handler in @($group.hooks)) {
                $commandProperty = $handler.PSObject.Properties['command']
                $commandWindowsProperty = $handler.PSObject.Properties['commandWindows']
                $command = if ($null -eq $commandProperty) { '' } else { [string]$commandProperty.Value }
                $commandWindows = if ($null -eq $commandWindowsProperty) {
                    ''
                }
                else {
                    [string]$commandWindowsProperty.Value
                }
                $combined = $command + ' ' + $commandWindows
                if ($combined.Contains('AgentBell.Hook.exe', [StringComparison]::OrdinalIgnoreCase) -and
                    $combined.Contains($definition.Option, [StringComparison]::Ordinal)) {
                    $handlers += [pscustomobject]@{
                        EventName = $definition.EventName
                        Option = $definition.Option
                        Handler = $handler
                    }
                }
            }
        }
    }
    return @($handlers)
}

function Assert-HooksDocument {
    param(
        [Parameter(Mandatory = $true)][string]$HooksPath,
        [Parameter(Mandatory = $true)][string]$ExpectedHookPath,
        [switch]$RequireOtherHook
    )

    $root = Get-Content -Raw -LiteralPath $HooksPath | ConvertFrom-Json
    $agentBellHandlers = @(Get-AgentBellHandlers -Root $root)
    if ($agentBellHandlers.Count -ne 3) {
        throw "Expected one Stop, one PermissionRequest, and one PostToolUse Hook; found $($agentBellHandlers.Count)."
    }

    foreach ($definition in @(
        [pscustomobject]@{ EventName = 'Stop'; Option = '--codex-stop-hook' },
        [pscustomobject]@{
            EventName = 'PermissionRequest'
            Option = '--codex-permission-request-hook'
        },
        [pscustomobject]@{
            EventName = 'PostToolUse'
            Option = '--codex-post-tool-use-hook'
        }
    )) {
        $match = @($agentBellHandlers | Where-Object EventName -CEQ $definition.EventName)
        if ($match.Count -ne 1) {
            throw "The $($definition.EventName) Hook is not unique."
        }
        $handler = $match[0].Handler
        $expectedDirect = '"' + [System.IO.Path]::GetFullPath($ExpectedHookPath) + '" ' +
            $definition.Option
        $expectedWindows = 'cmd.exe /d /s /c "' + $expectedDirect + '"'
        if ([string]$handler.command -cne $expectedDirect -or
            [string]$handler.commandWindows -cne $expectedWindows -or
            [int]$handler.timeout -ne 3) {
            throw "The managed $($definition.EventName) handler is not exact."
        }
    }

    if ($RequireOtherHook) {
        $otherCount = 0
        foreach ($group in @($root.hooks.Stop)) {
            foreach ($handler in @($group.hooks)) {
                if ([string]$handler.command -ceq 'other-tool.exe --stop') {
                    $otherCount++
                }
            }
        }
        if ($otherCount -ne 1 -or [string]$root.owner -cne 'installer-integration-test') {
            throw 'The unrelated Hook or top-level property was not preserved exactly once.'
        }
    }
}

function Assert-IntegrationSuccess {
    param(
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$ExpectedCode
    )

    if ($Invocation.Process.ExitCode -ne 0 -or
        $null -eq $Invocation.Result -or
        $Invocation.Result.success -ne $true -or
        [string]$Invocation.Result.code -cne $ExpectedCode -or
        -not [string]::IsNullOrEmpty($Invocation.Process.StandardError)) {
        throw "Integration operation failed its safe contract. Exit code: $($Invocation.Process.ExitCode)."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Wait-Condition {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$TimeoutSeconds = 20
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt [TimeSpan]::FromSeconds($TimeoutSeconds)) {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for $Description after $TimeoutSeconds seconds."
}

function Remove-TestDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    Wait-Condition -Description 'the isolated installer test directory to be released and deleted' -Condition {
        if (-not (Test-Path -LiteralPath $Path)) {
            return $true
        }

        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        }
        catch {
            return $false
        }
        return -not (Test-Path -LiteralPath $Path)
    }
}

function Assert-OnlyOtherHookRemains {
    param([Parameter(Mandatory = $true)][string]$HooksPath)

    $root = Get-Content -Raw -LiteralPath $HooksPath | ConvertFrom-Json
    if (@(Get-AgentBellHandlers -Root $root).Count -ne 0) {
        throw 'The uninstaller left an AgentBell-managed Hook behind.'
    }

    $otherCount = 0
    foreach ($group in @($root.hooks.Stop)) {
        foreach ($handler in @($group.hooks)) {
            if ([string]$handler.command -ceq 'other-tool.exe --stop') {
                $otherCount++
            }
        }
    }
    if ($otherCount -ne 1 -or [string]$root.owner -cne 'installer-integration-test') {
        throw 'The uninstaller changed an unrelated Hook or top-level property.'
    }
}

function Invoke-TestUninstaller {
    param(
        [Parameter(Mandatory = $true)][string]$UninstallerPath,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][hashtable]$Environment
    )

    $result = Invoke-CapturedProcess -FilePath $UninstallerPath -Arguments @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/DELETEUSERDATA=0',
        "/LOG=$LogPath"
    ) -Environment $Environment
    if ($result.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($result.ExitCode)."
    }

    Wait-Condition -Description 'the uninstall registration and install directory to be removed' -Condition {
        $null -eq (Get-AgentBellRegistration) -and
            -not (Test-Path -LiteralPath $InstallDirectory)
    }
    return $result
}

$testRootParent = if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    [System.IO.Path]::GetTempPath()
}
else {
    [System.IO.Path]::GetFullPath($TestRoot)
}
$tempRoot = Join-Path $testRootParent `
    ('AgentBell-Codex-Installer-' + [Guid]::NewGuid().ToString('N'))
$directCodexHome = Join-Path $tempRoot 'direct CODEX_HOME 中文'
$uninstallCodexHome = Join-Path $tempRoot 'uninstall CODEX_HOME 中文'
$setupCodexHome = Join-Path $tempRoot 'setup CODEX_HOME 中文'
$fallbackProfile = Join-Path $tempRoot 'fallback USERPROFILE 空格中文'
$fallbackCodexHome = Join-Path $fallbackProfile '.codex'
$chineseCodexHome = Join-Path $tempRoot 'zhcn CODEX_HOME 中文'
$setupInstallDirectory = Join-Path $tempRoot 'Install AgentBell 中文'
$setupLog = Join-Path $tempRoot 'setup.log'
$partialUninstallLog = Join-Path $tempRoot 'partial-uninstall.log'
$normalUninstallLog = Join-Path $tempRoot 'normal-uninstall.log'
$invalidUninstallLog = Join-Path $tempRoot 'invalid-uninstall.log'
$missingUninstallLog = Join-Path $tempRoot 'missing-uninstall.log'
$criticalUninstallLog = Join-Path $tempRoot 'critical-uninstall.log'
$criticalCleanupLog = Join-Path $tempRoot 'critical-cleanup.log'
$missingProfileSetupLog = Join-Path $tempRoot 'missing-profile-setup.log'
$missingProfileCleanupLog = Join-Path $tempRoot 'missing-profile-cleanup.log'
$fallbackSetupLog = Join-Path $tempRoot 'fallback-profile-setup.log'
$fallbackUpgradeLog = Join-Path $tempRoot 'fallback-profile-upgrade.log'
$fallbackUninstallLog = Join-Path $tempRoot 'fallback-profile-uninstall.log'
$chineseSetupLog = Join-Path $tempRoot 'zhcn-setup.log'
$chineseUninstallLog = Join-Path $tempRoot 'zhcn-uninstall.log'
$failureHomeFile = Join-Path $tempRoot 'not-a-directory'
$uninstallerPath = Join-Path $setupInstallDirectory 'unins000.exe'
$setupInstalled = $false

try {
    New-Item -ItemType Directory `
        -Path $directCodexHome, $uninstallCodexHome, $setupCodexHome, $fallbackProfile, $chineseCodexHome `
        -Force | Out-Null

    Write-Host 'Testing structured uninstall diagnostic parsing...'
    Test-UninstallDiagnosticParser

    if ($Scenario -eq 'NormalUninstall') {
        Write-Host 'Testing targeted normal uninstall with a fresh isolated root...'
        $setupHooksPath = Join-Path $setupCodexHome 'hooks.json'
        $setup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($setup.ExitCode -ne 0) {
            throw "Targeted Setup first installation failed with exit code $($setup.ExitCode)."
        }
        $setupInstalled = $true
        $installedHookPath = Join-Path $setupInstallDirectory 'AgentBell.Hook.exe'
        Assert-HooksDocument -HooksPath $setupHooksPath -ExpectedHookPath $installedHookPath
        if (@(Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*').Count -ne 0) {
            throw 'Targeted Setup fresh creation produced a meaningless backup.'
        }

        $setupLegacyHookPath = Join-Path $tempRoot `
            'Setup Repository\AgentBell\artifacts\m0-hook\AgentBell.Hook.exe'
        $setupFixture = [ordered]@{
            owner = 'installer-integration-test'
            hooks = [ordered]@{
                Stop = @(
                    [ordered]@{
                        hooks = @([ordered]@{ type = 'command'; command = 'other-tool.exe --stop' })
                    },
                    [ordered]@{
                        hooks = @([ordered]@{
                            type = 'command'
                            command = '"' + $setupLegacyHookPath + '" --codex-stop-hook'
                            timeout = 1
                        })
                    }
                )
            }
        } | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($setupHooksPath, $setupFixture, $utf8NoBom)
        $setupFixtureHash = Get-Sha256 -Path $setupHooksPath
        $setupUpgrade = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($setupUpgrade.ExitCode -ne 0) {
            throw "Targeted Setup in-place upgrade failed with exit code $($setupUpgrade.ExitCode)."
        }
        Assert-HooksDocument -HooksPath $setupHooksPath `
            -ExpectedHookPath $installedHookPath `
            -RequireOtherHook
        $setupBackups = @(Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*')
        if ($setupBackups.Count -ne 1 -or
            (Get-Sha256 -Path $setupBackups[0].FullName) -cne $setupFixtureHash) {
            throw 'Targeted Setup upgrade did not preserve one byte-for-byte legacy backup.'
        }

        $preUninstallHash = Get-Sha256 -Path $setupHooksPath
        $preUninstallBackupCount = $setupBackups.Count
        $retainedDataPath = Join-Path $setupCodexHome 'retained-user-data.json'
        [System.IO.File]::WriteAllText($retainedDataPath, '{"retain":true}', $utf8NoBom)
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $normalUninstallLog `
            -Environment @{ CODEX_HOME = $setupCodexHome })
        $setupInstalled = $false
        Assert-OnlyOtherHookRemains -HooksPath $setupHooksPath
        if (-not (Test-Path -LiteralPath $retainedDataPath -PathType Leaf)) {
            throw 'The targeted normal uninstall removed retained user data.'
        }
        $postUninstallBackups = @(
            Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*'
        )
        $matchingUninstallBackups = @($postUninstallBackups | Where-Object {
            (Get-Sha256 -Path $_.FullName) -ceq $preUninstallHash
        })
        if ($postUninstallBackups.Count -ne ($preUninstallBackupCount + 1) -or
            $matchingUninstallBackups.Count -ne 1) {
            throw 'The targeted normal uninstall did not preserve one byte-for-byte pre-uninstall backup.'
        }

        $normalLog = Get-Content -Raw -LiteralPath $normalUninstallLog
        Assert-NormalUninstallDiagnostic -LogContent $normalLog -ExpectedBeforeCount 3
        foreach ($requiredLogText in @(
            'AgentBell uninstall resolved CODEX_HOME:',
            'AgentBell uninstall hooks.json exists: yes',
            'AgentBell uninstall backup candidate count:',
            'AgentBell uninstall stage: codex_hook_cleanup.',
            'AgentBell uninstall Codex cleanup: completed or safely skipped.')) {
            if (-not $normalLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Targeted normal uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Targeted normal uninstall scenario passed.' -ForegroundColor Green
        return
    }

    Write-Host 'Testing first creation with an isolated missing hooks.json...'
    $hooksPath = Join-Path $directCodexHome 'hooks.json'
    $first = Invoke-Integration -Operation 'repair' -CodexHome $directCodexHome
    Assert-IntegrationSuccess -Invocation $first -ExpectedCode 'installed'
    if (-not (Test-Path -LiteralPath $hooksPath -PathType Leaf) -or
        $null -ne $first.Result.backupPath) {
        throw 'Fresh creation did not create hooks.json without a meaningless backup.'
    }
    Assert-HooksDocument -HooksPath $hooksPath -ExpectedHookPath $hookExe

    Write-Host 'Testing legacy Hook upgrade, unrelated Hook preservation, and byte-for-byte backup...'
    $legacyHookPath = Join-Path $tempRoot 'Repository\AgentBell\artifacts\m0-hook\AgentBell.Hook.exe'
    $fixture = [ordered]@{
        owner = 'installer-integration-test'
        hooks = [ordered]@{
            Stop = @(
                [ordered]@{ hooks = @([ordered]@{ type = 'command'; command = 'other-tool.exe --stop' }) },
                [ordered]@{ hooks = @([ordered]@{
                    type = 'command'
                    command = '"' + $legacyHookPath + '" --codex-stop-hook'
                    timeout = 1
                }) }
            )
        }
    } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($hooksPath, $fixture, $utf8NoBom)
    $fixtureHash = Get-Sha256 -Path $hooksPath
    $upgrade = Invoke-Integration -Operation 'repair' -CodexHome $directCodexHome
    Assert-IntegrationSuccess -Invocation $upgrade -ExpectedCode 'installed'
    if ([string]::IsNullOrWhiteSpace([string]$upgrade.Result.backupPath) -or
        -not (Test-Path -LiteralPath $upgrade.Result.backupPath -PathType Leaf) -or
        (Get-Sha256 -Path $upgrade.Result.backupPath) -cne $fixtureHash) {
        throw 'The existing hooks.json backup is missing or not byte-for-byte identical.'
    }
    Assert-HooksDocument -HooksPath $hooksPath -ExpectedHookPath $hookExe -RequireOtherHook

    $backupCount = @(Get-ChildItem -LiteralPath $directCodexHome -Filter '*.agentbell-backup-*').Count
    $second = Invoke-Integration -Operation 'repair' -CodexHome $directCodexHome
    Assert-IntegrationSuccess -Invocation $second -ExpectedCode 'installed'
    if ($second.Result.changed -ne $false -or
        @(Get-ChildItem -LiteralPath $directCodexHome -Filter '*.agentbell-backup-*').Count -ne $backupCount) {
        throw 'An idempotent upgrade changed hooks.json or created an extra backup.'
    }

    Write-Host 'Testing invalid JSON preservation...'
    $invalidBytes = $utf8NoBom.GetBytes('{invalid-json')
    [System.IO.File]::WriteAllBytes($hooksPath, $invalidBytes)
    $invalid = Invoke-Integration -Operation 'repair' -CodexHome $directCodexHome
    if ($invalid.Process.ExitCode -ne 11 -or
        [string]$invalid.Result.code -cne 'hooks_json_invalid' -or
        [System.Convert]::ToHexString($invalidBytes) -cne
            [System.Convert]::ToHexString([System.IO.File]::ReadAllBytes($hooksPath))) {
        throw 'Invalid hooks.json was not rejected without modification.'
    }

    Write-Host 'Testing read-only failure preservation...'
    [System.IO.File]::WriteAllText($hooksPath, $fixture, $utf8NoBom)
    $readOnlyHash = Get-Sha256 -Path $hooksPath
    [System.IO.File]::SetAttributes($hooksPath, [System.IO.FileAttributes]::ReadOnly)
    try {
        $readOnly = Invoke-Integration -Operation 'repair' -CodexHome $directCodexHome
        if ($readOnly.Process.ExitCode -eq 0 -or (Get-Sha256 -Path $hooksPath) -cne $readOnlyHash) {
            throw 'A read-only hooks.json was not rejected with the original bytes preserved.'
        }
    }
    finally {
        [System.IO.File]::SetAttributes($hooksPath, [System.IO.FileAttributes]::Normal)
    }

    Write-Host 'Testing uninstall when only timestamped backups exist...'
    $uninstallHooksPath = Join-Path $uninstallCodexHome 'hooks.json'
    $managedBackup = "$uninstallHooksPath.agentbell-backup-20260805-010203"
    $manualBackup = "$uninstallHooksPath.manual-backup-20260805-010204"
    [System.IO.File]::WriteAllText($managedBackup, '{}', $utf8NoBom)
    [System.IO.File]::WriteAllText($manualBackup, '{}', $utf8NoBom)
    $missingUninstall = Invoke-Integration -Operation 'uninstall' -CodexHome $uninstallCodexHome
    Assert-IntegrationSuccess -Invocation $missingUninstall -ExpectedCode 'hook_missing'
    if ([int]$missingUninstall.Result.backupCandidateCount -ne 2 -or
        -not (Test-Path -LiteralPath $managedBackup -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manualBackup -PathType Leaf) -or
        (Test-Path -LiteralPath $uninstallHooksPath)) {
        throw 'Missing hooks.json did not remain an idempotent skip with backups preserved.'
    }

    Write-Host 'Testing known M0 Hook removal, unrelated Hook preservation, and repeated cleanup...'
    [System.IO.File]::WriteAllText($uninstallHooksPath, $fixture, $utf8NoBom)
    $removeM0 = Invoke-Integration -Operation 'uninstall' -CodexHome $uninstallCodexHome
    Assert-IntegrationSuccess -Invocation $removeM0 -ExpectedCode 'uninstalled'
    Assert-OnlyOtherHookRemains -HooksPath $uninstallHooksPath
    $repeatUninstall = Invoke-Integration -Operation 'uninstall' -CodexHome $uninstallCodexHome
    Assert-IntegrationSuccess -Invocation $repeatUninstall -ExpectedCode 'hook_missing'

    Write-Host 'Testing invalid hooks.json uninstall preservation...'
    [System.IO.File]::WriteAllBytes($uninstallHooksPath, $invalidBytes)
    $invalidUninstall = Invoke-Integration -Operation 'uninstall' -CodexHome $uninstallCodexHome
    if ($invalidUninstall.Process.ExitCode -ne 11 -or
        [string]$invalidUninstall.Result.code -cne 'hooks_json_invalid' -or
        [System.Convert]::ToHexString($invalidBytes) -cne
            [System.Convert]::ToHexString([System.IO.File]::ReadAllBytes($uninstallHooksPath))) {
        throw 'Invalid hooks.json was not preserved during uninstall.'
    }

    if (-not $SkipSetup) {
        Write-Host 'Testing Setup failure exit code with an unusable isolated CODEX_HOME...'
        [System.IO.File]::WriteAllText($failureHomeFile, 'fixture', $utf8NoBom)
        $failedSetup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $failureHomeFile }
        if ($failedSetup.ExitCode -eq 0) {
            throw 'Setup returned zero after the Codex integration child failed.'
        }
        $setupInstalled = Test-Path -LiteralPath $uninstallerPath -PathType Leaf
        $failureLog = Get-Content -Raw -LiteralPath $setupLog
        foreach ($requiredLogText in @(
            'AgentBell Integration executable:',
            'AgentBell Integration child started: yes',
            'AgentBell Integration exit code:',
            'AgentBell Integration stdout:',
            'AgentBell Integration stderr:',
            'stage')) {
            if (-not $failureLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Setup failure log omitted required diagnostic marker: $requiredLogText"
            }
        }

        if (-not $setupInstalled) {
            throw 'The interrupted Setup scenario did not leave an uninstaller for cleanup testing.'
        }

        Write-Host 'Testing uninstall after interrupted installation with optional files missing...'
        $partialIntegrationPath = Join-Path $setupInstallDirectory 'AgentBell.Integration.exe'
        Remove-Item -LiteralPath $partialIntegrationPath -Force
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $partialUninstallLog `
            -Environment @{ CODEX_HOME = $failureHomeFile })
        $setupInstalled = $false
        $partialLog = Get-Content -Raw -LiteralPath $partialUninstallLog
        foreach ($requiredLogText in @(
            'AgentBell uninstall stage: initialize.',
            'AgentBell uninstall hooks.json exists: no',
            'AgentBell uninstall Codex cleanup: skipped (hooks.json missing).')) {
            if (-not $partialLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Partial uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Testing Setup first installation into a path with spaces and Unicode...'
        $setupHooksPath = Join-Path $setupCodexHome 'hooks.json'
        if (Test-Path -LiteralPath $setupHooksPath) {
            throw 'The isolated Setup CODEX_HOME unexpectedly contains hooks.json before first installation.'
        }
        $setup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($setup.ExitCode -ne 0) {
            throw "Setup first installation failed with exit code $($setup.ExitCode)."
        }
        $setupInstalled = $true
        Assert-HooksDocument -HooksPath $setupHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe')
        if (@(Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*').Count -ne 0) {
            throw 'Setup fresh creation produced a meaningless backup.'
        }

        Write-Host 'Testing Setup in-place upgrade...'
        $setupLegacyHookPath = Join-Path $tempRoot `
            'Setup Repository\AgentBell\artifacts\m0-hook\AgentBell.Hook.exe'
        $setupFixture = [ordered]@{
            owner = 'installer-integration-test'
            hooks = [ordered]@{
                Stop = @(
                    [ordered]@{
                        hooks = @([ordered]@{ type = 'command'; command = 'other-tool.exe --stop' })
                    },
                    [ordered]@{
                        hooks = @([ordered]@{
                            type = 'command'
                            command = '"' + $setupLegacyHookPath + '" --codex-stop-hook'
                            timeout = 1
                        })
                    }
                )
            }
        } | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($setupHooksPath, $setupFixture, $utf8NoBom)
        $setupFixtureHash = Get-Sha256 -Path $setupHooksPath
        $setupUpgrade = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($setupUpgrade.ExitCode -ne 0) {
            throw "Setup in-place upgrade failed with exit code $($setupUpgrade.ExitCode)."
        }
        Assert-HooksDocument -HooksPath $setupHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe') `
            -RequireOtherHook
        $setupBackups = @(Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*')
        if ($setupBackups.Count -ne 1 -or (Get-Sha256 -Path $setupBackups[0].FullName) -cne $setupFixtureHash) {
            throw 'Setup upgrade did not create one byte-for-byte backup of the previous hooks.json.'
        }

        Write-Host 'Testing normal uninstall, registration cleanup, data retention, and Hook preservation...'
        $preUninstallHash = Get-Sha256 -Path $setupHooksPath
        $preUninstallBackupCount = $setupBackups.Count
        $retainedDataPath = Join-Path $setupCodexHome 'retained-user-data.json'
        [System.IO.File]::WriteAllText($retainedDataPath, '{"retain":true}', $utf8NoBom)
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $normalUninstallLog `
            -Environment @{ CODEX_HOME = $setupCodexHome })
        $setupInstalled = $false
        Assert-OnlyOtherHookRemains -HooksPath $setupHooksPath
        if (-not (Test-Path -LiteralPath $retainedDataPath -PathType Leaf)) {
            throw 'The user-selected retained data marker was removed.'
        }
        $postUninstallBackups = @(
            Get-ChildItem -LiteralPath $setupCodexHome -Filter '*.agentbell-backup-*'
        )
        $matchingUninstallBackups = @($postUninstallBackups | Where-Object {
            (Get-Sha256 -Path $_.FullName) -ceq $preUninstallHash
        })
        if ($postUninstallBackups.Count -ne ($preUninstallBackupCount + 1) -or
            $matchingUninstallBackups.Count -ne 1) {
            throw 'Normal uninstall did not preserve one byte-for-byte pre-uninstall backup.'
        }
        $normalLog = Get-Content -Raw -LiteralPath $normalUninstallLog
        Assert-NormalUninstallDiagnostic -LogContent $normalLog -ExpectedBeforeCount 3
        foreach ($requiredLogText in @(
            'AgentBell uninstall resolved CODEX_HOME:',
            'AgentBell uninstall hooks.json exists: yes',
            'AgentBell uninstall backup candidate count:',
            'AgentBell uninstall stage: codex_hook_cleanup.',
            'AgentBell uninstall Codex cleanup: completed or safely skipped.')) {
            if (-not $normalLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Normal uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Testing reinstall after a clean uninstall...'
        $reinstall = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($reinstall.ExitCode -ne 0) {
            throw "Setup reinstall failed with exit code $($reinstall.ExitCode)."
        }
        $setupInstalled = $true
        Assert-HooksDocument -HooksPath $setupHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe') `
            -RequireOtherHook

        Write-Host 'Testing invalid hooks.json warning while program uninstall still succeeds...'
        [System.IO.File]::WriteAllBytes($setupHooksPath, $invalidBytes)
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $invalidUninstallLog `
            -Environment @{ CODEX_HOME = $setupCodexHome })
        $setupInstalled = $false
        if ([System.Convert]::ToHexString($invalidBytes) -cne
            [System.Convert]::ToHexString([System.IO.File]::ReadAllBytes($setupHooksPath))) {
            throw 'The uninstaller changed invalid hooks.json instead of preserving it.'
        }
        $invalidLog = Get-Content -Raw -LiteralPath $invalidUninstallLog
        foreach ($requiredLogText in @(
            'AgentBell uninstall optional integration cleanup failed.',
            'AgentBell uninstall failed stage: integration',
            'AgentBell uninstall child exit code: 11')) {
            if (-not $invalidLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Invalid JSON uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Testing reinstall followed by uninstall after hooks.json is removed externally...'
        [System.IO.File]::WriteAllText($setupHooksPath, $setupFixture, $utf8NoBom)
        $missingReinstall = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$setupLog"
        ) -Environment @{ CODEX_HOME = $setupCodexHome }
        if ($missingReinstall.ExitCode -ne 0) {
            throw "Setup before missing-file uninstall failed with exit code $($missingReinstall.ExitCode)."
        }
        $setupInstalled = $true
        Remove-Item -LiteralPath $setupHooksPath -Force
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $missingUninstallLog `
            -Environment @{ CODEX_HOME = $setupCodexHome })
        $setupInstalled = $false
        $missingLog = Get-Content -Raw -LiteralPath $missingUninstallLog
        foreach ($requiredLogText in @(
            'AgentBell uninstall hooks.json exists: no',
            'AgentBell uninstall Codex cleanup: skipped (hooks.json missing).')) {
            if (-not $missingLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Missing-file uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Testing an explicit failure when CODEX_HOME and USERPROFILE are unavailable...'
        $missingProfileSetup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$missingProfileSetupLog"
        ) -Environment @{ CODEX_HOME = $null; USERPROFILE = $null }
        if ($missingProfileSetup.ExitCode -eq 0) {
            throw 'Setup returned zero when neither CODEX_HOME nor USERPROFILE was available.'
        }
        $setupInstalled = Test-Path -LiteralPath $uninstallerPath -PathType Leaf
        $missingProfileLog = Get-Content -Raw -LiteralPath $missingProfileSetupLog
        foreach ($requiredLogText in @(
            'AgentBell Integration Codex home resolution failed:',
            'USERPROFILE is not available',
            'AgentBell installation failed during Codex Integration stage codex_home_resolve')) {
            if (-not $missingProfileLog.Contains($requiredLogText, [StringComparison]::Ordinal)) {
                throw "Missing-profile Setup log omitted required diagnostic marker: $requiredLogText"
            }
        }
        if (-not $setupInstalled) {
            throw 'The missing-profile Setup scenario did not leave an uninstaller for cleanup testing.'
        }
        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $missingProfileCleanupLog `
            -Environment @{ CODEX_HOME = $setupCodexHome })
        $setupInstalled = $false

        Write-Host 'Testing USERPROFILE fallback with spaces and Unicode through fresh install, upgrade, and uninstall...'
        $fallbackHooksPath = Join-Path $fallbackCodexHome 'hooks.json'
        $fallbackEnvironment = @{ CODEX_HOME = $null; USERPROFILE = $fallbackProfile }
        $fallbackSetup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/LANG=en',
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$fallbackSetupLog"
        ) -Environment $fallbackEnvironment
        if ($fallbackSetup.ExitCode -ne 0) {
            throw "USERPROFILE fallback Setup failed with exit code $($fallbackSetup.ExitCode)."
        }
        $setupInstalled = $true
        Assert-HooksDocument -HooksPath $fallbackHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe')

        [System.IO.File]::WriteAllText($fallbackHooksPath, $setupFixture, $utf8NoBom)
        $fallbackUpgrade = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/LANG=en',
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$fallbackUpgradeLog"
        ) -Environment $fallbackEnvironment
        if ($fallbackUpgrade.ExitCode -ne 0) {
            throw "USERPROFILE fallback upgrade failed with exit code $($fallbackUpgrade.ExitCode)."
        }
        Assert-HooksDocument -HooksPath $fallbackHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe') `
            -RequireOtherHook

        $fallbackSetupLogContent = Get-Content -Raw -LiteralPath $fallbackSetupLog
        if (-not $fallbackSetupLogContent.Contains(
                "AgentBell Integration resolved CODEX_HOME: $fallbackCodexHome",
                [StringComparison]::OrdinalIgnoreCase) -or
            $fallbackSetupLogContent.Contains('Unknown constant', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The USERPROFILE fallback Setup did not log the resolved Codex home safely.'
        }

        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $fallbackUninstallLog `
            -Environment $fallbackEnvironment)
        $setupInstalled = $false
        Assert-OnlyOtherHookRemains -HooksPath $fallbackHooksPath
        $fallbackUninstallLogContent = Get-Content -Raw -LiteralPath $fallbackUninstallLog
        foreach ($requiredLogText in @(
            "AgentBell uninstall resolved CODEX_HOME: $fallbackCodexHome",
            'AgentBell uninstall hooks.json exists: yes',
            'AgentBell uninstall Codex cleanup: completed or safely skipped.')) {
            if (-not $fallbackUninstallLogContent.Contains(
                    $requiredLogText,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "USERPROFILE fallback uninstall log omitted required diagnostic marker: $requiredLogText"
            }
        }

        Write-Host 'Testing Simplified Chinese fresh install and stored-language uninstall...'
        $chineseHooksPath = Join-Path $chineseCodexHome 'hooks.json'
        [System.IO.File]::WriteAllText($chineseHooksPath, $setupFixture, $utf8NoBom)
        $chineseSetup = Invoke-CapturedProcess -FilePath $SetupPath -Arguments @(
            '/LANG=zhcn',
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/NOICONS',
            '/TASKS=',
            "/DIR=$setupInstallDirectory",
            "/LOG=$chineseSetupLog"
        ) -Environment @{ CODEX_HOME = $chineseCodexHome }
        if ($chineseSetup.ExitCode -ne 0) {
            throw "Simplified Chinese Setup failed with exit code $($chineseSetup.ExitCode)."
        }
        $setupInstalled = $true
        Assert-HooksDocument -HooksPath $chineseHooksPath `
            -ExpectedHookPath (Join-Path $setupInstallDirectory 'AgentBell.Hook.exe') `
            -RequireOtherHook
        $chineseSetupLogContent = Get-Content -Raw -LiteralPath $chineseSetupLog
        if (-not $chineseSetupLogContent.Contains('/LANG=zhcn', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Simplified Chinese Setup log did not record the selected language.'
        }

        [void](Invoke-TestUninstaller `
            -UninstallerPath $uninstallerPath `
            -InstallDirectory $setupInstallDirectory `
            -LogPath $chineseUninstallLog `
            -Environment @{ CODEX_HOME = $chineseCodexHome })
        $setupInstalled = $false
        Assert-OnlyOtherHookRemains -HooksPath $chineseHooksPath

        if (-not [string]::IsNullOrWhiteSpace($CriticalFailureSetupPath)) {
            Write-Host 'Testing critical uninstall failure returns nonzero and leaves installation intact...'
            $criticalSetup = Invoke-CapturedProcess -FilePath $CriticalFailureSetupPath -Arguments @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                '/NOICONS',
                '/TASKS=',
                "/DIR=$setupInstallDirectory",
                "/LOG=$setupLog"
            ) -Environment @{ CODEX_HOME = $setupCodexHome }
            if ($criticalSetup.ExitCode -ne 0) {
                throw "Critical-failure test Setup failed with exit code $($criticalSetup.ExitCode)."
            }
            $setupInstalled = $true

            $criticalUninstall = Invoke-CapturedProcess -FilePath $uninstallerPath -Arguments @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                '/DELETEUSERDATA=0',
                '/FORCECRITICALUNINSTALLFAILURE=1',
                "/LOG=$criticalUninstallLog"
            ) -Environment @{ CODEX_HOME = $setupCodexHome }
            if ($criticalUninstall.ExitCode -eq 0) {
                throw 'A forced critical uninstaller failure returned zero.'
            }
            if (-not (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) -or
                $null -eq (Get-AgentBellRegistration)) {
                throw 'The critical failure did not stop before deleting installation state.'
            }
            $criticalLog = Get-Content -Raw -LiteralPath $criticalUninstallLog
            if (-not $criticalLog.Contains(
                'AgentBell uninstall test stage: forced_critical_failure.',
                [StringComparison]::Ordinal)) {
                throw 'The forced critical failure was not recorded in the uninstall log.'
            }
            Write-Host "Verified critical uninstaller exit code: $($criticalUninstall.ExitCode)"

            [void](Invoke-TestUninstaller `
                -UninstallerPath $uninstallerPath `
                -InstallDirectory $setupInstallDirectory `
                -LogPath $criticalCleanupLog `
                -Environment @{ CODEX_HOME = $setupCodexHome })
            $setupInstalled = $false
        }
    }

    Write-Host 'Codex installer integration tests passed.' -ForegroundColor Green
}
finally {
    if ($setupInstalled -and (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
        try {
            [void](Invoke-CapturedProcess -FilePath $uninstallerPath -Arguments @(
                '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
            ) -Environment @{ CODEX_HOME = $setupCodexHome })
        }
        catch {
            Write-Warning 'The isolated test uninstaller could not complete; inspect the temporary test directory.'
        }
    }

    if (Test-Path -LiteralPath $tempRoot) {
        Remove-TestDirectory -Path $tempRoot
    }
}
