<#
    Creates the portable GitHub Release assets for Zapret Control Center.

    The archive is built from an explicit allowlist and uses fixed ZIP entry
    timestamps so the package layout is stable across retries.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish'),
    [Parameter(Mandatory)]
    [string]$OutputDir,
    [Parameter(Mandatory)]
    [string]$UpstreamVersion,
    [Parameter(Mandatory)]
    [string]$UpstreamCommit,
    [string]$ForkCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$MaxZipBytes = 512L * 1024 * 1024
$MaxExtractedBytes = 1L * 1024 * 1024 * 1024
$MaxArchiveEntries = 10000
$MaxManagedFiles = $MaxArchiveEntries - 1

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory does not exist: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-SafeVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if ($Value -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$') {
        throw "$Label contains characters that are unsafe for release names: $Value"
    }
}

function Assert-NumericVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Label must use the numeric x.y.z format: $Value"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Value
    )

    [IO.File]::WriteAllText(
        $Path,
        $Value,
        [Text.UTF8Encoding]::new($false)
    )
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required release file is missing: $Source"
    }
    if ((Get-Item -LiteralPath $Source).Length -le 0) {
        throw "Required release file is empty: $Source"
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-StreamSha256 {
    param(
        [Parameter(Mandatory)]
        [IO.Stream]$Stream
    )

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($sha.ComputeHash($Stream))
        return $hash.Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$BasePath,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $baseFullPath = [IO.Path]::GetFullPath($BasePath)
    $baseFullPath = $baseFullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $pathFullPath = [IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]$baseFullPath
    $pathUri = [Uri]$pathFullPath
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()
    )
}

$RepoRoot = Resolve-ExistingDirectory -Path $RepoRoot -Label 'Repository root'
$PublishDir = Resolve-ExistingDirectory -Path $PublishDir -Label 'GUI publish'
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

Assert-SafeVersion -Value $UpstreamVersion -Label 'Flowseal version'
if ($UpstreamCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Flowseal commit must be a full 40-character SHA: $UpstreamCommit"
}
$UpstreamCommit = $UpstreamCommit.ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($ForkCommit)) {
    $ForkCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine the fork commit.'
    }
}
if ($ForkCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Fork commit must be a full 40-character SHA: $ForkCommit"
}
$ForkCommit = $ForkCommit.ToLowerInvariant()

