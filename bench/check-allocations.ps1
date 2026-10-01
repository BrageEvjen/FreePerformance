<#
Checks that the optimizations don't allocate more memory than the unmodified game. Extra garbage means extra garbage
collections, which show up as a stutter every few seconds (1.0.5 allocated 7x vanilla, from closures in hot methods).

  powershell -ExecutionPolicy Bypass -File bench\check-allocations.ps1 -GameDir "<RimWorld folder>"

Runs vanilla and every optimization switched on, and compares KB allocated per tick. If the difference is above
-Threshold it runs each optimization alone to find the one responsible (skipped with -SkipDrilldown), and exits
with code 1.

The number varies between launches (vanilla 11-13 KB/tick; with the optimizations on 13-29, because the game itself
allocates 14-25 KB/tick depending on what is going on), and each launch plays a different game. So the threshold is
set between what the fixed build showed (at most +17 KB/tick over vanilla) and what 1.0.5 showed (+79 to +87): it catches
a leak like that one, not a small one. For a small one, use the in-process A/B, which compares on and off inside one
session and reports allocation too:  run-bench.ps1 -AB <key> -Ticks 10000
#>
param(
    [double]$Threshold = 40,
    [int]$Ticks = 3000,
    [switch]$SkipDrilldown,
    [string]$Save = "Teroum Confederation (Permadeath)",
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\RimWorld"
)
$ErrorActionPreference = "Stop"
$bench = Join-Path $PSScriptRoot "run-bench.ps1"
$root = Split-Path $PSScriptRoot -Parent

$keys = Get-ChildItem (Join-Path $root "Source\ParallelTick\Optimizations") -Filter *.cs |
    ForEach-Object { [regex]::Matches((Get-Content $_.FullName -Raw), 'Key = "([a-z]+)"') } |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

function Measure-Allocation([string]$Label, [string[]]$On) {
    $run = @("-ExecutionPolicy", "Bypass", "-File", $bench, "-Label", $Label, "-NoBreakdown", "-NoWait",
             "-Ticks", $Ticks, "-Save", $Save, "-GameDir", $GameDir)
    if ($On.Count -gt 0) { $run += @("-Opt", (($On | ForEach-Object { "$_=on" }) -join ",")) }
    $out = (& powershell @run) -join "`n"
    if ($out -notmatch 'allocated:\s+([\d.]+) KB per tick') { throw "No allocation figure from run '$Label':`n$out" }
    $kb = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    $gcs = if ($out -match 'collections:\s+(\d+) during') { [int]$Matches[1] } else { -1 }
    [pscustomobject]@{ Label = $Label; KB = $kb; Collections = $gcs }
}

$vanilla = Measure-Allocation "alloc-vanilla" @()
$all = Measure-Allocation "alloc-all" $keys
$delta = $all.KB - $vanilla.KB
Write-Host ("vanilla: {0:F1} KB/tick, {1} collections" -f $vanilla.KB, $vanilla.Collections)
Write-Host ("all {0} optimizations: {1:F1} KB/tick, {2} collections ({3:+0.0;-0.0} KB/tick)" -f $keys.Count, $all.KB, $all.Collections, $delta)

if ($delta -le $Threshold) {
    Write-Host "OK: within $Threshold KB/tick of vanilla."
    exit 0
}

Write-Host "TOO MUCH GARBAGE: more than $Threshold KB/tick above vanilla."
if ($SkipDrilldown) { exit 1 }
Write-Host "Running each optimization alone..."
$rows = foreach ($key in $keys) {
    $r = Measure-Allocation "alloc-$key" @($key)
    [pscustomobject]@{ Optimization = $key; KB = $r.KB; Over = $r.KB - $vanilla.KB; Collections = $r.Collections }
}
$rows | Sort-Object Over -Descending | Format-Table -AutoSize
exit 1
