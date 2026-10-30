# Running the 30 days on this laptop

From Monday 2 Nov the solution runs here on real data, by itself, once a day. This is what to do once, what
happens every day, and where to look.

## Once

1. **Build** (PowerShell, in the repo folder):

   ```powershell
   $env:PATH="C:\Program Files\dotnet;$env:PATH"; dotnet build PricingTool.sln
   ```

2. **Register the task** (as yourself, no admin needed). This is the one command:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1
   ```

   It creates the Task Scheduler task **"PricingTool Hackathon daily cycle"**: at log-on, and daily at 07:00 with
   "run as soon as possible after a missed start"; on battery too; no time limit; a second start while it runs is
   ignored. Start it now without logging off: `Start-ScheduledTask -TaskName 'PricingTool Hackathon daily cycle'`.

3. **Settings only you can enter** (until then the pages say so):
   - The monthly raise, on the **Ledger** page (the bar is 3× that, measured in 30 days).
   - "Minutes by hand" for each task type, on the **Time sheet** page (rows of a type without it are kept, not exported).
   - Your email for column B of the time sheet, in user-secrets for the Engine and Web projects:
     `dotnet user-secrets set "Agent:TimeSheetOwnerEmail" "<your email>" --project PricingTool.Engine` (and `--project PricingTool.Web`).
   - Confirm or change the assumed limits and holdouts in the `Agent:Limits` section of
     `PricingTool.Engine/appsettings.Hackathon.json`, `PricingTool.Web/appsettings.Hackathon.json` and
     `PricingTool.AgentTools/appsettings.json` (a test keeps the three the same).

4. **Keep the laptop awake on power** during the 30 days (Settings → System → Power: never sleep when plugged in).
   The task doesn't wake the laptop; a missed 07:00 start runs as soon as the laptop is on again.

## Every day, by itself

The task runs `scripts\run-laptop-host.ps1`. It copies the current build to `%LOCALAPPDATA%\PricingToolAgent\host`
(so you can keep building the repo while it runs) and starts the engine host (`--hackathon-host`). The host runs
the daily cycle at start and then each day from 07:00:

size check (warns at 8 GB) → incremental hk refresh from the replica → engine runs, Gjirafa50 then GjirafaMall →
Alpha's floor/target pass (Mondays) → run checks → checked exports for the push (holdout categories left out) →
money ledger → time-sheet export → retention (latest 2 local runs per layer) → backup (keeps 7).

Each step writes a row (page **Daily cycle**); a failed step doesn't stop the rest and is tried again after an
hour, three times a day at most. The cost watcher and Nova's weekly pass are not built yet; the cycle logs them as
NotBuilt.

## What you do each day

1. Open the **Ledger** page. The day's export files are listed under "Exports for the team's push".
2. The team pushes the file the way it does today.
3. Click **Mark as pushed** on that export (or `PricingTool.AgentTools mark-pushed <export id>`). That records it as
   applied (an Approval) and starts its 7- and 30-day measurement against the holdout.

## Where things are

| What | Where |
|---|---|
| Host log | `%LOCALAPPDATA%\PricingToolAgent\logs\engine-host-<date>.log` |
| Export files for the push | `%LOCALAPPDATA%\PricingToolAgent\exports` |
| Time-sheet files (xlsx, csv, json) | `%LOCALAPPDATA%\PricingToolAgent\timesheet` |
| Database backups (7 kept) | `%LOCALAPPDATA%\PricingToolAgent\backups` |
| The day's steps | page **Daily cycle**, or `PricingTool.AgentTools cycle-status` |
| Test and demo runs (never in the live DB) | database `PricingToolHackathonTest`: `PricingTool.AgentTools test-db` |

## Changing, pausing, stopping

- **New build:** build as usual, then restart the task so it copies the new build:
  `Stop-ScheduledTask -TaskName 'PricingTool Hackathon daily cycle'; Get-Process PricingTool.Engine -ErrorAction SilentlyContinue | Stop-Process; Start-ScheduledTask -TaskName 'PricingTool Hackathon daily cycle'`
- **Stop the AI calls only (kill switch):** set `Agent:Enabled` to `false` in the three settings files and restart
  the task. The engine runs and exports still happen; checks fail closed, so nothing is exported unchecked.
- **Remove the task:** `powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1 -Unregister`.
