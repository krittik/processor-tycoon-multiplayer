# Run ON a second machine (e.g. over SSH) to start its game copy headless as the desktop user, so the game reaches
# that user's Steam client (a process started from SSH runs as another user in session 0 and cannot). Uses a scheduled
# task that runs only while the user is logged on (no password stored), below-normal priority, -batchmode -nographics
# (or -Windowed: a visible 1280x720 window).
# Drive it through <GameDir>\mp-dev\cmd.txt (first `steam`, then `join steam:<host id> <name>`); status in status.json.
# Needs admin rights and the user to have Modify rights on GameDir. `-Action remove` deletes the task afterwards.
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$User,   # e.g. MACHINE\name
    [ValidateSet('start', 'remove')][string]$Action = 'start',
    [switch]$Windowed   # visible 1280x720 window instead of headless (when nobody else uses the machine)
)
$ErrorActionPreference = 'Stop'
$task = 'ProcessorTycoonMP-headless'
if ($Action -eq 'remove') { Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue; 'removed'; return }
$exe = Join-Path $GameDir 'Processor Tycoon Beta.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Processor Tycoon not found in '$GameDir'." }
$log = Join-Path $GameDir 'player-headless.log'
$arguments = if ($Windowed) { "-screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile `"$log`"" } else { "-batchmode -nographics -logFile `"$log`"" }
$taskAction = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $GameDir
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -Priority 7 -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $task -Action $taskAction -Principal $principal -Settings $settings -Force | Out-Null
$dev = Join-Path $GameDir 'mp-dev'
New-Item -ItemType Directory -Force -Path $dev | Out-Null
Remove-Item -LiteralPath (Join-Path $dev 'status.json'), (Join-Path $dev 'cmd.txt') -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName $task
Start-Sleep -Seconds 3
Get-Process 'Processor Tycoon Beta' -IncludeUserName -ErrorAction SilentlyContinue | Select-Object Id, SessionId, UserName, PriorityClass | Format-Table -AutoSize | Out-String
