[CmdletBinding()]
param(
    [string]$SetupPath,
    [switch]$PathResolutionSelfTestOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Get-AgentBellRegisteredInstallLocations {
    $appId = 'A17863B4-7E64-4D74-A0B4-004000000001'
    $uninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    $locations = New-Object System.Collections.Generic.List[string]
    foreach ($uninstallRoot in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) {
            continue
        }

        foreach ($entry in Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue) {
            if ($entry.PSChildName.IndexOf($appId, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            $properties = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $properties) {
                continue
            }

            $installLocationProperty = $properties.PSObject.Properties['InstallLocation']
            if ($null -ne $installLocationProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$installLocationProperty.Value)) {
                $locations.Add([string]$installLocationProperty.Value)
            }
        }
    }

    return @($locations | Select-Object -Unique)
}

function Resolve-AgentBellInstallDirectory {
    param(
        [AllowEmptyCollection()][string[]]$RegisteredInstallLocations,
        [AllowEmptyString()][string]$KnownFolderLocalApplicationData
    )

    if (-not $PSBoundParameters.ContainsKey('RegisteredInstallLocations')) {
        $RegisteredInstallLocations = @(Get-AgentBellRegisteredInstallLocations)
    }

    foreach ($registeredLocation in $RegisteredInstallLocations) {
        if (-not [string]::IsNullOrWhiteSpace($registeredLocation)) {
            return [System.IO.Path]::GetFullPath($registeredLocation.Trim().Trim('"'))
        }
    }

    if (-not $PSBoundParameters.ContainsKey('KnownFolderLocalApplicationData')) {
        $KnownFolderLocalApplicationData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)
    }

    if ([string]::IsNullOrWhiteSpace($KnownFolderLocalApplicationData)) {
        throw 'The Windows LocalApplicationData Known Folder is unavailable.'
    }

    return [System.IO.Path]::GetFullPath(
        (Join-Path $KnownFolderLocalApplicationData 'Programs\AgentBell'))
}

function Assert-AgentBellPathResolution {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
        ('AgentBell-M4-Path-' + [Guid]::NewGuid().ToString('N'))
    $environmentLocation = Join-Path $fixtureRoot 'environment-local-app-data'
    $knownFolderLocation = Join-Path $fixtureRoot 'known-folder-local-app-data'
    $registeredLocation = Join-Path $fixtureRoot 'registered-install-location'
    $priorLocalAppData = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = $environmentLocation
        $fallbackResult = Resolve-AgentBellInstallDirectory `
            -RegisteredInstallLocations @() `
            -KnownFolderLocalApplicationData $knownFolderLocation
        $expectedFallback = Join-Path $knownFolderLocation 'Programs\AgentBell'
        if (-not [string]::Equals(
            $fallbackResult,
            [System.IO.Path]::GetFullPath($expectedFallback),
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Known Folder fallback incorrectly used the LOCALAPPDATA environment variable.'
        }

        $registeredResult = Resolve-AgentBellInstallDirectory `
            -RegisteredInstallLocations @($registeredLocation) `
            -KnownFolderLocalApplicationData $knownFolderLocation
        if (-not [string]::Equals(
            $registeredResult,
            [System.IO.Path]::GetFullPath($registeredLocation),
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The registered AgentBell InstallLocation did not take precedence.'
        }
    }
    finally {
        if ($null -eq $priorLocalAppData) {
            Remove-Item Env:LOCALAPPDATA -ErrorAction SilentlyContinue
        }
        else {
            $env:LOCALAPPDATA = $priorLocalAppData
        }
    }
}

Assert-AgentBellPathResolution
Write-Host 'M4 Known Folder path resolution self-test passed.'
if ($PathResolutionSelfTestOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $setupCandidates = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts\m4-installer') `
        -Filter 'AgentBell-Setup-*.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($setupCandidates.Count -ne 1) {
        throw 'Pass -SetupPath or leave exactly one AgentBell-Setup-*.exe in artifacts\m4-installer.'
    }

    $SetupPath = $setupCandidates[0].FullName
}
else {
    $SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
}

$setupInfo = Get-Item -LiteralPath $SetupPath
if ($setupInfo.Length -le 0) {
    throw "Setup is empty: $SetupPath"
}

