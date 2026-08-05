[CmdletBinding()]
param(
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repoRoot 'artifacts\audit\public-release-audit.json'
}
else {
    $ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
}

$approvedCredentialPlaceholders = @(
    '<TOKEN>',
    '<PRIVATE_IPV4>',
    '<PORT>',
    '<INSTALL_DIR>',
    '<REPOSITORY_ROOT>',
    '<CODEX_HOME>',
    '<LOCAL_APP_DATA>',
    '<REDACTED>'
)
$placeholderDocumentExtensions = @('.md', '.txt')
$textExtensions = @(
    '.cs', '.csproj', '.props', '.sln', '.kt', '.kts', '.xml', '.json',
    '.md', '.txt', '.ps1', '.yml', '.yaml', '.iss', '.properties'
)
$excludedDirectoryNames = @(
    '.git', 'artifacts', 'bin', 'obj', 'build', '.gradle', '.idea', '.vs', '.kotlin'
)
$dangerousExtensions = @('.jks', '.keystore', '.pfx', '.p12', '.snk', '.cer', '.key', '.pem')
$buildArtifactExtensions = @('.apk', '.exe', '.dll', '.pdb')
$privateIpv4Pattern = '(?<![0-9])(?:10(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){3}|192\.168(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){2}|172\.(?:1[6-9]|2[0-9]|3[01])(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){2})(?![0-9])'
$localUserDirectoryPattern = '(?i)[A-Z]:\\Users\\(?!Example(?:\\|$)|Public(?:\\|$)|Private(?:\\|$)|First Last(?:\\|$)|\.\.\.(?:\\|$))[^\\\s`"'']+'
$absoluteAgentBellPathPattern = '(?i)(?<![A-Za-z0-9_])[A-Z]:\\(?:[^\\\r\n`"'']+\\)*AgentBell(?=\\|[\s`"'']|$)'
$credentialPatterns = [ordered]@{
    bearer_content = '\bBearer[ \t]+(?<value>[^\s`"'',}]+)'
    token_parameter_content = '(?i)(?:[?#&]|\b)(?:access_token|token)=(?<value>[^&\s`"'',)]+)'
    encrypted_pairing_token_content = '(?i)["'']encryptedPairingToken["'']\s*:\s*["''](?<value>[^"'']+)'
    password_or_secret_content = '(?i)\b(?:password|secret)\s*[:=]\s*["''](?<value>[^"'']{12,})["'']'
}

$findings = New-Object System.Collections.Generic.List[object]

function Get-Fingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Values)

    $joined = (@($Values | Sort-Object -Unique) -join "`0")
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($joined))
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').Substring(0, 16).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('error', 'warning', 'info')][string]$Severity,
        [Parameter(Mandatory = $true)][string]$Rule,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][ValidateSet(
            'real_sensitive_value',
            'local_path_or_username',
            'private_ip_literal',
            'safe_placeholder',
            'protocol_keyword',
            'false_positive',
            'repository_hygiene',
            'signing_material'
        )][string]$Category,
        [string]$Fingerprint
    )

    $safePath = $RelativePath.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($Fingerprint)) {
        $Fingerprint = Get-Fingerprint @($Rule, $safePath)
    }
    $findings.Add([pscustomobject]@{
            severity = $Severity
            code = $Rule
            rule = $Rule
            category = $Category
            path = $safePath
            fingerprint = $Fingerprint
        })
}

function Add-MatchFinding {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('error', 'warning', 'info')][string]$Severity,
        [Parameter(Mandatory = $true)][string]$Rule,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Values
    )

    Add-Finding $Severity $Rule $RelativePath $Category (Get-Fingerprint $Values)
}

function Test-IgnoredBuildPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = '/' + $RelativePath.Replace('\', '/').TrimStart('/')
    return $path -match '/(artifacts|bin|obj|build|\.gradle|\.idea|\.vs|\.kotlin)/'
}

