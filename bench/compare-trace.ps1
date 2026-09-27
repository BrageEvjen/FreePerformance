<#
Compares the state traces of two benchmark result files and reports the first tick where they differ.

  powershell -ExecutionPolicy Bypass -File bench\compare-trace.ps1 results\a.txt results\b.txt
#>
param([Parameter(Mandatory)][string]$A, [Parameter(Mandatory)][string]$B)

function Read-Trace($path) {
    $map = New-Object 'System.Collections.Generic.SortedDictionary[int,object]'
    foreach ($line in Get-Content $path) {
        if ($line -match '^trace (\d+) (\S+) rand=(\S+) things=(\S+) pawns=(\S+)') {
            $map[[int]$Matches[1]] = [pscustomobject]@{ All = $Matches[2]; Rand = $Matches[3]; Things = $Matches[4]; Pawns = $Matches[5] }
        }
    }
    $map
}

$ta = Read-Trace $A
$tb = Read-Trace $B
if ($ta.Count -eq 0 -or $tb.Count -eq 0) { throw "One of the files has no trace lines (run with -Trace N)." }

$prev = $null
foreach ($tick in $ta.Keys) {
    if (-not $tb.ContainsKey($tick)) { continue }
    $x = $ta[$tick]; $y = $tb[$tick]
    if ($x.All -ne $y.All) {
        $parts = @('Rand', 'Things', 'Pawns') | Where-Object { $x.$_ -ne $y.$_ }
        Write-Host "First difference at tick $tick (last match: $prev). Differs in: $($parts -join ', ')"
        foreach ($p in 'Rand', 'Things', 'Pawns') { Write-Host ("  {0,-6} {1}  vs  {2}" -f $p, $x.$p, $y.$p) }
        exit 1
    }
    $prev = $tick
}
Write-Host "Identical across $($ta.Count) trace points (last tick $prev)."
