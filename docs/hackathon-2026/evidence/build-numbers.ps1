# Builds docs/hackathon-2026/evidence/numbers.json: one ID per figure the one-pager, the financial sheet and the demo show,
# with value, label (measured / estimated / assumed), method, source and the script that reproduces it (plan section 10).
#
# Every figure is read from PricingToolHackathon (the latest backtest run, the AI call log, the sales lines), the test
# database (the Për ty reproduction) or hours.md. To refresh after new data:
#   PricingTool.AgentTools backtest-snapshot        (about a minute: every lever)
#   powershell -ExecutionPolicy Bypass -File docs\hackathon-2026\evidence\build-numbers.ps1
# The calculator's test phase (E20) and the below-floor loss (E21) are recomputed from this repo's data (e20-test-phase.sql,
# e21-by-cause.sql); only E20's high case is quoted from the main repo, with that label and source.

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path

function Query($db, $sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\mssqllocaldb;Database=$db;Integrated Security=true;TrustServerCertificate=true"
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
        $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return ,$t
    } finally { $cn.Close() }
}
function QuerySets($db, $sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\mssqllocaldb;Database=$db;Integrated Security=true;TrustServerCertificate=true"
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
        $ds = New-Object System.Data.DataSet; [void](New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds); return ,$ds.Tables
    } finally { $cn.Close() }
}
function Round0($v) { [math]::Round([decimal]$v, 0) }
$figures = New-Object System.Collections.ArrayList
function Add($id, $name, $value, $unit, $label, $method, $source, $script) {
    [void]$figures.Add([ordered]@{ id = $id; name = $name; value = $value; unit = $unit; label = $label; method = $method; source = $source; script = $script })
}

