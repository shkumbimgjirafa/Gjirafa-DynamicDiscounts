# The live app: three tabs (plan, 31 Oct)

## Why

The user's review of the app (31 Oct, two rounds): it still shows how the tool works inside, not what it did. The live app
should answer three questions and nothing else:

1. **What is the current run doing?** Floors and targets by category × band, what changed, whether it's working, Alpha's explanation.
2. **What are the agents thinking, and what needs me?** Their reasoning and chat in one place; only problems they couldn't
   solve reach the user, each with who can fix it. Goals for the month, week and campaigns are agreed with Nova there.
3. **Can I change one price?** Proposals stays; a price set there goes to NOP.

Schedule, Price bands and Movers leave the navigation (Alpha handles them). The proof pages (Evidence, Backtest, Money
ledger, the v1 Impact dashboard) leave the live app; the figures stay in `docs/hackathon-2026/evidence/` and the sheet.

## Navigation

**Run · Chat (badge) · Proposals**, plus on the right: layer picker, and an **Admin** menu (Daily cycle, Audit log, Time
sheet, Users). The strip under the navigation on every page: publishing target, kill switch state, data load time.
Run week, the kill switch and the autonomy switches move to the Run tab's header.

## Tab 1: Run (the current run, treated as live)

**Which run.** The layer's newest regular publish that went through: `ai.PublishBatch` with `Kind='Regular'`,
`EngineRunId` set, `Status='Published'`, not rolled back, highest id. Today: Gjirafa50 KS = run #1109 (batch #10102);
GjirafaMall KS = run #1104 (batch #10101), because #1107's publish was rolled back. (Batch #10101 carrying an older run than
#10099 should have been refused by `CheckedPricePublisher.cs:63-68`: check it first.) The page says once, at the top:
"treated as live; during the hackathon it is in the sandbox, so the shop's real prices didn't change" (the labelling rule).

**Sections, top to bottom:**

1. **Alpha's report** (new): 5 to 8 sentences Alpha writes after each publish: what it changed and why, what it expects,
   what blocked it. One model call per published run per layer (the cost rule allows one per run); code computes every number
   and hands them in; Alpha only writes. Until a run has one, the pass summary (`ai.AgentPass.Summary`) shows.
2. **Progress** (the measures, decided here):
   - **On the shelf:** the share of the run's prices that are actually the price a shopper pays, read back
     (`PublishBatch.RoundTripChecked` against requested; left-out SKUs by reason). Measured. Today about a third on
     Gjirafa50 (25,453 of 38,318 skipped, mostly cheaper team deals), which is the number the team-deal change below moves.
   - **Expected gross profit a month** of what is on the shelf: the `CheckedExport` formula (`CheckedExport.cs:168`).
     Estimated.
   - **Month to date vs the goal:** realised gross profit from `hk.SalesLines` against the goal agreed with Nova, pro rata.
     Measured, from the real shop (so not caused by sandbox prices; said on the page).
   - **Against the holdout, after 7 and 30 days:** the existing ledger method, shown once a publish is live (empty in the
     sandbox, said so).
3. **What changed vs last week:** raised / lowered / unchanged / held by the checker / holdout left out, with last week's
   numbers beside them.
4. **Category × band grid:** rows = main categories (`MainGroup` for Mall, `CategoryL1` for Gj50), columns = bands; each cell
   floor / target, SKUs, changed, average change. Cells Alpha changed this week are marked old → new with its reason.
   Band defaults from `PricingTool.PricingRunBandTargets`, Alpha's overrides from `PricingRunCategoryRules`; counts from the
   run's `ProposedPrices`.

