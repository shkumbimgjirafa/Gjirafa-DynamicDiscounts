param([string]$DestConnectionString = "Server=(localdb)\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True")
# Data prep, self-completing: rebuilds hk.Products from the replica into a side table, checks it,
# and swaps it in. If a check fails, hk.Products is left as it was and the reason is printed.
$ErrorActionPreference = "Stop"
$x = $PSScriptRoot
& "$x\Copy-ReplicaQuery.ps1" -SqlFile "$x\products_ks.sql" -DestTable "hk.Products_reload" `
    -Notes "Rebuild with the UTF-8 fix (Albanian literals); swapped in for hk.Products after its checks" -CreateSql @"
CREATE TABLE hk.Products_reload (
  LayerId int NOT NULL, ProductId int NOT NULL, Sku nvarchar(400) NULL, Name nvarchar(400) NULL, Brand nvarchar(400) NULL,
  CategoryL1 nvarchar(400) NOT NULL, CategoryL2 nvarchar(400) NULL, CategoryId int NULL, MainGroup nvarchar(100) NOT NULL,
  IsOutlet bit NOT NULL, IsExcludedBrand bit NOT NULL, IsGj50Listed bit NOT NULL, VendorId int NULL,
  CONSTRAINT PK_hk_Products_reload PRIMARY KEY (LayerId, ProductId));
"@

$c = New-Object System.Data.SqlClient.SqlConnection $DestConnectionString
$c.Open()
try {
    $cmd = $c.CreateCommand(); $cmd.CommandTimeout = 600
    $cmd.CommandText = @"
SELECT (SELECT COUNT_BIG(*) FROM hk.Products_reload),
       (SELECT COUNT_BIG(*) FROM hk.Products),
       (SELECT COUNT(*) FROM hk.Products_reload WHERE LayerId = 4 AND CategoryL1 = N'Çfarë ka të re?'),
       (SELECT COUNT(DISTINCT MainGroup) FROM hk.Products_reload WHERE LayerId = 1 AND MainGroup IN (N'Kozmetikë', N'Teknologji', N'Shtëpi', N'Sport', N'Vegla Pune', N'Fëmijë'))
"@
    $r = $cmd.ExecuteReader(); [void]$r.Read()
    $newRows = $r.GetInt64(0); $oldRows = $r.GetInt64(1); $whatsNew = $r.GetInt32(2); $groups = $r.GetInt32(3); $r.Close()
    "reload: $newRows rows (current $oldRows); 'what's new' products on Gj50: $whatsNew; Mall main groups found: $groups of 6"
    if ($newRows -lt 0.98 * $oldRows -or $whatsNew -gt 0 -or $groups -lt 6) { "CHECK FAILED: hk.Products left unchanged"; return }

    $cmd.CommandText = @"
BEGIN TRAN;
DROP TABLE hk.Products;
EXEC sp_rename 'hk.Products_reload', 'Products';
EXEC sp_rename 'hk.PK_hk_Products_reload', 'PK_hk_Products', 'OBJECT';
UPDATE hk.DataSources SET TableName = 'hk.Products' WHERE TableName = 'hk.Products_reload';
COMMIT;
"@
    [void]$cmd.ExecuteNonQuery()
    "swapped in: hk.Products now has $newRows rows"
}
finally { $c.Close() }