# --- The latest stored backtest run -------------------------------------------------------------------------------
$snapRow = (Query 'PricingToolHackathon' 'SELECT TOP 1 Id, CreatedUtc, Json FROM ai.BacktestSnapshot ORDER BY Id DESC').Rows[0]
$snap = $snapRow.Json | ConvertFrom-Json
$snapRef = "ai.BacktestSnapshot #$($snapRow.Id) ($(([datetime]$snapRow.CreatedUtc).ToString('yyyy-MM-dd HH:mm')) UTC)"
# The stored run itself, committed beside numbers.json (aggregates and campaign names only: no SKU, order or person).
$snapOut = [ordered]@{ snapshot = $snapRef; note = 'Every lever per layer, month and elasticity case; the campaign replays; lever 4''s months and effects. Estimated except the measured inputs each section names (backtest-results.md).'; data = $snap }
[System.IO.File]::WriteAllText((Join-Path $here 'backtest-snapshot.json'), ($snapOut | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
$leverScript = 'PricingTool.AgentTools backtest-snapshot (or backtest-levers 1 4 --detail); the Backtest page and its CSV'
$names = @{ 1 = 'Loss-sale block'; 2 = 'Per-category floors'; 3 = 'Cost reaction'; 5 = 'Cadence'; 6 = 'Learning (as built)' }
$test = @('2026-08', '2026-09')
# Every lever on the calculator's era, Aug-Sep 2026: framing A's baseline is today's process, the calculator with its pushes
# (a 12-month window tried on 31 Oct scored levers 1 and 3 against months before the calculator, a baseline that no longer
# exists). Lever 3's low case leaves out August, one cost event (backtest-results.md), so it isn't counted every month.
function MonthsFor($lever, $case) { if ($lever -eq 3 -and $case -eq 'low') { return @('2026-09') } else { return $test } }
function LeverMonthly($layerResult, $lever, $e, $case) {
    $months = MonthsFor $lever $case
    $g = @($layerResult.gains | Where-Object { $_.lever -eq $lever -and [decimal]$_.elasticity -eq $e -and $months -contains $_.month })
    if ($g.Count -eq 0) { return 0 }
    return Round0 (($g | Measure-Object -Property gain -Sum).Sum / $months.Count)
}
$ids = @{ 1 = 'E1'; 2 = 'E2'; 3 = 'E3'; 5 = 'E5'; 6 = 'E6' }
$totals = @{ high = 0; central = 0; low = 0 }
foreach ($lever in 1, 2, 3, 5, 6) {
    $high = 0; $central = 0; $low = 0; $parts = @()
    foreach ($r in $snap.levers) {
        $h = LeverMonthly $r $lever -0.5 'high'; $c = LeverMonthly $r $lever -1.0 'central'; $l = LeverMonthly $r $lever -2.0 'low'
        $high += $h; $central += $c; $low += $l; $parts += "layer $($r.layerId): $c ($l / $h)"
    }
    $totals.high += $high; $totals.central += $central; $totals.low += $low
    Add $ids[$lever] "Lever $lever, $($names[$lever]): extra GP a month, both KS layers" ([ordered]@{ central = $central; low = $low; high = $high }) 'EUR/month' 'estimated' `
        "Walk-forward on booked sales lines, monthly average over Aug-Sep 2026 (the calculator's era)$(if ($lever -eq 3) { '; the low case September only (August was one cost event)' }); central e -1.0, low e -2.0, high e -0.5 (elasticity assumed); central (low / high) $($parts -join '; ')" $snapRef $leverScript
}

# Lever 7: the median gain per campaign day (reallocate) x the campaign days of the last 12 months; the uplift curve as fitted
# (central), at half its slope (low) and at twice it (high), the sensitivity the replay stores.
function Lever7($scale) {
    $total = 0; $parts = @()
    foreach ($c in $snap.campaigns) {
        $perDay = @($c.depthGains | Where-Object { $_.strategy -eq 'reallocate' -and [decimal]$_.bScale -eq $scale -and $_.representative -and -not $_.provisional -and $_.skusScored -gt 0 } |
            ForEach-Object { [decimal]$_.gainPerDay } | Sort-Object)
        if ($perDay.Count -eq 0) { continue }
        $median = if ($perDay.Count % 2 -eq 1) { $perDay[[int][math]::Floor($perDay.Count / 2)] } else { ($perDay[$perDay.Count / 2 - 1] + $perDay[$perDay.Count / 2]) / 2 }
        $yearAgo = (Get-Date).AddDays(-365)
        $days = (@($c.replays | Where-Object { [datetime]$_.start -ge $yearAgo }) | Measure-Object -Property days -Sum).Sum +
                (@($c.depthGains | Where-Object { $_.provisional -and $_.strategy -eq 'reallocate' -and [decimal]$_.bScale -eq $scale }) | Measure-Object -Property days -Sum).Sum
        $monthly = Round0 ($median * $days / 12)
        $total += $monthly; $parts += "layer $($c.layerId): median $([math]::Round($median, 0)) EUR/day x $days days / 12 = $monthly"
    }
    return @{ total = $total; parts = $parts }
}
$l7 = [ordered]@{ central = Lever7 1; low = Lever7 0.5; high = Lever7 2 }
$totals.high += $l7.high.total; $totals.central += $l7.central.total; $totals.low += $l7.low.total
Add 'E7' 'Lever 7, campaign planning: extra GP a month from per-SKU depth, both KS layers' ([ordered]@{ central = $l7.central.total; low = $l7.low.total; high = $l7.high.total }) 'EUR/month' 'estimated' `
    "Each finished campaign replayed with the planner's depth per SKU, as deep on average; uplift curve leave-one-out, as fitted (central), at half its slope (low) and at twice it (high); central: $($l7.central.parts -join '; ')" $snapRef $leverScript

# Lever 4: the Kosovo effect on the MK/AL months without a push.
$l4 = [ordered]@{}
foreach ($case in 'low', 'mid', 'high') {
    $g = @($snap.coverage | Where-Object { $_.case -eq $case -and $test -contains $_.month })
    $l4[$case] = Round0 (($g | Measure-Object -Property gain -Sum).Sum / $test.Count)
}
$effects = ($snap.coverageEffects | ForEach-Object { "$($_.case) $([math]::Round(100 * [decimal]$_.gainPerMonth / [decimal]$_.ksGpPerMonth, 1)) %" }) -join ', '
Add 'E4' 'Lever 4, coverage MK/AL: extra GP a month on months without a push' $l4 'EUR/month' 'estimated' `
    "The calculator's measured Kosovo gain as a share of Kosovo GP ($effects) x MK/AL tool-scope GP (hk.SalesLinesMkAl, EUR at the load day's shop rate) on months with no push of their own (hk.PushDatesMkAl); Aug-Sep average" `
    $snapRef 'PricingTool.AgentTools backtest-coverage'
$totals.high += $l4.high; $totals.central += $l4.mid; $totals.low += $l4.low
Add 'E8' 'Framing A: all levers together, extra GP a month' ([ordered]@{ central = $totals.central; low = $totals.low; high = $totals.high }) 'EUR/month' 'estimated' `
    'E1 + E2 + E3 + E4 (mid / low / high) + E5 + E6 + E7; each line counted by one lever; the cases differ by elasticity (central e -1, low e -2, high e -0.5) and, for lever 4, by the calculator test-phase case' $snapRef 'this script'

# --- The planner's calibration and the Për ty reproduction -----------------------------------------------------------
$tails = @($snap.campaigns | Where-Object { $_.layerId -eq 1 } | ForEach-Object { $_.replays } | Where-Object { $_.usable } |
    ForEach-Object { [decimal]$_.tailSharePct } | Sort-Object)
$tailMedian = if ($tails.Count % 2 -eq 1) { $tails[[int][math]::Floor($tails.Count / 2)] } else { ($tails[$tails.Count / 2 - 1] + $tails[$tails.Count / 2]) / 2 }
Add 'E9' "Share of a GjirafaMall campaign's GP from SKUs with no sales in the weeks before (median)" $tailMedian 'percent' 'measured' `
    "Per usable campaign (representative, enough normal days before it): GP of SKUs with no normal-day sales in the planner's rate window over the campaign's GP; median of $($tails.Count)" $snapRef 'PricingTool.AgentTools backtest-campaigns 1'
$mall = $snap.campaigns | Where-Object { $_.layerId -eq 1 }
Add 'E10' "Planner's estimate vs what campaigns made, GjirafaMall" ([ordered]@{ leaveOneOutSeenType = $(if ($mall.seenType) { $mall.seenType.factor } else { $null }); inSample = $mall.inSample.factor; inSampleLow = $mall.inSample.low; inSampleHigh = $mall.inSample.high }) `
    'actual GP / planned GP' 'estimated' "Leave-one-out needs 4 usable replays of the case (a type with an event lift from another window); null = not enough, plans judge on the arithmetic. In-sample shown for comparison only." $snapRef 'PricingTool.AgentTools backtest-campaigns 1'

$plan = (Query 'PricingToolHackathonTest' "SELECT TOP 1 p.Id, p.Strategy, p.ExpectedMarginPct, p.ExpectedGrossProfit, p.AverageDepthPct FROM ai.CampaignPlan p JOIN ai.CampaignBrief b ON b.Id = p.BriefId WHERE b.Name LIKE N'P%r ty, Oct 2026 (reproduction test%' ORDER BY p.Id DESC").Rows[0]
$ourBase = (Query 'PricingToolHackathon' @"
SELECT CAST(100.0 * SUM(s.GrossRevenue - s.Qty * s.UnitCostInclVat) / SUM(s.GrossRevenue) AS decimal(9,1)) FROM agent.SalesLines s
WHERE s.LayerId = 1 AND s.SaleDate >= '2026-08-26' AND s.SaleDate < '2026-09-23' AND s.Qty > 0 AND s.UnitCostInclVat > 0 AND ISNULL(s.IsGj50Listed, 0) = 0
  AND ISNULL(s.MainGroup, N'Others') NOT IN (N'Sport', N'Vegla Pune')
  AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = 1 AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
"@).Rows[0][0]
$handBase = (Query 'PricingToolHackathon' "SELECT CAST(100.0 * SUM(GrossProfit) / SUM(NetRevenue) AS decimal(9,1)) FROM agent.SalesLines WHERE LayerId = 1 AND SaleDate >= '2026-09-06' AND SaleDate < '2026-10-07' AND ISNULL(IsGj50Listed, 0) = 0").Rows[0][0]
$m = [decimal]$plan.ExpectedMarginPct
Add 'E11' 'Për ty reproduction test (agents alone, data cut off at 7 Oct)' ([ordered]@{
        plan = $plan.Id; strategy = $plan.Strategy; expectedMarginPct = $m; handPlanForecastPct = 15.1; marginWithin = [math]::Abs($m - 15.1) -le 0.5
        keptPctOurBase = Round0 (100 * $m / [decimal]$ourBase); ourBasePct = $ourBase; keptPctHandBase = Round0 (100 * $m / [decimal]$handBase); handBasePct = $handBase
        handBaseNotedPct = 20.5; requiredKeptPct = '70-80'; verdict = 'FAIL (by one point on the kept share)' }) 'mixed' 'estimated' `
    'Expected margin of the plan the agents chose vs the hand plan of 6 Oct; kept share = expected margin / normal margin of Mall''s own SKUs, on our base (rate window, non-holdout) and the hand plan''s (booked, 6 Sep - 6 Oct)' `
    'PricingToolHackathonTest ai.CampaignPlan; agent.SalesLines' 'PricingTool.AgentTools reproduce-per-ty --test (two model calls)'
$actual = (Query 'PricingToolHackathon' "SELECT SUM(GrossProfit), SUM(NetRevenue) FROM agent.SalesLines WHERE LayerId = 1 AND SaleDate >= '2026-10-14' AND SaleDate < '2026-10-19' AND ISNULL(IsGj50Listed, 0) = 0").Rows[0]
Add 'E12' "What Për ty Oct 2026 made on GjirafaMall's own SKUs (scores the hand plan's 15.1 % forecast)" ([ordered]@{ gp = Round0 $actual[0]; marginPct = [math]::Round(100 * [decimal]$actual[0] / [decimal]$actual[1], 1) }) `
    'EUR / percent' 'measured' 'Booked sales lines 14-18 Oct 2026, not Gjirafa50-listed; provisional (cancellations post for two weeks)' 'agent.SalesLines' 'this script (SQL above)'

# --- Kosovo bases --------------------------------------------------------------------------------------------------
$ks = (Query 'PricingToolHackathon' "SELECT SUM(GrossProfit) / 2 FROM agent.SalesLines WHERE LayerId IN (1, 4) AND SaleDate >= '2026-08-01' AND SaleDate < '2026-10-01'").Rows[0][0]
Add 'E13' 'Kosovo gross profit a month, both layers, Aug-Sep 2026' (Round0 $ks) 'EUR/month' 'measured' 'Booked sales lines, every day, tool scope (no outlet, Apple, Starlink)' 'agent.SalesLines' 'this script (SQL above)'
$cd = (Query 'PricingToolHackathon' "SELECT -SUM(GrossProfit) / 12 FROM agent.SalesLines WHERE LayerId IN (1, 4) AND StockClass = 'CrossDock' AND GrossProfit < 0 AND SaleDate >= DATEADD(day, -365, '2026-10-16') AND SaleDate < '2026-10-16'").Rows[0][0]
Add 'E14' 'Cross-dock lines sold under booked cost, a month (last 12 months, KS)' (Round0 $cd) 'EUR/month' 'measured' 'Negative GP of cross-dock lines, 12 months ending 16 Oct 2026 (the newest two weeks left out), both layers' 'agent.SalesLines' 'this script (SQL above)'

# --- AI cost and hours ---------------------------------------------------------------------------------------------
$ai = (Query 'PricingToolHackathon' 'SELECT COUNT(*), SUM(CostUsd), SUM(InputTokens), SUM(OutputTokens), SUM(CacheReadTokens), SUM(CacheCreationTokens) FROM ai.AiCallLog').Rows[0]
$aiTest = (Query 'PricingToolHackathonTest' 'SELECT COUNT(*), ISNULL(SUM(CostUsd), 0) FROM ai.AiCallLog').Rows[0]
Add 'E15' 'Agents'' runtime Claude calls (Alpha, Nova, the run checker, the chat), as logged' ([ordered]@{ liveCalls = $ai[0]; liveUsd = [math]::Round([decimal]$ai[1], 2); testCalls = $aiTest[0]; testUsd = [math]::Round([decimal]$aiTest[1], 2)
        inputTokens = $ai[2]; outputTokens = $ai[3]; cacheReadTokens = $ai[4]; cacheCreationTokens = $ai[5] }) 'calls / USD' 'measured' `
    'total_cost_usd and usage from each headless call''s JSON (Claude Code reports the cost the API would charge; billed through the subscription)' 'ai.AiCallLog (live and test databases)' 'this script (SQL above)'
$kindSql = @"
SELECT k, COUNT(*) AS n, CAST(AVG(CostUsd) AS decimal(9,4)) AS avgUsd FROM (
  SELECT CostUsd, CASE WHEN Purpose LIKE 'floor-target%' THEN 'alphaWeeklyPass' WHEN Purpose LIKE 'campaign-briefs%' THEN 'novaWeeklyPass'
    WHEN Purpose LIKE 'run-check%' THEN 'runCheck' WHEN Purpose LIKE 'campaign-plan%' OR Purpose LIKE 'campaign-review%' THEN 'planningCall'
    WHEN Purpose LIKE 'chat%' OR Purpose LIKE 'ask%' THEN 'chat' END AS k FROM ai.AiCallLog) x
WHERE k IS NOT NULL GROUP BY k ORDER BY k
"@
$kinds = [ordered]@{}
foreach ($r in (Query 'PricingToolHackathon' $kindSql).Rows) { $kinds[$r.k] = [ordered]@{ calls = $r.n; avgUsd = [decimal]$r.avgUsd } }
Add 'E19' 'Cost of one agent call by kind (live database)' $kinds 'USD per call' 'measured' `
    'Average total_cost_usd per logged attempt, failed attempts included; kinds by Purpose (floor-target, campaign-briefs, run-check, campaign-plan/-review, chat and ask); the isolation probes left out' `
    'ai.AiCallLog' 'this script (SQL above)'
$rows = Get-Content (Join-Path $repo 'docs\hackathon-2026\hours.md') -Encoding UTF8 | Where-Object { $_ -match '^\| (Fri|Sat|Sun|Mon) ' }
$attended = 0.0; $unattended = 0
foreach ($row in $rows) {
    $cells = $row.Split('|') | ForEach-Object { $_.Trim() }
    $h = $cells[4]
    if ($h -match '^0 \(unattended') { $unattended++ }
    elseif ($h -match '^([0-9]+(\.[0-9]+)?)') { $attended += [double]$Matches[1] }
}
$usage = Get-Content (Join-Path $here 'ai-usage.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Add 'E18' 'The build sessions'' own AI usage (the two Claude Code sessions that built this)' ([ordered]@{
        sinceKickoffUsd = $usage.sinceKickoff.usdApiEquivalent; sinceKickoffResponses = $usage.sinceKickoff.responses
        unattendedBlocksUsd = $usage.unattendedBlocks.usdApiEquivalent; unattendedBlocksResponses = $usage.unattendedBlocks.responses }) 'USD (API-equivalent)' `
    'measured tokens; API-equivalent cost (the actual spend is the flat Claude subscription)' `
    "Usage fields of the session transcripts, each response once, priced per model at list prices ($($usage.prices)); blocks in CET, transcripts in UTC" `
    'Claude Code session transcripts (aggregates in docs/hackathon-2026/evidence/ai-usage.json)' 'python -I docs/hackathon-2026/evidence/build_ai_usage.py <transcripts>'
Add 'E16' 'Hackathon hours (hours.md)' ([ordered]@{ attendedHours = $attended; unattendedBlocks = $unattended; rows = $rows.Count }) 'hours' 'measured (attended rows only; the person''s time inside unattended blocks is not timed)' `
    'Attended rows: their hours; unattended rows ("0 (unattended, AI)") count as AI cost only (E18)' 'docs/hackathon-2026/hours.md' 'this script'
$testsLine = 'dotnet test PricingTool.sln: 918 passed, 1 skipped (the opt-in real headless call), 31 Oct about 01:30'
Add 'E17' 'Automated tests' ([ordered]@{ passed = 918; skipped = 1 }) 'tests' 'measured' $testsLine 'PricingTool.Tests' 'dotnet test PricingTool.sln'

# --- The calculator's own results ----------------------------------------------------------------------------------------
# E20 low and mid recomputed from PricingToolHackathon (the deployed tool's approvals and the booked sales lines); the
# high case's 2025 season adjustment is quoted from the main repo.
$e20File = Join-Path $here 'e20-test-phase.sql'
$e20Sets = QuerySets 'PricingToolHackathon' (Get-Content -Raw -Encoding UTF8 $e20File)
$byMonth = @{}; foreach ($r in $e20Sets[0].Rows) { $byMonth[$r.Month] = $r }
$ctl = @{}; foreach ($r in $e20Sets[1].Rows) { if ($r.Grp -eq 'Changed total') { $ctl[[int]$r.ApprovalId] = $r } }
$mallKept = Round0 $ctl[25].VsKeptPerMonth; $mallNotIn = Round0 $ctl[25].VsNotInApprovalPerMonth; $gj50NotIn = Round0 $ctl[1].VsNotInApprovalPerMonth
$aug = Round0 $byMonth['2026-08'].Total; $sep = Round0 $byMonth['2026-09'].Total
Add 'E20' "Calculator v0.5's test phase, Kosovo, extra GP a month (framing B's B0)" ([ordered]@{ low = $mallKept + $gj50NotIn; mid = Round0 $byMonth['Aug-Sep average'].Total; high = 13000 }) 'EUR/month' `
    ([ordered]@{ low = 'measured, same-weeks control, one run per layer, the stricter control on Mall'; mid = 'measured before/after, not controlled'; high = 'estimated: before/after season-adjusted with 2025, not controlled; quoted from the main repo' }) `
    "Low: changed vs unchanged products in the same weeks, one run per KS layer: Mall #25 (3 Sep) vs the products it kept $mallKept + Gj50 #1 (21 Jul; it changed every product it held) vs products not in the approval $gj50NotIn (with Mall's other control, vs not in the approval, $mallNotIn + $gj50NotIn = $($mallNotIn + $gj50NotIn)); mid: before/after on the approvals, SKU-deduplicated spans (Aug $aug, Sep $sep; their average); high: the same, season-adjusted with 2025 (main repo, 27 Oct, not recomputed). The layer-level year-over-year check does not support the uncontrolled two on GP (margin up about 2 pt, revenue lagging 2025''s seasonal growth)" `
    'PricingTool.Approvals / ApprovalItems (copied from the deployed tool DB) and hk.SalesLines, booked COGS' 'docs/hackathon-2026/evidence/e20-test-phase.sql (two result sets; this script runs it)'
# E21 from e21-by-cause.sql: the same rule, floors and scope as `backtest-below-floor` (its totals tie to it to the euro),
# each line with the first cause that fits. Monthly totals (normal days = causes 1-7, campaign days = cause 0), and the
# calculator's era (Aug-Sep 2026) split by cause, monthly average.
$e21Sets = QuerySets 'PricingToolHackathon' (Get-Content -Raw -Encoding UTF8 (Join-Path $here 'e21-by-cause.sql'))
function N0($v) { if ($v -is [DBNull] -or $null -eq $v) { 0 } else { [decimal]$v } }
$e21Months = [ordered]@{}
foreach ($m in ($e21Sets[0].Rows | Group-Object Month | Sort-Object Name)) {
    $campaign = ($m.Group | Where-Object { $_.Cause -like '0 *' } | ForEach-Object { N0 $_.Forgone } | Measure-Object -Sum).Sum
    $normal = ($m.Group | Where-Object { $_.Cause -notlike '0 *' } | ForEach-Object { N0 $_.Forgone } | Measure-Object -Sum).Sum
    $e21Months[$m.Name] = [ordered]@{ normalDays = Round0 $normal; campaignDays = Round0 $campaign }
}
$lanes = @{ '1' = 'allowed below the floor (dead-stock lane)'; '2' = 'GjirafaMall SKU priced by Gjirafa50 (shared SKUs: v2 moves it on Gjirafa50)'; '3' = 'not a price (order-level voucher)'
            '4' = 'lever 1 (loss-sale block)'; '5' = 'lever 5 (no calculator price live that day; the backtest can''t score it)'; '6' = 'team deals (superseding is a person''s call)'
            '7' = 'lever 3 / cost basis (at the calculator''s price, under the floor on booked cost)' }
$byCause = [ordered]@{}
foreach ($c in ($e21Sets[0].Rows | Where-Object { $_.Month -in '2026-08', '2026-09' -and $_.Cause -notlike '0 *' } | Group-Object Cause | Sort-Object Name)) {
    $mall = Round0 ((($c.Group | ForEach-Object { N0 $_.MallKs }) | Measure-Object -Sum).Sum / 2)
    $gj50 = Round0 ((($c.Group | ForEach-Object { N0 $_.Gj50Ks }) | Measure-Object -Sum).Sum / 2)
    $key = $c.Name.Substring(0, 1)
    $byCause[$c.Name] = [ordered]@{ mallKs = $mall; gj50Ks = $gj50; both = $mall + $gj50; lane = $lanes[$key] }
}
$e21Total = ($byCause.Values | ForEach-Object { $_.both } | Measure-Object -Sum).Sum
$notAPrice = ($byCause.GetEnumerator() | Where-Object { $_.Key -like '1 *' -or $_.Key -like '3 *' } | ForEach-Object { $_.Value.both } | Measure-Object -Sum).Sum
$shared = $e21Sets[1].Rows | Where-Object { $_.Where_ -eq "below Gj50's floor too" } | Select-Object -First 1
$raisedToday = @{}
foreach ($r in ($e21Sets[2].Rows | Where-Object { $_.Today -eq 'today: raise' })) { $raisedToday[[int]$r.LayerId] = (N0 $raisedToday[[int]$r.LayerId]) + (N0 $r.ForgonePerMonth) }
Add 'E21' 'Below-floor GP forgone, Kosovo: by month (Jun-Oct 2026) and, in the calculator''s era (Aug-Sep 2026), by cause' ([ordered]@{ months = $e21Months; augSepByCause = $byCause; augSepTotal = $e21Total }) 'EUR/month' 'measured (October provisional: late cancellations post for two weeks)' `
    ("Each booked line whose margin on booked COGS is under its band's hand-set floor of that day (rebuilt from the band edits in the audit log) lifted to the floor at the same units, both KS layers, tool scope (no outlet, Apple, Starlink; the June/July analysis in the main repo, Jun 24,400 and Jul 15,100, included them). " +
     "Each line gets the first cause that fits: 0 campaign day; 1 its SKU in a live markdown batch; 2 a GjirafaMall SKU listed on Gjirafa50, which takes Gjirafa50's price; 3 above the floor before an order-level voucher; 4 cross-dock sold under its booked cost; 5 no calculator price live for the SKU that day; 6 sold more than 2 % under the calculator's live price; 7 at the calculator's price but under the floor on booked cost. " +
     "Aug-Sep: $(Round0 $notAPrice) a month is allowed or not a price (dead stock, vouchers); the rest sits in the lanes of levers 1, 3 and 5, partly on Gjirafa50's side of shared SKUs (cause 2: $(Round0 (N0 $shared.ForgoneVsGj50FloorPerMonth)) a month under Gjirafa50's own floor, $(Round0 (N0 $shared.UnderCostPerMonth)) under cost). " +
     "Cause 5 as context only: on SKUs today's engine run raises, about $(Round0 ((N0 $raisedToday[1]) + (N0 $raisedToday[4]))) a month (Mall $(Round0 (N0 $raisedToday[1])), Gj50 $(Round0 (N0 $raisedToday[4]))), a proxy (August's runs weren't kept), not in the sheet. The totals equal PricingTool.AgentTools backtest-below-floor 1 4.") `
    'hk.SalesLines, hk.SalesLineDiscountsKs, PricingTool.AuditLog, PricingTool.Approvals / ApprovalItems, band config' 'docs/hackathon-2026/evidence/e21-by-cause.sql (three result sets; this script runs it); totals also by PricingTool.AgentTools backtest-below-floor 1 4'

# E22: the cost watcher on its first real cost refresh, from the ai tables (aggregates only).
$cw = (QuerySets 'PricingToolHackathon' (Get-Content -Raw -Encoding UTF8 (Join-Path $here 'e22-cost-watch.sql')))[0].Rows[0]
Add 'E22' 'Cost watcher on the first real cost refresh (31 Oct 2026, both KS layers)' ([ordered]@{
        distinctMoves = $cw.DistinctMoves; rises = $cw.Rises; falls = $cw.Falls; risesStillAboveTheFloor = $cw.RisesStillAboveTheFloor; raised = $cw.Raised
        heldTeamDeal = $cw.HeldTeamDeal; heldCostAboveShelf = $cw.HeldCostAboveShelf; heldDataError = $cw.HeldDataError
        observations = $cw.Observations; repeatsOfTheSameMove = $cw.RepeatsOfTheSameMove
        minutesAfterRefresh = [ordered]@{ first = [math]::Round($cw.FirstSecondsAfterRefresh / 60.0, 1); last = [math]::Round($cw.LastSecondsAfterRefresh / 60.0, 1) } }) 'SKUs / minutes' 'measured' `
    ("The replica's costs refreshed that morning; the feed read the costs of the SKUs no other rule owns that sit near their floor, and $($cw.DistinctMoves) had moved at least 0.5 % from the engine run's. " +
     "No rise was raised: $($cw.RisesStillAboveTheFloor) of the $($cw.Rises) rises left the live price above its floor, $($cw.HeldTeamDeal) put it under but the price was a cheaper team deal (held for a person: the publisher leaves those alone), and $($cw.HeldCostAboveShelf) had a floor above max(anchor, current) (held: the regular price needs changing); no jump looked like a data error. " +
     "The rebuild spanned two polls: $($cw.RepeatsOfTheSameMove) of the $($cw.Observations) observations were the same moves seen again (each held a second time); the feed now skips a cost already observed since the run. Ten observations were checked by hand against the engine run (hours.md, 31 Oct).") `
    'ai.CostObservation, ai.AgentDecision' 'docs/hackathon-2026/evidence/e22-cost-watch.sql (this script runs it)'

$out = [ordered]@{
    generated = (Get-Date).ToString('yyyy-MM-dd HH:mm'); generator = 'docs/hackathon-2026/evidence/build-numbers.ps1'
    note = 'Every figure carries its label. Framing A = the measured levers (E8). Framing B = the rollout of the test phase (E20) plus the levers; the two are never blended.'
    figures = $figures
}
$json = $out | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $here 'numbers.json'), $json, (New-Object System.Text.UTF8Encoding $false))
"numbers.json: $($figures.Count) figures from $snapRef"
