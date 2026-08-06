Set-StrictMode -Version Latest

function ConvertTo-AgentBellCanonicalWindowsDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'The path is empty.'
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    $fullPath = [System.IO.Path]::GetFullPath($expanded)
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)
        if ($isWindowsPlatform) {
            if ($null -eq ('AgentBell.Build.WindowsPath' -as [type])) {
                Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBell.Build
{
    public static class WindowsPath
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(
            string shortPath,
            StringBuilder longPath,
            uint bufferLength);

        public static string ExpandLongPath(string path)
        {
            uint required = GetLongPathName(path, null, 0);
            if (required == 0)
            {
                return path;
            }

            var buffer = new StringBuilder(checked((int)required));
            uint written = GetLongPathName(path, buffer, required);
            return written == 0 || written >= required ? path : buffer.ToString();
        }
    }
}
'@
            }

            $fullPath = [AgentBell.Build.WindowsPath]::ExpandLongPath($fullPath)
        }
        else {
            $fullPath = (Resolve-Path -LiteralPath $fullPath).Path
        }
    }

    $root = [System.IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $root.Length -and
        ($fullPath.EndsWith([string][System.IO.Path]::DirectorySeparatorChar) -or
            $fullPath.EndsWith([string][System.IO.Path]::AltDirectorySeparatorChar))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }

    return $fullPath
}

function ConvertFrom-AgentBellInnoVersionText {
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $match = [regex]::Match(
        $Value,
        '(?<!\d)(?<version>\d+\.\d+\.\d+(?:\.\d+)?)(?!\d)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $null
    }

    try {
        $parsed = [Version]$match.Groups['version'].Value
    }
    catch {
        return $null
    }

    if ($parsed.Major -eq 0 -and $parsed.Minor -eq 0 -and
        $parsed.Build -eq 0 -and ($parsed.Revision -le 0)) {
        return $null
    }

    return $parsed
}

function Get-AgentBellInnoSetupRegistryCandidates {
    [CmdletBinding()]
    param()

    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($root in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($entry in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
            $properties = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $properties) {
                continue
            }

            $displayNameProperty = $properties.PSObject.Properties['DisplayName']
            if ($null -eq $displayNameProperty -or
                -not ([string]$displayNameProperty.Value).StartsWith(
                    'Inno Setup', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $displayVersionProperty = $properties.PSObject.Properties['DisplayVersion']
            $installLocationProperty = $properties.PSObject.Properties['InstallLocation']
            $candidates.Add([pscustomobject]@{
                RegistryPath = $entry.PSPath
                DisplayName = [string]$displayNameProperty.Value
                DisplayVersion = if ($null -eq $displayVersionProperty) {
                    $null
                }
                else {
                    [string]$displayVersionProperty.Value
                }
                InstallLocation = if ($null -eq $installLocationProperty) {
                    $null
                }
                else {
                    [string]$installLocationProperty.Value
                }
            })
        }
    }

    return $candidates.ToArray()
}

function Invoke-AgentBellIsccVersionProbe {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$IsccPath)

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $IsccPath '/?' 2>&1)
    }
    catch {
        return [pscustomobject]@{
            Output = ''
            Failure = 'process_start_failed'
        }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        Output = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
        Failure = $null
    }
}

