<#
Diffs two random-call traces (*.randtrace.txt, produced with -RandTrace N) and prints the first call that differs,
with a few lines of context from each run.

  powershell -ExecutionPolicy Bypass -File bench\compare-randtrace.ps1 results\a.randtrace.txt results\b.randtrace.txt
#>
param([Parameter(Mandatory)][string]$PathA, [Parameter(Mandatory)][string]$PathB, [int]$Context = 6)

$linesA = [IO.File]::ReadAllLines($PathA)
$linesB = [IO.File]::ReadAllLines($PathB)
$workersA = @($linesA | Where-Object { $_ -like '*WORKER-THREAD*' }).Count
$workersB = @($linesB | Where-Object { $_ -like '*WORKER-THREAD*' }).Count
Write-Host "calls: A=$($linesA.Length) B=$($linesB.Length)   worker-thread calls: A=$workersA B=$workersB"
$n = [Math]::Min($linesA.Length, $linesB.Length)
for ($i = 0; $i -lt $n; $i++) {
    if ($linesA[$i] -ne $linesB[$i]) {
        Write-Host "FIRST DIFFERENCE at call #$i"
        $lo = [Math]::Max(0, $i - 2)
        Write-Host "--- A:"; $linesA[$lo..[Math]::Min($linesA.Length - 1, $i + $Context)] | ForEach-Object { "  $_" }
        Write-Host "--- B:"; $linesB[$lo..[Math]::Min($linesB.Length - 1, $i + $Context)] | ForEach-Object { "  $_" }
        exit 1
    }
}
Write-Host "Identical for all $n calls."
