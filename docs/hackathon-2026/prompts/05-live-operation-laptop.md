# Prompt 05: live operation on the laptop for the 30 days (the 10% bar)

Detail for plan section 6, row "Live operation on the laptop". Read `CLAUDE.md`, plan section 3
("The 10% bar" and "Live path") and section 5 "Live operation on the laptop".

Why it matters: from Monday 2 Nov this laptop runs the solution for 30 days on real data, and the
bar is judged on what it really does: hours the AI logs itself, real Claude calls, and real money
measured against the holdout. Jarvis adds a usage tracker on Monday; Mergca reads the code. Mock
data, inflated numbers or hand-typed rows count against us.

**Build, committing after each step with `dotnet test` green:**

1. **`DailyCycle`**, one idempotent job per day (a run-date key; a second start the same day resumes
   the steps not yet done): incremental `hk` refresh → engine runs for layers 1 and 4 → Alpha's pass
   (weekly, on its day) → run checker → export for the push → money-ledger update → time-sheet rows →
   backup. Each step logs its own row (`DailyCycleStep`: step, start, end, status, error) and a
   failing step doesn't stop the others. `CostWatcher` keeps its own 15-minute loop; Nova weekly.
2. **Incremental `hk` refresh in the app** (port the logic of `docs/hackathon-2026/exports/*.sql`,
   don't call those scripts): sales lines from the last loaded date minus 14 days (late
   cancellations), replacing that window; product map and discount snapshot refreshed; every
   refresh logged in `hk.DataSources`. Keep replica queries light and time them.
3. **Starts by itself:** an engine host mode that runs the cycle and the cost watcher, plus a script
   that registers a Windows Task Scheduler task (at log-on, and daily at 07:00 with "run as soon as
   possible after a missed start") in the `Hackathon` environment. Don't register it yourself; write
   the script and the one-line command for the user in `GO-LIVE-LAPTOP.md`.
4. **Retention and size:** turn retention back on for local runs (keep the imported prod runs and the
   latest two per layer); the cycle checks the DB size and warns at 8 GB.
5. **Export for the push:** the existing CSV export of the checked proposals, holdout categories left
   out, into a folder set in config; a "mark as pushed" action that records the set as applied
   (`Approval`, as the prod tool does), which starts its measurement.
6. **Money ledger:** per applied set and per cost-watcher fix: expected € at the time; measured € at
   7 and 30 days from the refreshed sales lines (booked COGS) against the holdout categories,
   season-adjusted where possible. Conservative by rule: anything not measured or not attributable
   counts as €0, and the page says so. A page that shows the 30-day total against "3× the raise"
   (the raise amount is a setting the user enters).
7. **Time sheet:** every real task writes its `TimeSheetEntry` (cycle steps that do real work: the run
   check, the export, Alpha's pass, a campaign plan, each cost-watcher fix); export in the organisers'
   template columns A–H (`docs/hackathon-2026/Time-sheet-template-hours-saved.xlsx`) and as CSV/JSON
   for Jarvis. `MinutesBefore` stays NULL until the user sets it per task type; rows without it are
   kept but not exported.
8. **Backup:** daily `BACKUP DATABASE` to a local folder outside the repo, keep 7.
9. **No mock data in the live DB:** move injected test cases to a separate test database
   (`PricingToolHackathonTest`); the demo can show them from there, clearly labelled.
10. **Run one full cycle end to end on real data** and record every step's result in `hours.md`.

**Done when:** one full real cycle ran on its own from the engine host, every step logged, the export
file exists with the holdout left out, the ledger and time-sheet pages show real (possibly zero)
values, the backup exists, `GO-LIVE-LAPTOP.md` tells the user exactly what to run once, and tests
are green.
