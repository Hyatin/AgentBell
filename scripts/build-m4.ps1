[CmdletBinding()]
param(
    [switch]$SkipAndroid,
    [switch]$SkipInstaller,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $repoRoot 'AgentBell.sln'
$stagingRoot = Join-Path $repoRoot 'artifacts\m4-staging'
$packageRoot = Join-Path $repoRoot 'artifacts\m4-package'
$installerOutput = Join-Path $repoRoot 'artifacts\m4-installer'
$androidRoot = Join-Path $repoRoot 'android\AgentBell'
$apkSource = Join-Path $androidRoot 'app\build\outputs\apk\debug\app-debug.apk'
$installerScript = Join-Path $repoRoot 'installer\AgentBell.iss'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function ConvertTo-NativeCommandLineArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Argument)

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell 5.1.
    # Quote according to the Windows command-line parsing rules instead.
    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append([char]34)
    $backslashCount = 0

    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashCount++
            continue
        }

        if ($character -eq [char]34) {
            [void]$quoted.Append([char]92, (($backslashCount * 2) + 1))
            [void]$quoted.Append([char]34)
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void]$quoted.Append([char]92, $backslashCount)
            $backslashCount = 0
        }

        [void]$quoted.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void]$quoted.Append([char]92, ($backslashCount * 2))
    }

    [void]$quoted.Append([char]34)
    return $quoted.ToString()
}

function Invoke-NativeCommandCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    $effectiveFilePath = $FilePath
    $effectiveArguments = $Arguments
    $fileExtension = [System.IO.Path]::GetExtension($FilePath)
    $isCommandScript = $fileExtension -eq '.bat' -or $fileExtension -eq '.cmd'
    if ($isCommandScript) {
        $commandInterpreter = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($commandInterpreter)) {
            $commandInterpreter = Join-Path $env:SystemRoot 'System32\cmd.exe'
        }

        $commandLine = New-Object System.Text.StringBuilder
        [void]$commandLine.Append((ConvertTo-NativeCommandLineArgument -Argument $FilePath))
        foreach ($argument in $Arguments) {
            [void]$commandLine.Append(' ')
            [void]$commandLine.Append((ConvertTo-NativeCommandLineArgument -Argument $argument))
        }

        $effectiveFilePath = $commandInterpreter
        $effectiveArguments = @('/d', '/s', '/c', $commandLine.ToString())
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $effectiveFilePath
    $startInfo.Arguments = (($effectiveArguments | ForEach-Object {
                ConvertTo-NativeCommandLineArgument -Argument $_
            }) -join ' ')
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.Path

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "The native process could not be started: $FilePath"
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    $combinedParts = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
        $combinedParts.Add($standardOutput.TrimEnd())
    }

    if (-not [string]::IsNullOrWhiteSpace($standardError)) {
        $combinedParts.Add($standardError.TrimEnd())
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        StandardOutput = $standardOutput
        StandardError = $standardError
        CombinedOutput = $combinedParts -join [Environment]::NewLine
    }
}

function Assert-NativeCommandCapture {
    $marker = 'agentbell-native-capture-stderr-only'
    $commandInterpreter = $env:ComSpec
    if ([string]::IsNullOrWhiteSpace($commandInterpreter)) {
        $commandInterpreter = Join-Path $env:SystemRoot 'System32\cmd.exe'
    }

    $result = Invoke-NativeCommandCapture `
        -FilePath $commandInterpreter `
        -Arguments @('/d', '/s', '/c', "echo $marker 1>&2")

    if ($result.ExitCode -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($result.StandardOutput) -or
        $result.StandardError -notmatch [regex]::Escape($marker) -or
        $result.CombinedOutput -notmatch [regex]::Escape($marker)) {
        throw 'Native command capture self-check failed: a successful stderr-only command was not captured correctly.'
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $result = Invoke-NativeCommandCapture -FilePath $FilePath -Arguments $Arguments
    if (-not [string]::IsNullOrWhiteSpace($result.StandardOutput)) {
        Write-Host $result.StandardOutput.TrimEnd()
    }

    if (-not [string]::IsNullOrWhiteSpace($result.StandardError)) {
        Write-Host $result.StandardError.TrimEnd()
    }

    if ($result.ExitCode -ne 0) {
        throw "$FailureMessage Exit code: $($result.ExitCode)."
    }
}

function Assert-ContainedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    $prefix = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository artifacts directory: $fullPath"
    }

    return $fullPath
}

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $safePath = Assert-ContainedPath -Path $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
}