$installDir = Resolve-AgentBellInstallDirectory
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AgentBell-M4-Smoke-' + [Guid]::NewGuid().ToString('N'))
$testCodexHome = Join-Path $tempRoot 'codex home 中文'
$testDataHome = Join-Path $tempRoot 'data home 中文'
$hooksPath = Join-Path $testCodexHome 'hooks.json'
$configTomlPath = Join-Path $testCodexHome 'config.toml'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$priorCodexHome = $env:CODEX_HOME
$priorDataHome = $env:AGENTBELL_DATA_HOME
$priorTestMode = $env:AGENTBELL_TEST_MODE
$priorTestLoopbackPort = $env:AGENTBELL_TEST_LOOPBACK_PORT
$priorTestLanPort = $env:AGENTBELL_TEST_LAN_PORT
$priorTestInstanceId = $env:AGENTBELL_TEST_INSTANCE_ID
$setupInstalled = $false
$testTrayProcessIds = New-Object System.Collections.Generic.List[int]

function Get-IsolatedTcpPort {
    param([int[]]$ExcludedPorts = @())

    while ($true) {
        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            0)
        try {
            $listener.Start()
            $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        }
        finally {
            $listener.Stop()
        }

        if ($port -ne 17863 -and
            ($port -lt 17864 -or $port -gt 17874) -and
            $ExcludedPorts -notcontains $port) {
            return $port
        }
    }
}

$testLoopbackPort = Get-IsolatedTcpPort
$testLanPort = Get-IsolatedTcpPort -ExcludedPorts @($testLoopbackPort)

function Invoke-ProcessChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments `
        -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "$FailureMessage Exit code: $($process.ExitCode)."
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-AgentBellHookCount {
    $root = Get-Content -Raw -LiteralPath $hooksPath | ConvertFrom-Json
    $count = 0
    foreach ($group in @($root.hooks.Stop)) {
        foreach ($hook in @($group.hooks)) {
            $commandText = ([string]$hook.command) + ' ' + ([string]$hook.commandWindows)
            if ($commandText.IndexOf('AgentBell.Hook.exe', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $commandText.IndexOf('--codex-stop-hook', [StringComparison]::Ordinal) -ge 0) {
                $count++
            }
        }
    }

    return $count
}

function Assert-OtherHookPreserved {
    $root = Get-Content -Raw -LiteralPath $hooksPath | ConvertFrom-Json
    $commands = @()
    foreach ($group in @($root.hooks.Stop)) {
        foreach ($hook in @($group.hooks)) {
            $commands += [string]$hook.command
        }
    }

    if ($commands -notcontains 'other-tool.exe --stop') {
        throw 'The pre-existing non-AgentBell Stop Hook was not preserved.'
    }

    if ([string]$root.owner -cne 'm4-smoke') {
        throw 'The unrelated top-level hooks.json field was not preserved.'
    }
}

function Wait-AgentBellListeners {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $ProcessId `
            -ErrorAction SilentlyContinue)
        $loopback = @($listeners | Where-Object {
            $_.LocalAddress -eq '127.0.0.1' -and $_.LocalPort -eq $testLoopbackPort
        })
        $lan = @($listeners | Where-Object {
            $_.LocalAddress -eq '127.0.0.1' -and $_.LocalPort -eq $testLanPort
        })
        if ($loopback.Count -eq 1 -and $lan.Count -eq 1) {
            return $listeners
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'AgentBell.Tray did not expose exactly the two isolated loopback test listeners within 20 seconds.'
}

