# Prompt 10 (data session; was filed as 09, which the reviewing session's message also used): what really holds the shelf; keep the sandbox true to the live shop; E23 (raises that didn't reach the shelf)

Prompt 07 part A left this in `HACKATHON.md`, the one-pager and dry run 2's row: *"cheaper team deals still hold 43 % of
the checked prices off the shelf until a person supersedes them"*. E22 (the cost watcher's first refresh) says the same of its 52 holds "at a cheaper team deal": on the sandbox, **49 of
the 52** are held by a discount carrying a price the calculator approved for that SKU, **30** by an app-only one, about
3 by a real team deal (`techtober-2026`). Apply Part A's rule to the cost watcher's holds too. The data session looked at what those deals are. Mostly
they aren't team deals: they are **the calculator's own earlier pushes**, which the team imports as discount sets, and
over a third of GjirafaMall's are **app-only**. And they all end tonight, which breaks the sandbox from tomorrow unless it
follows the live shop. Everything below reads `PricingToolHackathon` only (plus one new hk table, row #39 in
`hk.DataSources`).

## Part A: what holds the "cheaper deal" prices (31 Oct, batches 10100 / 10101, the cycle's publish of runs #1103 / #1104)

The blocking discount's name and price are in `ai.PublishException.Detail`. Matching its price to
`PricingTool.ApprovalItems` (same SKU) gives where it came from:

| Discount family | What it is | Gj50 KS | Mall KS | Ends (UTC) |
|---|---|---|---|---|
| `oct-deals-26-<sku>` | Gjirafa50 approval **#32** (pushed 7 Oct), on both shops (the sync) | 13,687 | 16,701 | 31 Oct 23:59 |
| `oct-all-disc-26-xk-<sku>` | Gjirafa50 approval **#37** (pushed 20 Oct), both shops | 10,249 | 11,109 | 31 Oct 23:59 |
| `oct-app-deals-26-<n>` | GjirafaMall approval **#35**'s prices, **app only** (`DiscountPlatformTypeId` 2) | — | 17,391 | 31 Oct 23:59 |
| `oct-disc-xk-<n>` | GjirafaMall approval **#30** (pushed 4–5 Oct) | — | 2,087 | 4 Nov 23:59 |
| `techtober-2026`, `*wh-disc*`, `weekend-deals-*`, `vetem sot`, others | **team deals** | 1,034 | 1,618 | various |
| **Total "cheaper-deal"** | | **24,970** | **48,906** | |

- **96.5 %** (71,302 of 73,876) carry a price an approval gave that SKU (Gj50 96.0 %, Mall 96.8 %). Real team deals:
  **2,652 SKUs, 1.5 %** of the 172,724 checked prices.
- **App-only deals don't set the web price.** Neither the writer's "other discounts" query
  (`NopDiscountWriter.cs` ~l.255 and ~l.585) nor the engine's `#disc` (`SourceQueries.cs`) filters
  `DiscountPlatformTypeId`, so 17,391 Mall prices were skipped although a web shopper would have paid ours (nopCommerce
  gives the app shopper the cheaper of the two).
- **Today's process stacks its own pushes.** The team imported #37 as a new set on 20 Oct while #32's set stayed live to
  31 Oct, and nopCommerce applies the cheaper. 18,932 of #37's raises on Gjirafa50 KS (18,925 on their Mall copies) never
  reached the shelf: of the 512 units sold on #37's raised SKUs from 21 to 30 Oct, **1** sold at #37's price (Part C).

**Do, committing with `dotnet test` green:**

1. **App-only deals:** a cheaper discount with `DiscountPlatformTypeId` 2 doesn't stop a regular publish. Write ours
   (web and app), record it as `app-deal-cheaper` (information, counted, not a skip). Don't change the engine (it's the
   imported calculator's). Add one line to `GO-LIVE.md` saying the engine takes an app-only deal as the current price,
   as a question for the team.