function Resolve-JavaHome {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add($env:JAVA_HOME)
    }

    $javaCommand = Get-Command 'java.exe' -ErrorAction SilentlyContinue
    if ($null -ne $javaCommand) {
        $javaBin = Split-Path -Parent $javaCommand.Source
        $candidates.Add((Split-Path -Parent $javaBin))
    }

    $androidStudioJbr = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'
    if (Test-Path -LiteralPath (Join-Path $androidStudioJbr 'bin\java.exe')) {
        $candidates.Add($androidStudioJbr)
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $javaExe = Join-Path $candidate 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $javaExe -PathType Leaf)) {
            continue
        }

        $versionResult = Invoke-NativeCommandCapture -FilePath $javaExe -Arguments @('-version')
        if ($versionResult.ExitCode -ne 0) {
            continue
        }

        $versionOutput = $versionResult.CombinedOutput
        $match = [regex]::Match($versionOutput, 'version\s+"(?<major>\d+)')
        if ($match.Success -and [int]$match.Groups['major'].Value -ge 17) {
            return [pscustomobject]@{
                Home = (Resolve-Path -LiteralPath $candidate).Path
                Major = [int]$match.Groups['major'].Value
                Description = $versionOutput.Trim()
            }
        }
    }

    throw 'A compatible 64-bit JDK 17 or newer was not found in JAVA_HOME, PATH, or the registered Android Studio runtime.'
}

function Resolve-AndroidSdk {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $candidates.Add($env:ANDROID_SDK_ROOT)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) {
        $candidates.Add($env:ANDROID_HOME)
    }

    $knownLocalAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($knownLocalAppData)) {
        $candidates.Add((Join-Path $knownLocalAppData 'Android\Sdk'))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk'))
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'platforms\android-36')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'build-tools\36.0.0')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'platform-tools'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Android SDK Platform 36, Build Tools 36.0.0, and Platform-Tools were not found.'
}

function Resolve-InnoCompiler {
    if (-not [string]::IsNullOrWhiteSpace($env:ISCC_PATH) -and
        (Test-Path -LiteralPath $env:ISCC_PATH -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:ISCC_PATH).Path
    }

    $isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $isccCommand) {
        return $isccCommand.Source
    }

    $registryRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($registryRoot in $registryRoots) {
        if (-not (Test-Path -LiteralPath $registryRoot)) {
            continue
        }

        $entries = Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue
        foreach ($entry in $entries) {
            $properties = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $properties) {
                continue
            }

            $displayNameProperty = $properties.PSObject.Properties['DisplayName']
            $installLocationProperty = $properties.PSObject.Properties['InstallLocation']
            if ($null -eq $displayNameProperty -or $null -eq $installLocationProperty) {
                continue
            }

            $displayName = [string]$displayNameProperty.Value
            $installLocation = [string]$installLocationProperty.Value
            if ([string]::IsNullOrWhiteSpace($displayName) -or
                -not $displayName.StartsWith('Inno Setup', [StringComparison]::OrdinalIgnoreCase) -or
                [string]::IsNullOrWhiteSpace($installLocation)) {
                continue
            }

            $candidate = Join-Path $installLocation 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }

    throw 'ISCC.exe was not found through ISCC_PATH, PATH, or an Inno Setup uninstall registry entry. Install a stable Inno Setup release or use -SkipInstaller.'
}

function Publish-SingleFile {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$ExecutableName
    )

    Reset-ArtifactDirectory -Path $OutputDirectory
    Invoke-Checked -FilePath 'dotnet' -Arguments @(
        'publish', $Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-o', $OutputDirectory
    ) -FailureMessage "Publishing $ExecutableName failed."

    $executablePath = Join-Path $OutputDirectory $ExecutableName
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Published executable does not exist: $executablePath"
    }

    return $executablePath
}

