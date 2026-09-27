<#
Measures the true cost of several systems by switching each one off in alternate A/B blocks (ablation), with the
default optimizations on. Prints one line per system: how much faster a tick gets without it.

  powershell -ExecutionPolicy Bypass -File bench\run-ablations.ps1
#>
param(
    [string[]]$Targets = @(
        "Verse.AI.Pawn_JobTracker.JobTrackerTick",
        "Verse.ThingWithComps.Tick",
        "Verse.Pawn_HealthTracker.HealthTick",
        "RimWorld.SituationalThoughtHandler.UpdateAllMoodThoughts",
        "RimWorld.CompOverseerSubject.CompTick",
        "RimWorld.Building_Door.Tick",
        "Verse.Effecter.EffectTick",
        "Verse.VerbTracker.VerbsTick",
        "Verse.Pawn_StanceTracker.StanceTrackerTick",
        "RimWorld.PowerNetManager.PowerNetsTick",
        "RimWorld.ListerHaulables.ListerHaulablesTick"
    ),
    [int]$Ticks = 12000
)
$script = Join-Path $PSScriptRoot "run-bench.ps1"
$defaults = "tempcache=on,solar=on,statcache=on,bedcheck=on,musiccheck=on,deliveryearly=on,mergeindex=on"
$rows = @()
foreach ($t in $Targets) {
    $short = ($t -split '\.')[-2..-1] -join '.'
    $out = powershell -NoProfile -ExecutionPolicy Bypass -File $script -Label "ablate-$short" -AB "ablate:$t" -Opt $defaults -Ticks $Ticks -NoBreakdown -NoWait
    $diff = $out | Select-String 'difference:\s+([-+][\d.,]+) ms \(([-+][\d.,]+)%\), 95% CI \+/-([\d.,]+) ms' | Select-Object -First 1
    $load = ($out | Select-String 'background load:\s+([\d.,]+)').Matches | Select-Object -First 1
    if ($diff) {
        $m = $diff.Matches[0].Groups
        $rows += [pscustomobject]@{ System = $short; 'Saved ms' = $m[1].Value; 'Saved %' = $m[2].Value; 'CI ms' = $m[3].Value; Load = $load.Groups[1].Value }
        Write-Host ("{0,-45} {1,8} ms ({2,6}%)  CI +/-{3} ms  load {4}" -f $short, $m[1].Value, $m[2].Value, $m[3].Value, $load.Groups[1].Value)
    } else {
        Write-Host "$short : no result"
        $out | Select-String 'FAILED|Error|not found' | Select-Object -First 3 | ForEach-Object { "   $_" }
    }
}
