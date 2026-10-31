# Code review and security review 1 (31 Oct 2026, 01:09–01:15)

The plan's dry run (section 10, step 3) asks for `/code-review` and `/security-review` on `9c977ca..HEAD`, the
hackathon's own work (about 52k lines with migrations). Run by the build session on Sat 31 Oct instead of Mon 06:00, so
there is time to fix what they find.

- **Code review:** the `/code-review` skill at its high-effort recipe (8 angles, no verify pass), on the range, read
  where the risk is: tonight's Run week (`RunWeekLoop`, its launcher, the Agent tab), the campaign planner's recent
  changes, the run checker, the chat. Findings and outcomes below.
- **Security review:** not the `/security-review` skill (it reviews a branch's pending changes; `main` has none), but
  the same questions by hand on the four areas the other session named. No defect found; the checks are listed.

## Code review findings

| # | Finding | Outcome |
|---|---|---|
| 1 | Run week's AI cost summed every agent call logged while it ran (a chat or the cycle's check at the same time would count) | **Fixed:** only its own calls: Alpha's pass for the layer and the check of its engine run |
| 2 | Run week publishes the whole checked set and adds an engine run (up to 0.5 GB on GjirafaMall) per press; LocalDB is at 7 GB of its 10 GB cap and only the cycle's retention cleans up | **Fixed:** a size check before Alpha refuses above the cycle's 8 GB warning line; only the cycle's two layers can be run (the others have no data here) |
| 3 | A chat brief's goal is always marked as the person's, even when the person gave no figure and Nova chose one | **Kept, disclosed:** a person asked for the campaign, so its goal waits for them rather than being adjusted by Nova; telling the two apart needs the chat's answer schema to say whose goal it is (a follow-up) |
| 4 | The publisher can only lower a price (a discount), so Run week's raises (lifts to the floor) are skipped as "at or above shelf" while the step said Succeeded | **Fixed:** the publish step reports them as raises that go out with the regular-price push file, and turns Warning |
| 5 | The one-at-a-time guard is per web process: Run week could overlap the engine host's daily cycle (two Alpha passes at once on a Monday) | **Fixed:** Run week stops at once while a daily-cycle step is running (test) |
| 6 | Confirmation messages showed twice (the layout and the Agent / Campaigns views both rendered them) | **Fixed:** the views no longer repeat the layout's messages |

## Security review

| Area | What was checked | Result |
|---|---|---|
| `query_data` guard | `QuerySqlGuard`: one statement, SELECT only, no INTO, every table reference `agent.*` or the query's own CTE, no OPENROWSET / OPENQUERY / OPENDATASOURCE / BULK, no other database or server; `QueryDataService` runs it as `agent_reader` (`EXECUTE AS USER`, no login, SELECT on schema `agent` only; the user name comes from configuration, not the query), 30 s timeout, 500-row cap, every query logged. Tests cover each refusal | No defect |
| Publisher's write path and login | `NopDiscountWriter`: every value is a SQL parameter; only integer ids and the configured schema are interpolated, and with `Publisher:Target` = Sandbox the schema must start with `shop_sandbox_` (`PublisherOptions.ShopFor`); the previous state goes to `ai.PublishAudit` before each write; nothing is deleted (a rollback end-dates or restores). No live write connection is configured anywhere; going live needs a least-privilege login in user-secrets (`GO-LIVE.md`) | No defect |
| Secrets in the tree and history | `git log -p --all` over the whole history, the import commit included, for `password=`, `pwd=`, `user id=`, `sk-ant`, `AccountKey=`, private keys: nothing. Tracked connection strings point at `(localdb)` only; the replica's is in user-secrets. `git grep -i -E "password\|pwd=\|api[_-]?key\|secret"` finds only option names with empty values and docs that say where secrets live | No defect |
| Personal data in logs and exports | The sales-line export SQL keeps `OrderId` only in a temp table for the cross-dock join; its final select has no order, customer, name or address. The AI call log redacts emails before storing output or errors. The push CSV carries SKU, prices, run, proposal and check ids. The time sheet carries the owner's email by the organisers' template (configured, empty by default). An employee's work email in the imported config was removed on 31 Oct (`80b8172`); it stays in the import commit | No defect; the import commit's email is history |