2. **Our own push vs a team deal:** a cheaper discount that isn't ours but whose amount equals a price the tool approved
   for that SKU (`ApprovalItems.NewPrice`; the checked export's rows once they're stored) is `own-push`, "the
   calculator's earlier push, imported by the team". Everything else stays `cheaper-deal` (a team deal). The inbox, the
   Run week summary, the run checker's notes and the weekly report count the two apart.
3. **Superseding at go-live:** `SupersedeTeamRegularDiscounts` + `TeamRegularNamePatterns` decides by name, and
   `%-disc-%` would also match campaigns. Replace the test with the `own-push` rule (single-product discount, carries a
   price the tool approved or exported for that SKU). The switch stays off by default. **While the October sets are
   still live** (before 31 Oct 23:59 UTC), run one sandbox Run week per KS layer with it on and record how many checked
   prices reach the shelf. Expected: about 98 %; the rest is team deals. If the sets have expired, measure it on the
   mirrored state (Part B) instead and say so.
4. **Documents:** wherever the 43 % appears, give the split: on 31 Oct, 96.5 % of the held prices were the calculator's
   own earlier pushes (all ending by 4 Nov), over a third of Mall's (23.5 % of all) were app-only, and real team deals held 1.5 % of the
   checked prices. Add the stacking as the reason v2 keeps **one** discount per SKU and ends its own in the same
   transaction.

## Part B: the sandbox has to follow the live shop (the 30-day live path depends on it)

At 31 Oct 23:59 UTC, **560,411 of the sandbox's 961,624 GjirafaMall discounts and 195,595 of 213,510 Gjirafa50's
expire** (the October sets). The live shop then gets November's sets: the next CSV push the team imports, Black Friday.
The sandbox gets none of them, so from tomorrow its "on the shelf" and "held by" counts describe a shop that doesn't
exist, and its tier prices drift as the team changes them.

1. **A daily cycle step `mirror-L<layer>` before `publish-L<layer>`**, in the engine host, on the read-only replica
   connection it already has (the cost watcher's). For the layer's store:
   - upsert the live shop's discounts that aren't ours (live now or starting within 2 days), with their product mappings
     and the products' discount flags;
   - end-date mirrored rows that have left the live shop;
   - refresh `TierPrice.Price` / `OldPrice` for products this writer hasn't written (no applied, un-rolled-back
     `PublishAudit` row).
   Never touch `ai-price` rows or rows the writer inserted. Keep it light: narrow columns, `NOLOCK`, and limit to the
   layer's latest run's SKUs through a `#temp` on the replica, as `SourceQueries.cs` already does. Put rows and seconds in
   the step detail.
2. **Tests:** mirror → publish → rollback leaves the mirrored rows as mirrored; a mirrored row never overwrites one of
   ours; a team discount that disappears is end-dated, not deleted.
3. **From then on, each day's publish is a shadow measurement against the live shop:** how many checked prices would
   reach the live shelf, how many are held by our own earlier push, by an app-only deal, or by a team deal. Record it in
   `hours.md` and let the weekly report carry it. It's the 30-day evidence that raises reach the shelf.

## Part C: E23, approved raises that didn't reach the shelf in today's process

New script, **`docs/hackathon-2026/evidence/raises-not-on-shelf.sql`** (about 10 s; its header defines everything). It
reads one new table, `hk.DiscountHistoryRaisedKs`: the Aug–Oct discount history of the 517 products concerned, loaded
read-only from the replica. Lines are sales of an approval's raised SKUs (New > Old × 1.02) from the day after it went live
to the day before the next one did, outside campaign days, so lever 5's run-to-push gap isn't included:

| Approval | Layer, live | Units | At the new price | At the old price | GP forgone, e −1 (constant units) |
|---|---|---|---|---|---|
| #12 | Mall KS, 6 Aug – 2 Sep | 927 | 618 | 158 (17 %) | €682 (€914) |
| #25 | Mall KS, 3 Sep – 5 Oct | 3,006 | 2,003 | 602 (20 %) | €2,372 (€3,440) |
| #35 | Mall KS, 19 – 30 Oct | 569 | 49 | 478 (84 %) | €1,177 (€1,794) |
| #37 | Gj50 KS, 21 – 30 Oct | 512 | 1 | 360 (70 %) | €3,774 (€4,868) |

