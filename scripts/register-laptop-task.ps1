# Registers the Windows Task Scheduler task that keeps the engine host running for the 30 days (prompt 05):
# at log-on, and daily at 07:00, with "run as soon as possible after a missed start". It runs as you, only while
# you are logged on (no stored password, no admin), on battery too.
#
# The Claude desktop app is MSIX-packaged, so everything started from a Claude session reads and writes AppData
# inside the package (%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache): the LocalDB instance that holds
# PricingToolHackathon, the user-secrets (the replica connection) and %LOCALAPPDATA%\PricingToolAgent. A task
# started by Task Scheduler runs outside the package and sees none of them (the first task, 30 Oct, crashed on
# CREATE DATABASE, error 5170). So the task starts run-laptop-host.ps1 inside the package with
# Invoke-CommandInDesktopPackage. Checked 2026-10-30 from a scheduled task: started directly it found no
# user-secrets and no PricingToolHackathon; started this way it found both.
#
# The task's own process ends as soon as the host is started; a start while the host is already running does
# nothing (run-laptop-host.ps1 checks), and the host itself starts each new day's cycle at 07:00.
#
#   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1               register (or replace) it
#   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1 -Unregister   remove it

param([switch]$Unregister)

$ErrorActionPreference = 'Stop'
$name = 'PricingTool Hackathon daily cycle'

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    "Removed the task '$name'. A host that is running keeps running until you stop it (Get-Process PricingTool.Engine | Stop-Process)."
    return
}

$package = Get-AppxPackage -Name Claude | Select-Object -First 1
if (-not $package) { throw 'The Claude desktop app (MSIX package "Claude") is not installed for this user.' }

$runner = Join-Path $PSScriptRoot 'run-laptop-host.ps1'
$user = "$env:USERDOMAIN\$env:USERNAME"
$inner = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$runner`""
$command = "Invoke-CommandInDesktopPackage -PackageFamilyName $($package.PackageFamilyName) -AppId Claude " +
           "-Command 'powershell.exe' -Args '$inner' -PreventBreakaway"

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -WorkingDirectory $PSScriptRoot `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"$($command.Replace('"', '\"'))`""
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User $user),
    (New-ScheduledTaskTrigger -Daily -At '07:00')
)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force `
    -Description 'Pricing Tool v2 (Gjirafa AI Hackathon 2026): starts the engine host inside the Claude package; the host runs the daily cycle on the hackathon database. See GO-LIVE-LAPTOP.md.' | Out-Null

$task = Get-ScheduledTask -TaskName $name
"Registered '$name' for $user ($($task.State)): at log-on and daily at 07:00, run as soon as possible after a missed start."
"Start it now with:  Start-ScheduledTask -TaskName '$name'"
