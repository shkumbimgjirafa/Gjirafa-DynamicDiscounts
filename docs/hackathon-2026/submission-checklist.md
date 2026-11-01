# Submission checklist

Deadline: **Mon 2 Nov, 08:00** (freeze from 06:00, plan section 6). Kept up to date as items are done; the plan's own
list is section 8. Last updated Sun 1 Nov, 19:49.

## The deliverables

- [x] **Prototype:** this repo, with the README's "Hackathon build" section and `HACKATHON.md` (how to run it, what's new).
- [x] **One-page summary:** `docs/hackathon-2026/one-pager.md` (rewritten 1 Nov evening by the one-pager session: four problems, then the
  engine and its agents, no score).
- [x] **Financial sheet:** `docs/hackathon-2026/Financial sheet.xlsx`, every line labelled measured / estimated / assumed. Rebuilt
  1 Nov 19:44 from the regenerated evidence, every category priced (no holdout categories, the user's decision): the claim is
  €10,430 a month; year 1 net benefit €88,017, ROI 795 %, payback 3.1 months.
- [x] **2-minute video:** `Pricetool demo cut.mp4` (1:54, silent with captions), in Downloads, submitted as a file (not in
  the repo). What it shows: `docs/hackathon-2026/demo-backup-video.md`.
- [x] **What is mocked or unfinished:** `HACKATHON.md` section 5.
- [x] **Evidence:** `docs/hackathon-2026/evidence/` (`numbers.json`, one script per figure, `evidence.html`). Regenerated 1 Nov
  19:30 from today's data (the levers from the same stored backtest); the evidence page, written at 19:48: 52 of
  52 checks match.
- [x] **Screenshots:** four of the running app in `docs/hackathon-2026/screenshots/`, listed in `HACKATHON.md` section 2.
- [x] **Hours for 1 Nov:** 8 h, the user's estimate, in `hours.md`, the sheet's costs and E16.
- [x] **Go-live runbook:** `GO-LIVE.md`.
- [x] **Tests green:** 1,040 passed, 1 skipped (1 Nov, 19:27), recorded in `HACKATHON.md`. Full runs that take minutes while LocalDB
  is busy have failed a few database-backed tests (the discount writer, the sandbox contract); those pass alone and in fast runs.
- [x] **No secrets in tracked files:** the `git grep` check finds only comments and code that handles a password; the meter's team
  token is in no tracked file and in no commit of the whole history (checked 1 Nov, 18:10).
- [x] **AI Pioneer meter v3 (Mergca, shortlist condition):** installed and verified (1 Nov); four task cards sent and accepted (Alpha's
  review, the run check, the push, Nova's campaign planning); a real run shown with the echo on (check #10016 of run #11112, event
  `04112d53…`), queue empty after flush. Mergca confirmed v3 on 1 Nov; the other three cards got their real runs the same
  evening (Alpha's pass #10025, the export of run #11112, the re-plan of brief #12), each with the card's name and one unit.
- [x] **Hours-saved time sheet:** minutes before set (240 / 120 / 30, the same as the cards); it exports runs from 2 Nov only.

## Still to do

- [ ] **Push and submit tonight (the user's decision, 1 Nov):** the cleaned history to `github.com/shkumbimgjirafa/Gjirafa-DynamicDiscounts`,
  tag `hackathon-submission`; then to Mergca: the repo link, the one-pager, the financial sheet and the video.
- [ ] **Keep the laptop on, awake and plugged in overnight.** The engine host (started 18:10) has no way to restart by itself: its
  start-up task is disabled. On 1 Nov its log stops at 15:00 and it was started again by hand at 17:09; Windows logged in
  again at 16:26 (the desktop's start time).
- [ ] **Before leaving the laptop overnight:** close heavy apps (ChatGPT, the Power BI gateways). LocalDB froze twice on
  1 Nov when memory ran out, and the 07:00 cycle (Alpha's and Nova's Monday passes, a GjirafaMall publish) runs during
  the freeze.

## Before the push (1 Nov evening)

- [x] A fresh AI-judge run on a clean clone of the submission branch; its factual findings fixed.
- [x] A fresh AI check for contradictions between the files: 16 found and fixed (1 Nov, 19:30); a second
  check found 5 more, fixed (19:45).
- [ ] `dotnet test` once more, and the count in `HACKATHON.md`.
- [ ] The secrets check again, on the final commit and its history.
- [ ] The submission branch rebuilt from the final `main` (plain commit messages; a colleague's email in the import commit
  replaced).

## Done along the way

- [x] Java e Portokalltë 2026: GjirafaMall's plan published to the sandbox; Gjirafa50's held plan decided by the person (1 Nov).
- [x] The demo video recorded and cut (1 Nov).