**What held the old price** (the live discount at the sold price):

- **Aug–Sep, per month at e −1, €1,574:**
  - **€300** that v2's publisher removes by construction:
    - the calculator's earlier push still live (`pppbi-p<n>`, `sept-disc-26`, `aug-disc-26`);
    - the regular price never raised (no discount at that price, and a discount can't raise one).
  - **€1,274** that is team deals:
    - the app-only `app-deals-august-2026`: €1,437 over the two months;
    - the vendor deal `zipro-sep-2026`: €939;
    - v2 leaves these to a person, as today.
- **October, the v2-removable part is larger:**
  - #37: €494 was held by #32's own set (the stacking above);
  - #35: about €400 was held by #30's set and by its regular price;
  - that is about **€2.8k a month** at October's pace;
  - the rest is team deals: `techtober-2026` alone is €2,665 in 9 days.

**Do:**

1. **E23 in `numbers.json`** (E22 is the cost watcher's record; take the next free number if E23 is gone too) with
   this script and its method. In `backtest-results.md` and the Evidence page: "in
   today's process 17–20 % of the units on SKUs an approval raised still sold at the old price (Aug–Sep), 70–84 % in late
   October".
2. **Lever 8, "raises reach the shelf" (the publisher):**
   - central = low = **€300 a month** (Aug–Sep, e −1, the part v2 removes by construction);
   - high = **€2.8k** (October's pace, labelled as the month two push sets overlapped).
   - Count a line once: drop from lever 8 any line that lever 1 or 3 already scores (match on `SrLineId` in the
     backtest).
   - Framing A only. It is what v2's publisher adds over the CSV import.
3. **The same team-deal limit applies to levers 1, 3 and 5**: v2 doesn't raise a price a team deal holds. Aug–Sep: 15 %
   of the units on raised SKUs (579 of 3,933) sold under a team deal at the old price.
   - Split each lever's gain by whether its line sold under a team deal. Use this rule:
     `hk.SalesLineDiscountsKs.ProductDiscount > 0`, and the unit price isn't any approval's price for that SKU.
   - State the share. Move the team-deal part from central to high ("if a person supersedes the deal"). It is the honest
     version of the 43 % caveat, now with its size.
4. Follow the numbers through the sheet, the one-pager and `HACKATHON.md`. Then add one row to dry run 2's findings table.

## Part D: LocalDB, beyond the hardening

`GO-LIVE-LAPTOP.md` explains the 07:27 stop. It **stopped a second time**:

- The instance that started at 07:27:34 was started by the web app reconnecting.
- At 07:32:19, `TaskStop` of the web app's background task killed its process tree, and `sqlservr` with it. The next
  instance (07:32:29) ran crash recovery.
- `sqlservr` is a child of whichever process connects first after LocalDB's idle stop (`user instance timeout` = 5
  minutes; it restarted 7 times between 05:18 and 07:32).
- So anything that kills that process tree takes the database down mid-write: `TaskStop`, `Stop-Process` on a parent
  shell, `Kill(entireProcessTree)`.

1. `run-laptop-host.ps1`, and any script that runs a Run week, starts the instance first with
   `sqllocaldb start MSSQLLocalDB`. Then `sqlservr`'s parent is the short-lived `sqllocaldb.exe`, never an app or a
   tool's process tree.
2. In `GO-LIVE-LAPTOP.md`, next to the memory cap (the user's call):
   - stop the web app with `Stop-Process -Id`, never with a tree kill;
   - optionally raise `user instance timeout` so the instance stays up between the engine host's 15-minute ticks.

**Done when:**

- The 43 % is told as its split everywhere.
- App-only and own-push blocks are classified.
- The mirror step runs in the cycle with tests.
- E23 and lever 8 are in `numbers.json` with the script, and levers 1, 3 and 5 show their team-deal share.
- `dotnet test` is green and `hours.md` is updated.
