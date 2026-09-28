# Builds the Multiplayer solution and runs the offline tests. Deploys only with -Deploy into an explicit test game copy (docs/TESTING.md).
param([string]$GameDir, [switch]$Deploy, [string]$DeployRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$game = Resolve-GameDir $GameDir
& (Join-Path $PSScriptRoot 'tools\fetch-steamworks.ps1') | Out-Null
$extra = @("-p:GameDir=$game")
if ($Deploy) {
    if (-not $DeployRoot) { throw 'Pass -DeployRoot with a test game copy (docs/TESTING.md).' }
    $extra += '-p:Deploy=true', "-p:DeployRoot=$DeployRoot"
}
dotnet build (Join-Path $PSScriptRoot 'Multiplayer.slnx') -c Debug @extra
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --no-build --project (Join-Path $PSScriptRoot 'tests\ProcessorTycoon.Mp.Tests\ProcessorTycoon.Mp.Tests.csproj') -c Debug
exit $LASTEXITCODE
