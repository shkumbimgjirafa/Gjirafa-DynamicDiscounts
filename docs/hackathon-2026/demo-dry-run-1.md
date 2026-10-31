# Demo dry run 1 (31 Oct 2026, 00:53–00:58)

Plan section 7, walked on the real app (http://localhost:5058, Hackathon environment, the live database) by the build
session, timed. No video yet. What works as scripted, what doesn't, and what was fixed on the spot.

## Step by step

| # | Script (time) | What happened | Measured | Verdict |
|---|---|---|---|---|
| 1 | The problem, declaration slide (20 s) | No slide in the repo; `DECLARATION.md` and `HACKATHON.md` section 1 (new vs reused table) carry the content | — | **Needs a slide** (the user's) |
| 2 | "Run week" for Gj50 KS, no clicks (60 s) | **There is no "Run week" button.** The loop runs in the engine host: the Daily cycle page shows today's (31 Oct, 00:35): runs #1103 / #1104, run checks #5 / #6, checked exports, ledger, outcomes, retention, backup. Alpha's pass runs on Mondays (pass #2's 8 changes and their reasons are on the Agent page). **The daily cycle doesn't publish regular prices:** it ends in a checked CSV export; the publisher writes discounts for the canary (CLI), campaign plans and the cost watcher. Ran the canary live instead: `publish-canary 4 --run 1103 --category "Aksesorë" --max 30` → sandbox batch #6, 4 created, 9 updated in place, 17 skipped (a cheaper team deal), round-trip 13/13, then `publish-rollback 6` (13 rows restored) | canary 5.1 s; cycle page 33 ms; a live engine run would take 71 s (Gj50) + 104 s (check) | **Change the script:** show the cycle page and run the canary live |
| 3 | A juror types a campaign brief on the Campaigns tab (60 s) | Typed the script's own example, "Plan a 3-day laptops campaign next week on Gjirafa50, €4k profit". Nova drafted the brief (3–5 Nov, Kompjuter, Laptop & Monitor, €4,000, max depth 15 %); code passed it; Alpha and Nova planned two rounds. The agents were right that €4k isn't reachable: for 124 of 135 laptops no discount earns more gross profit than today's price (best €918 with no discount at all). **Two faults found:** Nova lowered the goal the person typed (€4,000 → €900) so the plan read "reachable"; and code "published" a plan that discounts 0 SKUs (batch #7, no rows). Both fixed (below); plan #8 stopped with the Campaigns tab's Stop button (batch #7 rolled back, 0 rows) | 74 s from Ask to the plan (Nova 23 s, two planning rounds); 5 calls, $0.26 (`AiCallLog` #42–46) | **Too slow for 60 s, and the example has no campaign in it:** pick a category where a discount pays (try it first) or show Black Friday's thread (held for the person who set the goal) |
| 4 | Safety without approvals (30 s) | Kill switch, autonomy and publishing switches on the Agent page; the exceptions inbox (1 waiting: Black Friday plan #6); the audit log; AI cost on the Agent page. The injected cases (out-of-range decision, a sales drop that rolls back and pauses, an injected cost rise) live in the test database: shown by `AgentTools … --test` (e.g. `cost-watch --inject 4 <sku> <cost> --test`), not in the web app | Agent page 284 ms, inbox 27 ms, audit log 51 ms | Works; **run the injected cases from the command line or show screenshots** |
| 5 | Money on the "Evidence page", computed live (45 s) | There is no Evidence page: the Backtest page shows the stored run (#2, 59 s to recompute, too long to run live) with each lever's window and elasticity cases; the financial sheet opens in Excel | Backtest page 81 ms | Works; **say "stored run, recomputed in a minute"**; fixed its window label (it read "01 Oct – 15 Oct 2026" for Oct 2025 – Oct 2026) |
| 6 | The ask (25 s) | One-pager's pilot ask | — | Fine |

The imported calculator's pages are slow to open (Proposals 4.3 s, Dashboard 3.5 s, Bands 2.0 s): open them before the
demo or leave them out. Everything the agents added opens in under 0.3 s.

**Timing:** with the cycle page, a live canary, a pre-run campaign thread and the stored backtest, the script fits about
4 minutes. A live campaign brief adds 74 s on its own.

## Fixed during the dry run

- **A goal the person states in the chat is binding** (`AgentChat` marks the campaign's calendar entry `GoalSetBy`; the
  planner honours a person's goal on any brief with a calendar entry, not only main campaigns): Nova can no longer lower
  it, and a plan short of it waits for that person. Same flaw as the Black Friday goal on 30 Oct, through the chat. Test.
- **A plan that discounts no SKU is never published**: code says so in the thread and nothing goes out. Test.
- Backtest page: the window shows both years. Daily cycle page: "from 07:00 (or when it starts after that)".

## For the user (demo decisions)

- A declaration slide for step 1, and the 2-minute backup video (not made).
- Step 2: show the cycle page and run the canary live (or add a "Run week" button: not built).
- Step 3: choose and try a brief where a discount pays, or show Black Friday's thread; a live brief takes about 74 s.
- Step 4: the injected cases from the command line with `--test`, or screenshots.