function Get-RelativePath {
    param([Parameter(Mandatory = $true)][string]$FullPath)
    return $FullPath.Substring($repoRoot.Length).TrimStart('\', '/')
}

function Test-DocumentFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    return $placeholderDocumentExtensions -contains [System.IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
}

function Test-TestSourceFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = '/' + $RelativePath.Replace('\', '/').TrimStart('/')
    $extension = [System.IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    return $extension -in @('.cs', '.kt', '.kts', '.java') -and
        $path -match '/(?:tests?|src/test|src/androidTest)/'
}

function Test-ApprovedCredentialPlaceholder {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    return (Test-DocumentFile $RelativePath) -and $approvedCredentialPlaceholders -ccontains $Value
}

function Test-LegacyHistoricalPlaceholder {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    return (Test-DocumentFile $RelativePath) -and
        $Value -cmatch '^<(?:token|base64url|base64url-token|private-ip|actual-port|host|port|name|one-RFC1918-address|17864-17874)>$'
}

function Test-DynamicCredentialReference {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value -match '(?:^\$|\$\{|^\{|\{[A-Za-z_]|\$[A-Za-z_])'
}

function Test-SyntheticTestCredential {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    return (Test-TestSourceFile $RelativePath) -and $Value.Length -le 24 -and (
        $Value -match '(?i)^(?:test|invalid|wrong|fake|dummy|example|secret|redacted)$' -or
        $Value -match '(?i)^[a-z][a-z0-9]{0,11}(?:[-_][a-z0-9]{1,12}){1,3}$'
    )
}

function Test-CodeRedactionMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    $extension = [System.IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    return $extension -in @('.cs', '.kt', '.kts', '.java') -and
        $Value -cin @('<REDACTED>', '<redacted>')
}

function Test-ExplicitInvalidDocumentFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    return (Test-DocumentFile $RelativePath) -and $Value.Length -le 32 -and
        $Value -cmatch '^(?:definitely|intentionally)-(?:wrong|invalid)-token(?:-[0-9]+)?$'
}

function Add-CredentialFindings {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [switch]$Historical
    )

    foreach ($entry in $credentialPatterns.GetEnumerator()) {
        foreach ($match in [regex]::Matches($Content, $entry.Value)) {
            $value = $match.Groups['value'].Value
            if (Test-ApprovedCredentialPlaceholder $value $RelativePath) {
                Add-MatchFinding info 'approved_credential_placeholder' $RelativePath `
                    safe_placeholder @($entry.Key, $value)
            }
            elseif ($Historical -and (Test-LegacyHistoricalPlaceholder $value $RelativePath)) {
                Add-MatchFinding warning 'historical_legacy_safe_placeholder' $RelativePath `
                    safe_placeholder @($entry.Key, $value)
            }
            elseif ($Historical -and (Test-ExplicitInvalidDocumentFixture $value $RelativePath)) {
                Add-MatchFinding warning 'historical_explicit_invalid_credential_fixture' $RelativePath `
                    safe_placeholder @($entry.Key, $value)
            }
            elseif (Test-CodeRedactionMarker $value $RelativePath) {
                Add-MatchFinding info 'code_redaction_marker' $RelativePath `
                    safe_placeholder @($entry.Key, $value)
            }
            elseif (Test-DynamicCredentialReference $value) {
                Add-MatchFinding info 'dynamic_credential_reference' $RelativePath `
                    protocol_keyword @($entry.Key, $value)
            }
            elseif (Test-SyntheticTestCredential $value $RelativePath) {
                Add-MatchFinding warning 'synthetic_test_credential_requires_manual_review' $RelativePath `
                    safe_placeholder @($entry.Key, $value)
            }
            elseif ($value -match '^<[^>]+>$') {
                Add-MatchFinding error 'unapproved_credential_placeholder' $RelativePath `
                    real_sensitive_value @($entry.Key, $value)
            }
            else {
                $rule = if ($Historical) { 'concrete_credential_content_in_history' } else { 'concrete_credential_content' }
                Add-MatchFinding error $rule $RelativePath real_sensitive_value @($entry.Key, $value)
            }
        }
    }
}

function Add-ProtocolUrlFindings {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [switch]$Historical
    )

    foreach ($match in [regex]::Matches($Content, '(?i)(?:agentbell|https?|wss?)://[^\s`"'']+')) {
        $value = $match.Value
        if ($value -notmatch '(?i)(?:/pair(?:[/?#]|$)|(?:[?#&]|\b)(?:access_token|token)=|/ws/v1/events)') {
            continue
        }

        $placeholders = @([regex]::Matches($value, '<[^>]+>') | ForEach-Object Value)
        if ($placeholders.Count -ne 0) {
            $allApproved = (Test-DocumentFile $RelativePath) -and
                @($placeholders | Where-Object { $approvedCredentialPlaceholders -cnotcontains $_ }).Count -eq 0
            $legacyApproved = $Historical -and (Test-DocumentFile $RelativePath) -and
                @($placeholders | Where-Object {
                        -not (Test-LegacyHistoricalPlaceholder $_ $RelativePath) -and
                        $approvedCredentialPlaceholders -cnotcontains $_
                    }).Count -eq 0
            if ($allApproved) {
                Add-MatchFinding info 'approved_protocol_url_placeholder' $RelativePath `
                    safe_placeholder @($value)
            }
            elseif ($legacyApproved) {
                Add-MatchFinding warning 'historical_legacy_protocol_url_placeholder' $RelativePath `
                    safe_placeholder @($value)
            }
            else {
                Add-MatchFinding error 'unapproved_protocol_url_placeholder' $RelativePath `
                    real_sensitive_value @($value)
            }
            continue
        }

        if (Test-DynamicCredentialReference $value) {
            if (-not (Test-TestSourceFile $RelativePath) -and -not (Test-DocumentFile $RelativePath) -and
                $value -match '(?i)(?:agentbell://pair|/pair(?:[/?#]|$))') {
                Add-MatchFinding warning 'dynamic_pairing_url_builder_requires_manual_review' $RelativePath `
                    protocol_keyword @($value)
            }
            else {
                Add-MatchFinding info 'dynamic_protocol_url_reference' $RelativePath `
                    false_positive @($value)
            }
        }
        elseif (Test-TestSourceFile $RelativePath) {
            Add-MatchFinding info 'synthetic_test_protocol_url' $RelativePath false_positive @($value)
        }
        else {
            $rule = if ($Historical) { 'concrete_protocol_url_in_history' } else { 'concrete_protocol_url' }
            Add-MatchFinding warning $rule $RelativePath protocol_keyword @($value)
        }
    }
}

$tracked = @(& git -C $repoRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files failed.'
}

foreach ($relativePath in $tracked) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        continue
    }

    if (Test-IgnoredBuildPath $relativePath) {
        Add-Finding error 'tracked_build_output' $relativePath repository_hygiene
    }

    $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
    if ($dangerousExtensions -contains $extension) {
        Add-Finding error 'tracked_signing_material' $relativePath signing_material
    }

    if ($buildArtifactExtensions -contains $extension -or
        $relativePath -match '(^|/)AgentBell-Diagnostics-.*\.zip$') {
        Add-Finding error 'tracked_binary_or_diagnostic_artifact' $relativePath repository_hygiene
    }

    if ($relativePath -match '(^|/)(local\.properties|config\.json|events\.json|hooks\.json.*|secrets\..*|\.env(?:\..*)?)$' -and
        $relativePath -notmatch '\.env\.example$') {
        Add-Finding error 'tracked_private_state' $relativePath repository_hygiene
    }
}

$textFiles = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force | Where-Object {
        $relative = Get-RelativePath $_.FullName
        -not (($relative -split '[\\/]') | Where-Object { $excludedDirectoryNames -contains $_ }) -and
        $textExtensions -contains $_.Extension.ToLowerInvariant()
    })

foreach ($file in $textFiles) {
    $relative = Get-RelativePath $file.FullName
    if ($relative -eq 'scripts\audit-public-release.ps1') {
        continue
    }

    $content = [System.IO.File]::ReadAllText($file.FullName)
    $privateKeys = @([regex]::Matches($content, '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') |
        ForEach-Object Value)
    if ($privateKeys.Count -ne 0) {
        Add-MatchFinding error 'private_key_material' $relative real_sensitive_value $privateKeys
    }

    $localMachineValues = @(
        @([regex]::Matches($content, $localUserDirectoryPattern) | ForEach-Object Value)
        @([regex]::Matches($content, $absoluteAgentBellPathPattern) | ForEach-Object Value)
    )
    if ($localMachineValues.Count -ne 0) {
        Add-MatchFinding error 'local_machine_path_or_username' $relative `
            local_path_or_username $localMachineValues
    }

    Add-CredentialFindings $content $relative
    Add-ProtocolUrlFindings $content $relative

    $privateAddresses = @([regex]::Matches($content, $privateIpv4Pattern) | ForEach-Object Value)
    if ($privateAddresses.Count -ne 0) {
        if (Test-DocumentFile $relative) {
            Add-MatchFinding error 'private_ip_in_public_document' $relative `
                private_ip_literal $privateAddresses
        }
        else {
            Add-MatchFinding warning 'private_ip_literal_requires_manual_review' $relative `
                private_ip_literal $privateAddresses
        }
    }
}

$dangerousFiles = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force | Where-Object {
        $dangerousExtensions -contains $_.Extension.ToLowerInvariant()
    })
foreach ($file in $dangerousFiles) {
    $relative = Get-RelativePath $file.FullName
    $severity = if (Test-IgnoredBuildPath $relative) { 'warning' } else { 'error' }
    Add-Finding $severity 'signing_material_in_worktree' $relative signing_material
}

$publicApks = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts\release') `
        -Filter '*.apk' -File -Recurse -ErrorAction SilentlyContinue)
$apkSigner = $null
if ($publicApks.Count -ne 0) {
    $sdkCandidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) { $sdkCandidates.Add($env:ANDROID_SDK_ROOT) }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) { $sdkCandidates.Add($env:ANDROID_HOME) }
    $knownLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($knownLocalAppData)) {
        $sdkCandidates.Add((Join-Path $knownLocalAppData 'Android\Sdk'))
    }
    foreach ($sdk in $sdkCandidates | Select-Object -Unique) {
        $candidate = Get-ChildItem -LiteralPath (Join-Path $sdk 'build-tools') -Filter 'apksigner.bat' `
            -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($null -ne $candidate) {
            $apkSigner = $candidate.FullName
            break
        }
    }
}
foreach ($apk in $publicApks) {
    $relative = Get-RelativePath $apk.FullName
    if ($apk.Name -match '(?i)debug') {
        Add-Finding error 'debug_apk_in_release_assets' $relative repository_hygiene
    }
    elseif ($null -eq $apkSigner) {
        Add-Finding error 'release_apk_signature_unverified' $relative signing_material
    }
    else {
        $verificationOutput = @(& $apkSigner verify --verbose --print-certs $apk.FullName 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Add-Finding error 'release_apk_signature_invalid' $relative signing_material
        }
        elseif (($verificationOutput -join "`n") -match '(?i)Android Debug') {
            Add-Finding error 'release_apk_uses_debug_certificate' $relative signing_material
        }
        else {
            Add-Finding info 'release_apk_signature_verified' $relative signing_material
        }
    }
}

