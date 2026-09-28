# Starts a game copy headless (no window, GPU or sound; below-normal priority) as a multiplayer peer driven through
# <game>/mp-dev/cmd.txt, with its state in <game>/mp-dev/status.json (DevControl). With -Join it joins as soon as the mod
# is up, e.g. -Join steam:7656119... or -Join 192.168.1.10:27960. Stop it with: Add-Content <game>\mp-dev\cmd.txt quit
param([Parameter(Mandatory = $true)][string]$GameDir, [string]$Join, [string]$Name = $env:USERNAME)
$ErrorActionPreference = 'Stop'
$GameDir = [IO.Path]::GetFullPath($GameDir)
$exe = Join-Path $GameDir 'Processor Tycoon Beta.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Processor Tycoon not found in '$GameDir'." }
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\plugins\ProcessorTycoon.Mp\ProcessorTycoon.Mp.dll'))) { throw "The Multiplayer mod is not installed in '$GameDir'." }
$dev = Join-Path $GameDir 'mp-dev'
$status = Join-Path $dev 'status.json'
$commands = Join-Path $dev 'cmd.txt'
New-Item -ItemType Directory -Force -Path $dev | Out-Null
Remove-Item -LiteralPath $status, $commands -ErrorAction SilentlyContinue

$log = Join-Path $GameDir 'player-headless.log'
$process = Start-Process -FilePath $exe -ArgumentList "-batchmode -nographics -logFile `"$log`"" -WorkingDirectory $GameDir -PassThru
$process.PriorityClass = 'BelowNormal'
Write-Host "Started headless game, process $($process.Id)."

function Wait-Status([string]$Pattern, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if ($process.HasExited) { throw "The game exited (code $($process.ExitCode)); see $log and BepInEx\LogOutput.log." }
        if ((Test-Path -LiteralPath $status) -and ((Get-Content -Raw -LiteralPath $status) -match $Pattern)) { return }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for /$Pattern/ in $status."
}

Wait-Status '"time"' 120
Write-Host 'Multiplayer mod is up.'
if ($Join) {
    Add-Content -LiteralPath $commands -Value "join $Join $Name"
    Wait-Status '"session":"Running"' 180
    Write-Host "Joined $Join as $Name."
}
Get-Content -Raw -LiteralPath $status
