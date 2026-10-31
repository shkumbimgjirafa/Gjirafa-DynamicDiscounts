# Running the 30 days on this laptop

From Monday 2 Nov the solution runs here on real data, by itself, once a day. This is what to do once, what
happens every day, and where to look.

## The packaged app (read this first)

The Claude desktop app is MSIX-packaged. Everything started from a Claude session (the build, the web app, the
engine host) reads and writes AppData **inside the package**, `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache`:

- the LocalDB instance that holds `PricingToolHackathon` (and `PricingToolHackathonTest`),
- the user-secrets (the replica connection, `ConnectionStrings:SourceReadOnly`),
- `%LOCALAPPDATA%\PricingToolAgent` (host copy, logs, exports, time sheet, backups).

A program started anywhere else (a plain Task Scheduler task, a normal PowerShell window) sees a different
LocalDB instance and no user-secrets. On 30 Oct the first scheduled task did exactly that: it crashed trying to
create the database (error 5170) and is now **disabled**. Checked the same evening from a scheduled task: started
directly, it found no user-secrets and no `PricingToolHackathon`; started through `Invoke-CommandInDesktopPackage`,
it found both. So:

- **Start the host from a Claude session**, or through `Invoke-CommandInDesktopPackage` (what the task below does).
- **Open the files in Explorer** under `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\PricingToolAgent`.
  Inside a Claude session the same folder shows as `%LOCALAPPDATA%\PricingToolAgent`.

## Now (until the task is registered)

The engine host is running inside the app, started from a Claude session on 30 Oct at 20:46. It waits for 07:00
and then runs the day's cycle. **After any change the cycle uses, restart it** so it copies the newest build
(from a Claude session):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restart-laptop-host.ps1
```

If the laptop restarts, nothing starts the host again until a Claude session runs that line, or until the task
below is registered.

## Once

1. **Build** (in the repo folder, from a Claude session):

   ```powershell
   $env:PATH="C:\Program Files\dotnet;$env:PATH"; dotnet build PricingTool.sln
   ```

2. **Register the task** (as yourself, no admin needed). This is the one command; it replaces the disabled task:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1
   ```

   It creates the Task Scheduler task **"PricingTool Hackathon daily cycle"**: at log-on, and daily at 07:00 with
   "run as soon as possible after a missed start", on battery too. Its action starts `scripts\run-laptop-host.ps1`
   inside the Claude package with `Invoke-CommandInDesktopPackage`, then ends; a start while the host already
   runs does nothing. Not tested yet: a log-on start before the Claude app itself has started (the probe ran while
   the app was open). After registering, check the next morning's log (see "Where things are").

