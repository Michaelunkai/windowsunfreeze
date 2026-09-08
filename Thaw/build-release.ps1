[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'Thaw.csproj'
$installerScript = Join-Path $projectRoot 'installer\Thaw.iss'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot 'publish'
$releaseDir = Join-Path $artifactsRoot 'release'

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'Thaw.csproj does not contain a Version value.'
}
$version = $versionNode.InnerText.Trim()

$installerText = Get-Content -LiteralPath $installerScript -Raw
if ($installerText -notmatch ('#define AppVersion "' + [regex]::Escape($version) + '"')) {
    throw "Installer version does not match Thaw.csproj version $version."
}

$dotnetCandidates = @(
    (Join-Path $projectRoot '.dotnet-build\dotnet.exe'),
    (Join-Path $projectRoot '.dotnet\dotnet.exe'),
    'C:\Program Files\dotnet\dotnet.exe'
)
$dotnet = $dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $dotnetCommand) { $dotnet = $dotnetCommand.Source }
}
if ([string]::IsNullOrWhiteSpace($dotnet)) { throw 'A usable .NET SDK was not found.' }

$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($iscc)) { throw 'Inno Setup 6 was not found.' }

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$portable = Join-Path $releaseDir 'Thaw-Portable.exe'
$setup = Join-Path $releaseDir 'Thaw-Setup.exe'
$manifest = Join-Path $releaseDir 'SHA256SUMS.txt'
foreach ($file in @((Join-Path $publishDir 'Thaw.exe'), $portable, $setup, $manifest)) {
    if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
}

Push-Location $projectRoot
try {
    & $dotnet publish $projectFile -c Release -r win-x64 --self-contained true --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    [System.IO.File]::Copy((Join-Path $publishDir 'Thaw.exe'), $portable, $true)

    & $iscc $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

foreach ($file in @($portable, $setup)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release artifact: $file" }
}

$hashLines = @($portable, $setup) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($_)
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($manifest, $hashLines, $utf8NoBom)

Write-Output "Thaw $version release artifacts"
foreach ($file in @($portable, $setup, $manifest)) {
    $item = Get-Item -LiteralPath $file
    Write-Output ("{0} ({1} bytes)" -f $item.FullName, $item.Length)
}