function Stop-AgentBellTray {
    $trayPath = Join-Path $installDir 'AgentBell.Tray.exe'
    if (Test-Path -LiteralPath $trayPath -PathType Leaf) {
        $process = Start-Process -FilePath $trayPath -ArgumentList '--shutdown' `
            -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -notin @(0, 10)) {
            throw "Tray shutdown request failed with exit code $($process.ExitCode)."
        }
    }

    foreach ($processId in @($testTrayProcessIds)) {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while ($null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue) -and
            [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
    }
}

function Invoke-TestStopHook {
    $hookPath = Join-Path $installDir 'AgentBell.Hook.exe'
    $payload = [ordered]@{
        session_id = 'm4-smoke-session-' + [Guid]::NewGuid().ToString('N')
        turn_id = 'm4-smoke-turn-' + [Guid]::NewGuid().ToString('N')
        cwd = (Join-Path $tempRoot 'project')
        hook_event_name = 'Stop'
        last_assistant_message = 'M4 smoke completed.'
        stop_hook_active = $false
    } | ConvertTo-Json -Compress

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $hookPath
    $startInfo.Arguments = '--codex-stop-hook'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.StandardInputEncoding = $utf8NoBom
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $process.StandardInput.Write($payload)
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd().TrimEnd("`r", "`n")
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0 -or $stdout -cne '{"continue":true}' -or $stderr.Length -ne 0) {
        throw 'The installed Stop Hook did not preserve the required stdin/stdout contract.'
    }
}

if (Test-Path -LiteralPath $installDir) {
    throw "Refusing to overwrite an existing AgentBell installation during a smoke test: $installDir"
}

$existingStartup = Get-ItemPropertyValue -LiteralPath $runKeyPath -Name 'AgentBell' `
    -ErrorAction SilentlyContinue
if ($null -ne $existingStartup) {
    throw 'Refusing to overwrite an existing AgentBell HKCU Run value during a smoke test.'
}

try {
    New-Item -ItemType Directory -Path $testCodexHome, $testDataHome -Force | Out-Null
    $hooksFixture = @'
{
  "owner": "m4-smoke",
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "other-tool.exe --stop"
          }
        ]
      }
    ],
    "SessionStart": []
  }
}
'@
    $configFixture = "notify = ['existing-notify.exe']`r`nmodel = 'fixture'`r`n"
    [System.IO.File]::WriteAllText($hooksPath, $hooksFixture, $utf8NoBom)
    [System.IO.File]::WriteAllText($configTomlPath, $configFixture, $utf8NoBom)
    $configTomlBefore = Get-FileSha256 -Path $configTomlPath

    $env:CODEX_HOME = $testCodexHome
    $env:AGENTBELL_DATA_HOME = $testDataHome
    $env:AGENTBELL_TEST_MODE = '1'
    $env:AGENTBELL_TEST_LOOPBACK_PORT = [string]$testLoopbackPort
    $env:AGENTBELL_TEST_LAN_PORT = [string]$testLanPort
    $env:AGENTBELL_TEST_INSTANCE_ID = [Guid]::NewGuid().ToString('N')

    Write-Host 'Installing into the current-user stable directory...'
    Invoke-ProcessChecked -FilePath $SetupPath -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup'
    ) -FailureMessage 'Initial silent installation failed.'
    $setupInstalled = $true
    $installDir = Resolve-AgentBellInstallDirectory

    foreach ($relativePath in @(
        'AgentBell.Tray.exe',
        'AgentBell.Hook.exe',
        'AgentBell.Integration.exe',
        'android\AgentBell-debug.apk',
        'unins000.exe'
    )) {
        $installedPath = Join-Path $installDir $relativePath
        if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf)) {
            throw "Installed file is missing: $installedPath"
        }
    }

    if ((Get-AgentBellHookCount) -ne 1) {
        throw 'Installation did not merge exactly one AgentBell Stop Hook.'
    }

    Assert-OtherHookPreserved
    if ((Get-FileSha256 -Path $configTomlPath) -cne $configTomlBefore) {
        throw 'Installation changed config.toml or the existing notify setting.'
    }

    $expectedStartup = '"' + (Join-Path $installDir 'AgentBell.Tray.exe') + '" --startup'
    $startupValue = Get-ItemPropertyValue -LiteralPath $runKeyPath -Name 'AgentBell'
    if ($startupValue -cne $expectedStartup) {
        throw "The startup value is incorrect: $startupValue"
    }

    $trayPath = Join-Path $installDir 'AgentBell.Tray.exe'
    $trayProcess = Start-Process -FilePath $trayPath -ArgumentList '--startup' -PassThru
    $testTrayProcessIds.Add($trayProcess.Id)
    [void](Wait-AgentBellListeners -ProcessId $trayProcess.Id)
    Invoke-TestStopHook

    $configPath = Join-Path $testDataHome 'config.json'
    $eventsPath = Join-Path $testDataHome 'events.json'
    if (-not (Test-Path -LiteralPath $configPath) -or -not (Test-Path -LiteralPath $eventsPath)) {
        throw 'Tray did not create the expected isolated config.json and events.json.'
    }

    $configBeforeUpgrade = Get-FileSha256 -Path $configPath
    $eventsBeforeUpgrade = Get-FileSha256 -Path $eventsPath

    Write-Host 'Running an in-place upgrade while Tray is active...'
    Invoke-ProcessChecked -FilePath $SetupPath -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    ) -FailureMessage 'Silent upgrade failed.'

    if ((Get-AgentBellHookCount) -ne 1) {
        throw 'Upgrade produced a duplicate AgentBell Hook.'
    }

    Assert-OtherHookPreserved
    if ((Get-FileSha256 -Path $configTomlPath) -cne $configTomlBefore -or
        (Get-FileSha256 -Path $configPath) -cne $configBeforeUpgrade -or
        (Get-FileSha256 -Path $eventsPath) -cne $eventsBeforeUpgrade) {
        throw 'Upgrade changed config.toml, pairing configuration, or events.json.'
    }

    $startupAfterUpgrade = Get-ItemPropertyValue -LiteralPath $runKeyPath -Name 'AgentBell'
    if ($startupAfterUpgrade -cne $expectedStartup) {
        throw 'Upgrade did not preserve the startup selection.'
    }

    $trayProcess = Start-Process -FilePath $trayPath -ArgumentList '--startup' -PassThru
    $testTrayProcessIds.Add($trayProcess.Id)
    [void](Wait-AgentBellListeners -ProcessId $trayProcess.Id)

    Write-Host 'Uninstalling with the default data-retention behavior...'
    $uninstallerPath = Join-Path $installDir 'unins000.exe'
    Invoke-ProcessChecked -FilePath $uninstallerPath -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    ) -FailureMessage 'Silent uninstall failed.'
    $setupInstalled = $false

    if (Test-Path -LiteralPath (Join-Path $installDir 'AgentBell.Tray.exe')) {
        throw 'Uninstall did not remove the installed program files.'
    }

    $startupAfterUninstall = Get-ItemPropertyValue -LiteralPath $runKeyPath `
        -Name 'AgentBell' -ErrorAction SilentlyContinue
    if ($null -ne $startupAfterUninstall) {
        throw 'Uninstall did not remove the AgentBell startup value.'
    }

    if ((Get-AgentBellHookCount) -ne 0) {
        throw 'Uninstall did not remove exactly the AgentBell Hook.'
    }

    Assert-OtherHookPreserved
    if ((Get-FileSha256 -Path $configTomlPath) -cne $configTomlBefore) {
        throw 'Uninstall changed config.toml or notify.'
    }

    if (-not (Test-Path -LiteralPath $configPath) -or
        -not (Test-Path -LiteralPath $eventsPath) -or
        (Get-FileSha256 -Path $configPath) -cne $configBeforeUpgrade -or
        (Get-FileSha256 -Path $eventsPath) -cne $eventsBeforeUpgrade) {
        throw 'Default uninstall did not retain pairing and event data.'
    }

    Write-Host 'M4 installer smoke test passed.' -ForegroundColor Green
}
finally {
    try {
        Stop-AgentBellTray
        if ($setupInstalled) {
            $uninstallerPath = Join-Path $installDir 'unins000.exe'
            if (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) {
                Start-Process -FilePath $uninstallerPath -ArgumentList @(
                    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
                ) -Wait -WindowStyle Hidden | Out-Null
            }
        }
    }
    finally {
        if ($null -eq $priorCodexHome) {
            Remove-Item Env:CODEX_HOME -ErrorAction SilentlyContinue
        }
        else {
            $env:CODEX_HOME = $priorCodexHome
        }

        if ($null -eq $priorDataHome) {
            Remove-Item Env:AGENTBELL_DATA_HOME -ErrorAction SilentlyContinue
        }
        else {
            $env:AGENTBELL_DATA_HOME = $priorDataHome
        }

        if ($null -eq $priorTestMode) {
            Remove-Item Env:AGENTBELL_TEST_MODE -ErrorAction SilentlyContinue
        }
        else {
            $env:AGENTBELL_TEST_MODE = $priorTestMode
        }

        if ($null -eq $priorTestLoopbackPort) {
            Remove-Item Env:AGENTBELL_TEST_LOOPBACK_PORT -ErrorAction SilentlyContinue
        }
        else {
            $env:AGENTBELL_TEST_LOOPBACK_PORT = $priorTestLoopbackPort
        }

        if ($null -eq $priorTestLanPort) {
            Remove-Item Env:AGENTBELL_TEST_LAN_PORT -ErrorAction SilentlyContinue
        }
        else {
            $env:AGENTBELL_TEST_LAN_PORT = $priorTestLanPort
        }

        if ($null -eq $priorTestInstanceId) {
            Remove-Item Env:AGENTBELL_TEST_INSTANCE_ID -ErrorAction SilentlyContinue
        }
        else {
            $env:AGENTBELL_TEST_INSTANCE_ID = $priorTestInstanceId
        }

        $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
        $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            $resolvedTemp.IndexOf('AgentBell-M4-Smoke-', [StringComparison]::Ordinal) -ge 0) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