3. **Settings only you can enter** (until then the pages say so):
   - The monthly raise, on the **Ledger** page (the bar is 3× that, measured in 30 days).
   - "Minutes by hand" for each task type, on the **Time sheet** page (rows of a type without it are kept, not exported).
   - Your email for column B of the time sheet, in user-secrets for the Engine and Web projects (from a Claude
     session, so it lands in the package's user-secrets):
     `dotnet user-secrets set "Agent:TimeSheetOwnerEmail" "<your email>" --project PricingTool.Engine` (and `--project PricingTool.Web`).
   - The limits and holdouts in `Agent:Limits` were confirmed on 30 Oct. To change them, edit
     `PricingTool.Engine/appsettings.Hackathon.json`, `PricingTool.Web/appsettings.Hackathon.json` and
     `PricingTool.AgentTools/appsettings.json` together (a test keeps the three the same).

4. **Keep the laptop awake on power** during the 30 days (Settings → System → Power: never sleep when plugged in).
   The task doesn't wake the laptop; a missed 07:00 start runs as soon as the laptop is on again.

## Every day, by itself

`scripts\run-laptop-host.ps1` copies the current build to `%LOCALAPPDATA%\PricingToolAgent\host` (so the repo can
be rebuilt while it runs) and starts the engine host (`--hackathon-host`). The host runs the daily cycle at start
(a start before 07:00 waits for 07:00) and then each day from 07:00:

size check (warns at 8 GB) → incremental hk refresh from the replica → engine runs, Gjirafa50 then GjirafaMall →
Alpha's floor/target pass (Mondays) → run checks → checked exports for the push (holdout categories left out) →
the live shop's other discounts and regular prices into the shop sandbox (`mirror-L<layer>`, read-only from the replica,
about 20 s on Gjirafa50 and 45 s on GjirafaMall) → the checked prices to the sandbox (`publish-L<layer>`: its first
figure, "on the shelf at our price", is the day's shadow of the live shelf) → money ledger → time-sheet export →
retention (latest 2 local runs per layer, publish exceptions older than 14 days; the mirror deletes its own copies ended
over 14 days ago) → backup (keeps 7).

Each step writes a row (page **Daily cycle**); a failed step doesn't stop the rest and is tried again after an
hour, three times a day at most. Steps not built yet are logged as NotBuilt.

## What you do each day

1. Open the **Ledger** page. The day's export files are listed under "Exports for the team's push".
2. The team pushes the file the way it does today.
3. Click **Mark as pushed** on that export (or `PricingTool.AgentTools mark-pushed <export id>`). That records it as
   applied (an Approval) and starts its 7- and 30-day measurement against the holdout.
4. Look at the **Agent** page's "Waiting for you" (the exceptions inbox). On a normal day it is empty.

## Memory on this laptop (for you to decide; nothing here is changed for you)

On 31 Oct at 07:27 LocalDB's process stopped in the middle of a GjirafaMall publish, about 25 s after Windows flagged
the web app's memory during that layer's Run week (an engine run of 511,398 SKUs; the laptop has 15.8 GB, with the Claude
app and two Power BI gateways also using about 3.6 GB). The cause isn't established; the publisher now repairs an
interrupted batch by itself. Two things would lower the chance:

- **Cap LocalDB's memory.** SQL Server takes memory until the machine is short, and LocalDB is one instance shared by
  this database and the main repo's development database. A cap of about 4 GB leaves room for the engine host:
  `sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'max server memory (MB)', 4096; RECONFIGURE;"`
  (needs `sqlcmd`; undo with 2147483647). It is instance-wide, so it is yours to decide.
- **Close the web app when you don't use it.** The engine host runs the daily cycle on its own; the web app is only for
  looking and pressing buttons. Measured on 31 Oct: a GjirafaMall engine run (511,398 SKUs) in the web app peaked at
  1,605 MB private memory, free memory fell to 618 MB and SQL Server gave back 440 MB of its working set while it ran. The
  calculator's engine (imported, unchanged) holds the whole run in memory; the publish after it is small (the engine host
  peaked at 228 MB publishing 134,834 prices). The daily cycle runs the same engine run in the engine host at 07:00, so
  that is the hour to leave the laptop alone.

## Where things are

Paths as seen from a Claude session; in Explorer, prefix `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`
instead of `%LOCALAPPDATA%\`.

| What | Where |
|---|---|
| Host log | `%LOCALAPPDATA%\PricingToolAgent\logs\engine-host-<date>.log` |
| Export files for the push | `%LOCALAPPDATA%\PricingToolAgent\exports` |
| Time-sheet files (xlsx, csv, json) | `%LOCALAPPDATA%\PricingToolAgent\timesheet` |
| Database backups (7 kept) | `%LOCALAPPDATA%\PricingToolAgent\backups` |
| The day's steps | page **Daily cycle**, or `PricingTool.AgentTools cycle-status` |
| What waits for a person | page **Agent** → exceptions inbox |
| Test and demo runs (never in the live DB) | database `PricingToolHackathonTest`: `PricingTool.AgentTools test-db` |

## Changing, pausing, stopping

- **New build:** build as usual, then restart the host (from a Claude session):
  `powershell -ExecutionPolicy Bypass -File scripts\restart-laptop-host.ps1`.
- **Stop every AI call and the publisher at once:** the **kill switch** on the Agent page (no restart; audited).
  The engine runs still happen; run checks stop, so their runs stay blocked and nothing is exported unchecked.
- **Remove the task:** `powershell -ExecutionPolicy Bypass -File scripts\register-laptop-task.ps1 -Unregister`.
