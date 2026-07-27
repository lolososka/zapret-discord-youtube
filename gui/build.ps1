<#
    Сборка ZapretGUI и установка EXE в папку zapret.

    .\build.ps1                 — публикация + копирование в папку zapret по умолчанию
    .\build.ps1 -NoDeploy       — только публикация
    .\build.ps1 -Target "D:\zapret"
#>
[CmdletBinding()]
param(
    [string]$Target,
    [switch]$NoDeploy,
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'ZapretGui\ZapretGui.csproj'
$publish = Join-Path $root 'publish'

if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw "dotnet SDK не найден. Установите .NET 8 SDK или укажите -Dotnet." }
    $Dotnet = $cmd.Source
} elseif (-not (Test-Path -LiteralPath $Dotnet)) {
    throw "dotnet SDK не найден: $Dotnet"
}

Write-Host "==> publish" -ForegroundColor Cyan
& $Dotnet publish $proj -c Release -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "publish завершился с кодом $LASTEXITCODE" }

$exe = Join-Path $publish 'ZapretGUI.exe'
if (-not (Test-Path $exe)) { throw "EXE не найден: $exe" }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> собрано: $exe ($mb МБ)" -ForegroundColor Green

if ($NoDeploy) { return }

if ([string]::IsNullOrWhiteSpace($Target)) {
    # В составе форка GUI лежит в папке gui, а корень zapret — на уровень выше.
    $candidate = Split-Path -Parent $root
    if (Test-Path -LiteralPath (Join-Path $candidate 'bin\winws.exe')) {
        $Target = $candidate
    } else {
        throw "папка zapret не найдена автоматически. Укажите -Target `"путь\к\zapret`" или используйте -NoDeploy."
    }
}

$Target = [System.IO.Path]::GetFullPath($Target)
if (-not (Test-Path -LiteralPath $Target)) { throw "папка zapret не найдена: $Target" }
if (-not (Test-Path (Join-Path $Target 'bin\winws.exe'))) {
    throw "в $Target нет bin\winws.exe — это не папка zapret"
}

# Запущенный экземпляр держит EXE заблокированным
Get-Process -Name 'ZapretGUI' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "==> закрываю запущенный ZapretGUI (PID $($_.Id))" -ForegroundColor Yellow
    $_.Kill(); $_.WaitForExit(5000)
}

Copy-Item $exe (Join-Path $Target 'ZapretGUI.exe') -Force
Write-Host "==> установлено: $(Join-Path $Target 'ZapretGUI.exe')" -ForegroundColor Green
