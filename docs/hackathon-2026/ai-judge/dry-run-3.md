# AI-judge dry run 3 (31 Oct 2026, 10:13–10:22)

The same juror as [dry run 2](dry-run-2.md), on the state after Prompts 10–12: `d980294` (the sandbox mirror, the Evidence
page, framing B as the claim). What it found and what we did. Monday 06:00's run stays the final one.

## How it ran

The same isolation as dry runs 1 and 2: Claude Code headless, `claude-opus-5-5`, `--setting-sources ""`,
`--no-session-persistence`, no `CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables, Read / Grep / Glob and `git log / show /
diff / grep` only, `--max-budget-usd 15`. It read a fresh clone (no remote), the rules summary and a fresh cell-by-cell
dump of the financial sheet. Two changes to the prompt: item 1 says the team's claim is framing B, with framing A as the
cross-check, and asks it to score B, score A as the cross-check and say whether B's claim holds up; item 7 asks, for each
finding of dry run 2's report, whether it is resolved now, without taking our answer on trust. 57 turns, 505 s, $3.66 at
API list price (counted in E18). Its full report: [`dry-run-3-juror-report.md`](dry-run-3-juror-report.md).

## Scores

| Row (weight) | Our sheet, B · Mid | Juror, framing B | Our sheet, A · Mid | Juror, framing A (cross-check) |
|---|---|---|---|---|
| Net benefit (40) | 5 | 3 | 3 | 2 |
| ROI (10) | 5 | 4 | 4 | 3 |
| Payback (10) | 3 | 3 | 3 | 2 |
| Evidence (20) | 3 | 2 | 3 | 2 |
| Works (20) | 4 | 2 (3 if Run week is demoed live) | 4 | 2 (3) |
| **Total** | **84** | **54 (58)** | **66** | **42 (46)** |

**Does B's claim hold up? Its answer: no.** It scored B at the midpoint between B0 at the controlled rate and fully new
(net benefit band 4) and B0 entirely the existing process's money (B equals A). Its three reasons: B·Mid uses the
uncontrolled €10,409, which E20's own method says the year-over-year check doesn't support; the reasons we gave for B0
(E23, E5, E4) are money already counted as levers 8, 5 and 4; and the declaration says the calculator's effect isn't
claimed. On framing A it valued lever 4 at the controlled rate (€560), giving €12.9k net (band 2).

Of dry run 2's 39 findings and its biggest assumption: 7 resolved, 11 partly, 7 disclosed only, 8 not resolved, 7 pass.

## What it found and what we did

Fixed = changed in code, data or documents; disclosed = now stated in `HACKATHON.md`; kept = left as it is, with the
reason; **Prompt 13** = the framing B case, which the user has decided and Prompt 13 restates next (B's headline €10,409
with the controlled €4,402 shown under it; €13,000 out of the claim).

| # | Severity | Finding | Outcome |
|---|---|---|---|
| 9, 10, 11, 19, 26 | high | Framing B is mostly the declared calculator's money; B·Mid uses the uncontrolled rate the year-over-year check doesn't support; the reasons given for B0 (E23, E5, E4) are already counted as levers | **Prompt 13**: framing B and its headline are the user's decision, and which framing counts is the organisers' call. Prompt 13 restates the case: E23, E5 and E4 are counted once, as levers 8, 5 and 4; they describe why the rate needs the automated system, they don't add to B0 |
| 2 | high | E20's high case (€13,000) is quoted, with no script | **Prompt 13**: €13,000 leaves the claim; `numbers.json` keeps it, labelled quoted and not part of the claim |
| 25 | high | Lever 3's central case is 93 % one August cost event | **Disclosed** (known limitations); kept the cases as dry run 2 left them: the low case is September only |
| 1, 4, 7, 8 | high–low | Runtime figures need the database, which isn't supplied | **Kept** (inherent). The Evidence page recomputes nine of them live as a read-only user; the committed backtest snapshot ties out, as it checked |
| 12 | medium | "Only in Kosovo": MK/AL got manual pushes in some months | **Fixed** (one-pager): mostly in Kosovo, MK/AL in some months only. Lever 4 counts only the MK/AL months without a push of their own (`hk.PushDatesMkAl`), so it stands |
| 13 | medium | "Going live is configuration and a DBA's grant" leaves out the shop's answers and the owner's call | **Fixed** (one-pager, `HACKATHON.md`) |
| 14 | medium | "An outcome tracker… rolls back what hurts" in the present tense | **Fixed**: built and tested; no pushed change has completed its window yet |
| 15 | low | Stale figures: E15's calls, the unattended blocks, the commit and file counts | **Fixed**: counted at `d980294` (170 commits; 310 files; new files per project); E15 53 calls $7.90; E16 38 blocks; E18 refreshed ($393.76 since the kickoff, the three dry runs included), which moved net benefit by €121 |
| 16 | low | "A rule's block stays a block whatever the model says": a blocked row with a model fix that passes the rules becomes "fixed" | **Fixed** wording: the model can't downgrade a block; it can only replace the price with one that passes every rule again |
| 17 | low | "A person lifts only the step limit": approving a paused hold applies it | **Fixed** comment (`FloorTargetAgent.ApproveAsync`): a person decides both step-limited and paused changes; the pause itself stays for the next pass, as designed |
| 23 | medium | E21 labelled "measured", but the forgone GP is computed at the same units | **Fixed** label: estimated from measured lines. The score bands' source (the rules document) isn't in the repo: kept |
| 38 | low | The 57 % and 98.3 % shelf shares come from different runs | **Fixed** wording: which runs each is |
| 3, 5, 6, 21, 22, 35, 36 | medium–low | Chosen controls; E18's transcripts not in the repo; the kickoff rulings; an email and an Office name in history; replica reads; a chat goal is Nova's reading; the euros barely depend on the AI | **Disclosed** already (`HACKATHON.md` sections 3, 5 and 6) |
| 27 | medium | Lever 4's mid and high use E20's uncontrolled rate | **Kept**, disclosed: with the controlled rate only, E4 is €560 (section 4) |
| 28, 29, 30 | medium | Optimistic cases (lever 8 high, lever 7's range, lever 2 in-sample); inputs without a source; full scope from month 4 | **Kept**: each is labelled (estimated / assumed) with its method; lever 8 high is October's pace, said so. Lever 8's €7 of team-deal lines: kept (the holder rule excludes team deals; the line flag differs) |
| 32 | low | The campaign floor is configuration and could be set to 0 | **Disclosed** (known limitations); every configuration has 3 % |
| 18, 37 | medium–low | Commit 1's pre-kickoff tooling; features landed shortly before the review | **Kept**: the organisers rule on commit 1; the timing is what it is |
| 39 | low | `pass.Applied` counts keeps | **Kept** (as in dry run 2) |

Dry run 2's #13 ("fixed: current figures") wasn't true at `d980294`; it is now, with the commit named so the counts don't
go stale. `dotnet test`: 967 passed, 1 skipped.
