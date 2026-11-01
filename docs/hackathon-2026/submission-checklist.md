# Submission checklist

Deadline: **Mon 2 Nov, 08:00** (freeze from 06:00, plan section 6). Kept up to date as items are done; the plan's own
list is section 8. Last updated Sun 1 Nov, 15:01.

## The deliverables

- [x] **Prototype:** this repo, with the README's "Hackathon build" section and `HACKATHON.md` (how to run it, what's new).
- [x] **One-page summary:** `docs/hackathon-2026/one-pager.md` (updated 1 Nov).
- [x] **Financial sheet:** `docs/hackathon-2026/Financial sheet.xlsx`, every line labelled measured / estimated / assumed.
- [x] **2-minute video:** `Pricetool demo cut.mp4` (1:54, silent with captions), in Downloads, submitted as a file (not in
  the repo). What it shows: `docs/hackathon-2026/demo-backup-video.md`.
- [x] **What is mocked or unfinished:** `HACKATHON.md` section 5.
- [x] **Evidence:** `docs/hackathon-2026/evidence/` (`numbers.json`, one script per figure, `evidence.html`).
- [x] **Go-live runbook:** `GO-LIVE.md`.
- [x] **Tests green:** 1,032 passed, 1 skipped (1 Nov), recorded in `HACKATHON.md`.
- [x] **No secrets in tracked files:** the `git grep` check finds only comments and code that handles a password (1 Nov).

## Still to do

- [ ] **The organisers' remote URL** (waiting for it). Nothing can be pushed without it.
- [ ] **Your hours for 1 Nov** in `hours.md` (your estimate, at the end of the day).
- [ ] **Evidence file: 47 of 49 checks match.** E23 (#37: 70.3 % stored, 70 now) and E25 (Oct 2026, provisional: 1.7
  stored, 1.8 now) read the sales lines refreshed on 1 Nov 06:06 UTC. Either update the two stored values and where
  they are cited, or leave them and say why.
- [ ] **Before leaving the laptop overnight:** close heavy apps (ChatGPT, the Power BI gateways). LocalDB froze twice on
  1 Nov when memory ran out, and the 07:00 cycle (Alpha's and Nova's Monday passes, a GjirafaMall publish) runs during
  the freeze.

## The freeze, Mon 06:00–08:00

- [ ] A fresh AI-judge dry run on the final state.
- [ ] `dotnet test` once more, and the count in `HACKATHON.md`.
- [ ] The secrets check again, on the final commit and its history.
- [ ] Rebuild the push branch with plain commit messages from the final `main` (`clean-history` is behind it).
- [ ] Final commit, tag `hackathon-submission`, push to the organisers' remote.
- [ ] Submit before 08:00: the repo link, the one-pager, the financial sheet and the video.

## Done along the way

- [x] Java e Portokalltë 2026: GjirafaMall's plan published to the sandbox; Gjirafa50's held plan decided by the person (1 Nov).
- [x] The demo video recorded and cut (1 Nov).
