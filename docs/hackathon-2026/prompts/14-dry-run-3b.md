# Prompt 14 (data session): AI-judge dry run 3b on the €10.4k headline (after Prompt 13)

Dry run 3 started before Prompt 13 reached the build session. It scores framing B as three cases (€5,694 / €12,965 /
€19,291 a month), not as the €10,409 headline the user chose with the €4,402 floor under it. Without a rerun, the juror
first sees the headline on Monday at 06:00, with no time left to fix what it finds. This run tests the headline this
weekend. The user asked for it: "write the promt".

**When:** after Prompt 13 is committed, and after the defect fixes from dry run 3, so the juror reads the final framing.
Don't interrupt dry run 3.

## Do

1. **Same isolated juror as dry runs 2 and 3:**
   - same Claude Code version and model (`claude-opus-5-5`);
   - `--setting-sources ""`, `--no-session-persistence`, no `CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables;
   - Read / Grep / Glob and read-only git only;
   - a fresh clone of HEAD with no remote, the rules summary and a fresh cell-by-cell dump of the financial sheet.
2. **The brief is dry run 3's, with two changes:**
   - "Our claim is framing B at its headline: B0 €10,409 a month (E20 mid), €12,965 a month at full scope. The controlled
     €4,402 (E20 low) is disclosed as its floor. Framing A is the cross-check."
   - Ask it to score B **at the claim and at the floor**. Ask it to say whether it accepts €10,409 as the rate, and if
     not, what evidence in the repo would change its mind.
   - Keep the brief neutral. Don't argue the case in it; the documents do that.
   - Also ask it to judge dry run 3's findings: resolved or not, without taking our answer on trust.
3. **Record** it in `ai-judge/dry-run-3b.md`, with its report beside it as `dry-run-3b-juror-report.md`:
   - turns, seconds and cost;
   - a score table that puts dry run 2, dry run 3 and 3b side by side, with the B column split into "at the claim" and
     "at the floor".
4. **Fix defects only:** wrong numbers, broken claims, code bugs. No new features and no new claims.
   - If the juror rejects €10,409 on substance (it's before/after, not controlled), record its reason word for word.
   - Put a handoff line for the user: the headline is their framing call, and it doesn't change in this prompt.
5. `hours.md` row, `dotnet test` green, explicit-path commits, the secrets grep. Monday 06:00 stays the final run.

**Done when:**
- `dry-run-3b.md` and the juror's report are committed, with B scored at the claim and at the floor.
- Its defects are fixed, or disclosed with a reason.
- The handoff names any objection to €10,409 that only the user can settle.
