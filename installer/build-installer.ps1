param(
    # 省略時は PdfMangaViewer.csproj の <Version> を使う
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$root = Split-Path $installerDir -Parent
$project = Join-Path $root 'PdfMangaViewer.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be in x.y.z format: '$Version'"
}
Write-Output "Version: $Version"
$publishDir = Join-Path $root 'bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
$wixProject = Join-Path $installerDir 'PdfMangaViewer.Installer.wixproj'
$components = Join-Path $installerDir 'Components.generated.wxs'
$msi = Join-Path $installerDir "dist\PdfMangaViewer-Setup-$Version.msi"

Write-Output '=== Publishing application ==='
dotnet publish $project -c Release -r win-x64 "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) {
    throw 'Application publish failed.'
}

Write-Output '=== Generating installer file list ==='
& (Join-Path $installerDir 'generate-components.ps1') -PublishDir $publishDir -Output $components
if ($LASTEXITCODE -ne 0) {
    throw 'Component generation failed.'
}

Write-Output '=== Building MSI ==='
dotnet build $wixProject -c Release "-p:AppVersion=$Version"
if ($LASTEXITCODE -ne 0) {
    throw 'Installer build failed.'
}

if (-not (Test-Path -LiteralPath $msi)) {
    throw "MSI was not created: $msi"
}

$size = (Get-Item -LiteralPath $msi).Length
Write-Output ('MSI: {0} ({1:N1} MB)' -f $msi, ($size / 1MB))