$historyCommits = @(& git -C $repoRoot rev-list --all)
if ($LASTEXITCODE -ne 0) {
    throw 'git rev-list failed.'
}
if ($historyCommits.Count -eq 0) {
    Add-Finding info 'git_history_empty' '.' repository_hygiene
}
else {
    foreach ($commit in $historyCommits) {
        $historyFileNames = @(& git -C $repoRoot ls-tree -r --name-only $commit)
        foreach ($historyFileName in $historyFileNames) {
            $extension = [System.IO.Path]::GetExtension($historyFileName).ToLowerInvariant()
            if (Test-IgnoredBuildPath $historyFileName) {
                Add-Finding error 'tracked_build_output_in_history' $historyFileName repository_hygiene
            }
            if ($dangerousExtensions -contains $extension) {
                Add-Finding error 'signing_material_in_history' $historyFileName signing_material
            }
            if ($buildArtifactExtensions -contains $extension -or
                $historyFileName -match '(^|/)AgentBell-Diagnostics-.*\.zip$') {
                Add-Finding error 'binary_or_diagnostic_artifact_in_history' $historyFileName repository_hygiene
            }
            if ($historyFileName -match '(^|/)(local\.properties|config\.json|events\.json|hooks\.json.*|secrets\..*|\.env(?:\..*)?)$' -and
                $historyFileName -notmatch '\.env\.example$') {
                Add-Finding error 'private_state_in_history' $historyFileName repository_hygiene
            }

            if ($textExtensions -notcontains $extension) {
                continue
            }
            $content = @(& git -C $repoRoot show "$commit`:$historyFileName" 2>$null) -join "`n"
            if ($LASTEXITCODE -ne 0) {
                throw 'Reading a text blob from Git history failed.'
            }

            $privateKeys = @([regex]::Matches($content, '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') |
                ForEach-Object Value)
            if ($privateKeys.Count -ne 0) {
                Add-MatchFinding error 'private_key_material_in_history' $historyFileName `
                    real_sensitive_value $privateKeys
            }

            $localMachineValues = @(
                @([regex]::Matches($content, $localUserDirectoryPattern) | ForEach-Object Value)
                @([regex]::Matches($content, $absoluteAgentBellPathPattern) | ForEach-Object Value)
            )
            if ($localMachineValues.Count -ne 0) {
                Add-MatchFinding warning 'historical_local_machine_value_requires_manual_review' `
                    $historyFileName local_path_or_username $localMachineValues
            }

            Add-CredentialFindings $content $historyFileName -Historical
            Add-ProtocolUrlFindings $content $historyFileName -Historical

            $privateAddresses = @([regex]::Matches($content, $privateIpv4Pattern) | ForEach-Object Value)
            if ($privateAddresses.Count -ne 0) {
                Add-MatchFinding warning 'historical_private_ip_requires_manual_review' `
                    $historyFileName private_ip_literal $privateAddresses
            }
        }
    }
}

$uniqueFindings = @($findings | Sort-Object severity, code, path, fingerprint -Unique)
$errors = @($uniqueFindings | Where-Object severity -eq 'error')
$warnings = @($uniqueFindings | Where-Object severity -eq 'warning')
$report = [ordered]@{
    schemaVersion = 2
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    result = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
    trackedFileCount = $tracked.Count
    scannedTextFileCount = $textFiles.Count
    historyCommitCount = $historyCommits.Count
    errorCount = $errors.Count
    warningCount = $warnings.Count
    historyPrivacyReviewRequired = @(
        $warnings | Where-Object rule -eq 'historical_local_machine_value_requires_manual_review'
    ).Count -ne 0
    findings = $uniqueFindings
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $ReportPath,
    ($report | ConvertTo-Json -Depth 6),
    $utf8NoBom)

Write-Host "Public release audit: $($report.result); errors=$($report.errorCount); warnings=$($report.warningCount)"
$reportDisplayPath = if ($ReportPath.StartsWith(
        $repoRoot.TrimEnd('\') + '\',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    (Get-RelativePath $ReportPath).Replace('\', '/')
}
else {
    '<REDACTED>'
}
Write-Host "Sanitized report: $reportDisplayPath"
if ($errors.Count -ne 0) {
    exit 1
}
