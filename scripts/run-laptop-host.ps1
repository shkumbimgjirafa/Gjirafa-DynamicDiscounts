# Runs the engine host (--hackathon-host: the daily cycle, kept running) in the Hackathon environment.
# Started by the task that register-laptop-task.ps1 creates (see GO-LIVE-LAPTOP.md); it can also be run by hand.
#
# The host runs from a copy of the current build under %LOCALAPPDATA%\PricingToolAgent\host, so building the repo
# while it runs never hits a locked file. A restart of the task picks up the newest build. Output is appended to
# %LOCALAPPDATA%\PricingToolAgent\logs\engine-host-<date>.log.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $env:LOCALAPPDATA 'PricingToolAgent'
$hostDir = Join-Path $root 'host'
$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ('engine-host-{0:yyyyMMdd}.log' -f (Get-Date))
function Log($text) { "$(Get-Date -Format s) $text" | Out-File -Append -Encoding utf8 $log }

if (Get-Process PricingTool.Engine -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$hostDir\*" }) {
    Log 'the engine host is already running; nothing to do'
    exit 0
}

$builds = @{
    engine = Join-Path $repo 'PricingTool.Engine\bin\Debug\net8.0'
    tools  = Join-Path $repo 'PricingTool.AgentTools\bin\Debug\net8.0'
}
foreach ($name in $builds.Keys) {
    if (-not (Test-Path (Join-Path $builds[$name] '*.exe'))) {
        Log "no build in $($builds[$name]); run: dotnet build PricingTool.sln"
        exit 1
    }
    # /MIR keeps the copy identical to the build; robocopy exit codes below 8 mean success.
    robocopy $builds[$name] (Join-Path $hostDir $name) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Log "copying the $name build failed (robocopy $LASTEXITCODE)"; exit 1 }
}

$env:DOTNET_ENVIRONMENT = 'Hackathon'
# The data tools Claude Code starts for Alpha's pass: the copied AgentTools, not the repo's build.
$env:Agent__ToolsExecutable = Join-Path $hostDir 'tools\PricingTool.AgentTools.exe'
Set-Location (Join-Path $hostDir 'engine')
Log "starting the engine host (build copied from $($builds.engine))"
# .\ because cmd doesn't search the current folder when NoDefaultCurrentDirectoryInExePath is set.
cmd /c ".\PricingTool.Engine.exe --hackathon-host >> `"$log`" 2>&1"
$code = $LASTEXITCODE
Log "the engine host exited with code $code"
exit $code
