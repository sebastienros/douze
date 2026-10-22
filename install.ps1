<#
.SYNOPSIS
Installs douze, or updates it in place.

.DESCRIPTION
Installs douze from its NuGet packages, without the .NET SDK: the native build of the machine's platform
(douze.<rid>), downloaded from the feed's flat container and extracted to <install dir>\<version>, with
<install dir>\bin linking to the current version (a junction on Windows, a symbolic link elsewhere). Runs on Windows
PowerShell 5.1 and PowerShell 7, on Windows, and on Linux and macOS under pwsh.

  irm https://github.com/sebastienros/douze/releases/latest/download/install.ps1 | iex
  & ([scriptblock]::Create((irm https://github.com/sebastienros/douze/releases/latest/download/install.ps1))) -Preview

.PARAMETER Preview
Install the latest version, previews included (default: the latest stable).

.PARAMETER Version
Install this version.

.PARAMETER InstallDir
Where to install (default: $env:DOUZE_INSTALL_DIR, or %LOCALAPPDATA%\douze on Windows and ~/.douze elsewhere).

.PARAMETER NoModifyPath
Do not add <install dir>\bin to the user's PATH (on Windows) or to the PowerShell profile (elsewhere).

.PARAMETER Feed
The NuGet feed (service index) to install from (default: nuget.org, and the preview feed for previews or while
nuget.org has no stable version).

.PARAMETER Uninstall
Remove douze and what the installer added to PATH.
#>
[CmdletBinding()]
param(
    [switch] $Preview,
    [string] $Version,
    [string] $InstallDir,
    [switch] $NoModifyPath,
    [string] $Feed,
    [switch] $Uninstall
)

$DouzeNuGetFeed = 'https://api.nuget.org/v3/index.json'
$DouzePreviewFeed = 'https://f.feedz.io/sebastienros/douze/nuget/index.json'
$DouzeProfileMarker = '# Added by the douze installer'

function Test-DouzeWindows {
    return ($PSVersionTable.PSEdition -eq 'Desktop') -or ((Test-Path variable:IsWindows) -and $IsWindows)
}

function Get-DouzeRid {
    if (Test-DouzeWindows) {
        # The machine's architecture, not the process's (an x64 PowerShell on an arm64 Windows).
        $arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
        switch ($arch) {
            'AMD64' { return 'win-x64' }
            'ARM64' { return 'win-arm64' }
            default { throw "unsupported architecture $($arch): douze ships for x64 and arm64" }
        }
    }

    $os = (uname -s)
    switch ($os) {
        'Linux' { $os = 'linux' }
        'Darwin' { $os = 'osx' }
        default { throw "unsupported operating system $($os): douze ships for Linux, macOS and Windows" }
    }
    $machine = (uname -m)
    switch -Regex ($machine) {
        '^(x86_64|amd64)$' { $arch = 'x64' }
        '^(aarch64|arm64)$' { $arch = 'arm64' }
        default { throw "unsupported architecture $($machine): douze ships for x64 and arm64" }
    }
    # An x64 process under Rosetta on Apple silicon: the arm64 build is the native one.
    if ($os -eq 'osx' -and $arch -eq 'x64' -and ((sysctl -n sysctl.proc_translated 2>$null) -eq '1')) {
        $arch = 'arm64'
    }
    if ($os -eq 'linux' -and (@(Get-ChildItem '/lib/ld-musl-*' -ErrorAction SilentlyContinue).Count -gt 0)) {
        throw 'musl based Linux distributions (Alpine) are not supported: douze ships for glibc'
    }
    return "$os-$arch"
}

function Invoke-DouzeDownload([string] $Uri, [string] $OutFile) {
    $attempt = 0
    while ($true) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
            return
        }
        catch {
            $status = $null
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
                $status = [int] $_.Exception.Response.StatusCode
            }
            $attempt++
            if (($status -ge 400 -and $status -lt 500) -or $attempt -ge 3) { throw }
            Start-Sleep -Seconds $attempt
        }
    }
}

