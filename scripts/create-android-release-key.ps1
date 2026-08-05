[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Path = (Join-Path ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::MyDocuments)) 'AgentBell-Signing\agentbell-release.jks'),
    [string]$Alias = 'agentbell-release',
    [int]$ValidityDays = 10000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Alias)) {
    throw 'Path and Alias are required.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [System.IO.Path]::GetFullPath($Path)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if ($target.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release keystore must be created outside the repository.'
}

if (Test-Path -LiteralPath $target) {
    throw 'The target keystore already exists; it will not be overwritten.'
}

$keytool = Get-Command 'keytool.exe' -ErrorAction SilentlyContinue
if ($null -eq $keytool) {
    throw 'keytool.exe was not found. Install or select a JDK 17+ first.'
}

$targetDirectory = Split-Path -Parent $target
if ([string]::IsNullOrWhiteSpace($targetDirectory)) {
    throw 'The target directory is invalid.'
}

Write-Host "Target keystore: $target"
Write-Warning 'Back up this keystore and its credentials securely. Losing it prevents signed upgrades for com.hyatin.agentbell.'
if (-not $PSCmdlet.ShouldProcess($target, 'Create the long-term Android release signing keystore')) {
    return
}

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
& $keytool.Source -genkeypair -v `
    -keystore $target `
    -alias $Alias `
    -keyalg RSA `
    -keysize 4096 `
    -validity $ValidityDays
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw 'keytool did not create the release keystore.'
}

Write-Host 'Release keystore created. Passwords were handled by keytool and were not printed by this script.'
Write-Warning 'Store the keystore and credentials in separate secure backups. Never commit either one.'
