#requires -Version 7.2
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'InnoSetupVersion.ps1')

$testCount = 0

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-ThrowsLike {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Message Unexpected error: $($_.Exception.Message)"
        }
        return
    }
    throw "$Message No exception was thrown."
}

function New-Candidate {
    param([string]$Version, [string]$Location, [string]$Name = 'Inno Setup version fixture')
    return [pscustomobject]@{
        RegistryPath = "TestDrive:\Uninstall\$([Guid]::NewGuid().ToString('N'))"
        DisplayName = $Name
        DisplayVersion = $Version
        InstallLocation = $Location
    }
}

$noProbe = {
    param($Path)
    [pscustomobject]@{ Output = ''; Failure = 'disabled_for_test' }
}
$canonicalize = {
    param($Path)
    [System.IO.Path]::GetFullPath($Path.TrimEnd('\')).ToUpperInvariant()
}
$iscc = 'C:\Tools\Inno Setup 6\ISCC.exe'

$testCount++
$result = Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
    -ProductVersion '6.7.3.0' -FileVersion '6.6.0' -RegistryCandidates @() `
    -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
Assert-Equal '6.7.3.0' $result.Version.ToString() 'Valid ProductVersion was not selected.'
Assert-Equal 'ProductVersion' $result.Source 'ProductVersion source was not reported.'

$testCount++
$result = Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
    -ProductVersion '0.0.0.0' -FileVersion '6.6.1' -RegistryCandidates @() `
    -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
Assert-Equal '6.6.1' $result.Version.ToString() 'Valid FileVersion was not selected after zero ProductVersion.'
Assert-Equal 'FileVersion' $result.Source 'FileVersion source was not reported.'

$testCount++
$userInstall = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6'
$userIscc = Join-Path $userInstall 'ISCC.exe'
$result = Resolve-AgentBellInnoSetupVersion -IsccPath $userIscc `
    -ProductVersion '0.0.0.0' -FileVersion '0.0.0.0' `
    -RegistryCandidates @((New-Candidate '6.7.3' ($userInstall + '\'))) `
    -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
Assert-Equal '6.7.3' $result.Version.ToString() 'User-level registry version was not selected.'
Assert-Equal 'RegistryDisplayVersion' $result.Source 'Registry source was not reported.'

$testCount++
Assert-ThrowsLike -Pattern '*rejected=path_mismatch*' -Message 'A mismatched registry path was accepted.' -Action {
    Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
        -ProductVersion '0.0.0.0' -FileVersion '0.0.0.0' `
        -RegistryCandidates @((New-Candidate '6.7.3' 'D:\Different\Inno Setup 6')) `
        -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
}

$testCount++
Assert-ThrowsLike `
    -Pattern '*ISCC path:*ProductVersion raw: 0.0.0.0*FileVersion raw: invalid*DisplayVersion=6.7.3*rejected=path_mismatch*' `
    -Message 'Failure diagnostics omitted a raw source or registry rejection reason.' `
    -Action {
        Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
            -ProductVersion '0.0.0.0' -FileVersion 'invalid' `
            -RegistryCandidates @((New-Candidate '6.7.3' 'D:\Different\Inno Setup 6')) `
            -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
    }

$testCount++
$shortPathCanonicalizer = {
    param($Path)
    if ($Path -like 'C:\PROGRA~1\INNOSE~1*') {
        return 'C:\PROGRAM FILES\INNO SETUP 6'
    }
    return [System.IO.Path]::GetFullPath($Path.TrimEnd('\')).ToUpperInvariant()
}
$result = Resolve-AgentBellInnoSetupVersion `
    -IsccPath 'C:\Program Files\Inno Setup 6\ISCC.exe' `
    -ProductVersion '0.0.0.0' -FileVersion '0.0.0.0' `
    -RegistryCandidates @(
        (New-Candidate '6.4.0' 'C:\PROGRA~1\INNOSE~1'),
        (New-Candidate '6.7.3' 'C:\Program Files\Inno Setup 6\'),
        (New-Candidate '6.6.0' 'D:\Other')) `
    -CanonicalizeDirectory $shortPathCanonicalizer -CommandOutputProvider $noProbe
Assert-Equal '6.7.3' $result.Version.ToString() 'The highest matching qualified registry version was not selected.'

$testCount++
$compilerSelection = Select-AgentBellInnoSetupCompilerVersion -CompilerCandidates @(
    [pscustomobject]@{ Path = 'C:\Old\ISCC.exe'; ProductVersion = '6.3.3'; FileVersion = '6.3.3' },
    [pscustomobject]@{ Path = 'C:\Current\ISCC.exe'; ProductVersion = '6.7.3'; FileVersion = '6.7.3.0' },
    [pscustomobject]@{ Path = 'C:\Minimum\ISCC.exe'; ProductVersion = '6.4.0'; FileVersion = '6.4.0' }
) -RegistryCandidates @() -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
Assert-Equal '6.7.3' $compilerSelection.Version.ToString() 'The highest qualified compiler candidate was not selected.'
Assert-Equal 'C:\Current\ISCC.exe' $compilerSelection.IsccPath 'The highest qualified ISCC path was not selected.'

$testCount++
Assert-ThrowsLike -Pattern '*below 6.4.0*' -Message 'ProductVersion 6.3.x was accepted.' -Action {
    Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
        -ProductVersion '6.3.3' -FileVersion '6.7.3' -RegistryCandidates @() `
        -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
}

foreach ($accepted in @('6.4.0', '6.7.3', '6.7.3.0', 'Inno Setup version 6.7.3')) {
    $testCount++
    $result = Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
        -ProductVersion $accepted -FileVersion '' -RegistryCandidates @() `
        -CanonicalizeDirectory $canonicalize -CommandOutputProvider $noProbe
    if ($result.Version -lt [Version]'6.4.0') {
        throw "Accepted version '$accepted' resolved below the minimum."
    }
}

$testCount++
$result = Resolve-AgentBellInnoSetupVersion -IsccPath $iscc `
    -ProductVersion '0.0.0.0' -FileVersion '' -RegistryCandidates @() `
    -CanonicalizeDirectory $canonicalize -CommandOutputProvider {
        param($Path)
        [pscustomobject]@{ Output = 'Inno Setup version 6.7.3'; Failure = $null }
    }
Assert-Equal '6.7.3' $result.Version.ToString() 'Safe command output fallback was not parsed.'
Assert-Equal 'CommandOutput' $result.Source 'Command output source was not reported.'

Write-Host "Inno Setup version tests passed: $testCount/$testCount" -ForegroundColor Green
