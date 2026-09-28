# Packaging only: builds the Release plugin into artifacts/dist/ as a zip that extracts into the game folder. BepInEx 5 is a separate prerequisite.
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$game = Resolve-GameDir $GameDir
$steamworks = & (Join-Path $PSScriptRoot 'tools\fetch-steamworks.ps1')
$pluginProject = Join-Path $PSScriptRoot 'src\ProcessorTycoon.Mp\ProcessorTycoon.Mp.csproj'
$version = Get-ProjectVersion $pluginProject
$stage = Join-Path $ArtifactsDir ('dist\ProcessorTycoon-Multiplayer-' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$pluginDirectory = Join-Path $stage 'BepInEx\plugins\ProcessorTycoon.Mp'
New-Item -ItemType Directory -Path $pluginDirectory | Out-Null
dotnet build $pluginProject -c Release "-p:GameDir=$game"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$bin = Join-Path $PSScriptRoot 'src\ProcessorTycoon.Mp\bin\Release\netstandard2.1'
foreach ($name in @('ProcessorTycoon.Mp.dll', 'ProcessorTycoon.Mp.Core.dll', 'Steamworks.NET.dll', 'steam_api64.dll')) { Copy-Item -LiteralPath (Join-Path $bin $name) -Destination $pluginDirectory }
Copy-Item -LiteralPath (Join-Path $steamworks 'LICENSE.txt') -Destination (Join-Path $pluginDirectory 'Steamworks.NET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package\README-Multiplayer.md') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $pluginDirectory 'LICENSE.txt')
New-DistZip $stage
