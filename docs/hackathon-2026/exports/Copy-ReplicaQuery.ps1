param(
    [Parameter(Mandatory = $true)][string]$SqlFile,
    [Parameter(Mandatory = $true)][string]$DestTable,
    [Parameter(Mandatory = $true)][string]$CreateSql,
    [string]$Notes = "",
    [string]$DestConnectionString = "Server=(localdb)\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True",
    [int]$TimeoutSeconds = 1800
)
# Data prep, not solution code. Runs one read-only query file on the NOP replica and bulk-loads its
# last result set into a LocalDB table (dropped and recreated), then logs the load in hk.DataSources.
# The replica connection string comes from the hackathon user-secrets store and is never printed.

$ErrorActionPreference = "Stop"
$secrets = Get-Content (Join-Path $env:APPDATA "Microsoft\UserSecrets\gjirafa-hackathon-pricing-agent-2026\secrets.json") -Raw | ConvertFrom-Json
$sourceCs = $secrets.'ConnectionStrings:SourceReadOnly'
$started = Get-Date

$dest = New-Object System.Data.SqlClient.SqlConnection $DestConnectionString
$dest.Open()
$c = $dest.CreateCommand(); $c.CommandTimeout = 600
$c.CommandText = "IF SCHEMA_ID('hk') IS NULL EXEC('CREATE SCHEMA hk'); IF OBJECT_ID('$DestTable') IS NOT NULL DROP TABLE $DestTable;"
[void]$c.ExecuteNonQuery()
$c.CommandText = $CreateSql; [void]$c.ExecuteNonQuery()

$src = New-Object System.Data.SqlClient.SqlConnection $sourceCs
$src.Open()
try {
    # UTF-8 explicitly: Windows PowerShell would otherwise read the file as ANSI and mangle
    # Albanian literals such as N'Kozmetikë' (this happened once; the categories didn't match).
    $cmd = $src.CreateCommand(); $cmd.CommandText = (Get-Content $SqlFile -Raw -Encoding UTF8); $cmd.CommandTimeout = $TimeoutSeconds
    $reader = $cmd.ExecuteReader()
    $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($dest)
    $bulk.DestinationTableName = $DestTable; $bulk.BatchSize = 20000; $bulk.BulkCopyTimeout = 0
    for ($i = 0; $i -lt $reader.FieldCount; $i++) { [void]$bulk.ColumnMappings.Add($reader.GetName($i), $reader.GetName($i)) }
    $bulk.WriteToServer($reader); $reader.Close()
}
finally { $src.Close() }

$c.CommandText = "SELECT COUNT_BIG(*) FROM $DestTable"; $rows = [long]$c.ExecuteScalar()
$log = $dest.CreateCommand()
$log.CommandText = "INSERT hk.DataSources (TableName, Source, Origin, ExtractedAtUtc, [RowCount], ExtractedBy, Notes) VALUES (@t, 'nop-replica', @o, SYSUTCDATETIME(), @n, 'data session', @notes)"
[void]$log.Parameters.AddWithValue("@t", $DestTable)
[void]$log.Parameters.AddWithValue("@o", "NOP replica, read-only: docs/hackathon-2026/exports/" + (Split-Path $SqlFile -Leaf))
[void]$log.Parameters.AddWithValue("@n", $rows)
[void]$log.Parameters.AddWithValue("@notes", $(if ($Notes) { $Notes } else { [DBNull]::Value }))
[void]$log.ExecuteNonQuery()
$dest.Close()
"{0}: {1:N0} rows in {2:N0}s" -f $DestTable, $rows, ((Get-Date) - $started).TotalSeconds
