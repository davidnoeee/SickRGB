<#
.SYNOPSIS
    Builds (and optionally publishes) SickRGB.

.EXAMPLE
    .\build.ps1
    Builds in Release and launches the app.

.EXAMPLE
    .\build.ps1 -Publish
    Produces a standalone single-file dist\SickRGB.exe with the runtime bundled.
#>
param(
    [switch]$Publish,
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\SickRGB\SickRGB.csproj'
$dist    = Join-Path $root 'dist'

# Prefer dotnet on PATH; fall back to a user-local install.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) {
    throw "dotnet SDK not found. Install the .NET 8 SDK, or run: winget install Microsoft.DotNet.SDK.8"
}

# A running instance holds a lock on its own binary.
Get-Process SickRGB -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running instance (pid $($_.Id))..." -ForegroundColor DarkYellow
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Milliseconds 500

if ($Publish) {
    Write-Host "Publishing standalone build..." -ForegroundColor Cyan
    & $dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $dist -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    $exe = Join-Path $dist 'SickRGB.exe'
    $mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "`nPublished $exe ($mb MB)" -ForegroundColor Green
}
else {
    Write-Host "Building..." -ForegroundColor Cyan
    & $dotnet build $project -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $exe = Join-Path $root 'src\SickRGB\bin\Release\net8.0-windows\SickRGB.exe'
    Write-Host "`nBuilt $exe" -ForegroundColor Green
}

if (-not $NoRun) {
    Write-Host "Launching..." -ForegroundColor Cyan
    Start-Process -FilePath $exe
}
