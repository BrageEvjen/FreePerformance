<#
Compares two per-thing state dumps (*.dump.txt, produced with -DumpFrom/-DumpTo) and, for the first tick that
differs, lists things present in only one run and things whose fields differ.

  powershell -ExecutionPolicy Bypass -File bench\compare-dump.ps1 results\a.dump.txt results\b.dump.txt
#>
param([Parameter(Mandatory)][string]$PathA, [Parameter(Mandatory)][string]$PathB, [int]$Max = 25)

function Read-Dump($path) {
    # tick -> (thingId -> "fields" without the list index)
    $ticks = New-Object 'System.Collections.Generic.SortedDictionary[int,object]'
    foreach ($line in [IO.File]::ReadLines($path)) {
        if ($line -notmatch '^(\d+) #(\d+) (\S+) (.*)$') { continue }
        $tick = [int]$Matches[1]
        if (-not $ticks.ContainsKey($tick)) { $ticks[$tick] = @{ Things = @{}; Order = New-Object System.Collections.Generic.List[string] } }
        $ticks[$tick].Things[$Matches[3]] = $Matches[4]
        $ticks[$tick].Order.Add($Matches[3])
    }
    $ticks
}

$dumpA = Read-Dump $PathA
$dumpB = Read-Dump $PathB
foreach ($tick in $dumpA.Keys) {
    if (-not $dumpB.ContainsKey($tick)) { continue }
    $ta = $dumpA[$tick].Things; $tb = $dumpB[$tick].Things
    $onlyA = @($ta.Keys | Where-Object { -not $tb.ContainsKey($_) })
    $onlyB = @($tb.Keys | Where-Object { -not $ta.ContainsKey($_) })
    $changed = @($ta.Keys | Where-Object { $tb.ContainsKey($_) -and $ta[$_] -ne $tb[$_] })
    $orderSame = ($dumpA[$tick].Order -join ',') -eq ($dumpB[$tick].Order -join ',')
    if ($onlyA.Count -eq 0 -and $onlyB.Count -eq 0 -and $changed.Count -eq 0 -and $orderSame) { continue }

    Write-Host "First differing tick: $tick  (things A=$($ta.Count) B=$($tb.Count); only-in-A=$($onlyA.Count) only-in-B=$($onlyB.Count) changed=$($changed.Count) same-order=$orderSame)"
    if ($onlyA.Count) { Write-Host "-- only in A:"; $onlyA | Select-Object -First $Max | ForEach-Object { "  $_ $($ta[$_])" } }
    if ($onlyB.Count) { Write-Host "-- only in B:"; $onlyB | Select-Object -First $Max | ForEach-Object { "  $_ $($tb[$_])" } }
    if ($changed.Count) {
        Write-Host "-- changed:"
        $changed | Select-Object -First $Max | ForEach-Object { "  $_`n    A: $($ta[$_])`n    B: $($tb[$_])" }
    }
    exit 1
}
Write-Host "No differences in $($dumpA.Count) dumped ticks."
