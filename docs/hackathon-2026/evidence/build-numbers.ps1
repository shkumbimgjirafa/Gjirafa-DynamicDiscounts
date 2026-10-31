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
function N0($v) { if ($v -is [DBNull] -or $null -eq $v) { 0 } else { [decimal]$v } }
function RunFile($name) { QuerySets 'PricingToolHackathon' (Get-Content -Raw -Encoding UTF8 (Join-Path $here $name)) }

# The Evidence page's live recompute (Prompt 11): per figure, the SQL file it runs and the cells it compares, each with the value
# this script read from the same file. A cell is a result set, a column, optionally the row whose columns equal `where`, and
# optionally `sum` over the rows that match.
$recomputes = @{}
function C($label, $set, $column, $where = $null, $agg = $null) { @{ label = $label; set = $set; column = $column; where = $where; agg = $agg } }
function CellValue($sets, $c) {
    $rows = @($sets[$c.set].Rows)
    if ($c.where) { foreach ($k in $c.where.Keys) { $rows = @($rows | Where-Object { "$($_[$k])" -eq "$($c.where[$k])" }) } }
    if ($c.agg -eq 'sum') { return [math]::Round([decimal](($rows | ForEach-Object { N0 $_[$c.column] } | Measure-Object -Sum).Sum), 2) }
    if ($rows.Count -eq 0) { return $null }
    return [math]::Round((N0 $rows[0][$c.column]), 2)
}
function Recompute($id, $file, $sets, $checks) {
    $list = @(foreach ($c in $checks) {
        $o = [ordered]@{ label = $c.label; set = $c.set; column = $c.column }
        if ($c.where) { $o.where = $c.where }
        if ($c.agg) { $o.agg = $c.agg }
        $o.value = CellValue $sets $c
        $o
    })
    $recomputes[$id] = [ordered]@{ file = $file; checks = $list }
}
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
$names = @{ 1 = 'Loss-sale block'; 2 = 'Per-category floors'; 3 = 'Cost reaction'; 5 = 'Cadence'; 6 = 'Learning (as built)'; 8 = 'Raises reach the shelf' }
$test = @('2026-08', '2026-09')
# Every lever on the calculator's era, Aug-Sep 2026: the baseline is today's process, the calculator with its pushes
# (a 12-month window tried on 31 Oct scored levers 1 and 3 against months before the calculator, a baseline that no longer
# exists). Lever 3's low case leaves out August, one cost event (backtest-results.md), so it isn't counted every month.
function MonthsFor($lever, $case) { if ($lever -eq 3 -and $case -eq 'low') { return @('2026-09') } else { return $test } }
function LeverMonthly($layerResult, $lever, $e, $case) {
    $months = MonthsFor $lever $case
    $g = @($layerResult.gains | Where-Object { $_.lever -eq $lever -and [decimal]$_.elasticity -eq $e -and $months -contains $_.month })
    if ($g.Count -eq 0) { return 0 }
    return Round0 (($g | Measure-Object -Property gain -Sum).Sum / $months.Count)
}
# The part of a lever's gain on lines sold under a team deal (a product discount at a price no approval gave the SKU).
function LeverTeam($layerResult, $lever, $e, $case) {
    $months = MonthsFor $lever $case
    $g = @($layerResult.gains | Where-Object { $_.lever -eq $lever -and [decimal]$_.elasticity -eq $e -and $months -contains $_.month -and $null -ne $_.teamDealGain })
    if ($g.Count -eq 0) { return 0 }
    return Round0 (($g | Measure-Object -Property teamDealGain -Sum).Sum / $months.Count)
}
$ids = @{ 1 = 'E1'; 2 = 'E2'; 3 = 'E3'; 5 = 'E5'; 6 = 'E6' }
$totals = @{ high = 0; central = 0; low = 0 }
$teamMoved = @{ central = 0; before = 0 }
foreach ($lever in 1, 2, 3, 5, 6) {
    $high = 0; $central = 0; $low = 0; $parts = @(); $teamC = 0; $teamL = 0
    foreach ($r in $snap.levers) {
        $h = LeverMonthly $r $lever -0.5 'high'; $c = LeverMonthly $r $lever -1.0 'central'; $l = LeverMonthly $r $lever -2.0 'low'
        $tc = LeverTeam $r $lever -1.0 'central'; $tl = LeverTeam $r $lever -2.0 'low'
        $high += $h; $central += $c; $low += $l; $teamC += $tc; $teamL += $tl; $parts += "layer $($r.layerId): $c ($l / $h), under a team deal $tc"
    }
    # v2 doesn't raise a price a team deal holds (the publisher leaves it to a person), so a raise lever's gain on those lines
    # moves from central and low to high ("if a person supersedes the deal"). Lever 6 undoes raises; it keeps its cases.
    $moved = $lever -ne 6
    $value = [ordered]@{ central = $(if ($moved) { $central - $teamC } else { $central }); low = $(if ($moved) { $low - $teamL } else { $low }); high = $high }
    if ($moved) { $value.underTeamDealCentral = $teamC; $teamMoved.central += $teamC; $teamMoved.before += $central }
    $totals.high += $value.high; $totals.central += $value.central; $totals.low += $value.low
    $teamText = if ($moved -and $central -ne 0) { "; the part on lines sold under a team deal ($teamC of the central $central, $([math]::Round(100.0 * $teamC / $central, 0)) %) is in high only: v2 doesn't raise a price a team deal holds, a person may supersede it" } else { '' }
    Add $ids[$lever] "Lever $lever, $($names[$lever]): extra GP a month, both KS layers" $value 'EUR/month' 'estimated' `
        "Walk-forward on booked sales lines, monthly average over Aug-Sep 2026 (the calculator's era)$(if ($lever -eq 3) { '; the low case September only (August was one cost event)' }); central e -1.0, low e -2.0, high e -0.5 (elasticity assumed)$teamText; central (low / high) before that move $($parts -join '; ')" $snapRef $leverScript
}

# E23: approved raises that didn't reach the shelf in today's process (the CSV push the team imports as discounts).
$e23Sets = RunFile 'raises-not-on-shelf.sql'
Recompute 'E23' 'raises-not-on-shelf.sql' $e23Sets (@(12, 25, 35, 37 | ForEach-Object { C "#$_ units at the old price, %" 0 'PctAtOld' @{ ApprovalId = "$_" } }) +
    @((C 'Aug-Sep, a month at e -1: what v2 removes' 2 'EurPerMonthEm1' @{ Part = "earlier push still live or regular price not raised (v2's publisher removes it)" }),
      (C 'Aug-Sep, a month at e -1: team deals' 2 'EurPerMonthEm1' @{ Part = 'team deal (v2 leaves it to a person)' })))
$perApproval = @($e23Sets[0].Rows | ForEach-Object { [ordered]@{ approval = $_.ApprovalId; layer = $_.Layer; days = $_.Days; units = $_.Units; atNewPrice = $_.AtNewPrice
    atOldPrice = $_.AtOldPrice; pctAtOld = $_.PctAtOld; gpForgoneEm1 = $_.GpForgoneEm1; gpForgoneConstantUnits = $_.GpForgoneConstUnits } })
$augSep = [ordered]@{}
foreach ($row in $e23Sets[2].Rows) { $augSep[$(if ($row.Part -like 'team deal*') { 'teamDeal' } else { 'v2Removes' })] = $row.EurPerMonthEm1 }
# October's pace: the part v2 removes (the earlier push still live, the regular price never raised) per day of #35 and #37, a month.
$octRemovable = 0
foreach ($a in 35, 37) {
    $days = ($e23Sets[0].Rows | Where-Object { $_.ApprovalId -eq $a }).Days
    $gp = ($e23Sets[1].Rows | Where-Object { $_.ApprovalId -eq $a -and $_.Holder -ne 'team deal' } | Measure-Object -Property GpForgoneEm1 -Sum).Sum
    $octRemovable += [decimal]$gp / $days * 30.4
}
$octRemovable = Round0 $octRemovable
$pcts = ($perApproval | ForEach-Object { "#$($_.approval) $($_.pctAtOld) %" }) -join ', '
Add 'E23' 'Approved raises that did not reach the shelf in today''s process (KS)' ([ordered]@{ perApproval = $perApproval; augSepPerMonthEm1 = $augSep; octoberV2RemovesPerMonthEm1 = $octRemovable }) 'units / EUR a month' 'measured (units); estimated (GP at e -1)' `
    ("Sales of each approval's raised SKUs (new price over old x 1.02) on its layer, from the day after it went live to the day before the next one did, outside campaign days; at the old price within 1.1 %. Share of units at the old price: $pcts (17-20 % Aug-Sep, 70-84 % in late October, when two push sets overlapped). " +
     "What held the old price, from the live discount at the sold price (hk.DiscountHistoryRaisedKs): the calculator's earlier push still live, or the regular price never raised (v2's publisher removes both), or a team deal (left to a person). " +
     "Aug-Sep, GP forgone a month at e -1: $($augSep.v2Removes) that v2 removes, $($augSep.teamDeal) team deals; October's pace of the part v2 removes: $octRemovable a month.") `
    'PricingTool.ApprovalItems, hk.SalesLines, hk.CampaignWindows, hk.DiscountHistoryRaisedKs' 'docs/hackathon-2026/evidence/raises-not-on-shelf.sql (three result sets; this script runs it)'

# Lever 8, raises reach the shelf (the publisher): E23's part v2 removes, scored in the backtest so a line another lever
# already scores is counted once; central = low (e -1, Aug-Sep), high = October's pace (two push sets overlapped).
$l8 = 0; $l8parts = @()
foreach ($r in $snap.levers) { $c = LeverMonthly $r 8 -1.0 'central'; $l8 += $c; $l8parts += "layer $($r.layerId): $c" }
$l8value = [ordered]@{ central = $l8; low = $l8; high = $octRemovable }
$totals.high += $l8value.high; $totals.central += $l8value.central; $totals.low += $l8value.low
Add 'E24' 'Lever 8, raises reach the shelf (the publisher): extra GP a month, both KS layers' $l8value 'EUR/month' 'estimated' `
    ("Lines of an approved raise sold at the old price because today's process left the calculator's earlier push live or never raised the regular price (E23's rule), priced at the new price; walk-forward in the backtest after levers 1-6, so a line another lever scores is counted once. " +
     "Central = low: the Aug-Sep monthly average at e -1 ($($l8parts -join '; '); E23's whole spans give $($augSep.v2Removes)). High: October's pace ($octRemovable a month, E23), the month two push sets overlapped. It is what v2's publisher adds over the CSV import; team deals stay a person's call.") `
    $snapRef $leverScript

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
Add 'E8' 'All levers together, extra GP a month' ([ordered]@{ central = $totals.central; low = $totals.low; high = $totals.high; underTeamDealCentral = $teamMoved.central }) 'EUR/month' 'estimated' `
    ("E1 + E2 + E3 + E4 (mid / low / high) + E5 + E6 + E7 + E24 (lever 8); each line counted by one lever; the cases differ by elasticity (central e -1, low e -2, high e -0.5), for lever 4 by the calculator test-phase case and for lever 8 by month (high = October's pace). " +
     "The raise levers' gain on lines sold under a team deal, $($teamMoved.central) a month of their central $($teamMoved.before) ($([math]::Round(100.0 * $teamMoved.central / [math]::Max(1, $teamMoved.before), 0)) %), is in high only.") $snapRef 'this script'

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
$e12Sets = RunFile 'e12-per-ty-actual.sql'
$actual = $e12Sets[0].Rows[0]
Add 'E12' "What Për ty Oct 2026 made on GjirafaMall's own SKUs (scores the hand plan's 15.1 % forecast)" ([ordered]@{ gp = Round0 $actual.Gp; marginPct = [decimal]$actual.MarginPct }) `
    'EUR / percent' 'measured' 'Booked sales lines 14-18 Oct 2026, not Gjirafa50-listed; provisional (cancellations post for two weeks)' 'agent.SalesLines' 'docs/hackathon-2026/evidence/e12-per-ty-actual.sql (this script runs it)'
Recompute 'E12' 'e12-per-ty-actual.sql' $e12Sets @((C 'gross profit' 0 'Gp'), (C 'margin %' 0 'MarginPct'))

# --- Kosovo bases --------------------------------------------------------------------------------------------------
$e13Sets = RunFile 'e13-ks-gross-profit.sql'
Add 'E13' 'Kosovo gross profit a month, both layers, Aug-Sep 2026' (Round0 $e13Sets[0].Rows[0].KsGpPerMonth) 'EUR/month' 'measured' 'Booked sales lines, every day, tool scope (no outlet, Apple, Starlink)' 'agent.SalesLines' 'docs/hackathon-2026/evidence/e13-ks-gross-profit.sql (this script runs it)'
Recompute 'E13' 'e13-ks-gross-profit.sql' $e13Sets @((C 'gross profit a month' 0 'KsGpPerMonth'))
$e14Sets = RunFile 'e14-cross-dock-loss.sql'
Add 'E14' 'Cross-dock lines sold under booked cost, a month (last 12 months, KS)' (Round0 $e14Sets[0].Rows[0].CrossDockLossPerMonth) 'EUR/month' 'measured' 'Negative GP of cross-dock lines, 12 months ending 16 Oct 2026 (the newest two weeks left out), both layers' 'agent.SalesLines' 'docs/hackathon-2026/evidence/e14-cross-dock-loss.sql (this script runs it)'
Recompute 'E14' 'e14-cross-dock-loss.sql' $e14Sets @((C 'cross-dock loss a month' 0 'CrossDockLossPerMonth'))

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
$testsLine = 'dotnet test: 1017 passed, 1 skipped (the opt-in real headless call), 31 Oct 18:52'
Add 'E17' 'Automated tests' ([ordered]@{ passed = 1017; skipped = 1 }) 'tests' 'measured' $testsLine 'PricingTool.Tests' 'dotnet test PricingTool.sln'

# --- The calculator's own results ----------------------------------------------------------------------------------------
# E20 low and mid recomputed from PricingToolHackathon (the deployed tool's approvals and the booked sales lines); the
# high case's 2025 season adjustment is quoted from the main repo.
$e20Sets = RunFile 'e20-test-phase.sql'
Recompute 'E20' 'e20-test-phase.sql' $e20Sets @((C 'mid: before/after, the Aug-Sep average' 0 'Total' @{ Month = 'Aug-Sep average' }),
    (C 'low: Mall #25 vs the products it kept, a month' 1 'VsKeptPerMonth' @{ ApprovalId = '25'; Grp = 'Changed total' }),
    (C 'low: Gj50 #1 vs products not in the approval, a month' 1 'VsNotInApprovalPerMonth' @{ ApprovalId = '1'; Grp = 'Changed total' }))
$byMonth = @{}; foreach ($r in $e20Sets[0].Rows) { $byMonth[$r.Month] = $r }
$ctl = @{}; foreach ($r in $e20Sets[1].Rows) { if ($r.Grp -eq 'Changed total') { $ctl[[int]$r.ApprovalId] = $r } }
$mallKept = Round0 $ctl[25].VsKeptPerMonth; $mallNotIn = Round0 $ctl[25].VsNotInApprovalPerMonth; $gj50NotIn = Round0 $ctl[1].VsNotInApprovalPerMonth
$aug = Round0 $byMonth['2026-08'].Total; $sep = Round0 $byMonth['2026-09'].Total
# The claim uses the mid case (the user's decision, 31 Oct); the low case is a robustness check in the evidence;
# the high case is quoted from the main repo and not part of the claim.
$e20Months = [ordered]@{}
foreach ($m in '2026-08', '2026-09') { $e20Months[$m] = [ordered]@{ mallKs = Round0 $byMonth[$m].MallKs; gj50Ks = Round0 $byMonth[$m].Gj50Ks; total = Round0 $byMonth[$m].Total
    byApproval = "$($byMonth[$m].Spans)" } }
Add 'E20' "Calculator v0.5's test phase, Kosovo, extra GP a month (B0)" ([ordered]@{ low = $mallKept + $gj50NotIn; mid = Round0 $byMonth['Aug-Sep average'].Total; high = 13000; byShopAugSepAvg = [ordered]@{ mallKs = Round0 $byMonth['Aug-Sep average'].MallKs; gj50Ks = Round0 $byMonth['Aug-Sep average'].Gj50Ks }
        byMonth = $e20Months; controls = [ordered]@{ mall25VsKept = $mallKept; mall25VsNotInApproval = $mallNotIn; gj50No1VsNotInApproval = $gj50NotIn } }) 'EUR/month' `
    ([ordered]@{ low = 'robustness check: measured, same-weeks control, one run per layer, the stricter control on Mall'; mid = 'measured before/after each approval, not controlled (B0 in the claim)'; high = 'sensitivity: estimated, before/after season-adjusted with 2025, not controlled; quoted from the main repo, not recomputed, not part of the claim' }) `
    "Low: changed vs unchanged products in the same weeks, one run per KS layer: Mall #25 (3 Sep) vs the products it kept $mallKept + Gj50 #1 (21 Jul; it changed every product it held) vs products not in the approval $gj50NotIn (with Mall's other control, vs not in the approval, $mallNotIn + $gj50NotIn = $($mallNotIn + $gj50NotIn)); mid: before/after on the approvals, SKU-deduplicated spans (Aug $aug, Sep $sep; their average); high: the same, season-adjusted with 2025 (main repo, 27 Oct, not recomputed). The layer-level year-over-year check does not support the uncontrolled two on GP (margin up about 2 pt, revenue lagging 2025's seasonal growth)" `
    'PricingTool.Approvals / ApprovalItems (copied from the deployed tool DB) and hk.SalesLines, booked COGS' 'docs/hackathon-2026/evidence/e20-test-phase.sql (two result sets; this script runs it)'
# E21 from e21-by-cause.sql: the same rule, floors and scope as `backtest-below-floor` (its totals tie to it to the euro),
# each line with the first cause that fits. Monthly totals (normal days = causes 1-7, campaign days = cause 0), and the
# calculator's era (Aug-Sep 2026) split by cause, monthly average.
$e21Sets = RunFile 'e21-by-cause.sql'
Recompute 'E21' 'e21-by-cause.sql' $e21Sets @((C 'August, every cause' 0 'Forgone' @{ Month = '2026-08' } 'sum'), (C 'September, every cause' 0 'Forgone' @{ Month = '2026-09' } 'sum'),
    (C 'August, cross-dock under cost (lever 1)' 0 'Forgone' @{ Month = '2026-08'; Cause = '4 cross-dock under cost' }),
    (C 'September, sold under the calculator price (team deals)' 0 'Forgone' @{ Month = '2026-09'; Cause = '6 sold under the calculator price' }))
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
Add 'E21' 'Below-floor GP forgone, Kosovo: by month (Jun-Oct 2026) and, in the calculator''s era (Aug-Sep 2026), by cause' ([ordered]@{ months = $e21Months; augSepByCause = $byCause; augSepTotal = $e21Total }) 'EUR/month' 'estimated from measured lines: the gap to the floor at the same units (October provisional: late cancellations post for two weeks)' `
    ("Each booked line whose margin on booked COGS is under its band's hand-set floor of that day (rebuilt from the band edits in the audit log) lifted to the floor at the same units, both KS layers, tool scope (no outlet, Apple, Starlink; the June/July analysis in the main repo, Jun 24,400 and Jul 15,100, included them). " +
     "Each line gets the first cause that fits: 0 campaign day; 1 its SKU in a live markdown batch; 2 a GjirafaMall SKU listed on Gjirafa50, which takes Gjirafa50's price; 3 above the floor before an order-level voucher; 4 cross-dock sold under its booked cost; 5 no calculator price live for the SKU that day; 6 sold more than 2 % under the calculator's live price; 7 at the calculator's price but under the floor on booked cost. " +
     "Aug-Sep: $(Round0 $notAPrice) a month is allowed or not a price (dead stock, vouchers); the rest sits in the lanes of levers 1, 3 and 5, partly on Gjirafa50's side of shared SKUs (cause 2: $(Round0 (N0 $shared.ForgoneVsGj50FloorPerMonth)) a month under Gjirafa50's own floor, $(Round0 (N0 $shared.UnderCostPerMonth)) under cost). " +
     "Cause 5 as context only: on SKUs today's engine run raises, about $(Round0 ((N0 $raisedToday[1]) + (N0 $raisedToday[4]))) a month (Mall $(Round0 (N0 $raisedToday[1])), Gj50 $(Round0 (N0 $raisedToday[4]))), a proxy (August's runs weren't kept), not in the sheet. The totals equal PricingTool.AgentTools backtest-below-floor 1 4.") `
    'hk.SalesLines, hk.SalesLineDiscountsKs, PricingTool.AuditLog, PricingTool.Approvals / ApprovalItems, band config' 'docs/hackathon-2026/evidence/e21-by-cause.sql (three result sets; this script runs it); totals also by PricingTool.AgentTools backtest-below-floor 1 4'

# E22: the cost watcher on its first real cost refresh, from the ai tables (aggregates only).
$cwSets = RunFile 'e22-cost-watch.sql'
$cw = $cwSets[0].Rows[0]
$hb = $cwSets[1].Rows[0]
Recompute 'E22' 'e22-cost-watch.sql' $cwSets @((C 'distinct moves' 0 'DistinctMoves'), (C 'rises' 0 'Rises'), (C 'rises still above the floor' 0 'RisesStillAboveTheFloor'),
    (C 'raised' 0 'Raised'), (C 'held at a cheaper deal' 0 'HeldTeamDeal'), (C 'held by our own earlier push' 1 'HeldByOwnPush'), (C 'held only by an app-only deal' 1 'AppOnlyOnly'),
    (C 'held by a team deal' 1 'HeldByTeamDeal'))
Add 'E22' 'Cost watcher on the first real cost refresh (31 Oct 2026, both KS layers)' ([ordered]@{
        distinctMoves = $cw.DistinctMoves; rises = $cw.Rises; falls = $cw.Falls; risesStillAboveTheFloor = $cw.RisesStillAboveTheFloor; raised = $cw.Raised
        heldTeamDeal = $cw.HeldTeamDeal
        heldTeamDealBy = [ordered]@{ ownEarlierPush = $hb.HeldByOwnPush; appOnlyOnly = $hb.AppOnlyOnly; teamDeal = $hb.HeldByTeamDeal; noCheaperDealFound = $hb.NoCheaperDealFound }
        heldCostAboveShelf = $cw.HeldCostAboveShelf; heldDataError = $cw.HeldDataError
        observations = $cw.Observations; repeatsOfTheSameMove = $cw.RepeatsOfTheSameMove
        minutesAfterRefresh = [ordered]@{ first = [math]::Round($cw.FirstSecondsAfterRefresh / 60.0, 1); last = [math]::Round($cw.LastSecondsAfterRefresh / 60.0, 1) } }) 'SKUs / minutes' 'measured' `
    ("The replica's costs refreshed that morning; the feed read the costs of the SKUs no other rule owns that sit near their floor, and $($cw.DistinctMoves) had moved at least 0.5 % from the engine run's. " +
     "No rise was raised: $($cw.RisesStillAboveTheFloor) of the $($cw.Rises) rises left the live price above its floor, $($cw.HeldTeamDeal) put it under but the price was a cheaper deal (held for a person: the publisher leaves those alone), and $($cw.HeldCostAboveShelf) had a floor above max(anchor, current) (held: the regular price needs changing); no jump looked like a data error. " +
     "The rebuild spanned two polls: $($cw.RepeatsOfTheSameMove) of the $($cw.Observations) observations were the same moves seen again (each held a second time); the feed now skips a cost already observed since the run. " +
     "What held the $($cw.HeldTeamDeal) (Prompt 10, on the sandbox's discounts at the observation): $($hb.HeldByOwnPush) the calculator's own earlier push (an October set the team imported, carrying a price an approval gave that SKU), $($hb.AppOnlyOnly) only an app-only deal (no longer a hold: the web price is ours, written since 31 Oct), $($hb.HeldByTeamDeal) a real team deal. With the go-live switch on (own pushes superseded), $([int]$hb.HeldByOwnPush + [int]$hb.AppOnlyOnly) of the $($cw.HeldTeamDeal) would have been raised; with it off (the default), $($hb.AppOnlyOnly). Ten observations were checked by hand against the engine run (hours.md, 31 Oct).") `
    'ai.CostObservation, ai.AgentDecision, ai.PublishException, the sandbox discounts, PricingTool.Approvals / ApprovalItems' 'docs/hackathon-2026/evidence/e22-cost-watch.sql (two result sets; this script runs it)'

# E25: the year-over-year margin fingerprint (plan section 5).
$e25Sets = RunFile 'e25-yoy-margin.sql'
$periods = [ordered]@{}; foreach ($r in $e25Sets[1].Rows) { $periods[$r.Period] = [ordered]@{ marginPct = $r.MarginPct; yearBeforePct = $r.YearBeforePct; deltaPp = $r.DeltaPp } }
$monthsDelta = [ordered]@{}; foreach ($r in $e25Sets[0].Rows) { $monthsDelta[$r.Month] = $r.DeltaPp }
$pd = @($e25Sets[1].Rows)
Add 'E25' 'Kosovo margin against the same months a year earlier (the YoY fingerprint), both KS layers' ([ordered]@{ periods = $periods; deltaPpByMonth = $monthsDelta }) 'percentage points' 'measured (October 2026 provisional)' `
    ("Booked gross profit over net revenue, tool scope, every day, against the same months a year earlier (hk.SalesLines starts 1 Oct 2024). $($pd[2].Period): $($pd[2].MarginPct) % vs $($pd[2].YearBeforePct) %, +$($pd[2].DeltaPp) pt; " +
     "$($pd[1].Period): +$($pd[1].DeltaPp) pt; but $($pd[0].Period), with no calculator, ran +$($pd[0].DeltaPp) pt, so the fingerprint alone doesn't separate the calculator from what else moved margin (E20's same-weeks control does). $($pd[3].Period): +$($pd[3].DeltaPp) pt.") `
    'hk.SalesLines' 'docs/hackathon-2026/evidence/e25-yoy-margin.sql (two result sets; this script runs it)'
Recompute 'E25' 'e25-yoy-margin.sql' $e25Sets @($pd | ForEach-Object { C "$($_.Period), points over a year earlier" 1 'DeltaPp' @{ Period = $_.Period } })

# E26: the Sales Report tie-out (plan section 4; exports/README.md).
$e26Sets = RunFile 'e26-sales-report-tie-out.sql'
$tie = @(foreach ($r in $e26Sets[0].Rows) { [ordered]@{ approval = $r.ApprovalId; layer = $r.LayerId; approved = ([datetime]$r.Approved).ToString('yyyy-MM-dd')
    toolProfit30Before = $r.ToolProfit30Before; fromSalesLines = $r.FromSalesLines; diffPct = $r.DiffPct } })
$maxDiff = ($e26Sets[0].Rows | ForEach-Object { [math]::Abs((N0 $_.DiffPct)) } | Measure-Object -Maximum).Maximum
Add 'E26' "Sales Report tie-out: each approval's 30-day gross profit before it, the tool's vs hk.SalesLines" ([ordered]@{ approvals = $tie; maxAbsDiffPct = $maxDiff }) 'EUR' 'measured' `
    "The deployed tool computes Profit30Before from the Sales Report; the same SKUs, layer and 30 days from the exported lines (hk.SalesLines). The largest gap is $maxDiff % (the newest approvals: late cancellations still post); the exported lines are the Sales Report's." `
    'PricingTool.Approvals / ApprovalItems, hk.SalesLines' 'docs/hackathon-2026/evidence/e26-sales-report-tie-out.sql (this script runs it)'
Recompute 'E26' 'e26-sales-report-tie-out.sql' $e26Sets @($e26Sets[0].Rows | ForEach-Object { C "#$($_.ApprovalId) from hk.SalesLines" 0 'FromSalesLines' @{ ApprovalId = "$($_.ApprovalId)" } })

# E27: B0 against a comparison group, a robustness check (Prompt 15): the same approvals and spans as E20, campaign days
# out, three ways per shop.
$e27Sets = RunFile 'e20-aug-sep-comparison.sql'
function E27Row($month, $shop) { $e27Sets[0].Rows | Where-Object { $_.Month -eq $month -and $_.Shop -eq $shop } | Select-Object -First 1 }
$e27Shops = [ordered]@{}
foreach ($shop in 'GjirafaMall KS', 'Gjirafa50 KS') {
    $r = E27Row 'Aug-Sep avg' $shop
    $e27Shops[$shop] = [ordered]@{ beforeAfter = Round0 $r.BeforeAfter; vsUntouched = Round0 $r.VsUntouched; vsUntouchedSoldInBothWindows = Round0 $r.VsUntouchedBoth }
}
$e27Total = [ordered]@{ beforeAfter = ($e27Shops.Values | ForEach-Object { $_.beforeAfter } | Measure-Object -Sum).Sum
    vsUntouched = ($e27Shops.Values | ForEach-Object { $_.vsUntouched } | Measure-Object -Sum).Sum
    vsUntouchedSoldInBothWindows = ($e27Shops.Values | ForEach-Object { $_.vsUntouchedSoldInBothWindows } | Measure-Object -Sum).Sum }
$sepMall = E27Row '2026-09' 'GjirafaMall KS'
$a12 = $e27Sets[1].Rows | Where-Object { $_.ApprovalId -eq 12 } | Select-Object -First 1
$p25 = $e27Sets[2].Rows | Where-Object { $_.ApprovalId -eq 25 -and $_.Grp -eq 'repriced' } | Select-Object -First 1
$u25 = $e27Sets[2].Rows | Where-Object { $_.ApprovalId -eq 25 -and $_.Grp -eq 'untouched' } | Select-Object -First 1
function Pct($a, $b) { [math]::Round(100.0 * ([double]$b - [double]$a) / [double]$a, 0) }
Add 'E27' "B0 against untouched products in the same category and weeks, Aug-Sep 2026 (a robustness check of E20)" ([ordered]@{
        byShop = $e27Shops; total = $e27Total
        september = [ordered]@{ gjirafaMallVsUntouched = Round0 $sepMall.VsUntouched; gjirafaMallSoldInBothWindows = Round0 $sepMall.VsUntouchedBoth }
        run12 = [ordered]@{ repricedSkusSoldInEitherWindow = [int]$a12.RepricedSkus; soldInBothWindows = [int]$a12.RepricedSkusBoth }
        run25SoldInBothWindows = [ordered]@{ gpPerUnitBefore = [decimal]$p25.GpPerUnitBefore; gpPerUnitAfter = [decimal]$p25.GpPerUnitAfter
            gpPerUnitChangePct = Pct $p25.GpPerUnitBefore $p25.GpPerUnitAfter; unitsChangePct = Pct $p25.UnitsBefore $p25.UnitsAfter
            untouchedGpPerUnitChangePct = Pct $u25.GpPerUnitBefore $u25.GpPerUnitAfter; untouchedUnitsChangePct = Pct $u25.UnitsBefore $u25.UnitsAfter } }) 'EUR/month' 'robustness check: measured' `
    ("The approvals and live spans of E20, 30 days before vs after each, campaign days out of both windows, per shop, Aug-Sep average: before/after on the SKUs each run repriced $($e27Total.beforeAfter); against untouched SKUs of the same shop and category in the same weeks $($e27Total.vsUntouched) when every SKU that sold in either window counts, and $($e27Total.vsUntouchedSoldInBothWindows) when only SKUs that sold in both windows count (#12 repriced $($a12.RepricedSkus) SKUs that sold in either window, $($a12.RepricedSkusBoth) in both). " +
     "September alone gives GjirafaMall KS $(Round0 $sepMall.VsUntouched) against untouched SKUs and $(Round0 $sepMall.VsUntouchedBoth) on SKUs that sold in both windows. On run #25's SKUs that sold in both windows, gross profit per unit rose $(Pct $p25.GpPerUnitBefore $p25.GpPerUnitAfter) % with units $(Pct $p25.UnitsBefore $p25.UnitsAfter) %, against $(Pct $u25.GpPerUnitBefore $u25.GpPerUnitAfter) % and $(Pct $u25.UnitsBefore $u25.UnitsAfter) % on untouched SKUs. GjirafaMall lines are Gjirafa's own vendors only, at booked cost; returns after delivery are not netted out.") `
    'PricingTool.Approvals / ApprovalItems, hk.SalesLines, hk.Products, hk.CampaignWindows' 'docs/hackathon-2026/evidence/e20-aug-sep-comparison.sql (three result sets; this script runs it)'
Recompute 'E27' 'e20-aug-sep-comparison.sql' $e27Sets @(
    (C 'Aug-Sep, before/after, both shops' 0 'BeforeAfter' @{ Month = 'Aug-Sep avg' } 'sum'),
    (C 'Aug-Sep, vs untouched SKUs, both shops' 0 'VsUntouched' @{ Month = 'Aug-Sep avg' } 'sum'),
    (C 'Aug-Sep, vs untouched, SKUs that sold in both windows' 0 'VsUntouchedBoth' @{ Month = 'Aug-Sep avg' } 'sum'),
    (C 'September, GjirafaMall KS vs untouched' 0 'VsUntouched' @{ Month = '2026-09'; Shop = 'GjirafaMall KS' }),
    (C 'September, GjirafaMall KS, sold in both windows' 0 'VsUntouchedBoth' @{ Month = '2026-09'; Shop = 'GjirafaMall KS' }))

# E28: the test, run by hand, the facts beside the without-the-build line (Prompt 15). The line itself is an assumption
# (the sheet's Inputs); these are what was measured.
$e28Sets = RunFile 'e28-runs-by-hand.sql'
function RunDays($shop, $kind) { @($e28Sets[0].Rows | Where-Object { $_.Shop -eq $shop -and $_.Kind -eq $kind } | ForEach-Object { ([datetime]$_.RunDay).ToString('yyyy-MM-dd') }) }
$gj50Gap = ($e28Sets[1].Rows | Where-Object { $_.Shop -eq 'Gjirafa50 KS' } | ForEach-Object { N0 $_.DaysToNext } | Measure-Object -Maximum).Maximum
$mkal = [ordered]@{}; foreach ($r in $e28Sets[2].Rows) { $mkal["$($r.Layer) $($r.Month)"] = ([datetime]$r.FirstDay).ToString('yyyy-MM-dd') }
$ksSets = @($e28Sets[3].Rows | Where-Object { $_.Shop -eq 'GjirafaMall KS' } | ForEach-Object { [int]$_.LiveDays } | Measure-Object -Minimum -Maximum)
$e23rows = @(($figures | Where-Object { $_.id -eq 'E23' }).value.perApproval | Where-Object { $_.approval -in 12, 25 })
$oldPct = [math]::Round(100.0 * ($e23rows | ForEach-Object { [double]$_.atOldPrice } | Measure-Object -Sum).Sum / ($e23rows | ForEach-Object { [double]$_.units } | Measure-Object -Sum).Sum, 0)
$mallRuns = RunDays 'GjirafaMall KS' 'regular'; $gj50Runs = RunDays 'Gjirafa50 KS' 'regular'; $gj50Dead = RunDays 'Gjirafa50 KS' 'dead stock'
Add 'E28' 'The test, run by hand: the facts beside the without-the-build line (Kosovo runs, MK/AL pushes)' ([ordered]@{
        gjirafaMallKsRegularRuns = $mallRuns; gjirafa50KsRegularRuns = $gj50Runs; gjirafa50KsDeadStockRuns = $gj50Dead; gjirafa50KsLongestGapDays = $gj50Gap
        mkalFullPushes = $mkal; unitsAtOldPriceAugSepPct = $oldPct }) 'dates / days' 'measured' `
    ("The deployed tool's runs (PricingTool.Approvals) and the sets pushed to the shops (hk.PushDatesKs, hk.PushDatesMkAl): one person ran them by hand from 21 Jul, with no schedule. " +
     "GjirafaMall KS regular runs on $($mallRuns -join ', '); Gjirafa50 KS regular runs on $($gj50Runs -join ', ') ($gj50Gap days from the first to the second), with dead-stock runs on $(($gj50Dead | Where-Object { $_ -lt '2026-10-01' }) -join ', '). " +
     "MK/AL full pushes (50,000+ public discounts started in a month): $(($mkal.GetEnumerator() | ForEach-Object { "$($_.Key) from $($_.Value)" }) -join '; '). " +
     "GjirafaMall KS's sets of 50,000+ public discounts stayed live $($ksSets.Minimum)-$($ksSets.Maximum) days. Even when a run went live, $oldPct % of the units on SKUs approvals #12 and #25 raised still sold at the old price in Aug-Sep (E23); late October's 70-84 % was the month two push sets overlapped.") `
    'PricingTool.Approvals / ApprovalItems, hk.PushDatesKs, hk.PushDatesMkAl' 'docs/hackathon-2026/evidence/e28-runs-by-hand.sql (four result sets; this script runs it)'
Recompute 'E28' 'e28-runs-by-hand.sql' $e28Sets @((C "Gjirafa50 KS: days from 21 Jul to its next regular run" 1 'DaysToNext' @{ Shop = 'Gjirafa50 KS' }),
    (C 'GjirafaMall AL: discounts pushed in Sep (the one full MK/AL push in Aug-Sep)' 2 'Discounts' @{ Layer = 'GjirafaMall AL'; Month = '2026-09' }),
    (C 'GjirafaMall MK: discounts in its first full push, Oct' 2 'Discounts' @{ Layer = 'GjirafaMall MK'; Month = '2026-10' }))

# Why the calculator's figure moved since the registration (the Evidence page shows it with E20).
$e20 = $figures | Where-Object { $_.id -eq 'E20' }
$e20.sinceRegistration = "The registration gave the unadjusted before/after, about 10k EUR a month; recomputed from this repo's data it is $($e20.value.mid) a month (E20 mid), the claim's rate. Its robustness checks are E20 low (a same-weeks control) and E27 (against untouched products in the same category and weeks); E20 high (season-adjusted, quoted from the main repo) is sensitivity, not part of the claim."

foreach ($f in $figures) { if ($recomputes.ContainsKey($f.id)) { $f.recompute = $recomputes[$f.id] } }

$out = [ordered]@{
    generated = (Get-Date).ToString('yyyy-MM-dd HH:mm'); generator = 'docs/hackathon-2026/evidence/build-numbers.ps1'
    note = 'Every figure carries its label. One claim: B0 (E20 mid) made permanent by the build, plus the levers (E8, E24). Smaller numbers are robustness checks or sensitivity, never alternative claims.'
    figures = $figures
}
$json = $out | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $here 'numbers.json'), $json, (New-Object System.Text.UTF8Encoding $false))
"numbers.json: $($figures.Count) figures from $snapRef"
