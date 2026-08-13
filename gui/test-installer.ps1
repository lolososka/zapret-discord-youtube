<# Performs a silent fresh-install, upgrade, preservation, and uninstall smoke test. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$GuiVersion,
    [Parameter(Mandatory)]
    [string]$UpstreamVersion,
    [Parameter(Mandatory)]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedProcess {
    param([string]$FilePath, [string[]]$Arguments)

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    # Windows PowerShell runs on .NET Framework, where ProcessStartInfo has
    # Arguments but not ArgumentList. These test switches contain no quotes
    # or trailing backslashes, so quoting every complete argument is lossless.
    $start.Arguments = ($Arguments | ForEach-Object {
        if ($_ -match '["\r\n]' -or $_.EndsWith('\')) {
            throw "Unsafe native test argument: $_"
        }
        '"' + $_ + '"'
    }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) {
        throw "Could not start: $FilePath"
    }
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer does not exist: $InstallerPath"
}
if ($GuiVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid GUI version: $GuiVersion"
}
if ($UpstreamVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$') {
    throw "Invalid Flowseal version: $UpstreamVersion"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'zapret-installer-smoke-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'Zapret Control Center'
$runtimeRoot = Join-Path $installRoot 'runtime'
$setupLog = Join-Path $testRoot 'setup.log'
$upgradeLog = Join-Path $testRoot 'upgrade.log'
$smokeInstallerDir = Join-Path $testRoot 'installer'
$smokeAppId = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$smokeInstallerName = [IO.Path]::GetFileName($InstallerPath)
$repoRoot = Split-Path -Parent $PSScriptRoot
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Recompile the already verified payload with an isolated AppId. This keeps
    # the smoke test from touching a developer's real Apps & Features record.
    $packageRoot = Join-Path $testRoot 'payload'
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $extractRoot = Join-Path $testRoot 'extract'
    $portableZipName = "zapret-control-center-$GuiVersion-flowseal-$UpstreamVersion-win-x64.zip"
    $portableZipPath = Join-Path `
        (Split-Path -Parent $InstallerPath) `
        $portableZipName
    if (-not (Test-Path -LiteralPath $portableZipPath -PathType Leaf)) {
        throw "Installer smoke could not find the matching portable ZIP: $portableZipName"
    }
    [IO.Compression.ZipFile]::ExtractToDirectory(
        $portableZipPath,
        $extractRoot)
    $roots = @(Get-ChildItem -LiteralPath $extractRoot -Directory)
    if ($roots.Count -ne 1) {
        throw 'Installer smoke expected one package root in the portable ZIP.'
    }
    $packageRoot = $roots[0].FullName
    [IO.Directory]::CreateDirectory($smokeInstallerDir) | Out-Null
    & (Join-Path $PSScriptRoot 'build-installer.ps1') `
        -PackageRoot $packageRoot `
        -OutputDir $smokeInstallerDir `
        -InstallerName $smokeInstallerName `
        -GuiVersion $GuiVersion `
        -UpstreamVersion $UpstreamVersion `
        -RepoRoot $repoRoot `
        -InstallerAppId $smokeAppId `
        -InstallerAppName 'Zapret Control Center Installer Smoke' `
        -InstallerPrivilegesRequired lowest `
        -InstallerApplicationMutexes (
            'Local\ZapretGUI.InstallerSmoke.' +
            [guid]::NewGuid().ToString('N'))
    $InstallerPath = Join-Path $smokeInstallerDir $smokeInstallerName

    $setupArgs = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NOICONS',
        "/DIR=$installRoot",
        "/LOG=$setupLog"
    )
    Invoke-CheckedProcess $InstallerPath $setupArgs

    foreach ($relative in @(
        'ZapretGUI.exe',
        'bin\winws.exe',
        'bin\WinDivert.dll',
        'bin\WinDivert64.sys',
        'service.bat',
        'UPDATE_MANIFEST.json'
    )) {
        $path = Join-Path $runtimeRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -le 0) {
            throw "Installed runtime is missing: $relative"
        }
    }
    if (@(Get-ChildItem -LiteralPath $runtimeRoot -Filter 'general*.bat' -File).Count -eq 0) {
        throw 'Installed runtime contains no strategies.'
    }
    $uninstallers = @(
        Get-ChildItem -LiteralPath (Join-Path $installRoot 'uninstall') `
            -Filter 'unins*.exe' -File
    )
    if ($uninstallers.Count -ne 1) {
        throw "Expected one uninstaller outside runtime; found $($uninstallers.Count)."
    }
    if (@(Get-ChildItem -LiteralPath $runtimeRoot -Filter 'unins*.exe' -Recurse -File).Count -ne 0) {
        throw 'Uninstaller must not be placed inside the portable runtime.'
    }

    $manifest = Get-Content `
        -LiteralPath (Join-Path $runtimeRoot 'UPDATE_MANIFEST.json') `
        -Raw |
        ConvertFrom-Json
    if ([string]$manifest.Tag -ne $Tag -or
        [string]$manifest.GuiVersion -ne $GuiVersion -or
        [string]$manifest.UpstreamVersion -ne $UpstreamVersion) {
        throw 'Installed update manifest does not match the requested release.'
    }
    $managedPaths = [Collections.Generic.List[string]]::new()
    $seenManifestPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $runtimeFull = [IO.Path]::GetFullPath($runtimeRoot).TrimEnd('\')
    $runtimePrefix = $runtimeFull + '\'
    foreach ($item in @($manifest.Files)) {
        $relative = [string]$item.Path
        $expectedHash = ([string]$item.Sha256).ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            $expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Installed manifest contains an invalid entry: $relative"
        }
        $installedPath = [IO.Path]::GetFullPath(
            (Join-Path $runtimeFull $relative))
        if (-not $installedPath.StartsWith(
                $runtimePrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not $seenManifestPaths.Add($installedPath)) {
            throw "Installed manifest contains an unsafe or duplicate path: $relative"
        }
        if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf)) {
            throw "Installed manifest file is missing: $relative"
        }
        $installedFile = Get-Item -LiteralPath $installedPath
        if ([long]$installedFile.Length -ne [long]$item.Size) {
            throw "Installed manifest file has the wrong size: $relative"
        }
        $actualHash = (Get-FileHash `
            -LiteralPath $installedPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "Installed manifest file has the wrong SHA-256: $relative"
        }
        $managedPaths.Add($installedPath)
    }
    if ($managedPaths.Count -eq 0) {
        throw 'Installed update manifest contains no managed files.'
    }
    $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        (Join-Path $runtimeRoot 'ZapretGUI.exe'))
    if ($installedVersion.FileVersion -ne "$GuiVersion.0") {
        throw "Installed GUI version is $($installedVersion.FileVersion); expected $GuiVersion.0."
    }

    $userList = Join-Path $runtimeRoot 'lists\list-general-user.txt'
    $customStrategy = Join-Path $runtimeRoot 'general (CUSTOM-SMOKE).bat'
    $gameFilter = Join-Path $runtimeRoot 'utils\game_filter.enabled'
    $checkUpdates = Join-Path $runtimeRoot 'utils\check_updates.enabled'
    $activeDiscord = Join-Path $runtimeRoot 'bin\ACTIVE_DISCORD_UDP.bin'
    $ipset = Join-Path $runtimeRoot 'lists\ipset-all.txt'
    $userMarker = "installer-user-list-$([guid]::NewGuid().ToString('N'))"
    $strategyMarker = "@echo off`r`nrem installer-custom-strategy"
    $activeMarker = [byte[]](1, 3, 3, 7, 42)
    [IO.File]::WriteAllText(
        $userList,
        $userMarker,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $customStrategy,
        $strategyMarker,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $gameFilter,
        'enabled',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($activeDiscord, $activeMarker)
    if (Test-Path -LiteralPath $checkUpdates) {
        Remove-Item -LiteralPath $checkUpdates -Force
    }
    [IO.File]::WriteAllText($ipset, '', [Text.UTF8Encoding]::new($false))

    $upgradeArgs = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NOICONS',
        "/DIR=$installRoot",
        "/LOG=$upgradeLog"
    )
    Invoke-CheckedProcess $InstallerPath $upgradeArgs

    if ([IO.File]::ReadAllText($userList) -ne $userMarker) {
        throw 'Installer upgrade changed the user domain list.'
    }
    if ([IO.File]::ReadAllText($customStrategy) -ne $strategyMarker) {
        throw 'Installer upgrade changed the custom strategy.'
    }
    if (-not (Test-Path -LiteralPath $gameFilter -PathType Leaf)) {
        throw 'Installer upgrade lost the game-filter mode.'
    }
    if (Test-Path -LiteralPath $checkUpdates -PathType Leaf) {
        throw 'Installer upgrade re-enabled update checks against the user choice.'
    }
    $actualActive = [BitConverter]::ToString(
        [IO.File]::ReadAllBytes($activeDiscord))
    $expectedActive = [BitConverter]::ToString($activeMarker)
    if ($actualActive -ne $expectedActive) {
        throw 'Installer upgrade changed the selected Discord payload.'
    }
    if ((Get-Item -LiteralPath $ipset).Length -ne 0) {
        throw 'Installer upgrade changed the selected IPSet mode.'
    }

    $uninstaller = @(
        Get-ChildItem -LiteralPath (Join-Path $installRoot 'uninstall') `
            -Filter 'unins*.exe' -File
    )[0]
    Invoke-CheckedProcess $uninstaller.FullName @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    )

    foreach ($managedPath in $managedPaths) {
        if (Test-Path -LiteralPath $managedPath -PathType Leaf) {
            throw "Uninstall left a managed file behind: $managedPath"
        }
    }
    if (-not (Test-Path -LiteralPath $userList -PathType Leaf) -or
        [IO.File]::ReadAllText($userList) -ne $userMarker) {
        throw 'Uninstall removed or changed the user domain list.'
    }
    if (-not (Test-Path -LiteralPath $customStrategy -PathType Leaf)) {
        throw 'Uninstall removed the custom strategy.'
    }

    Write-Host 'Installer smoke test passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).Path
        $resolvedTemp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolvedTestRoot.StartsWith(
                $resolvedTemp + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an installer test path outside the temp directory: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