$projectPath = Join-Path $RepoRoot 'gui\ZapretGui\ZapretGui.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "GUI project is missing: $projectPath"
}
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNodes = @(
    $project.Project.PropertyGroup.Version |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($versionNodes.Count -ne 1) {
    throw 'ZapretGui.csproj must contain exactly one non-empty <Version>.'
}
$GuiVersion = [string]$versionNodes[0]
Assert-NumericVersion -Value $GuiVersion -Label 'GUI version'

$upstreamShort = $UpstreamCommit.Substring(0, 12)
$tag = "gui-v$GuiVersion-flowseal-v$UpstreamVersion-u$upstreamShort"
$title = "Zapret Control Center v$GuiVersion + Flowseal v$UpstreamVersion"
$packageRootName = "zapret-control-center-$GuiVersion-flowseal-$UpstreamVersion-win-x64"
$zipName = "$packageRootName.zip"
$exeName = 'ZapretGUI.exe'
$checksumsName = 'SHA256SUMS.txt'
$notesName = 'RELEASE_NOTES.md'
$metadataName = 'release-metadata.json'
$updateManifestName = 'UPDATE_MANIFEST.json'

$zipPath = Join-Path $OutputDir $zipName
$releaseExePath = Join-Path $OutputDir $exeName
$checksumsPath = Join-Path $OutputDir $checksumsName
$notesPath = Join-Path $OutputDir $notesName
$metadataPath = Join-Path $OutputDir $metadataName
$stagingRoot = Join-Path $OutputDir ("staging-" + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stagingRoot $packageRootName
$verifyRoot = Join-Path $OutputDir ("verify-" + [guid]::NewGuid().ToString('N'))

foreach ($knownOutput in @(
    $zipPath,
    $releaseExePath,
    $checksumsPath,
    $notesPath,
    $metadataPath
)) {
    if (Test-Path -LiteralPath $knownOutput -PathType Leaf) {
        Remove-Item -LiteralPath $knownOutput -Force
    }
}

try {
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null

    foreach ($directoryName in @('bin', 'lists', 'utils')) {
        $source = Join-Path $RepoRoot $directoryName
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "Required release directory is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination $packageRoot -Recurse
    }

    $strategies = @(
        Get-ChildItem -LiteralPath $RepoRoot -Filter 'general*.bat' -File |
            Sort-Object -Property Name
    )
    if ($strategies.Count -eq 0) {
        throw 'No general*.bat strategies were found.'
    }
    foreach ($strategy in $strategies) {
        Copy-Item -LiteralPath $strategy.FullName -Destination $packageRoot
    }

    Copy-RequiredFile `
        -Source (Join-Path $RepoRoot 'service.bat') `
        -Destination $packageRoot
    Copy-RequiredFile `
        -Source (Join-Path $RepoRoot 'README.md') `
        -Destination $packageRoot
    Copy-RequiredFile `
        -Source (Join-Path $RepoRoot 'LICENSE.txt') `
        -Destination $packageRoot
    Copy-RequiredFile `
        -Source (Join-Path $RepoRoot 'gui\LICENSE') `
        -Destination (Join-Path $packageRoot 'LICENSE-GUI.txt')
    Copy-RequiredFile `
        -Source (Join-Path $RepoRoot 'gui\THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $packageRoot 'THIRD_PARTY_NOTICES.md')

    $publishedExe = Join-Path $PublishDir 'ZapretGUI.exe'
    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        $publishedExe
    )
    $expectedFileVersion = "$GuiVersion.0"
    if ($publishedVersion.FileVersion -ne $expectedFileVersion) {
        throw "Published EXE version is $($publishedVersion.FileVersion); expected $expectedFileVersion."
    }
    $productPattern = '^' + [regex]::Escape($GuiVersion) + '(?:\+[0-9a-fA-F]{40})?$'
    if ($publishedVersion.ProductVersion -notmatch $productPattern) {
        throw "Published EXE product version is $($publishedVersion.ProductVersion); expected $GuiVersion."
    }

    Copy-RequiredFile -Source $publishedExe -Destination $packageRoot
    Copy-RequiredFile -Source $publishedExe -Destination $releaseExePath

    $buildInfo = @"
Zapret Control Center version: $GuiVersion
Flowseal version: $UpstreamVersion
Flowseal upstream commit: $UpstreamCommit
Fork source commit: $ForkCommit
Source repository: https://github.com/lolososka/zapret-discord-youtube
Flowseal repository: https://github.com/Flowseal/zapret-discord-youtube
"@
    Write-Utf8NoBom `
        -Path (Join-Path $packageRoot 'BUILD_INFO.txt') `
        -Value ($buildInfo.Trim() + "`n")

    $requiredPackageFiles = @(
        'ZapretGUI.exe',
        'bin\winws.exe',
        'bin\WinDivert.dll',
        'bin\WinDivert64.sys',
        'service.bat',
        'README.md',
        'LICENSE.txt',
        'LICENSE-GUI.txt',
        'THIRD_PARTY_NOTICES.md',
        'BUILD_INFO.txt'
    )
    foreach ($relativePath in $requiredPackageFiles) {
        $fullPath = Join-Path $packageRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required packaged file is missing: $relativePath"
        }
        if ((Get-Item -LiteralPath $fullPath).Length -le 0) {
            throw "Required packaged file is empty: $relativePath"
        }
    }

    $packagedStrategies = @(
        Get-ChildItem -LiteralPath $packageRoot -Filter 'general*.bat' -File
    )
    if ($packagedStrategies.Count -ne $strategies.Count) {
        throw "Expected $($strategies.Count) strategies, packaged $($packagedStrategies.Count)."
    }

    $forbiddenFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Where-Object {
                $_.Name -like '*-user.txt' -or
                $_.Name -eq 'game_filter.enabled'
            }
    )
    if ($forbiddenFiles.Count -gt 0) {
        throw "User-specific files entered the release: $($forbiddenFiles.FullName -join ', ')"
    }

    # Манифест находится внутри уже проверяемого ZIP и перечисляет каждый
    # управляемый файл. Клиент сверяет его после безопасной распаковки, прежде
    # чем side-by-side helper заменит portable-папку.
    $managedFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Sort-Object -Property FullName
    )
    if ($managedFiles.Count -gt $MaxManagedFiles) {
        throw "Portable manifest contains $($managedFiles.Count) files; maximum is $MaxManagedFiles."
    }
    $managedEntries = @(
        foreach ($file in $managedFiles) {
            [ordered]@{
                Path = Get-RelativePath `
                    -BasePath $packageRoot `
                    -Path $file.FullName
                Size = [long]$file.Length
                Sha256 = (
                    Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        }
    )
    $updateManifest = [ordered]@{
        SchemaVersion = 1
        Tag = $tag
        GuiVersion = $GuiVersion
        UpstreamVersion = $UpstreamVersion
        UpstreamCommit = $UpstreamCommit
        ForkCommit = $ForkCommit
        PackageRoot = $packageRootName
        Files = $managedEntries
    }
    Write-Utf8NoBom `
        -Path (Join-Path $packageRoot $updateManifestName) `
        -Value (($updateManifest | ConvertTo-Json -Depth 6) + "`n")

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $sourceFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Sort-Object -Property FullName
    )
    if ($sourceFiles.Count -eq 0) {
        throw 'The portable package is empty.'
    }
    if ($sourceFiles.Count -gt $MaxArchiveEntries) {
        throw "Portable package contains $($sourceFiles.Count) files; maximum is $MaxArchiveEntries."
    }
    $extractedBytes = [long](
        ($sourceFiles | Measure-Object -Property Length -Sum).Sum
    )
    if ($extractedBytes -gt $MaxExtractedBytes) {
        throw "Portable package expands to $extractedBytes bytes; maximum is $MaxExtractedBytes."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $updateManifestName) -PathType Leaf)) {
        throw "Portable update manifest is missing: $updateManifestName"
    }

    $sourceManifest = @{}
    foreach ($file in $sourceFiles) {
        $relative = Get-RelativePath `
            -BasePath $stagingRoot `
            -Path $file.FullName
        $sourceManifest[$relative] = (
            Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        ).Hash.ToLowerInvariant()
    }

    $zipStream = [IO.File]::Open(
        $zipPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $zipStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true
        )
        try {
            foreach ($file in $sourceFiles) {
                $relative = Get-RelativePath `
                    -BasePath $stagingRoot `
                    -Path $file.FullName
                $entry = $archive.CreateEntry(
                    $relative,
                    [IO.Compression.CompressionLevel]::Optimal
                )
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    1980,
                    1,
                    1,
                    0,
                    0,
                    0,
                    [TimeSpan]::Zero
                )
                $input = [IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }
    $zipBytes = (Get-Item -LiteralPath $zipPath).Length
    if ($zipBytes -gt $MaxZipBytes) {
        throw "Portable ZIP is $zipBytes bytes; maximum is $MaxZipBytes."
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $archiveFiles = @($archive.Entries | Where-Object { $_.Name })
        if ($archiveFiles.Count -ne $sourceManifest.Count) {
            throw "ZIP contains $($archiveFiles.Count) files; expected $($sourceManifest.Count)."
        }

        foreach ($entry in $archiveFiles) {
            if (-not $sourceManifest.ContainsKey($entry.FullName)) {
                throw "Unexpected ZIP entry: $($entry.FullName)"
            }
            $entryStream = $entry.Open()
            try {
                $archiveHash = Get-StreamSha256 -Stream $entryStream
            }
            finally {
                $entryStream.Dispose()
            }
            if ($archiveHash -ne $sourceManifest[$entry.FullName]) {
                throw "ZIP verification failed for $($entry.FullName)."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $verifyRoot)
    $verifiedRoot = Join-Path $verifyRoot $packageRootName
    foreach ($relativePath in @('ZapretGUI.exe', 'bin\winws.exe', 'service.bat')) {
        if (-not (Test-Path -LiteralPath (Join-Path $verifiedRoot $relativePath) -PathType Leaf)) {
            throw "Extracted ZIP is missing: $relativePath"
        }
    }
    if (@(Get-ChildItem -LiteralPath $verifiedRoot -Filter 'general*.bat' -File).Count -eq 0) {
        throw 'Extracted ZIP contains no strategies.'
    }

    $zipHash = (
        Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $exeHash = (
        Get-FileHash -LiteralPath $releaseExePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $checksums = @(
        "$zipHash  $zipName",
        "$exeHash  $exeName"
    ) -join "`n"
    Write-Utf8NoBom -Path $checksumsPath -Value ($checksums + "`n")

    $releaseNotes = @'
## Готовая portable-сборка

Это **неофициальный community fork**. Flowseal не связан с Zapret Control Center
и не одобрял эту сборку как официальную.

1. Скачайте `__ZIP_NAME__`.
2. Распакуйте архив в новую папку.
3. Запустите `ZapretGUI.exe` от имени администратора.

В архив уже входят GUI, Flowseal-стратегии, `bin`, `lists`, `utils` и `service.bat`.
Отдельный `ZapretGUI.exe` предназначен для ручного обновления уже существующей папки.
Контрольные суммы находятся в `__CHECKSUMS_NAME__`.

### Возможности Zapret Control Center

- Проверенное обновление portable-сборки с резервной копией и автоматическим откатом.
- Переключение работающей стратегии без ручной остановки; неудачный профиль откатывается.
- Защита несохранённых пользовательских списков при закрытии редактора.
- Отменяемая диагностика отличает исправную систему от проверки, которую не удалось выполнить.
- Обезличенный ZIP-отчёт для поддержки без внешнего IP, секретов, полных путей и содержимого списков.
- Строгая единая версия GUI и автоматические проверки updater, стратегий и portable-пакета.

### Состав сборки

- Zapret Control Center: `__GUI_VERSION__`
- Flowseal: `__UPSTREAM_VERSION__`
- Flowseal commit: `__UPSTREAM_COMMIT__`
- Fork commit: `__FORK_COMMIT__`

Исходный проект: https://github.com/Flowseal/zapret-discord-youtube
'@
    $releaseNotes = $releaseNotes.Replace('__ZIP_NAME__', $zipName)
    $releaseNotes = $releaseNotes.Replace('__CHECKSUMS_NAME__', $checksumsName)
    $releaseNotes = $releaseNotes.Replace('__GUI_VERSION__', $GuiVersion)
    $releaseNotes = $releaseNotes.Replace('__UPSTREAM_VERSION__', $UpstreamVersion)
    $releaseNotes = $releaseNotes.Replace('__UPSTREAM_COMMIT__', $UpstreamCommit)
    $releaseNotes = $releaseNotes.Replace('__FORK_COMMIT__', $ForkCommit)
    Write-Utf8NoBom -Path $notesPath -Value ($releaseNotes.Trim() + "`n")

    $metadata = [ordered]@{
        Tag = $tag
        Title = $title
        GuiVersion = $GuiVersion
        UpstreamVersion = $UpstreamVersion
        UpstreamCommit = $UpstreamCommit
        ForkCommit = $ForkCommit
        PackageRoot = $packageRootName
        FileCount = $sourceFiles.Count
        ZipName = $zipName
        ZipPath = $zipPath
        ZipSha256 = $zipHash
        ExeName = $exeName
        ExePath = $releaseExePath
        ExeSha256 = $exeHash
        ChecksumsName = $checksumsName
        ChecksumsPath = $checksumsPath
        NotesPath = $notesPath
    }
    Write-Utf8NoBom `
        -Path $metadataPath `
        -Value (($metadata | ConvertTo-Json -Depth 4) + "`n")

    Write-Host "Release package: $zipPath" -ForegroundColor Green
    Write-Host "Release tag: $tag" -ForegroundColor Green
    Write-Host "Files in ZIP: $($sourceFiles.Count)" -ForegroundColor Green
}
finally {
    foreach ($temporaryRoot in @($stagingRoot, $verifyRoot)) {
        if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}
