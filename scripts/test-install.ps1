<#
.SYNOPSIS
Tests install.sh and install.ps1: installs douze with each of them, runs it, updates and uninstalls it.

.DESCRIPTION
Installs from the packages of a folder (served as a local feed by test-feed.cs) or from a feed. Each script installs
the latest preview into a temporary folder; the installed douze prints its version, builds a site whose image needs
libvips (and the Sample site), and is uninstalled. install.sh runs on Linux and macOS, install.ps1 on PowerShell 7,
and on Windows PowerShell 5.1 on Windows.

.PARAMETER Packages
A folder of .nupkg files to install from.

.PARAMETER Feed
A feed (service index) to install from, instead of -Packages.

.PARAMETER Version
The version to install and expect; waits up to 10 minutes for the feed to list it (a feed indexes a package a while
after the push).

.PARAMETER TestPath
Also lets the scripts add douze to PATH (the user's PATH on Windows, the profile elsewhere), checks it, and that
uninstalling removes it. Meant for CI machines.
#>
[CmdletBinding()]
param(
    [string] $Packages,
    [string] $Feed,
    [string] $Version,
    [switch] $TestPath
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repository = Split-Path -Parent $PSScriptRoot
$windows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$executable = if ($windows) { 'douze.exe' } else { 'douze' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("douze-install-test-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    Write-Host "> $Command $($Arguments -join ' ')"
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command exited with $LASTEXITCODE" }
}

$server = $null
try {
    if ($Packages) {
        $port = Get-Random -Minimum 20000 -Maximum 40000
        $feedApp = Join-Path $PSScriptRoot 'test-feed.cs'
        Invoke-Checked 'dotnet' @('build', $feedApp)
        $server = Start-Process 'dotnet' -ArgumentList @('run', '--no-build', $feedApp, '--', (Resolve-Path $Packages).Path, $port) -PassThru -NoNewWindow
        $Feed = "http://localhost:$port/index.json"
    }
    if (-not $Feed) { throw 'pass -Packages or -Feed' }

    # Waits for the feed, and for it to list the version.
    $deadline = (Get-Date).AddMinutes(10)
    while ($true) {
        try {
            $index = Invoke-RestMethod $Feed
            $base = ($index.resources | Where-Object { $_.'@type' -eq 'PackageBaseAddress/3.0.0' } | Select-Object -First 1).'@id'.TrimEnd('/')
            if ($Packages -or -not $Version) { break }
            # The pointer package, pushed after the RID-specific packages.
            $listed = (Invoke-RestMethod "$base/douze/index.json").versions
            if ($listed -contains $Version.ToLowerInvariant()) { break }
        }
        catch {
        }
        if ((Get-Date) -gt $deadline) { throw "$Feed does not answer, or does not list $Version" }
        Start-Sleep -Seconds $(if ($server) { 1 } else { 5 })
    }

    $runs = @()
    if (-not $windows) { $runs += 'sh' }
    $runs += 'pwsh'
    if ($windows) { $runs += 'powershell' }

    foreach ($run in $runs) {
        Write-Host "::group::$run"
        $installDir = Join-Path $work "install-$run"
        $bin = Join-Path $installDir 'bin'
        $options = @{ Preview = $true; Version = $Version; Feed = $Feed; InstallDir = $installDir; NoModifyPath = -not $TestPath }

        $install = {
            param([hashtable] $Extra)
            $o = $options + $Extra
            if ($run -eq 'sh') {
                $arguments = @((Join-Path $repository 'install.sh'))
                if ($o['Uninstall']) { $arguments += '--uninstall' }
                else {
                    $arguments += @('--preview', '--feed', $o.Feed)
                    if ($o.Version) { $arguments += @('--version', $o.Version) }
                    if ($o.NoModifyPath) { $arguments += '--no-modify-path' }
                }
                $arguments += @('--install-dir', $o.InstallDir)
                Invoke-Checked 'sh' $arguments
            }
            else {
                $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repository 'install.ps1'))
                if ($o['Uninstall']) { $arguments += '-Uninstall' }
                else {
                    $arguments += @('-Preview', '-Feed', $o.Feed)
                    if ($o.Version) { $arguments += @('-Version', $o.Version) }
                    if ($o.NoModifyPath) { $arguments += '-NoModifyPath' }
                }
                $arguments += @('-InstallDir', $o.InstallDir)
                Invoke-Checked $run $arguments
            }
        }

        & $install @{}
        $douze = Join-Path $bin $executable
        $reported = (& $douze --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw "$douze --version exited with $LASTEXITCODE" }
        Write-Host "douze --version: $reported"
        if ($Version -and ($reported -replace '\+.*$', '') -ne $Version) { throw "installed $reported, expected $Version" }

        # libvips loads from the install folder.
        $site = Join-Path $work "site-$run"
        Invoke-Checked $douze @('-s', (Join-Path $PSScriptRoot 'install-test-site'), '-o', $site, '--plain', '--no-cache')
        if (-not (Get-ChildItem (Join-Path $site 'assets/images') -Filter '*.webp')) { throw 'the image variant was not written' }
        Invoke-Checked $douze @('-s', (Join-Path $repository 'Sample'), '-o', (Join-Path $work "sample-$run"), '--plain', '--no-cache', '-q')
        if (-not (Test-Path (Join-Path $work "sample-$run/index.html"))) { throw 'the Sample site has no index.html' }

        if ($TestPath) {
            if ($windows) {
                $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
                if (-not ($userPath.Split(';') -contains $bin)) { throw "the user's PATH does not have $bin" }
            }
            elseif ($run -eq 'pwsh') {
                $profilePath = pwsh -NoProfile -Command '$PROFILE.CurrentUserAllHosts'
                if (-not (Select-String -LiteralPath $profilePath -SimpleMatch $bin -Quiet)) { throw "$profilePath does not add $bin to PATH" }
            }
        }

        # Running it again keeps the installed version.
        & $install @{}

        & $install @{ Uninstall = $true }
        if (Test-Path $installDir) { throw "$installDir is still there after uninstalling" }
        if ($TestPath -and $windows) {
            $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            if ($userPath -and ($userPath.Split(';') -contains $bin)) { throw "the user's PATH still has $bin" }
        }
        Write-Host '::endgroup::'
    }
    Write-Host "install.sh and install.ps1 installed, ran and uninstalled douze ($($runs -join ', '))."
}
finally {
    if ($server) {
        # dotnet run starts the server as a child process: stop it through the feed.
        try { Invoke-WebRequest "http://localhost:$port/shutdown" -UseBasicParsing | Out-Null } catch { }
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
