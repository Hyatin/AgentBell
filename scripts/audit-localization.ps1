[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\localization\hardcoded-ui-strings.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results = [Collections.Generic.List[object]]::new()
$remaining = 0
$resourceDocument = [xml](Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'src\AgentBell.Localization\Resources\Strings.resx'))
$windowsResourceKeys = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($item in $resourceDocument.root.data) {
    [void]$windowsResourceKeys.Add([string]$item.name)
}

function Add-Finding {
    param(
        [string]$Path,
        [int]$Line,
        [string]$Text,
        [string]$Classification,
        [bool]$Migrated,
        [string]$ExclusionReason
    )

    $script:results.Add([pscustomobject]@{
        file            = $Path.Replace('\', '/')
        line            = $Line
        text            = $Text
        classification  = $Classification
        migrated        = $Migrated
        exclusionReason = $ExclusionReason
    })
    if ($Classification -eq 'user_visible_hardcoded' -and -not $Migrated) {
        $script:remaining++
    }
}

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($repositoryRoot, $Path)
}

$windowsFiles = @(
    'src\AgentBell.Tray\MainForm.cs'
    'src\AgentBell.Tray\TrayApplicationContext.cs'
    'src\AgentBell.Tray\PairingUrlDisclosurePolicy.cs'
    'src\AgentBell.Tray\WindowsNotificationProjection.cs'
)
$windowsPattern = '(?:Text\s*=|MessageBox\.Show\(|CreateButton\(|Items\.Add\(|ToolStripMenuItem\()\s*\$?"(?<text>[^"\r\n]+)"'
foreach ($relative in $windowsFiles) {
    $path = Join-Path $repositoryRoot $relative
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        foreach ($match in [regex]::Matches($line, $windowsPattern)) {
            $text = $match.Groups['text'].Value
            if ($text -in @('AgentBell', '—')) {
                Add-Finding (Get-RelativePath $path) $lineNumber $text `
                    'product_name_or_symbol' $true 'Brand names and language-neutral symbols are intentionally unchanged.'
            }
            elseif ($windowsResourceKeys.Contains($text)) {
                Add-Finding (Get-RelativePath $path) $lineNumber $text `
                    'resource_key' $true 'The semantic key is resolved through the shared .resx localizer.'
            }
            else {
                Add-Finding (Get-RelativePath $path) $lineNumber $text `
                    'user_visible_hardcoded' $false ''
            }
        }
    }
}

$androidFiles = @(
    'android\AgentBell\app\src\main\java\com\hyatin\agentbell\MainActivity.kt'
    'android\AgentBell\app\src\main\java\com\hyatin\agentbell\notification\AgentBellNotificationManager.kt'
    'android\AgentBell\app\src\main\java\com\hyatin\agentbell\service\AgentBellConnectionService.kt'
)
$androidPattern = '(?:Text|setContentTitle|setContentText)\(\s*"(?<text>[^"\r\n]+)"'
foreach ($relative in $androidFiles) {
    $path = Join-Path $repositoryRoot $relative
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        foreach ($match in [regex]::Matches($line, $androidPattern)) {
            $text = $match.Groups['text'].Value
            if ($text -eq 'AgentBell') {
                Add-Finding (Get-RelativePath $path) $lineNumber $text `
                    'product_name' $true 'AgentBell is an untranslated product name.'
            }
            else {
                Add-Finding (Get-RelativePath $path) $lineNumber $text `
                    'user_visible_hardcoded' $false ''
            }
        }
    }
}

$installerPath = Join-Path $repositoryRoot 'installer\AgentBell.iss'
$inCustomMessages = $false
$expectsMessageArgument = $false
$lineNumber = 0
foreach ($line in Get-Content -LiteralPath $installerPath) {
    $lineNumber++
    if ($line -match '^\[CustomMessages\]') {
        $inCustomMessages = $true
        continue
    }
    if ($inCustomMessages -and $line -match '^\[') {
        $inCustomMessages = $false
    }
    if ($inCustomMessages -or $line -match '^\s*Log\(' -or $line -match '^\s*//') {
        continue
    }

    $isDirectMessageArgument = $expectsMessageArgument -and $line -match "^\s*'"
    if ($line -notmatch '^\s*$') {
        $expectsMessageArgument = $false
    }
    if ($line -match '(?:MsgBox|SuppressibleMsgBox)\(\s*$') {
        $expectsMessageArgument = $true
    }

    if ($isDirectMessageArgument -or
        $line -match '[\p{IsCJKUnifiedIdeographs}]' -or
        $line -match "(?:MsgBox|SuppressibleMsgBox)\(\s*'" -or
        $line -match "\.Caption\s*:=\s*'") {
        Add-Finding 'installer/AgentBell.iss' $lineNumber '<redacted-ui-literal>' `
            'user_visible_hardcoded' $false ''
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$localizationOnlyWindowsKeys = @(
    'Common_Back',
    'Common_Save',
    'Language_ChineseSimplified',
    'Language_English',
    'Language_System',
    'Localization_MissingText',
    'Settings_Language',
    'Settings_SaveLanguageFailed',
    'Settings_Title',
    'WindowsNotification_Body',
    'WindowsNotification_Title'
)
$androidResourceDocument = [xml](Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'android\AgentBell\app\src\main\res\values\strings.xml'))
$localizationOnlyAndroidKeys = @(
    'common_back',
    'common_settings',
    'language_chinese_simplified',
    'language_english',
    'language_system',
    'settings_language'
)
$installerEnglishKeys = @(
    Get-Content -LiteralPath $installerPath |
        Where-Object { $_ -match '^en\.([^=]+)=' } |
        ForEach-Object { $Matches[1] }
)
$baselineWindowsCount = @($resourceDocument.root.data).Count - $localizationOnlyWindowsKeys.Count
$baselineAndroidCount = @($androidResourceDocument.resources.string).Count - $localizationOnlyAndroidKeys.Count
$baselineInstallerCount = $installerEnglishKeys.Count
$baselineCount = $baselineWindowsCount + $baselineAndroidCount + $baselineInstallerCount
$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scannedAreas = @('Windows WinForms UI', 'Android Compose and notifications', 'Inno Setup Pascal UI')
    baselineUserVisibleHardcodedCount = $baselineCount
    baselineBreakdown = [ordered]@{
        windows = $baselineWindowsCount
        android = $baselineAndroidCount
        installer = $baselineInstallerCount
    }
    baselineDefinition = 'Unique semantic UI strings in the 0.5 source inventory; localization-setting strings introduced by this migration are excluded.'
    findings = $results
    remainingUserVisibleHardcodedCount = $remaining
}
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    $json,
    [Text.UTF8Encoding]::new($false))

Write-Host "Localization audit: findings=$($results.Count), remaining=$remaining"
Write-Host "Report: $([IO.Path]::GetFullPath($OutputPath))"
if ($remaining -ne 0) {
    exit 1
}
