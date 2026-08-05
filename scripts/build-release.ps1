[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-beta\.\d+)?$')]
    [string]$Version = '0.5.0-beta.1',
    [switch]$Clean,
    [switch]$DryRun,
    [switch]$SkipAndroid,
    [switch]$SkipInstaller,
    [switch]$SkipSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'AgentBell.sln'
$androidRoot = Join-Path $repoRoot 'android\AgentBell'
$releaseRoot = Join-Path $repoRoot "artifacts\release\v$Version"
$stagingRoot = Join-Path $repoRoot "artifacts\release\.staging-v$Version"
$packageRoot = Join-Path $stagingRoot 'package'
$publishRoot = Join-Path $stagingRoot 'publish'
$installerRoot = Join-Path $stagingRoot 'installer'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$productVersion = $Version.Split('-')[0]
$androidAssetName = "AgentBell-Android-$Version.apk"
$setupAssetName = "AgentBell-Setup-$Version.exe"
$androidDebugUnitTestTask = ':app:testDebugUnitTest'
$androidReleaseUnitTestTask = ':app:testReleaseUnitTest'
$androidDebugLintTask = ':app:lintDebug'
$androidReleaseLintTask = ':app:lintRelease'
$androidAssembleTask = ':app:assembleRelease'
$androidSigningNames = @(
    'AGENTBELL_ANDROID_KEYSTORE',
    'AGENTBELL_ANDROID_KEYSTORE_PASSWORD',
    'AGENTBELL_ANDROID_KEY_ALIAS',
    'AGENTBELL_ANDROID_KEY_PASSWORD'
)
$androidSigned = $false
$windowsSigned = $false
$setupSigned = $false
$apkCertificateSha256 = $null
$apkSignatureSchemes = $null
$androidPackageName = $null
$androidVersionName = $null
$androidVersionCode = $null
$installerBuilt = $false

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [string]$WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        $nativeResult = Invoke-NativeCaptured $FilePath $Arguments
        Write-NativeCapturedOutput $nativeResult
        if ($nativeResult.ExitCode -ne 0) {
            throw "$FailureMessage Exit code: $($nativeResult.ExitCode)."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-NativeCaptured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 wraps redirected native stderr as ErrorRecord objects.
        # Capture those records without treating warning text as command failure; the
        # native exit code remains the sole success criterion.
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Write-NativeCapturedOutput([pscustomobject]$NativeResult) {
    foreach ($line in $NativeResult.Output) {
        $lineText = if ($line -is [System.Management.Automation.ErrorRecord]) {
            $line.Exception.Message
        }
        else {
            $line.ToString()
        }
        if (-not [string]::IsNullOrWhiteSpace($lineText)) { Write-Host $lineText }
    }
}

function Invoke-AndroidGradleWithoutSigning {
    param(
        [Parameter(Mandatory = $true)][string]$GradlePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $savedValues = @{}
    foreach ($name in $androidSigningNames) {
        $savedValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    Push-Location $androidRoot
    try {
        return Invoke-NativeCaptured $GradlePath $Arguments
    }
    finally {
        Pop-Location
        foreach ($name in $androidSigningNames) {
            [Environment]::SetEnvironmentVariable($name, $savedValues[$name], 'Process')
        }
    }
}

function Assert-AndroidReleaseUnitTestConfiguration {
    $propertiesPath = Join-Path $androidRoot 'gradle.properties'
    $propertiesText = [System.IO.File]::ReadAllText($propertiesPath)
    $matches = [regex]::Matches(
        $propertiesText,
        '(?m)^android\.onlyEnableUnitTestForTheTestedBuildType=false\s*$')
    if ($matches.Count -ne 1) {
        throw 'gradle.properties must contain exactly one release unit-test enablement setting.'
    }
}

function Assert-AndroidGradleTasks {
    param(
        [Parameter(Mandatory = $true)][string]$GradlePath,
        [Parameter(Mandatory = $true)][string[]]$RequiredTasks
    )

    Push-Location $androidRoot
    try {
        $taskResult = Invoke-NativeCaptured $GradlePath @(
            ':app:tasks',
            '--all',
            '--console=plain',
            '--no-daemon'
        )
        if ($taskResult.ExitCode -ne 0) {
            throw "Android Gradle task discovery failed. Exit code: $($taskResult.ExitCode)."
        }
    }
    finally {
        Pop-Location
    }

    $taskText = ($taskResult.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    foreach ($taskPath in $RequiredTasks) {
        $taskName = $taskPath.Substring($taskPath.LastIndexOf(':') + 1)
        $taskPattern = '(?m)^\s*{0}(?:\s+-|\s*$)' -f [regex]::Escape($taskName)
        if ($taskText -notmatch $taskPattern) {
            throw "Required Android Gradle task is missing: $taskPath"
        }
    }
}

function Assert-ReleaseContainedPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $allowed = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release')).TrimEnd('\') + '\'
    if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside artifacts\release: $full"
    }

    return $full
}

function Reset-Directory([string]$Path) {
    $safe = Assert-ReleaseContainedPath $Path
    if (Test-Path -LiteralPath $safe) {
        Remove-Item -LiteralPath $safe -Recurse -Force
    }
    New-Item -ItemType Directory -Path $safe -Force | Out-Null
}

function Resolve-JavaHome {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) { $candidates.Add($env:JAVA_HOME) }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'))
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe') -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'JDK 17+ was not found in JAVA_HOME or the Android Studio runtime.'
}

function Resolve-AndroidSdk {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) { $candidates.Add($env:ANDROID_SDK_ROOT) }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) { $candidates.Add($env:ANDROID_HOME) }
    $knownLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($knownLocalAppData)) {
        $candidates.Add((Join-Path $knownLocalAppData 'Android\Sdk'))
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'platforms\android-36')) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'Android SDK Platform 36 was not found.'
}