function Resolve-AgentBellInnoSetupVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$IsccPath,
        [AllowNull()][AllowEmptyString()][string]$ProductVersion,
        [AllowNull()][AllowEmptyString()][string]$FileVersion,
        [AllowNull()][object[]]$RegistryCandidates,
        [Version]$MinimumVersion = [Version]'6.4.0',
        [scriptblock]$CanonicalizeDirectory,
        [scriptblock]$CommandOutputProvider
    )

    $resolvedIsccPath = [System.IO.Path]::GetFullPath($IsccPath)
    $isccDirectory = Split-Path -Parent $resolvedIsccPath
    if ($null -eq $CanonicalizeDirectory) {
        $CanonicalizeDirectory = ${function:ConvertTo-AgentBellCanonicalWindowsDirectory}
    }
    if ($null -eq $RegistryCandidates) {
        $RegistryCandidates = @(Get-AgentBellInnoSetupRegistryCandidates)
    }
    if ($null -eq $CommandOutputProvider) {
        $CommandOutputProvider = ${function:Invoke-AgentBellIsccVersionProbe}
    }

    $diagnostics = New-Object System.Collections.Generic.List[string]
    $diagnostics.Add("ISCC path: $resolvedIsccPath")
    $diagnostics.Add("ProductVersion raw: $(if ([string]::IsNullOrWhiteSpace($ProductVersion)) { '<empty>' } else { $ProductVersion })")
    $diagnostics.Add("FileVersion raw: $(if ([string]::IsNullOrWhiteSpace($FileVersion)) { '<empty>' } else { $FileVersion })")

    foreach ($metadataSource in @(
        [pscustomobject]@{ Name = 'ProductVersion'; Raw = $ProductVersion },
        [pscustomobject]@{ Name = 'FileVersion'; Raw = $FileVersion })) {
        $version = ConvertFrom-AgentBellInnoVersionText -Value $metadataSource.Raw
        if ($null -eq $version) {
            $diagnostics.Add("$($metadataSource.Name) rejected: empty, zero, or invalid.")
            continue
        }

        if ($version -lt $MinimumVersion) {
            $diagnostics.Add("$($metadataSource.Name) rejected: $version is below $MinimumVersion.")
            if (@($RegistryCandidates).Count -eq 0) {
                $diagnostics.Add('Registry candidates: none.')
            }
            else {
                foreach ($candidate in @($RegistryCandidates)) {
                    $registryPath = if ($null -eq $candidate.PSObject.Properties['RegistryPath']) {
                        '<unknown>'
                    }
                    else {
                        [string]$candidate.RegistryPath
                    }
                    $displayVersion = if ($null -eq $candidate.PSObject.Properties['DisplayVersion']) {
                        '<empty>'
                    }
                    else {
                        [string]$candidate.DisplayVersion
                    }
                    $diagnostics.Add(
                        "Registry candidate: path=$registryPath; DisplayVersion=$displayVersion; " +
                        'rejected=authoritative_metadata_below_minimum')
                }
            }
            throw ($diagnostics -join [Environment]::NewLine)
        }

        return [pscustomobject]@{
            Version = $version
            Source = $metadataSource.Name
            RawValue = $metadataSource.Raw
            IsccPath = $resolvedIsccPath
            Diagnostics = @($diagnostics)
        }
    }

    try {
        $canonicalIsccDirectory = & $CanonicalizeDirectory $isccDirectory
    }
    catch {
        throw (($diagnostics + 'ISCC directory rejected: canonicalization_failed.') -join [Environment]::NewLine)
    }

    $matchingRegistryVersions = New-Object System.Collections.Generic.List[object]
    if (@($RegistryCandidates).Count -eq 0) {
        $diagnostics.Add('Registry candidates: none.')
    }
    foreach ($candidate in @($RegistryCandidates)) {
        $registryPath = if ($null -eq $candidate.PSObject.Properties['RegistryPath']) {
            '<unknown>'
        }
        else {
            [string]$candidate.RegistryPath
        }
        $displayVersion = if ($null -eq $candidate.PSObject.Properties['DisplayVersion']) {
            $null
        }
        else {
            [string]$candidate.DisplayVersion
        }
        $installLocation = if ($null -eq $candidate.PSObject.Properties['InstallLocation']) {
            $null
        }
        else {
            [string]$candidate.InstallLocation
        }
        $candidatePrefix = "Registry candidate: path=$registryPath; DisplayVersion=$(if ([string]::IsNullOrWhiteSpace($displayVersion)) { '<empty>' } else { $displayVersion }); InstallLocation=$(if ([string]::IsNullOrWhiteSpace($installLocation)) { '<empty>' } else { $installLocation })"

        if ([string]::IsNullOrWhiteSpace($installLocation)) {
            $diagnostics.Add("$candidatePrefix; rejected=install_location_missing")
            continue
        }

        try {
            $canonicalInstallLocation = & $CanonicalizeDirectory $installLocation
        }
        catch {
            $diagnostics.Add("$candidatePrefix; rejected=install_location_invalid")
            continue
        }
        if (-not [string]::Equals(
            $canonicalInstallLocation,
            $canonicalIsccDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
            $diagnostics.Add("$candidatePrefix; rejected=path_mismatch")
            continue
        }

        $version = ConvertFrom-AgentBellInnoVersionText -Value $displayVersion
        if ($null -eq $version) {
            $diagnostics.Add("$candidatePrefix; rejected=display_version_invalid")
            continue
        }
        if ($version -lt $MinimumVersion) {
            $diagnostics.Add("$candidatePrefix; rejected=below_minimum_$MinimumVersion")
            continue
        }

        $diagnostics.Add("$candidatePrefix; accepted=$version")
        $matchingRegistryVersions.Add([pscustomobject]@{
            Version = $version
            RawValue = $displayVersion
            RegistryPath = $registryPath
        })
    }

    $registryVersion = $matchingRegistryVersions |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -ne $registryVersion) {
        return [pscustomobject]@{
            Version = $registryVersion.Version
            Source = 'RegistryDisplayVersion'
            RawValue = $registryVersion.RawValue
            IsccPath = $resolvedIsccPath
            Diagnostics = @($diagnostics)
        }
    }

    $probe = & $CommandOutputProvider $resolvedIsccPath
    $probeFailure = if ($null -eq $probe.PSObject.Properties['Failure']) {
        $null
    }
    else {
        [string]$probe.Failure
    }
    $probeOutput = if ($null -eq $probe.PSObject.Properties['Output']) {
        ''
    }
    else {
        [string]$probe.Output
    }
    if (-not [string]::IsNullOrWhiteSpace($probeFailure)) {
        $diagnostics.Add("Command output rejected: $probeFailure")
    }
    else {
        $commandVersion = ConvertFrom-AgentBellInnoVersionText -Value $probeOutput
        if ($null -eq $commandVersion) {
            $diagnostics.Add('Command output rejected: exact semantic version not found.')
        }
        elseif ($commandVersion -lt $MinimumVersion) {
            $diagnostics.Add("Command output rejected: $commandVersion is below $MinimumVersion.")
        }
        else {
            return [pscustomobject]@{
                Version = $commandVersion
                Source = 'CommandOutput'
                RawValue = $probeOutput
                IsccPath = $resolvedIsccPath
                Diagnostics = @($diagnostics)
            }
        }
    }

    throw (($diagnostics + 'No trustworthy Inno Setup version source satisfied the minimum version.') -join [Environment]::NewLine)
}

function Select-AgentBellInnoSetupCompilerVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$CompilerCandidates,
        [AllowNull()][object[]]$RegistryCandidates,
        [Version]$MinimumVersion = [Version]'6.4.0',
        [scriptblock]$CanonicalizeDirectory,
        [scriptblock]$CommandOutputProvider
    )

    $accepted = New-Object System.Collections.Generic.List[object]
    $rejected = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($CompilerCandidates)) {
        $path = [string]$candidate.Path
        try {
            $resolutionArguments = @{
                IsccPath = $path
                ProductVersion = [string]$candidate.ProductVersion
                FileVersion = [string]$candidate.FileVersion
                RegistryCandidates = $RegistryCandidates
                MinimumVersion = $MinimumVersion
            }
            if ($null -ne $CanonicalizeDirectory) {
                $resolutionArguments.CanonicalizeDirectory = $CanonicalizeDirectory
            }
            if ($null -ne $CommandOutputProvider) {
                $resolutionArguments.CommandOutputProvider = $CommandOutputProvider
            }
            $resolution = Resolve-AgentBellInnoSetupVersion @resolutionArguments
            $accepted.Add([pscustomobject]@{
                IsccPath = $resolution.IsccPath
                Version = $resolution.Version
                Source = $resolution.Source
                RawValue = $resolution.RawValue
                Diagnostics = $resolution.Diagnostics
            })
        }
        catch {
            $rejected.Add("Compiler candidate rejected: $path$([Environment]::NewLine)$($_.Exception.Message)")
        }
    }

    $selected = $accepted |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -ne $selected) {
        return $selected
    }

    if ($rejected.Count -eq 0) {
        throw 'No ISCC.exe candidates were discovered.'
    }
    throw ($rejected -join ([Environment]::NewLine + [Environment]::NewLine))
}
