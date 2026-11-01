# The demo video (1:54)

`Pricetool demo cut.mp4`: 1 min 54 s, 1920×1080, silent with a caption bar. Recorded on 1 Nov 2026 on the local app
(http://localhost:5058), publishing to the shop sandbox only; the live shop is never touched. It is submitted with the
entry, not kept in this repository (95 MB). Cut on the laptop with Windows' built-in video APIs; nothing was uploaded.

Sources: the main recording of 1 Nov 13:24–13:31 (7:22), a 29 s clip of the two Run week results and a 17 s clip of a
price list in Excel. Waits for the agents' answers are shortened and typing is sped up (×8); the captions say where.

| Time | What it shows | What it is |
|---|---|---|
| 0:00–0:03 | Title card | Pricing Tool v2, Alpha and Nova, Track A |
| 0:03–0:33 | Chat tab: the person tells @alpha the margin targets for both Kosovo shops, 1–4 Nov (GjirafaMall 19.5 %, Gjirafa50 15 %); Alpha's answer | Alpha records one margin-target rule per shop, says where each shop stands (booked margin, measured) and that Run week starts for Gjirafa50, then GjirafaMall |
| 0:33–1:04 | Campaigns tab: a question to Nova about the Java e Portokalltë campaign, 4–9 Nov; Nova's answer | Nova answers from the 2024 edition (measured, campaign history) and what to expect this year (estimated), and notes that the margin targets overlap the campaign's first day |
| 1:04–1:14 | A follow-up ("Black Friday is at month end, mirror Për ty Oct 2026 with higher margins") and Nova's revised proposal | The person's instruction turned into a proposal |
| 1:14–1:32 | Chat tab: the results of Run week #10005 (Gjirafa50) and #10006 (GjirafaMall), posted when each finished | Alpha's pass, the engine run, the run checker (blocked prices held alone, the rest went out), what reached the sandbox shelf, the expected gross profit a month (estimated), the price list link |
| 1:32–1:39 | Run tab, Gjirafa50: floors and targets by category × price band | The matrix Alpha's pass wrote (each shaded cell is that category's own rule) |
| 1:39–1:54 | Gjirafa50's price list, opened in Excel | `Sku, ProposedPrice, StartDate, EndDate, AlgorithmUsed, NewMargin`: the run's prices on the sandbox shelf, downloaded from the Run tab's "Download price list (CSV)" |

The two runs, from the records (1 Nov): Gjirafa50 run #11112, 32,225 of 33,219 checked prices on the sandbox shelf, all
read back as written, expected €8,291 a month (estimated); GjirafaMall run #11113, 133,957 of 135,255 on the shelf, all
read back, 2 blocked and 16,157 warned prices held, expected €12,477 a month (estimated). Both runs read the live shop's
October deals, which the shop extended to 2 Nov during the morning.

**Left out of the cut:** the waits for the agents' answers; the Run tab while Run week was still running, which showed
this morning's run ("Expected gross profit a month −€42", "€11 this month, −93 % on the same days last month": the
month was a few hours old); an earlier recording of 31 Oct on an older version of the app.

**Not in the video:** GjirafaMall's category × band matrix (only Gjirafa50's is shown), the cost watcher, the kill switch
and autonomy switch, the evidence file and the financial sheet. The video has no sound.
