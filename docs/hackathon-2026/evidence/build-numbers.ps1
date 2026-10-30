# Builds docs/hackathon-2026/evidence/numbers.json: one ID per figure the one-pager, the financial sheet and the demo show,
# with value, label (measured / estimated / assumed), method, source and the script that reproduces it (plan section 10).
#
# Every figure is read from PricingToolHackathon (the latest backtest run, the AI call log, the sales lines), the test
# database (the Për ty reproduction) or hours.md. To refresh after new data:
#   PricingTool.AgentTools backtest-snapshot        (about a minute: every lever)
#   powershell -ExecutionPolicy Bypass -File docs\hackathon-2026\evidence\build-numbers.ps1
# The calculator's own results (E20-E22) were measured before the hackathon in the main repo; they are quoted with that
# label and source, not recomputed here.

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
function Round0($v) { [math]::Round([decimal]$v, 0) }
$figures = New-Object System.Collections.ArrayList
function Add($id, $name, $value, $unit, $label, $method, $source, $script) {
    [void]$figures.Add([ordered]@{ id = $id; name = $name; value = $value; unit = $unit; label = $label; method = $method; source = $source; script = $script })
}

# --- The latest stored backtest run -------------------------------------------------------------------------------
$snapRow = (Query 'PricingToolHackathon' 'SELECT TOP 1 Id, CreatedUtc, Json FROM ai.BacktestSnapshot ORDER BY Id DESC').Rows[0]
$snap = $snapRow.Json | ConvertFrom-Json
$snapRef = "ai.BacktestSnapshot #$($snapRow.Id) ($(([datetime]$snapRow.CreatedUtc).ToString('yyyy-MM-dd HH:mm')) UTC)"
$leverScript = 'PricingTool.AgentTools backtest-snapshot (or backtest-levers 1 4 --detail); the Backtest page and its CSV'
$names = @{ 1 = 'Loss-sale block'; 2 = 'Per-category floors'; 3 = 'Cost reaction'; 5 = 'Cadence'; 6 = 'Learning (as built)' }
$test = @('2026-08', '2026-09')
function LeverMonthly($layerResult, $lever, $e) {
    $g = @($layerResult.gains | Where-Object { $_.lever -eq $lever -and [decimal]$_.elasticity -eq $e -and $test -contains $_.month })
    if ($g.Count -eq 0) { return 0 }
    return Round0 (($g | Measure-Object -Property gain -Sum).Sum / $test.Count)
}
$ids = @{ 1 = 'E1'; 2 = 'E2'; 3 = 'E3'; 5 = 'E5'; 6 = 'E6' }
$totals = @{ central = 0; low = 0 }
foreach ($lever in 1, 2, 3, 5, 6) {
    $central = 0; $low = 0; $parts = @()
    foreach ($r in $snap.levers) {
        $c = LeverMonthly $r $lever -1.0; $l = LeverMonthly $r $lever -2.0
        $central += $c; $low += $l; $parts += "layer $($r.layerId): $c ($l)"
    }
    $totals.central += $central; $totals.low += $low
    Add $ids[$lever] "Lever $lever, $($names[$lever]): extra GP a month, both KS layers" ([ordered]@{ central = $central; low = $low }) 'EUR/month' 'estimated' `
        "Walk-forward on booked sales lines, Aug-Sep 2026 monthly average; central e -1.0, low e -2.0; $($parts -join '; ')" $snapRef $leverScript
}

# Lever 7: the median gain per campaign day (reallocate, curve as fitted) x the campaign days of the last 12 months.
$l7 = 0; $l7parts = @()
foreach ($c in $snap.campaigns) {
    $perDay = @($c.depthGains | Where-Object { $_.strategy -eq 'reallocate' -and [decimal]$_.bScale -eq 1 -and $_.representative -and -not $_.provisional -and $_.skusScored -gt 0 } |
        ForEach-Object { [decimal]$_.gainPerDay } | Sort-Object)
    if ($perDay.Count -eq 0) { continue }
    $median = if ($perDay.Count % 2 -eq 1) { $perDay[[int][math]::Floor($perDay.Count / 2)] } else { ($perDay[$perDay.Count / 2 - 1] + $perDay[$perDay.Count / 2]) / 2 }
    $yearAgo = (Get-Date).AddDays(-365)
    $days = (@($c.replays | Where-Object { [datetime]$_.start -ge $yearAgo }) | Measure-Object -Property days -Sum).Sum +
            (@($c.depthGains | Where-Object { $_.provisional -and $_.strategy -eq 'reallocate' -and [decimal]$_.bScale -eq 1 }) | Measure-Object -Property days -Sum).Sum
    $monthly = Round0 ($median * $days / 12)
    $l7 += $monthly; $l7parts += "layer $($c.layerId): median $median EUR/day x $days days / 12 = $monthly"
}
$totals.central += $l7; $totals.low += $l7
Add 'E7' 'Lever 7, campaign planning: extra GP a month from per-SKU depth, both KS layers' ([ordered]@{ central = $l7; low = $l7 }) 'EUR/month' 'estimated' `
    "Each finished campaign replayed with the planner's depth per SKU, as deep on average; uplift curve leave-one-out; $($l7parts -join '; ')" $snapRef $leverScript

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
$totals.central += $l4.mid; $totals.low += $l4.low
Add 'E8' 'Framing A: all levers together, extra GP a month' ([ordered]@{ central = $totals.central; low = $totals.low }) 'EUR/month' 'estimated' `
    'E1 + E2 + E3 + E4 (mid / low) + E5 + E6 + E7; each line counted by one lever' $snapRef 'this script'

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
Add 'E15' 'AI cost of the hackathon build and runs (Claude Code subscription, as logged)' ([ordered]@{ liveCalls = $ai[0]; liveUsd = [math]::Round([decimal]$ai[1], 2); testCalls = $aiTest[0]; testUsd = [math]::Round([decimal]$aiTest[1], 2)
        inputTokens = $ai[2]; outputTokens = $ai[3]; cacheReadTokens = $ai[4]; cacheCreationTokens = $ai[5] }) 'calls / USD' 'measured' `
    'total_cost_usd and usage from each headless call''s JSON (Claude Code reports the cost the API would charge; billed through the subscription)' 'ai.AiCallLog (live and test databases)' 'this script (SQL above)'
$rows = Get-Content (Join-Path $repo 'docs\hackathon-2026\hours.md') -Encoding UTF8 | Where-Object { $_ -match '^\| (Fri|Sat|Sun|Mon) ' }
$attended = 0.0; $unattended = 0
foreach ($row in $rows) {
    $cells = $row.Split('|') | ForEach-Object { $_.Trim() }
    $h = $cells[4]
    if ($h -match '^0 \(unattended') { $unattended++ }
    elseif ($h -match '^([0-9]+(\.[0-9]+)?)') { $attended += [double]$Matches[1] }
}
Add 'E16' 'Hackathon hours (hours.md)' ([ordered]@{ attendedHours = $attended; unattendedBlocks = $unattended; rows = $rows.Count }) 'hours' 'measured' `
    'Attended rows: their hours; unattended rows ("0 (unattended, AI)") count as AI cost only (E15)' 'docs/hackathon-2026/hours.md' 'this script'
$testsLine = 'dotnet test PricingTool.sln: 909 passed, 1 skipped (the opt-in real headless call), 31 Oct 00:02'
Add 'E17' 'Automated tests' ([ordered]@{ passed = 909; skipped = 1 }) 'tests' 'measured' $testsLine 'PricingTool.Tests' 'dotnet test PricingTool.sln'

# --- The calculator's own results, measured before the hackathon (quoted, not recomputed here) -------------------------
Add 'E20' "Calculator v0.5's test phase, Kosovo, extra GP a month (framing B's B0)" ([ordered]@{ low = 4000; mid = 10600; high = 13000 }) 'EUR/month' 'measured' `
    'Low: same-weeks control (changed vs unchanged products, one run on one layer, a floor); mid: before/after; high: before/after season-adjusted with 2025' `
    'Main repo analysis before the hackathon (kickoff-plan.md sections 8 and 10)' 'not in this repo'
Add 'E21' 'Below-floor GP forgone, Kosovo (the June/July analysis)' ([ordered]@{ jun = 24400; jul = 15100 }) 'EUR/month' 'measured' 'Lines priced under the hand-set floor, lifted to it, booked COGS' 'Main repo analysis before the hackathon (kickoff-plan.md section 5, lever 2)' 'not in this repo'

$out = [ordered]@{
    generated = (Get-Date).ToString('yyyy-MM-dd HH:mm'); generator = 'docs/hackathon-2026/evidence/build-numbers.ps1'
    note = 'Every figure carries its label. Framing A = the measured levers (E8). Framing B = the rollout of the test phase (E20) plus the levers; the two are never blended.'
    figures = $figures
}
$json = $out | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $here 'numbers.json'), $json, (New-Object System.Text.UTF8Encoding $false))
"numbers.json: $($figures.Count) figures from $snapRef"
