# Registers the Windows Task Scheduler task that keeps the engine host running for the 30 days (prompt 05):
# at log-on, and daily at 07:00, with "run as soon as possible after a missed start". It runs as you, only while
# you are logged on (no stored password), on battery too, with no time limit. A start while the host is already
# running is ignored; the host itself starts each new day's cycle at 07:00.
#
#   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1               register (or update) it
#   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1 -Unregister   remove it

param([switch]$Unregister)

$ErrorActionPreference = 'Stop'
$name = 'PricingTool Hackathon daily cycle'

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    "Removed the task '$name'. A host that is running keeps running until you stop it (Get-Process PricingTool.Engine | Stop-Process)."
    return
}

$runner = Join-Path $PSScriptRoot 'run-laptop-host.ps1'
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$runner`"" -WorkingDirectory $PSScriptRoot
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User $user),
    (New-ScheduledTaskTrigger -Daily -At '07:00')
)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 10)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force `
    -Description 'Pricing Tool v2 (Gjirafa AI Hackathon 2026): the engine host runs the daily cycle on the hackathon database. See GO-LIVE-LAPTOP.md.' | Out-Null

$task = Get-ScheduledTask -TaskName $name
"Registered '$name' for $user ($($task.State)): at log-on and daily at 07:00, run as soon as possible after a missed start."
"Start it now with:  Start-ScheduledTask -TaskName '$name'"
