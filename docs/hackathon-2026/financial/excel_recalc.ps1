param([Parameter(Mandatory = $true)][string]$Path)
# Recalculate a workbook in Excel itself, save the cached values, and report error cells + the Summary grid.
$xl = New-Object -ComObject Excel.Application
$xl.Visible = $false
$xl.DisplayAlerts = $false
try {
    $wb = $xl.Workbooks.Open($Path)
    $xl.CalculateFull()
    $errors = @()
    $formulas = 0
    foreach ($ws in $wb.Worksheets) {
        $used = $ws.UsedRange
        foreach ($cell in $used.Cells) {
            if ($cell.HasFormula) { $formulas++ }
            $t = [string]$cell.Text
            if ($t -match '^#(N/A|NAME\?|REF!|VALUE!|DIV/0!|NUM!|NULL!)') { $errors += "$($ws.Name)!$($cell.Address($false,$false)) = $t" }
        }
    }
    "formulas: $formulas, errors: $($errors.Count)"
    $errors | Select-Object -First 30
    $s = $wb.Worksheets.Item("Summary")
    foreach ($r in 5..21 + 26..30) {
        $line = @()
        foreach ($c in 1..7) { $line += [string]$s.Cells.Item($r, $c).Text }
        ($line -join " | ")
    }
    $wb.Save()
    $wb.Close($true)
}
finally {
    $xl.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($xl) | Out-Null
}