# Compares two versions in NuGet's order: numbers, then a version without a prerelease label above one with it,
# labels compared part by part (numeric parts as numbers, below the others, which compare case-insensitively).
function Compare-DouzeVersion([string] $Left, [string] $Right) {
    $aParts = ($Left -replace '\+.*$', '').Split(@('-'), 2, [StringSplitOptions]::None)
    $bParts = ($Right -replace '\+.*$', '').Split(@('-'), 2, [StringSplitOptions]::None)
    $aNumbers = $aParts[0].Split('.')
    $bNumbers = $bParts[0].Split('.')
    for ($i = 0; $i -lt 4; $i++) {
        $x = if ($i -lt $aNumbers.Length) { [long] $aNumbers[$i] } else { 0 }
        $y = if ($i -lt $bNumbers.Length) { [long] $bNumbers[$i] } else { 0 }
        if ($x -ne $y) { return [Math]::Sign($x - $y) }
    }
    $aLabel = if ($aParts.Length -gt 1) { $aParts[1] } else { '' }
    $bLabel = if ($bParts.Length -gt 1) { $bParts[1] } else { '' }
    if ($aLabel -eq '' -or $bLabel -eq '') {
        if ($aLabel -eq $bLabel) { return 0 }
        if ($aLabel -eq '') { return 1 } else { return -1 }
    }
    $aLabels = $aLabel.Split('.')
    $bLabels = $bLabel.Split('.')
    for ($i = 0; $i -lt [Math]::Min($aLabels.Length, $bLabels.Length); $i++) {
        $x = $aLabels[$i]
        $y = $bLabels[$i]
        $xNumeric = $x -match '^[0-9]+$'
        $yNumeric = $y -match '^[0-9]+$'
        if ($xNumeric -and $yNumeric) { $c = ([long] $x).CompareTo([long] $y) }
        elseif ($xNumeric) { $c = -1 }
        elseif ($yNumeric) { $c = 1 }
        else { $c = [string]::Compare($x, $y, [StringComparison]::OrdinalIgnoreCase) }
        if ($c -ne 0) { return [Math]::Sign($c) }
    }
    return [Math]::Sign($aLabels.Length - $bLabels.Length)
}

function Get-DouzeMaxVersion([string[]] $Versions) {
    $best = $null
    foreach ($v in $Versions) {
        if ($null -eq $best -or (Compare-DouzeVersion $v $best) -gt 0) { $best = $v }
    }
    return $best
}

# The package base address (flat container) of a feed's service index.
function Get-DouzePackageBase([string] $ServiceIndex) {
    try {
        $index = Invoke-RestMethod -Uri $ServiceIndex -UseBasicParsing
    }
    catch {
        throw "cannot read the feed $($ServiceIndex): $($_.Exception.Message)"
    }
    $resource = @($index.resources | Where-Object { $_.'@type' -eq 'PackageBaseAddress/3.0.0' }) | Select-Object -First 1
    if (-not $resource) { throw "the feed $ServiceIndex has no PackageBaseAddress/3.0.0 resource" }
    $base = [string] $resource.'@id'
    if (-not $base.EndsWith('/')) { $base += '/' }
    return $base
}

# Looks for the version to install in a feed: returns the base address and the version, or $null.
function Find-DouzeVersion([string] $ServiceIndex, [string] $Package, [string] $Wanted, [bool] $IncludePreviews) {
    $base = Get-DouzePackageBase $ServiceIndex
    try {
        $versions = @((Invoke-RestMethod -Uri "$base$Package/index.json" -UseBasicParsing).versions)
    }
    catch {
        $versions = @()
    }
    if ($Wanted) {
        $found = $versions | Where-Object { $_ -eq $Wanted.ToLowerInvariant() } | Select-Object -First 1
    }
    elseif ($IncludePreviews) {
        $found = Get-DouzeMaxVersion $versions
    }
    else {
        $found = Get-DouzeMaxVersion @($versions | Where-Object { -not $_.Contains('-') })
    }
    if (-not $found) { return $null }
    return @{ Base = $base; Version = [string] $found }
}

# The version folders of the install folder (a folder with a douze executable).
function Get-DouzeVersionFolders([string] $Root, [string] $Executable) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -Directory -Force | Where-Object {
            -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and (Test-Path -LiteralPath (Join-Path $_.FullName $Executable))
        } | ForEach-Object { $_.Name })
}

function Remove-DouzeLink([string] $Link) {
    if (Test-DouzeWindows) {
        # Deletes the junction, not the folder it points to.
        [IO.Directory]::Delete($Link)
    }
    else {
        rm -f $Link
    }
}

function Get-DouzeLinkTarget([string] $Link) {
    $item = Get-Item -LiteralPath $Link -Force -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Link exists and is not a link the installer made"
    }
    return Split-Path -Leaf (@($item.Target)[0])
}

function Set-DouzeLink([string] $Link, [string] $Folder) {
    if (Test-DouzeWindows) {
        if (Test-Path -LiteralPath $Link) { Remove-DouzeLink $Link }
        New-Item -ItemType Junction -Path $Link -Target $Folder | Out-Null
    }
    else {
        ln -sfn (Split-Path -Leaf $Folder) $Link
        if ($LASTEXITCODE -ne 0) { throw "cannot link $Link to $Folder" }
    }
}