Push-Location $repoRoot
try {
    Write-Step 'Checking toolchains'
    Assert-NativeCommandCapture
    Write-Host 'Native command capture self-check: passed (exit 0 with stderr-only output).'

    $dotnetVersionResult = Invoke-NativeCommandCapture -FilePath 'dotnet' -Arguments @('--version')
    $dotnetVersionText = $dotnetVersionResult.StandardOutput.Trim()
    if ([string]::IsNullOrWhiteSpace($dotnetVersionText)) {
        $dotnetVersionText = $dotnetVersionResult.CombinedOutput.Trim()
    }

    if ($dotnetVersionResult.ExitCode -ne 0 -or
        -not $dotnetVersionText.StartsWith('10.', [StringComparison]::Ordinal)) {
        throw ".NET SDK 10 is required; detected '$dotnetVersionText'."
    }

    $java = Resolve-JavaHome
    $androidSdk = Resolve-AndroidSdk
    $iscc = $null
    if ($SkipInstaller) {
        Write-Host 'Inno Setup check skipped by -SkipInstaller.' -ForegroundColor Yellow
    }
    else {
        $iscc = Resolve-InnoCompiler
        $isccVersion = (Get-Item -LiteralPath $iscc).VersionInfo.ProductVersion
        Write-Host "Inno Setup compiler: $iscc ($isccVersion)"
    }

    Write-Host ".NET SDK: $dotnetVersionText"
    Write-Host "JDK: $($java.Major) at $($java.Home)"
    Write-Host "Android SDK: $androidSdk"

    $env:JAVA_HOME = $java.Home
    $env:ANDROID_SDK_ROOT = $androidSdk
    $env:ANDROID_HOME = $androidSdk

    Write-Step 'Running the M4 Known Folder path regression test'
    & (Join-Path $PSScriptRoot 'test-m4-install.ps1') -PathResolutionSelfTestOnly

    if ($Clean) {
        Write-Step 'Cleaning M4 artifacts'
        foreach ($path in @($stagingRoot, $packageRoot, $installerOutput)) {
            $safePath = Assert-ContainedPath -Path $path
            if (Test-Path -LiteralPath $safePath) {
                Remove-Item -LiteralPath $safePath -Recurse -Force
            }
        }
    }

    Write-Step 'Verifying formatting'
    Invoke-Checked -FilePath 'dotnet' -Arguments @(
        'format', $solutionPath, '--verify-no-changes'
    ) -FailureMessage 'dotnet format verification failed.'

    Write-Step 'Restoring the solution'
    Invoke-Checked -FilePath 'dotnet' -Arguments @(
        'restore', $solutionPath
    ) -FailureMessage 'dotnet restore failed.'

    Write-Step 'Building the Release solution'
    Invoke-Checked -FilePath 'dotnet' -Arguments @(
        'build', $solutionPath, '-c', 'Release', '--no-restore'
    ) -FailureMessage 'Release build failed.'

    Write-Step 'Running all Windows tests'
    Invoke-Checked -FilePath 'dotnet' -Arguments @(
        'test', $solutionPath, '-c', 'Release', '--no-build'
    ) -FailureMessage 'Windows tests failed.'

    Write-Step 'Publishing isolated self-contained Windows executables'
    Reset-ArtifactDirectory -Path $stagingRoot
    $hookExe = Publish-SingleFile `
        -Project (Join-Path $repoRoot 'src\AgentBell.Hook\AgentBell.Hook.csproj') `
        -OutputDirectory (Join-Path $stagingRoot 'hook') `
        -ExecutableName 'AgentBell.Hook.exe'
    $trayExe = Publish-SingleFile `
        -Project (Join-Path $repoRoot 'src\AgentBell.Tray\AgentBell.Tray.csproj') `
        -OutputDirectory (Join-Path $stagingRoot 'tray') `
        -ExecutableName 'AgentBell.Tray.exe'
    $integrationExe = Publish-SingleFile `
        -Project (Join-Path $repoRoot 'src\AgentBell.Integration\AgentBell.Integration.csproj') `
        -OutputDirectory (Join-Path $stagingRoot 'integration') `
        -ExecutableName 'AgentBell.Integration.exe'

    Reset-ArtifactDirectory -Path $packageRoot
    Copy-Item -LiteralPath $hookExe -Destination $packageRoot
    Copy-Item -LiteralPath $trayExe -Destination $packageRoot
    Copy-Item -LiteralPath $integrationExe -Destination $packageRoot

    $versionResult = Invoke-NativeCommandCapture `
        -FilePath (Join-Path $packageRoot 'AgentBell.Integration.exe') `
        -Arguments @('version', '--json')
    if ($versionResult.ExitCode -ne 0) {
        throw 'The published Integration executable could not report its product version.'
    }

    $versionJson = $versionResult.StandardOutput.Trim()
    if ([string]::IsNullOrWhiteSpace($versionJson)) {
        $versionJson = $versionResult.CombinedOutput.Trim()
    }

    $versionInfo = $versionJson | ConvertFrom-Json
    $productVersion = [string]$versionInfo.productVersion
    if ($productVersion -notmatch '^\d+\.\d+\.\d+$' -or [int]$versionInfo.protocolVersion -ne 1) {
        throw "Published version metadata is invalid: $versionJson"
    }

    Write-Host "AgentBell product version: $productVersion; protocol version: $($versionInfo.protocolVersion)"

    if ($SkipAndroid) {
        Write-Step 'Using an existing Android debug APK (-SkipAndroid)'
    }
    else {
        Write-Step 'Running Android unit tests'
        Push-Location $androidRoot
        try {
            Invoke-Checked -FilePath (Join-Path $androidRoot 'gradlew.bat') `
                -Arguments @('testDebugUnitTest', '--no-daemon') `
                -FailureMessage 'Android unit tests failed.'

            Write-Step 'Assembling the Android debug APK'
            Invoke-Checked -FilePath (Join-Path $androidRoot 'gradlew.bat') `
                -Arguments @('assembleDebug', '--no-daemon') `
                -FailureMessage 'Android debug assembly failed.'
        }
        finally {
            Pop-Location
        }
    }

    if (-not (Test-Path -LiteralPath $apkSource -PathType Leaf)) {
        throw "The real Android APK does not exist: $apkSource"
    }

    $apkInfo = Get-Item -LiteralPath $apkSource
    if ($apkInfo.Length -le 0) {
        throw "The Android APK is empty: $apkSource"
    }

    $packageAndroid = Join-Path $packageRoot 'android'
    New-Item -ItemType Directory -Path $packageAndroid -Force | Out-Null
    $packagedApk = Join-Path $packageAndroid 'AgentBell-debug.apk'
    Copy-Item -LiteralPath $apkSource -Destination $packagedApk -Force

    $packageHashes = @()
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName) {
        $relativePath = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $packageHashes += "$hash *$relativePath"
    }

    [System.IO.File]::WriteAllLines(
        (Join-Path $packageRoot 'SHA256SUMS.txt'),
        $packageHashes,
        $utf8NoBom)

    if (-not $SkipInstaller) {
        Write-Step 'Compiling the per-user Inno Setup installer'
        Reset-ArtifactDirectory -Path $installerOutput
        Invoke-Checked -FilePath $iscc -Arguments @(
            "/DSourceDir=$packageRoot",
            "/DOutputDir=$installerOutput",
            "/DProductVersion=$productVersion",
            $installerScript
        ) -FailureMessage 'Inno Setup compilation failed.'

        $setupPath = Join-Path $installerOutput "AgentBell-Setup-$productVersion.exe"
        if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
            throw "Inno Setup reported success but the expected Setup does not exist: $setupPath"
        }

        $setupInfo = Get-Item -LiteralPath $setupPath
        if ($setupInfo.Length -le 0) {
            throw "The generated Setup is empty: $setupPath"
        }

        $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllLines(
            (Join-Path $installerOutput 'SHA256SUMS.txt'),
            @("$setupHash *$($setupInfo.Name)"),
            $utf8NoBom)
        Write-Host "Setup: $setupPath"
        Write-Host "Setup bytes: $($setupInfo.Length)"
        Write-Host "Setup SHA-256: $setupHash"
    }
    else {
        Write-Host 'Setup compilation was intentionally skipped; no installer success is claimed.' -ForegroundColor Yellow
    }

    $packagedApkInfo = Get-Item -LiteralPath $packagedApk
    $packagedApkHash = (Get-FileHash -LiteralPath $packagedApk -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Step 'M4 build completed successfully'
    Write-Host "Package: $packageRoot"
    Write-Host "APK: $packagedApk"
    Write-Host "APK bytes: $($packagedApkInfo.Length)"
    Write-Host "APK SHA-256: $packagedApkHash"
}
finally {
    Pop-Location
}
