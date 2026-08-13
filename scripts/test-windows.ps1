#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Solution = (Join-Path $PSScriptRoot '..\AgentBell.sln'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$solutionPath = [System.IO.Path]::GetFullPath($Solution)
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found: $solutionPath"
}

$repositoryRoot = Split-Path -Parent $solutionPath
$testProjects = @(
    'tests\AgentBell.Contracts.Tests\AgentBell.Contracts.Tests.csproj'
    'tests\AgentBell.Localization.Tests\AgentBell.Localization.Tests.csproj'
    'tests\AgentBell.Hook.Tests\AgentBell.Hook.Tests.csproj'
    'tests\AgentBell.Desktop.Tests\AgentBell.Desktop.Tests.csproj'
    'tests\AgentBell.Integration.Tests\AgentBell.Integration.Tests.csproj'
    'tests\AgentBell.Tray.Tests\AgentBell.Tray.Tests.csproj'
)

# Keep test assemblies out of the same process-pressure window. xUnit remains
# free to parallelize ordinary tests inside each assembly.
foreach ($relativeProject in $testProjects) {
    $project = Join-Path $repositoryRoot $relativeProject
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Windows test project not found: $relativeProject"
    }

    Write-Host "`n==> Testing $relativeProject" -ForegroundColor Cyan
    $arguments = @(
        'test'
        $project
        '-c'
        $Configuration
    )
    if ($NoBuild) {
        $arguments += '--no-build'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Windows test project failed with exit code $LASTEXITCODE`: $relativeProject"
    }
}

Write-Host "`nWindows test orchestration passed." -ForegroundColor Green