# --- PATH ---

function Get-DouzeUserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try {
        # Unexpanded, so that writing it back keeps its %VARIABLES%.
        return [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        $key.Close()
    }
}

function Set-DouzeUserPath([string] $Path) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $key.SetValue('Path', $Path, [Microsoft.Win32.RegistryValueKind]::ExpandString)
    }
    finally {
        $key.Close()
    }
    # Setting a variable through the .NET API tells the running programs (Explorer) that the environment changed.
    [Environment]::SetEnvironmentVariable('DOUZE_INSTALLER', '1', 'User')
    [Environment]::SetEnvironmentVariable('DOUZE_INSTALLER', $null, 'User')
}

function Test-DouzePathContains([string] $Path, [string] $Folder) {
    $separator = [IO.Path]::PathSeparator
    foreach ($entry in $Path.Split($separator)) {
        if ($entry.TrimEnd('\', '/') -eq $Folder.TrimEnd('\', '/')) { return $true }
    }
    return $false
}

function Get-DouzeProfile {
    return $PROFILE.CurrentUserAllHosts
}

function Add-DouzePath([string] $Bin) {
    $separator = [IO.Path]::PathSeparator
    if (-not (Test-DouzePathContains $env:PATH $Bin)) {
        $env:PATH = "$Bin$separator$env:PATH"
    }
    if (Test-DouzeWindows) {
        $userPath = Get-DouzeUserPath
        if (Test-DouzePathContains $userPath $Bin) { return }
        if ($NoModifyPath) {
            Write-Host "Add $Bin to PATH to run douze in new terminals."
            return
        }
        $trimmed = $userPath.TrimEnd(';')
        Set-DouzeUserPath ($(if ($trimmed) { "$Bin;$trimmed" } else { $Bin }))
        Write-Host "Added $Bin to the user's PATH: new terminals run douze."
        return
    }

    $profilePath = Get-DouzeProfile
    if ($NoModifyPath) {
        Write-Host "Add $Bin to PATH to run douze, for instance in $profilePath."
        return
    }
    if ((Test-Path -LiteralPath $profilePath) -and (Select-String -LiteralPath $profilePath -SimpleMatch $DouzeProfileMarker -Quiet)) {
        return
    }
    $line = "`$env:PATH = `"$Bin`" + [IO.Path]::PathSeparator + `$env:PATH"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $profilePath) | Out-Null
    Add-Content -LiteralPath $profilePath -Value @('', $DouzeProfileMarker, $line)
    Write-Host "Added to $($profilePath):"
    Write-Host "  $line"
    Write-Host 'Other shells need it on their PATH too: install.sh adds it to their profile.'
}

function Remove-DouzePath([string] $Bin) {
    if (Test-DouzeWindows) {
        $userPath = Get-DouzeUserPath
        if (Test-DouzePathContains $userPath $Bin) {
            $kept = @($userPath.Split(';') | Where-Object { $_ -and $_.TrimEnd('\') -ne $Bin.TrimEnd('\') })
            Set-DouzeUserPath ($kept -join ';')
            Write-Host "Removed $Bin from the user's PATH."
        }
        return
    }

    $profilePath = Get-DouzeProfile
    if (-not (Test-Path -LiteralPath $profilePath)) { return }
    $lines = @(Get-Content -LiteralPath $profilePath)
    $kept = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -eq $DouzeProfileMarker) {
            # The marker, the line after it, and the empty line the installer wrote before them.
            if ($kept.Count -gt 0 -and $kept[$kept.Count - 1] -eq '') { $kept.RemoveAt($kept.Count - 1) }
            $i++
            continue
        }
        $kept.Add($lines[$i])
    }
    if ($kept.Count -eq $lines.Length) { return }
    if ($kept.Count -eq 0) { Remove-Item -LiteralPath $profilePath } else { Set-Content -LiteralPath $profilePath -Value $kept }
    Write-Host "Removed douze from PATH in $profilePath."
}

# --- install ---

function Install-Douze {
    # In this function's scope, which keeps them out of the session that runs the script with Invoke-Expression.
    Set-StrictMode -Version 2
    $ErrorActionPreference = 'Stop'
    # Windows PowerShell's progress bar makes downloads several times slower.
    $ProgressPreference = 'SilentlyContinue'

    $windows = Test-DouzeWindows
    $executable = if ($windows) { 'douze.exe' } else { 'douze' }
    $root = $InstallDir
    if (-not $root) { $root = $env:DOUZE_INSTALL_DIR }
    if (-not $root) { $root = if ($windows) { Join-Path $env:LOCALAPPDATA 'douze' } else { Join-Path $HOME '.douze' } }
    $root = [IO.Path]::GetFullPath($root)
    $bin = Join-Path $root 'bin'

    if ($Uninstall) {
        if (Test-Path -LiteralPath $root) {
            if (Get-DouzeLinkTarget $bin) { Remove-DouzeLink $bin }
            foreach ($name in (Get-DouzeVersionFolders $root $executable)) {
                Remove-Item -LiteralPath (Join-Path $root $name) -Recurse -Force
            }
            if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { Remove-Item -LiteralPath $root }
            else { Write-Host "Kept $root, which has other files." }
        }
        Remove-DouzePath $bin
        Write-Host "douze was uninstalled from $root."
        return
    }

    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $rid = Get-DouzeRid
    $package = "douze.$rid"

    if ($Feed) {
        $found = Find-DouzeVersion $Feed $package $Version $Preview.IsPresent
        if (-not $found) {
            if ($Version) { throw "no version $Version of $package in $Feed" } else { throw "no version of $package in $Feed" }
        }
    }
    elseif ($Version) {
        $found = Find-DouzeVersion $DouzeNuGetFeed $package $Version $true
        if (-not $found) { $found = Find-DouzeVersion $DouzePreviewFeed $package $Version $true }
        if (-not $found) { throw "no version $Version of $package on nuget.org or the preview feed" }
    }
    elseif ($Preview) {
        $found = Find-DouzeVersion $DouzePreviewFeed $package $null $true
        if (-not $found) { throw "no version of $package in the preview feed" }
    }
    else {
        $found = Find-DouzeVersion $DouzeNuGetFeed $package $null $false
        if (-not $found) {
            Write-Host "No stable version of douze on nuget.org yet: installing the latest preview, from $DouzePreviewFeed."
            $found = Find-DouzeVersion $DouzePreviewFeed $package $null $true
            if (-not $found) { throw "no version of $package in the preview feed" }
        }
    }

    $foundVersion = $found.Version
    $target = Join-Path $root $foundVersion
    $installed = $false
    if (Test-Path -LiteralPath (Join-Path $target $executable)) {
        & (Join-Path $target $executable) --version *> $null
        $installed = $LASTEXITCODE -eq 0
    }

    if ($installed) {
        Write-Host "douze $foundVersion ($rid) is already installed."
    }
    else {
        Write-Host "Downloading douze $foundVersion ($rid)..."
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("douze-" + [Guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $temp | Out-Null
        try {
            $nupkg = Join-Path $temp 'package.nupkg'
            try {
                Invoke-DouzeDownload "$($found.Base)$package/$foundVersion/$package.$foundVersion.nupkg" $nupkg
            }
            catch {
                throw "cannot download $package $($foundVersion): $($_.Exception.Message)"
            }
            $extracted = Join-Path $temp 'package'
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            try {
                [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $extracted)
            }
            catch {
                throw "the downloaded package is not a valid zip file: $($_.Exception.Message)"
            }
            $tool = Get-ChildItem -LiteralPath (Join-Path $extracted 'tools') -Recurse -File -Filter $executable -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq $rid } | Select-Object -First 1
            if (-not $tool) { throw "the package has no tools/*/$rid/$executable" }
            $toolFolder = $tool.Directory.FullName
            Remove-Item -LiteralPath (Join-Path $toolFolder 'DotnetToolSettings.xml') -ErrorAction SilentlyContinue
            if (-not $windows) {
                chmod +x $tool.FullName
            }
            & $tool.FullName --version *> $null
            if ($LASTEXITCODE -ne 0) { throw "the downloaded douze does not run ($($tool.FullName) --version failed)" }

            New-Item -ItemType Directory -Force -Path $root | Out-Null
            foreach ($path in @($target, "$target.tmp")) {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            }
            Copy-Item -LiteralPath $toolFolder -Destination "$target.tmp" -Recurse
            Rename-Item -LiteralPath "$target.tmp" -NewName $foundVersion
        }
        finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $previous = Get-DouzeLinkTarget $bin
    if ($previous -ne $foundVersion) {
        Set-DouzeLink $bin $target
        # Keep the previous version, remove the older ones (a version still running stays until a later update).
        foreach ($name in (Get-DouzeVersionFolders $root $executable)) {
            if ($name -ne $foundVersion -and $name -ne $previous) {
                Remove-Item -LiteralPath (Join-Path $root $name) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $reported = & (Join-Path $bin $executable) --version
    Write-Host "douze $reported is installed in $target, linked from $bin."
    Add-DouzePath $bin
}

Install-Douze
