# Restarts the engine host so it copies the newest build: stops the running host (the one started from
# %LOCALAPPDATA%\PricingToolAgent\host) and starts run-laptop-host.ps1 again, hidden. Run it from a Claude session
# or a task that launches inside the Claude package (see GO-LIVE-LAPTOP.md, "The packaged app"): started anywhere
# else the host would not find the database or the user-secrets.
#
#   powershell -ExecutionPolicy Bypass -File scripts\restart-laptop-host.ps1

$ErrorActionPreference = 'Stop'
# LocalDB's instance first, so its sqlservr belongs to sqllocaldb.exe, not to a process tree that may be stopped.
& sqllocaldb start MSSQLLocalDB | Out-Null
$hostDir = Join-Path $env:LOCALAPPDATA 'PricingToolAgent\host'
$running = Get-Process PricingTool.Engine -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$hostDir\*" }
foreach ($p in $running) {
    "Stopping the engine host (PID $($p.Id), started $($p.StartTime))."
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(30000) | Out-Null
}
$runner = Join-Path $PSScriptRoot 'run-laptop-host.ps1'
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
foreach ($i in 1..60) {
    Start-Sleep -Seconds 1
    $new = Get-Process PricingTool.Engine -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$hostDir\*" } | Select-Object -First 1
    if ($new) { "The engine host is running again (PID $($new.Id)); log: $(Join-Path $env:LOCALAPPDATA 'PricingToolAgent\logs')"; exit 0 }
}
"The engine host did not start within 60 s; see the log in $(Join-Path $env:LOCALAPPDATA 'PricingToolAgent\logs')."
exit 1
