param(
    [Parameter(Mandatory = $true)][string]$SourceConnectionString,
    [string]$DestConnectionString = "Server=(localdb)\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True",
    [string]$KsLayers = "1,4"
)
# Data prep, not solution code. Copies the pricing tool's own state for the Kosovo layers
# (1 GjirafaMall KS, 4 Gjirafa50 KS) from the deployed tool DB, read-only, into the hackathon
# LocalDB, which has the same EF schema. Config tables come over whole (they're small and the
# engine needs every layer's row); per-SKU and history tables only for the KS layers.
# Not copied: run output (DailySnapshots, ProposedPrices, AlgorithmVotes), users and identity tables.
# People: who-columns keep 'scheduler' / 'engine' and become 'team member' otherwise; audit rows
# about users are skipped and any value holding an email is blanked.
# The connection string is passed in by the caller and never stored or printed.

$ErrorActionPreference = "Stop"
$people = { param($c) "CASE WHEN $c IS NULL OR $c IN ('scheduler','engine') THEN $c ELSE 'team member' END" }
$noEmail = { param($c) "CASE WHEN $c LIKE '%@%' THEN '[redacted: contained an email]' ELSE $c END" }

$tables = @(
    @{ Name = "PriceBands" }
    @{ Name = "BandAlgorithmSettings" }
    @{ Name = "DiscountTrimRules" }
    @{ Name = "ToolSettings"; Expr = @{ UpdatedBy = (& $people "UpdatedBy") } }
    @{ Name = "PricingRuns"; Expr = @{ TriggeredBy = (& $people "TriggeredBy") } }
    @{ Name = "PricingRunBandTargets" }
    @{ Name = "DailyLayerKpi" }
    @{ Name = "DeadStockDailyKpi" }
    @{ Name = "SkuElasticity"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "SkuOverrides"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "SkuDeadStockStates"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "DeadStockEpisodes"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "DeadStockWindowLogs"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "DeadStockWindowClearedItems"; Where = "DeadStockWindowLogId IN (SELECT Id FROM PricingTool.DeadStockWindowLogs WHERE LayerId IN ($KsLayers))" }
    @{ Name = "ApprovedBatches"; Where = "LayerId IN ($KsLayers)" }
    @{ Name = "ApprovedBatchItems"; Where = "ApprovedBatchId IN (SELECT Id FROM PricingTool.ApprovedBatches WHERE LayerId IN ($KsLayers))" }
    @{ Name = "Approvals"; Where = "LayerId IN ($KsLayers)"; Expr = @{ ApprovedBy = (& $people "ApprovedBy") } }
    @{ Name = "ApprovalItems"; Where = "ApprovalId IN (SELECT Id FROM PricingTool.Approvals WHERE LayerId IN ($KsLayers))" }
    @{ Name = "AuditLog"; Where = "ISNULL(EntityType,'') NOT IN ('User','Users') AND ISNULL(Category,'') NOT IN ('User','Users')"
       Expr = @{ UserName = (& $people "UserName"); OldValue = (& $noEmail "OldValue"); NewValue = (& $noEmail "NewValue") } }
    @{ Name = "PriceChangeOutcomes"; Where = "LayerId IN ($KsLayers)" }
    # Last on purpose: its hk.DataSources row is the build session's signal that the whole copy is in.
    @{ Name = "Layers" }
)

$dest = New-Object System.Data.SqlClient.SqlConnection $DestConnectionString
$dest.Open()
function Exec-Dest([string]$sql) { $c = $dest.CreateCommand(); $c.CommandText = $sql; $c.CommandTimeout = 600; [void]$c.ExecuteNonQuery() }

Exec-Dest @"
IF SCHEMA_ID('hk') IS NULL EXEC('CREATE SCHEMA hk');
IF OBJECT_ID('hk.DataSources') IS NULL
CREATE TABLE hk.DataSources (
    Id int IDENTITY PRIMARY KEY,
    TableName nvarchar(200) NOT NULL,
    Source nvarchar(100) NOT NULL,
    Origin nvarchar(max) NULL,
    ExtractedAtUtc datetime2 NOT NULL,
    [RowCount] bigint NOT NULL,
    ExtractedBy nvarchar(100) NOT NULL,
    Notes nvarchar(max) NULL);
"@

# Constraints off while loading (the KS subset leaves a few references to other layers' rows).
foreach ($t in $tables) { Exec-Dest "ALTER TABLE PricingTool.[$($t.Name)] NOCHECK CONSTRAINT ALL" }
foreach ($t in $tables) { Exec-Dest "DELETE FROM PricingTool.[$($t.Name)]" }

$src = New-Object System.Data.SqlClient.SqlConnection $SourceConnectionString
$src.Open()
try {
    foreach ($t in $tables) {
        $started = Get-Date
        $colCmd = $dest.CreateCommand()
        $colCmd.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('PricingTool.[$($t.Name)]') AND is_computed = 0 ORDER BY column_id"
        $r = $colCmd.ExecuteReader(); $cols = @(); while ($r.Read()) { $cols += $r.GetString(0) }; $r.Close()
        $select = ($cols | ForEach-Object {
            if ($t.Expr -and $t.Expr.ContainsKey($_)) { "$($t.Expr[$_]) AS [$_]" } else { "[$_]" } }) -join ", "
        $where = if ($t.Where) { " WHERE $($t.Where)" } else { "" }
        $sql = "SELECT $select FROM PricingTool.[$($t.Name)]$where"

        $cmd = $src.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 900
        $reader = $cmd.ExecuteReader()
        $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($dest, [System.Data.SqlClient.SqlBulkCopyOptions]::KeepIdentity, $null)
        $bulk.DestinationTableName = "PricingTool.[$($t.Name)]"
        $bulk.BatchSize = 20000; $bulk.BulkCopyTimeout = 0
        foreach ($c in $cols) { [void]$bulk.ColumnMappings.Add($c, $c) }
        $bulk.WriteToServer($reader); $reader.Close()

        $n = $dest.CreateCommand(); $n.CommandText = "SELECT COUNT_BIG(*) FROM PricingTool.[$($t.Name)]"; $rows = [long]$n.ExecuteScalar()
        $log = $dest.CreateCommand()
        $log.CommandText = "INSERT hk.DataSources (TableName, Source, Origin, ExtractedAtUtc, [RowCount], ExtractedBy, Notes) VALUES (@t, 'tool-db', @o, SYSUTCDATETIME(), @n, 'data session', @notes)"
        [void]$log.Parameters.AddWithValue("@t", "PricingTool.$($t.Name)")
        [void]$log.Parameters.AddWithValue("@o", "Deployed pricing tool DB, schema PricingTool, read-only.$where")
        [void]$log.Parameters.AddWithValue("@n", $rows)
        [void]$log.Parameters.AddWithValue("@notes", $(if ($t.Expr) { "Who-columns redacted: " + (($t.Expr.Keys) -join ", ") } else { [DBNull]::Value }))
        [void]$log.ExecuteNonQuery()
        "{0,-28} {1,10:N0} rows  {2,6:N1}s" -f $t.Name, $rows, ((Get-Date) - $started).TotalSeconds
    }
}
finally { $src.Close() }

foreach ($t in $tables) { Exec-Dest "ALTER TABLE PricingTool.[$($t.Name)] CHECK CONSTRAINT ALL" }
$dest.Close()
"done"