**New storage, because retention deletes proposals after two runs per layer:** `ai.RunReport` (one row per published run:
counts, coverage, expected GP, Alpha's text) and `ai.RunReportCell` (one row per category × band). Written by code right
after a publish (Run week and the daily cycle). They keep "vs last week" possible from now on. Nothing earlier than 30 Oct
can be shown.

## Tab 2: Chat (the agents' reasoning, the chat, what needs you)

**Left: one continuous thread** (no conversation switching, day separators). It holds the person's messages and the agents'
answers, plus what the agents post on their own: Alpha's run report, problems fixed, problems that need a person, Nova's
weekly report and campaign threads. Each post folds its reasoning under "why?". The decision-level feed folds into these posts.

**Right column:**
- **Waiting for you:** only what an agent could not resolve, each with who can fix it and a "done" button. Things that
  resolved themselves are not shown.
- **Goals:** this month's and this week's goals per layer (gross profit, margin), agreed with Nova in the chat.
- **Coming up:** campaigns in the next three weeks with the Stop button (campaign pages stay, linked).

**Chat turns run in the background:** today a turn blocks the request for 30 seconds to several minutes
(`AgentController.Chat`). The message is queued, the page shows "Nova is reading the data…" and refreshes until the answer
lands.

## What the agents do about problems (instead of asking)

| Problem | Today | New |
|---|---|---|
| Hold resolved by itself (deal ended, cost fell back, price back above the floor) | Waits forever (no re-check) | Re-checked after every cost-watcher tick; marked resolved, not reported |
| Cheaper team deal under the floor after a cost rise | Held for a person | **Alpha ends the team deal** (end date, never delete; audited, rollback restores it), publishes our price at the floor, reads it back, posts what it did |
| Cost above the shelf price | Held | Can't be fixed by an agent (the ceiling is max(anchor, current), a firm rule): Alpha reports it to whoever sets regular prices |
| Cost jump over 50 % (data error) | Held | Alpha checks it (the supplier's other SKUs, the cost history); a data error goes to the data owner, a real rise is treated as a normal rise |
| Publish failed, run blocked, cycle step failed | Listed | Retried as today; reported only if still failing, to the tool's owner |
| Campaign plan short of a person's goal | Held | Unchanged: Nova reports it, the person decides |

**How:** one Alpha call a day over the unresolved list (not one per SKU), with read-only data tools. For each item it
returns a cause, an action from a fixed list (end team deal / raise to floor / treat as data error / report), and an owner
role. Code checks every action against the limits before anything is written. **The AI decides, code enforces.**

**Who can fix it:** no owner data exists today. A config map from problem kind to role and name (for example cost above shelf
→ purchasing, data error → ERP data owner, campaign deal → campaign owner, tool failure → pricing tool team). Until names are
filled in, Alpha names the role. Notifications stay in-app (badge on Chat); email isn't built.

**What ending team deals needs:**
- the blocking discount's id stored on the hold (today it's only text in `ai.PublishException.Detail`);
- the supersede path widened beyond our own pushes (`NopDiscountWriter.CanSupersede`, `PublisherOptions.SupersedeTeamRegularDiscounts`);
- the sandbox mirror (`SandboxMirror.cs:254-259`) must stop undoing a deal we ended, or it comes back the next day;
- GO-LIVE.md, the plan and `PublisherOptions` updated: the user decided (31 Oct) that team deals under the floor are ended, not held.

In the sandbox every team deal covers one product, so ending one touches no other SKU.

## Tab 3: Proposals

Stays as the list of every SKU of the current run. **Setting a price publishes it** for that SKU, through the same checked
path as Run week (floor, ceiling and charm rounding enforced by code; raises written as regular prices; read back). The
result shows inline: published, left out (and why), or refused by a limit. Approve, Reject and "Export approved (manual)"
leave the page: they don't change what the agents publish (`CheckedExport` ignores approval status).

## Order of work (the deadline is Mon 08:00)

1. **Navigation and the two new tabs from existing data:** Run (current-run rule, grid, counts, coverage, expected GP, pass
   summary), Chat (thread + right column from the inbox, campaigns), background chat turns. Proposals' buttons.
2. **Run report storage and Alpha's report:** `ai.RunReport` / `RunReportCell` written after each publish; Alpha's text.
3. **Agents handle the problems:** re-check and resolve; end team deals (evidence id, writer, mirror); the daily
   investigation call; owner map; agents' posts into the thread; the data-error label bug (`data-error-cost-jump` vs `data-error`).
4. **Goals with Nova:** goals table, chat schema field, the Run tab's month-to-date line; Nova uses them in briefs.
5. **Proposals publishes a set price.**
6. **Docs and demo:** HACKATHON.md, README, GO-LIVE.md, the plan's sections 5 and 7, the backup-video shot list; a dry run.

Each step: tests for the new logic, `dotnet test` green, a commit, an hours row.

## Verification

- `dotnet test` (968 today) plus new tests: the current-run rule, the grid totals against `ProposedPrices`, the re-check
  (a hold whose deal ended is resolved), ending a team deal (audit first, rollback restores, the mirror leaves it), the action
  list refusing anything outside the limits, the goal parsing.
- The app on :5058: each tab against the database (the grid's SKU total = the run's priced SKUs; coverage = the batch's
  round-trip count), one chat turn in the background, one price set in Proposals read back from the sandbox.

## Decisions (the user, 31 Oct)

1. **Team deals:** Alpha ends any team deal that sells under the floor, campaign or not, **unless a rule the user gave in the
   chat protects it**, e.g. "discounts named `dd-<sku>` are not touched". New chat rule kind `protect-discount` (a name
   pattern, a layer, dates); code checks every deal against the active rules before ending it, and a protected one is
   reported instead.
2. **The proof pages leave the app.** Evidence becomes a generated HTML file (from `numbers.json`, with each figure's label,
   method, sources and script); demo step 5 shows that file and the financial sheet. Backtest and the Money ledger stay as
   files (`backtest-results.md`, the exports); `EvidenceService` stays for the tests and the file's generator.
3. **Owners:** the names behind each role, when convenient; roles are used until then.
