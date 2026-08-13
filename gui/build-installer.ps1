<# Builds the single-file Windows installer from an already verified package tree. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,
    [Parameter(Mandatory)]
    [string]$OutputDir,
    [Parameter(Mandatory)]
    [string]$InstallerName,
    [Parameter(Mandatory)]
    [string]$GuiVersion,
    [Parameter(Mandatory)]
    [string]$UpstreamVersion,
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$CompilerPath,
    [string]$InstallerAppId = '{407AED06-6513-413B-8B56-D5576529BE4A}',
    [string]$InstallerAppName = 'Zapret Control Center',
    [ValidateSet('admin', 'lowest')]
    [string]$InstallerPrivilegesRequired = 'admin',
    [string]$InstallerApplicationMutexes =
        'Global\ZapretGUI.SingleInstance,Global\ZapretGUI.Update.Apply'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$MaxInstallerBytes = 512L * 1024 * 1024

function Resolve-RequiredDirectory {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory does not exist: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-InnoCompiler {
    param([string]$ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_COMPILER)) {
        $candidates += $env:INNO_SETUP_COMPILER
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates += $command.Source
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += Join-Path `
            $env:LOCALAPPDATA `
            'Programs\Inno Setup 6\ISCC.exe'
    }
    $candidates += @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found.'
}

if ($GuiVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "GUI version must use numeric x.y.z format: $GuiVersion"
}
if ($UpstreamVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$') {
    throw "Flowseal version is unsafe for a release filename: $UpstreamVersion"
}
if ($InstallerName -notmatch '^zapret-control-center-setup-[0-9]+\.[0-9]+\.[0-9]+-flowseal-[0-9A-Za-z][0-9A-Za-z._+-]{0,63}-win-x64\.exe$') {
    throw "Unexpected installer name: $InstallerName"
}
if ($InstallerAppId -notmatch '^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$') {
    throw "Invalid installer AppId: $InstallerAppId"
}
if ([string]::IsNullOrWhiteSpace($InstallerAppName) -or
    $InstallerAppName.Length -gt 80 -or
    $InstallerAppName -match '[\r\n"]') {
    throw 'Installer AppName must contain 1-80 characters.'
}
if ([string]::IsNullOrWhiteSpace($InstallerApplicationMutexes) -or
    $InstallerApplicationMutexes.Length -gt 255 -or
    $InstallerApplicationMutexes -match '[\r\n"]') {
    throw 'Installer application mutex list is invalid.'
}

$PackageRoot = Resolve-RequiredDirectory $PackageRoot 'Package root'
$RepoRoot = Resolve-RequiredDirectory $RepoRoot 'Repository root'
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

foreach ($relative in @(
    'ZapretGUI.exe',
    'bin\winws.exe',
    'bin\WinDivert.dll',
    'bin\WinDivert64.sys',
    'lists',
    'utils',
    'service.bat',
    'UPDATE_MANIFEST.json'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relative))) {
        throw "Installer payload is missing: $relative"
    }
}
$privateFiles = @(
    Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
        Where-Object {
            $_.Name -like '*-user.txt' -or
            $_.Name -eq 'game_filter.enabled'
        }
)
if ($privateFiles.Count -gt 0) {
    throw 'User-owned files must not be embedded in the installer.'
}

$compiler = Resolve-InnoCompiler $CompilerPath
$compilerInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler)
$compilerVersionText = $compilerInfo.FileVersion
$compilerVersion = $null
if ($compilerInfo.FileDescription -ne 'Inno Setup Command-Line Compiler') {
    throw "Unexpected installer compiler: $compiler"
}
# Recent signed ISCC builds intentionally expose 0.0.0.0 in Win32 version
# metadata, so only enforce the minimum when a real semantic version exists.
if ([Version]::TryParse($compilerVersionText, [ref]$compilerVersion) -and
    $compilerVersion.Major -gt 0 -and
    $compilerVersion -lt [Version]'6.4.0') {
    throw "Inno Setup 6.4 or newer is required; found $compilerVersionText."
}

$scriptPath = Join-Path $RepoRoot 'gui\installer\ZapretControlCenter.iss'
$iconPath = Join-Path $RepoRoot 'gui\ZapretGui\Assets\app.ico'
$licensePath = Join-Path $RepoRoot 'gui\LICENSE'
foreach ($path in @($scriptPath, $iconPath, $licensePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required installer source is missing: $path"
    }
}

$installerPath = Join-Path $OutputDir $InstallerName
if (Test-Path -LiteralPath $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}
$baseName = [IO.Path]::GetFileNameWithoutExtension($InstallerName)
$innoAppId = '{' + $InstallerAppId
$arguments = @(
    '/Qp',
    "/DSourceDir=$PackageRoot",
    "/DOutputDir=$OutputDir",
    "/DInstallerBaseName=$baseName",
    "/DGuiVersion=$GuiVersion",
    "/DUpstreamVersion=$UpstreamVersion",
    "/DInstallerAppId=$innoAppId",
    "/DInstallerAppName=$InstallerAppName",
    "/DInstallerPrivilegesRequired=$InstallerPrivilegesRequired",
    "/DInstallerApplicationMutexes=$InstallerApplicationMutexes",
    "/DSetupIconPath=$iconPath",
    "/DLicensePath=$licensePath",
    $scriptPath
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup did not create the expected installer: $installerPath"
}
$installer = Get-Item -LiteralPath $installerPath
if ($installer.Length -le 0 -or $installer.Length -gt $MaxInstallerBytes) {
    throw "Installer size is outside the allowed range: $($installer.Length) bytes."
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath)
$actualVersion = ([string]$versionInfo.FileVersion).Trim()
if ($actualVersion -notin @($GuiVersion, "$GuiVersion.0")) {
    throw "Installer file version is $actualVersion; expected $GuiVersion."
}

Write-Host "Installer: $installerPath" -ForegroundColor Green
