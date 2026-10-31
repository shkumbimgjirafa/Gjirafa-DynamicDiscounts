# Demo dry run 2 (31 Oct 2026, 08:25–08:36)

Plan section 7 on today's build (`cf189b4` plus the fixes below), the real app (http://localhost:5058, Hackathon
environment), Gjirafa50 KS only: no GjirafaMall engine run during the demo (the web app peaked at 1.6 GB on one, and
LocalDB stopped once this morning). Timed. What changed since [dry run 1](demo-dry-run-1.md), and what needs the user.

## Step by step

| # | Script (time) | What happened | Measured | Verdict |
|---|---|---|---|---|
| 1 | The problem, declaration slide (20 s) | Still no slide; `DECLARATION.md` and `HACKATHON.md` section 1 carry the content | — | **Needs the slide** (the user's) |
| 2 | Run week for Gj50 KS (60 s) | Run week #4, pressed on the Agent tab: all steps succeeded (timeline below). The publish wrote 416 discounts, updated 480, kept 11,041 as they were, ended 8 of ours, and **raised 7 regular prices** (5 still under a cheaper deal); 12,865 of 12,865 read back. 25,453 were left to cheaper deals (Prompt 10 splits what those are) | 310 s in all; the Agent page took 4.5 s on its first (cold) load | **Press it before the demo** and show the timeline; it can't run inside 60 s |
| 3 | A campaign brief (60 s) | Not re-run (no change since dry run 1; a live brief takes about 74 s and makes 5 calls). Black Friday's thread (plan #6, held for the person who set the goal) is the one to show | — | As in dry run 1 |
| 4 | Safety without approvals (30 s) | **Injected cost rise, live on the test database:** `cost-watch --inject 4 1772258mo 42.50 --test`: cost 38.61 → 42.50 put 46.50 under the 13 % floor (48.85); **raised to 49.50 in one watch, 5.9 s**, written as the store's regular price (checked on the sandbox: `TierPrice` 46.50 → 49.50, no public deal under it). Doing this found two defects, fixed below. The out-of-range decision, the simulated drop (rule back, the raised prices and the discounts they ended back, the band × category paused) and the kill switch: 58 tests of those classes run green (list below); no live model call, as nothing in them changed today | 5.9 s for the watch | Works; **show the injected rise live**, the rest from the tests or screenshots |
| 5 | Money (45 s) | Backtest page (stored run #3) and the financial sheet; E20 and E21 now from this repo's data, E22 the cost watcher's real refresh | — | As in dry run 1 |
| 6 | The ask (25 s) | The one-pager's pilot ask | — | Fine |

**Run week #4's timeline** (Gjirafa50 KS, 08:25:27–08:30:37): size check 0 s (6.73 GB of 10), Alpha 144 s (pass #14:
0 changed; this week's one-point step was used by pass #2), engine 50 s (run #1109, 129,658 SKUs), check 101 s (check
#10010: warn, 6,800 flagged, 150 judged by the model, 6,770 held), publish 12 s (batch #10102, 38,318 checked prices),
verify 0 s. AI: 2 calls, $0.94.

**The tests behind step 4**, all green: `SafetyLimitsTests` (out-of-range values rejected, five points held for a person,
the one-point step), `FloorTargetAgentTests` (autonomous applies within the limits and holds the rest; the kill switch
stops the pass), `OutcomeTrackerTests.A_drop_rolls_the_rule_back_pauses_the_category_and_the_next_change_there_waits_for_a_person`
(with the publisher's restore called), `NopDiscountWriterTests.A_drop_restores_the_raised_regular_prices…` and
`…A_drop_brings_back_our_discount_the_raise_had_ended…`, the kill-switch tests of the writer, Run week, the run checker,
the cost watcher and the Claude Code client.

## Fixed during the dry run

- **The cost watcher's raise didn't move the shelf when the price was the regular price.** It put our discount at the
  floor price, but a discount above the store's regular price changes nothing (the engine takes the regular price), and
  the round-trip only checked that our discount was the cheapest deal. It now publishes the way the daily publish does:
  our discount under the regular price, the regular price itself at or above it, with the run's view and ceiling; a
  price that doesn't stand (a cheaper team deal, a row changed since the run) is held with that reason. Tests: the
  sandbox shelf really moves; a changed row is held.
- **`AgentDecision.Action` held 10 characters**, so the watcher's `raise-to-floor`, `end-our-discount` and
  `end-campaign-discount` could not be saved on a real database (the in-memory tests don't enforce lengths); the raise
  was published but its decision failed, and the observation would have been retried every watch. No real watch hit it
  (31 Oct's raised none). Widened to 30 (migration `WidenDecisionAction`); a test checks every action the watcher records fits.
- `PricingTool.AgentTools` isn't built by `dotnet test`: build it before using the command line (`dotnet build
  PricingTool.AgentTools`).

## For the user

- The declaration slide for step 1, and the 2-minute backup video ([shot list](demo-backup-video.md)).
- Press Run week for Gjirafa50 before the demo (about 5 minutes) and show its timeline in step 2.
- For step 4, run the injected cost rise live (one command, 6 s) on a SKU picked beforehand; show the rest from the tests.
