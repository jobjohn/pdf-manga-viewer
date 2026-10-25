param(
    [string]$Version = '0.0.1'
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$root = Split-Path $installerDir -Parent
$project = Join-Path $root 'PdfMangaViewer.csproj'
$publishDir = Join-Path $root 'bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
$wixProject = Join-Path $installerDir 'PdfMangaViewer.Installer.wixproj'
$components = Join-Path $installerDir 'Components.generated.wxs'
$msi = Join-Path $installerDir "dist\PdfMangaViewer-Setup-$Version.msi"

Write-Output '=== Publishing application ==='
dotnet publish $project -c Release -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw 'Application publish failed.'
}

Write-Output '=== Generating installer file list ==='
& (Join-Path $installerDir 'generate-components.ps1') -PublishDir $publishDir -Output $components
if ($LASTEXITCODE -ne 0) {
    throw 'Component generation failed.'
}

Write-Output '=== Building MSI ==='
dotnet build $wixProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Installer build failed.'
}

if (-not (Test-Path -LiteralPath $msi)) {
    throw "MSI was not created: $msi"
}

$size = (Get-Item -LiteralPath $msi).Length
Write-Output ('MSI: {0} ({1:N1} MB)' -f $msi, ($size / 1MB))