function Resolve-ApkSigner([string]$AndroidSdk) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'build-tools') `
            -Filter 'apksigner.bat' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) { throw 'apksigner.bat was not found in the Android SDK.' }
    return $candidates[0].FullName
}

function Resolve-Aapt2([string]$AndroidSdk) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'build-tools') `
            -Filter 'aapt2.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) { throw 'aapt2.exe was not found in the Android SDK.' }
    return $candidates[0].FullName
}

function Get-ApkSignatureReport([string]$ApkSigner, [string]$ApkPath) {
    $verificationResult = Invoke-NativeCaptured $ApkSigner @(
        'verify',
        '--verbose',
        '--print-certs',
        $ApkPath
    )
    if ($verificationResult.ExitCode -ne 0) { throw 'APK signature verification failed.' }
    $verificationText = ($verificationResult.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine

    $certificateMatch = [regex]::Match(
        $verificationText,
        '(?im)certificate SHA-256 digest:\s*([0-9a-f:]+)\s*$')
    if (-not $certificateMatch.Success) { throw 'APK certificate SHA-256 fingerprint was not reported.' }

    $certificateDnMatch = [regex]::Match($verificationText, '(?im)certificate DN:\s*(.+?)\s*$')
    if (-not $certificateDnMatch.Success) { throw 'APK certificate identity was not reported.' }
    if ($certificateDnMatch.Groups[1].Value -match '(?i)CN\s*=\s*Android Debug') {
        throw 'The APK is signed with an Android debug certificate.'
    }

    $schemes = [ordered]@{}
    foreach ($scheme in @(1, 2, 3, 4)) {
        $schemeMatch = [regex]::Match(
            $verificationText,
            "(?im)^Verified using v$scheme scheme[^:]*:\s*(true|false)\s*$")
        $schemes["v$scheme"] = if ($schemeMatch.Success) {
            [bool]::Parse($schemeMatch.Groups[1].Value)
        }
        else {
            $null
        }
    }
    if ($schemes.v2 -ne $true -and $schemes.v3 -ne $true) {
        throw 'The APK does not report a verified v2 or v3 signature.'
    }

    return [pscustomobject]@{
        CertificateSha256 = $certificateMatch.Groups[1].Value.Replace(':', '').ToLowerInvariant()
        Schemes = $schemes
    }
}

function Get-ApkPackageReport([string]$Aapt2, [string]$ApkPath) {
    $badgingResult = Invoke-NativeCaptured $Aapt2 @('dump', 'badging', $ApkPath)
    if ($badgingResult.ExitCode -ne 0) { throw 'APK package metadata inspection failed.' }
    $badgingText = ($badgingResult.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $packageMatch = [regex]::Match(
        $badgingText,
        "(?m)^package:\s+name='([^']+)'\s+versionCode='([^']+)'\s+versionName='([^']*)'")
    if (-not $packageMatch.Success) { throw 'APK package metadata was not reported.' }
    if ($badgingText -match '(?m)^application-debuggable(?:\s|$)') {
        throw 'The APK manifest is debuggable.'
    }

    return [pscustomobject]@{
        PackageName = $packageMatch.Groups[1].Value
        VersionCode = $packageMatch.Groups[2].Value
        VersionName = $packageMatch.Groups[3].Value
    }
}

function Resolve-InnoCompiler {
    if (-not [string]::IsNullOrWhiteSpace($env:ISCC_PATH) -and
        (Test-Path -LiteralPath $env:ISCC_PATH -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($env:ISCC_PATH)
    }
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($root in $roots) {
        $candidate = Join-Path $root 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'ISCC.exe was not found. Set ISCC_PATH or install Inno Setup 6.'
}

function Resolve-SignTool {
    if (-not [string]::IsNullOrWhiteSpace($env:AGENTBELL_WINDOWS_SIGNTOOL) -and
        (Test-Path -LiteralPath $env:AGENTBELL_WINDOWS_SIGNTOOL -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($env:AGENTBELL_WINDOWS_SIGNTOOL)
    }
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $matches = @(Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -File -Recurse `
            -ErrorAction SilentlyContinue | Where-Object FullName -Match '\\x64\\' |
        Sort-Object FullName -Descending)
    if ($matches.Count -eq 0) { throw 'signtool.exe was not found.' }
    return $matches[0].FullName
}

function Publish-Executable([string]$Project, [string]$Name) {
    $output = Join-Path $publishRoot ([System.IO.Path]::GetFileNameWithoutExtension($Name))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-Checked 'dotnet' @('restore', $Project, '-r', 'win-x64') `
        "Restoring the win-x64 runtime assets for $Name failed."
    Invoke-Checked 'dotnet' @(
        'publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--no-restore', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $output
    ) "Publishing $Name failed."
    $path = Join-Path $output $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Published executable missing: $Name" }
    return $path
}

function Sign-WindowsFile([string]$Path, [string]$SignTool) {
    Invoke-Checked $SignTool @(
        'sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $env:AGENTBELL_WINDOWS_TIMESTAMP_URL,
        '/f', $env:AGENTBELL_WINDOWS_SIGN_CERTIFICATE,
        '/p', $env:AGENTBELL_WINDOWS_SIGN_CERTIFICATE_PASSWORD,
        $Path
    ) 'Authenticode signing failed.'
    Invoke-Checked $SignTool @('verify', '/pa', '/all', $Path) 'Authenticode verification failed.'
    if ((Get-AuthenticodeSignature -LiteralPath $Path).Status -ne 'Valid') {
        throw 'Authenticode verification did not return Valid.'
    }
}

function Get-CentralProperty([string]$Name) {
    $content = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Directory.Build.props'))
    $match = [regex]::Match($content, "<$Name>([^<]+)</$Name>")
    if (-not $match.Success) { throw "Missing central property: $Name" }
    return $match.Groups[1].Value
}

Push-Location $repoRoot
try {
    Write-Step 'Validating release version and Git state'
    if ((Get-CentralProperty 'AgentBellProductVersion') -cne $productVersion -or
        (Get-CentralProperty 'AgentBellInformationalVersion') -cne $Version) {
        throw 'The requested release does not match Directory.Build.props.'
    }
    $gitState = @(& git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($gitState.Count -ne 0) {
        if (-not $DryRun) { throw 'A non-dry-run release requires a clean Git worktree.' }
        Write-Warning 'Dry-run continues with a dirty worktree; no publish action will occur.'
    }

    if ($Clean) {
        Reset-Directory $releaseRoot
        Reset-Directory $stagingRoot
    }
    else {
        New-Item -ItemType Directory -Path $releaseRoot, $stagingRoot -Force | Out-Null
    }
    Reset-Directory $packageRoot
    Reset-Directory $publishRoot
    Reset-Directory $installerRoot

    Write-Step 'Running the public release audit'
    & (Join-Path $PSScriptRoot 'audit-public-release.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Public release audit failed.' }

    Write-Step 'Formatting, restoring, building, and testing Windows'
    Invoke-Checked 'dotnet' @('format', $solution, '--verify-no-changes') 'dotnet format failed.'
    Invoke-Checked 'dotnet' @('restore', $solution) 'dotnet restore failed.'
    Invoke-Checked 'dotnet' @('build', $solution, '-c', 'Release', '--no-restore') 'Release build failed.'
    Invoke-Checked 'dotnet' @('test', $solution, '-c', 'Release', '--no-build') 'Windows tests failed.'

    $javaHome = Resolve-JavaHome
    $androidSdk = Resolve-AndroidSdk
    $env:JAVA_HOME = $javaHome
    $env:ANDROID_SDK_ROOT = $androidSdk
    $env:ANDROID_HOME = $androidSdk

    $gradle = Join-Path $androidRoot 'gradlew.bat'
    Assert-AndroidReleaseUnitTestConfiguration
    Write-Step 'Checking required Android Gradle tasks'
    Assert-AndroidGradleTasks $gradle @(
        $androidDebugUnitTestTask,
        $androidReleaseUnitTestTask,
        $androidDebugLintTask,
        $androidReleaseLintTask,
        $androidAssembleTask
    )

    Write-Step 'Running Android keyless verification'
    $keylessResult = Invoke-AndroidGradleWithoutSigning $gradle @(
        $androidDebugUnitTestTask,
        $androidReleaseUnitTestTask,
        $androidDebugLintTask,
        $androidReleaseLintTask,
        '--no-daemon'
    )
    Write-NativeCapturedOutput $keylessResult
    if ($keylessResult.ExitCode -ne 0) {
        throw "Android keyless unit tests or lint failed. Exit code: $($keylessResult.ExitCode)."
    }

    Write-Step 'Checking the Android release signing guard'
    $missingSigningResult = Invoke-AndroidGradleWithoutSigning $gradle @(
        $androidAssembleTask,
        '--no-daemon',
        '--console=plain'
    )
    $missingSigningText = ($missingSigningResult.Output | ForEach-Object {
            $_.ToString()
        }) -join [Environment]::NewLine
    if ($missingSigningResult.ExitCode -eq 0 -or
        $missingSigningText -notmatch 'Android release signing requires all AGENTBELL_ANDROID_\* signing environment variables') {
        throw 'Android release signing guard did not fail with the expected stable error.'
    }
    Write-Host 'Android release signing guard: expected no-key failure confirmed.'

    $androidSigningAvailable = -not ($androidSigningNames | Where-Object {
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
        })
    $packagedApk = $null
    if (-not $SkipAndroid -and -not $SkipSigning -and $androidSigningAvailable) {
        Write-Step 'Building and verifying the signed Android release APK'
        Invoke-Checked $gradle @($androidAssembleTask, '--no-daemon') `
            'Android release build failed.' $androidRoot
        $releaseApkOutput = Join-Path $androidRoot 'app\build\outputs\apk\release'
        $releaseApks = @(Get-ChildItem -LiteralPath $releaseApkOutput -Filter '*.apk' -File `
                -ErrorAction SilentlyContinue | Where-Object Name -NotMatch '(?i)debug|unsigned')
        if ($releaseApks.Count -ne 1) {
            throw 'Expected exactly one non-debug, non-unsigned APK in the release output directory.'
        }
        $sourceApk = $releaseApks[0].FullName
        $apkSigner = Resolve-ApkSigner $androidSdk
        $signatureReport = Get-ApkSignatureReport $apkSigner $sourceApk
        $apkCertificateSha256 = $signatureReport.CertificateSha256
        $apkSignatureSchemes = $signatureReport.Schemes

        $aapt2 = Resolve-Aapt2 $androidSdk
        $packageReport = Get-ApkPackageReport $aapt2 $sourceApk
        $androidPackageName = $packageReport.PackageName
        $androidVersionCode = $packageReport.VersionCode
        $androidVersionName = $packageReport.VersionName
        if ($androidPackageName -cne 'com.hyatin.agentbell' -or
            $androidVersionName -cne $Version -or
            $androidVersionCode -cne (Get-CentralProperty 'AgentBellAndroidVersionCode')) {
            throw 'The signed APK package or version metadata is inconsistent.'
        }

        $packagedApk = Join-Path $releaseRoot $androidAssetName
        Copy-Item -LiteralPath $sourceApk -Destination $packagedApk -Force
        $androidSigned = $true
    }
    elseif (-not $SkipAndroid -and -not $DryRun) {
        throw 'A public Android asset requires all long-term release signing environment variables.'
    }
    else {
        Write-Warning 'Signed Android release build was skipped; no public APK success is claimed.'
    }

    Write-Step 'Publishing Windows single-file executables'
    $hook = Publish-Executable (Join-Path $repoRoot 'src\AgentBell.Hook\AgentBell.Hook.csproj') 'AgentBell.Hook.exe'
    $tray = Publish-Executable (Join-Path $repoRoot 'src\AgentBell.Tray\AgentBell.Tray.csproj') 'AgentBell.Tray.exe'
    $integration = Publish-Executable (Join-Path $repoRoot 'src\AgentBell.Integration\AgentBell.Integration.csproj') 'AgentBell.Integration.exe'

    $windowsSigningAvailable = -not $SkipSigning -and
        -not [string]::IsNullOrWhiteSpace($env:AGENTBELL_WINDOWS_SIGN_CERTIFICATE) -and
        -not [string]::IsNullOrWhiteSpace($env:AGENTBELL_WINDOWS_SIGN_CERTIFICATE_PASSWORD) -and
        -not [string]::IsNullOrWhiteSpace($env:AGENTBELL_WINDOWS_TIMESTAMP_URL)
    if ($windowsSigningAvailable) {
        Write-Step 'Authenticode-signing Windows executables'
        $signTool = Resolve-SignTool
        foreach ($file in @($hook, $tray, $integration)) { Sign-WindowsFile $file $signTool }
        $windowsSigned = $true
    }
    else {
        Write-Warning 'Windows binaries are UNSIGNED BETA; SmartScreen warnings are expected.'
    }

    foreach ($file in @($hook, $tray, $integration)) {
        Copy-Item -LiteralPath $file -Destination $packageRoot -Force
    }
    $versionJson = & (Join-Path $packageRoot 'AgentBell.Integration.exe') version --json | ConvertFrom-Json
    if ($versionJson.productVersion -cne $productVersion -or
        $versionJson.informationalVersion -cne $Version -or
        [int]$versionJson.protocolVersion -ne 1) {
        throw 'Published Windows version metadata is inconsistent.'
    }

    $effectiveSkipInstaller = $SkipInstaller -or -not $androidSigned
    if (-not $effectiveSkipInstaller) {
        Write-Step 'Building the per-user Inno Setup package'
        $packageAndroid = Join-Path $packageRoot 'android'
        New-Item -ItemType Directory -Path $packageAndroid -Force | Out-Null
        Copy-Item -LiteralPath $packagedApk -Destination (Join-Path $packageAndroid $androidAssetName)
        $iscc = Resolve-InnoCompiler
        Invoke-Checked $iscc @(
            "/DSourceDir=$packageRoot", "/DOutputDir=$installerRoot",
            "/DProductVersion=$productVersion", "/DInformationalVersion=$Version",
            "/DAndroidApkFileName=$androidAssetName", "/DLicenseFile=$(Join-Path $repoRoot 'LICENSE')",
            (Join-Path $repoRoot 'installer\AgentBell.iss')
        ) 'Inno Setup compilation failed.'
        $builtSetup = Join-Path $installerRoot $setupAssetName
        if (-not (Test-Path -LiteralPath $builtSetup -PathType Leaf)) { throw 'Setup output is missing.' }
        if ($windowsSigningAvailable) {
            Sign-WindowsFile $builtSetup $signTool
            $setupSigned = $true
        }
        Copy-Item -LiteralPath $builtSetup -Destination (Join-Path $releaseRoot $setupAssetName) -Force
        $installerBuilt = $true
    }
    else {
        Write-Warning 'Installer was skipped because it was requested or no signed release APK is available.'
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\releases\v0.5.0-beta.1.md') `
        -Destination (Join-Path $releaseRoot 'RELEASE_NOTES.md') -Force

    $sbom = [ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = "AgentBell-$Version"
        documentNamespace = "https://github.com/OWNER/AgentBell/sbom/$Version/dry-run-placeholder"
        creationInfo = [ordered]@{
            created = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            creators = @('Tool: scripts/build-release.ps1')
        }
        packages = @(
            [ordered]@{
                name = 'AgentBell'
                SPDXID = 'SPDXRef-Package-AgentBell'
                versionInfo = $Version
                downloadLocation = 'NOASSERTION'
                filesAnalyzed = $false
                licenseConcluded = 'Apache-2.0'
                licenseDeclared = 'Apache-2.0'
                copyrightText = 'NOASSERTION'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $releaseRoot 'SBOM.spdx.json'),
        ($sbom | ConvertTo-Json -Depth 8),
        $utf8NoBom)

    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        expectedTag = "v$Version"
        protocolVersion = 1
        prerelease = $true
        dryRun = [bool]$DryRun
        windows = [ordered]@{
            installerBuilt = $installerBuilt
            signed = $windowsSigned -and $setupSigned
            authenticodeExecutablesSigned = $windowsSigned
            authenticodeSetupSigned = $setupSigned
        }
        android = [ordered]@{
            packageName = 'com.hyatin.agentbell'
            versionName = $androidVersionName
            versionCode = $androidVersionCode
            signedReleaseBuilt = $androidSigned
            signing = if ($androidSigned) { 'release key' } else { 'not built' }
            certificateSha256 = $apkCertificateSha256
            signatureSchemes = $apkSignatureSchemes
            unitTestTask = $androidReleaseUnitTestTask
            lintTask = $androidReleaseLintTask
            assembleTask = $androidAssembleTask
            debugApkPublished = $false
        }
        sbom = 'SPDX-2.3 file inventory; dependency notices are in THIRD_PARTY_NOTICES.md'
        releasePublished = $false
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $releaseRoot 'release-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 8),
        $utf8NoBom)

    $allowedNames = @(
        $setupAssetName,
        $androidAssetName,
        'SHA256SUMS.txt',
        'RELEASE_NOTES.md',
        'SBOM.spdx.json',
        'release-manifest.json'
    )
    $unexpected = @(Get-ChildItem -LiteralPath $releaseRoot -File -Recurse | Where-Object {
            $allowedNames -notcontains $_.Name -or $_.Name -match '(?i)debug|\.pdb$|\.jks$|\.keystore$|\.pfx$'
        })
    if ($unexpected.Count -ne 0) { throw 'The public release staging directory contains a non-whitelisted asset.' }
    $publicApks = @(Get-ChildItem -LiteralPath $releaseRoot -Filter '*.apk' -File -Recurse)
    if (($androidSigned -and ($publicApks.Count -ne 1 -or $publicApks[0].Name -cne $androidAssetName)) -or
        (-not $androidSigned -and $publicApks.Count -ne 0)) {
        throw 'The public release staging directory does not contain exactly the expected signed release APK.'
    }

    $hashLines = @(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object Name | ForEach-Object {
            '{0} *{1}' -f ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()), $_.Name
        })
    [System.IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $hashLines, $utf8NoBom)

    Write-Step 'Release dry-run/build report'
    Write-Host "Version: $Version"
    Write-Host "DryRun: $([bool]$DryRun)"
    Write-Host "Windows Signed: $windowsSigned"
    Write-Host "Setup Signed: $setupSigned"
    Write-Host "Android release signed: $androidSigned"
    if ($null -ne $apkCertificateSha256) { Write-Host "APK certificate SHA-256: $apkCertificateSha256" }
    if ($null -ne $apkSignatureSchemes) {
        Write-Host "APK signature schemes: v1=$($apkSignatureSchemes.v1), v2=$($apkSignatureSchemes.v2), v3=$($apkSignatureSchemes.v3), v4=$($apkSignatureSchemes.v4)"
    }
    Write-Host "AndroidUnitTestTask: $androidReleaseUnitTestTask"
    Write-Host "AndroidLintTask: $androidReleaseLintTask"
    Write-Host "AndroidAssembleTask: $androidAssembleTask"
    Write-Host "AndroidSigning: $(if ($androidSigned) { 'release key' } else { 'not built' })"
    Write-Host 'DebugApkPublished: false'
    Write-Host "Installer built: $installerBuilt"
    Write-Host "Assets: $releaseRoot"
    Write-Host 'GitHub release created: false'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $stagingRoot) {
        $safeStaging = Assert-ReleaseContainedPath $stagingRoot
        Remove-Item -LiteralPath $safeStaging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
