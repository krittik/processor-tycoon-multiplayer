# Downloads the pinned Steamworks.NET standalone release (MIT; bundles Valve's redistributable steam_api64.dll) into
# artifacts/lib/ (D50). Build and package use it from there; it is never committed. Prints the folder.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\common.ps1')
$version = '2025.164.1'
$sha256 = '9412348CC404563BE5A43A28347CFEDA3C679EE044A14D87A507ED2D796A537D'
$target = Join-Path $ArtifactsDir "lib\Steamworks.NET-$version"
if ((Test-Path -LiteralPath (Join-Path $target 'Windows-x64\Steamworks.NET.dll')) -and -not $Force) { return $target }

$url = "https://github.com/rlabrecque/Steamworks.NET/releases/download/$version/Steamworks.NET-Standalone_$version.zip"
$zip = Join-Path $ArtifactsDir "lib\Steamworks.NET-Standalone_$version.zip"
New-Item -ItemType Directory -Force -Path (Split-Path $zip) | Out-Null
Write-Host "Downloading $url"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actual -ne $sha256) { Remove-Item -LiteralPath $zip; throw "Steamworks.NET download hash mismatch: $actual" }
if (Test-Path -LiteralPath $target) { Remove-Item -Recurse -Force -LiteralPath $target }
Expand-Archive -LiteralPath $zip -DestinationPath $target
Remove-Item -LiteralPath $zip
$target
