<#
Runs the ParallelTick benchmark on a copy of a save, using an isolated save-data folder
(bench\data) so the real config, mod list and saves are never touched.

  powershell -ExecutionPolicy Bypass -File bench\run-bench.ps1 -Label vanilla
#>
param(
    [string]$Save = "Teroum Confederation (Permadeath)",
    [int]$Warmup = 2500,
    [int]$Ticks = 5000,
    [int]$Seed = 12345,
    [switch]$NoBreakdown,
    [int]$Trace = 0,
    [int]$RandTrace = 0,
    [int]$DumpFrom = -1,
    [int]$DumpTo = -1,
    # Optimization modes, e.g. -Opt tempcache=on,solar=verify (unlisted optimizations are off).
    [string[]]$Opt = @(),
    [string]$AB = "",
    # Skip waiting for known background programs (fine for correctness-only runs, where timing doesn't matter).
    [switch]$NoWait,
    # Process names to wait for before launching (e.g. a render job); see -NoWait.
    [string[]]$WaitFor = @(),
    # Real-play test instead of the tick benchmark: Superfast with rendering for -PlaySeconds, -PlayOpts on/off.
    [switch]$Play,
    [int]$PlaySeconds = 180,
    [ValidateSet("on", "off")][string]$PlayOpts = "on",
    [switch]$FrameProfile,
    [int]$TicksPerFrame = 60,
    [int]$TickBudget = 0,
    # In-play A/B (with -Play): "opts" (all optimizations), an optimization key, or "budget:<ms>"; switches every -PlayABSeconds.
    [string]$PlayAB = "",
    [int]$PlayABSeconds = 10,
    # Save the game under this name in bench\data\Saves when the run ends (checks what the mod writes into saves).
    [string]$SaveAs = "",
    # Start a freshly generated colony (like the dev "Quick test" button) instead of loading a save; needed to test DLC subsets.
    [switch]$QuickTest,
    # Active DLCs, comma list (royalty, ideology, biotech, anomaly, odyssey); "none" for Core only.
    [string]$Dlcs = "royalty,ideology,biotech,anomaly,odyssey",
    # Play mode: save this many screen frames per second (bench\datarames) for comparison videos.
    [double]$Record = 0,
    # Portrait mode: "key=value|key=value" (see PortraitComponent), e.g. "hairs=ShortCut,Recruit|heads=Male_AverageWide".
    [string]$Portrait = "",
    # A ticking comp on every pawn race (inert | disrupt), standing in for animation mods; see BenchTestComp.
    [string]$TestComp = "",
    # Another mod's patches (inert | disrupt) on every method the idle-pawn skip reasons about; see BenchTestPatches.
    [string]$TestPatches = "",
    # Log methods' instructions with every mod's patches applied: "Type:Method;Type:Method" (see BenchComponent.DumpIl).
    [string]$DumpIl = "",
    # Bench only: ignore other-mod patch guards (measures what they cost; not exact).
    [switch]$NoGuards,
    [int]$ABBlock = 250,
    [string]$Label = "run",
    [string[]]$ExtraMods = @(),
    [int]$TimeoutMinutes = 25,
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\RimWorld"
)
$ErrorActionPreference = "Stop"
# With "powershell -File", "-Opt a=on,b=on" arrives as one string.
$Opt = @($Opt | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$ExtraMods = @($ExtraMods | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })

$root = Split-Path $PSScriptRoot -Parent
$realData = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$data = Join-Path $PSScriptRoot "data"
$results = Join-Path $PSScriptRoot "results"

if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) {
    throw "RimWorld is already running - close it first."
}

# Link the mod into the game's Mods folder for this run only; removed again when the game exits, so the player's own
# game (which may be subscribed to the Workshop copy, same packageId) never sees two copies.
if (Test-Path (Join-Path $GameDir "Mods\FreePerformance")) {
    throw "The release folder is installed (same packageId); run bench\make-release.ps1 -Uninstall first."
}
$link = Join-Path $GameDir "Mods\ParallelTick"
if (-not (Test-Path $link)) {
    New-Item -ItemType Junction -Path $link -Target $root | Out-Null
    Write-Host "Linked mod: $link -> $root"
}

New-Item -ItemType Directory -Force "$data\Config", "$data\Saves", $results | Out-Null
Copy-Item (Join-Path $realData "Saves\$Save.rws") "$data\Saves\bench.rws" -Force
Copy-Item (Join-Path $realData "Config\LastPlayedVersion.txt") "$data\Config\" -Force -ErrorAction SilentlyContinue

# Mod list: Harmony, Core + DLCs, this mod, plus anything passed in -ExtraMods.
$version = (Get-Content (Join-Path $GameDir "Version.txt") -TotalCount 1).Trim()
$expansions = @($Dlcs -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ -and $_ -ne "none" } | ForEach-Object { "ludeon.rimworld.$_" })
# All DLCs stay "known" even when inactive; the game switches on any installed DLC it hasn't seen before.
$allExpansions = "royalty", "ideology", "biotech", "anomaly", "odyssey" | ForEach-Object { "ludeon.rimworld.$_" }
$active = @("brrainz.harmony", "ludeon.rimworld") + $expansions + $ExtraMods + @("brage.paralleltick")
$li = { param($ids) ($ids | ForEach-Object { "    <li>$_</li>" }) -join "`r`n" }
@"
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>$version</version>
  <activeMods>
$(& $li $active)
  </activeMods>
  <knownExpansions>
$(& $li $allExpansions)
  </knownExpansions>
</ModsConfigData>
"@ | Set-Content -Encoding UTF8 "$data\Config\ModsConfig.xml"

# Prefs: start from the player's own, but windowed, silent, paused on load, running in background.
[xml]$prefs = Get-Content (Join-Path $realData "Config\Prefs.xml") -Raw
$set = @{ fullscreen = "False"; screenWidth = "1280"; screenHeight = "720"; volumeMaster = "0";
          runInBackground = "True"; pauseOnLoad = "True"; devMode = "False"; resetModsConfigOnCrash = "False" }
foreach ($k in $set.Keys) {
    $node = $prefs.PrefsData.SelectSingleNode($k)
    if (-not $node) { $node = $prefs.PrefsData.AppendChild($prefs.CreateElement($k)) }
    $node.InnerText = $set[$k]
}
$prefs.Save("$data\Config\Prefs.xml")

@"
save=$(if ($QuickTest) { "@new" } else { "bench" })
mode=$(if ($Portrait) { "portrait" } elseif ($Play) { "play" } else { "bench" })
playseconds=$PlaySeconds
playopts=$($PlayOpts -eq "on")
frameprofile=$([bool]$FrameProfile)
ticksperframe=$TicksPerFrame
tickbudget=$TickBudget
playab=$PlayAB
playabseconds=$PlayABSeconds
saveas=$SaveAs
testcomp=$TestComp
testpatches=$TestPatches
dumpil=$DumpIl
noguards=$([bool]$NoGuards)
record=$([string]::Format([Globalization.CultureInfo]::InvariantCulture, "{0}", $Record))
warmup=$Warmup
ticks=$Ticks
seed=$Seed
trace=$Trace
randtrace=$RandTrace
dumpfrom=$DumpFrom
dumpto=$DumpTo
$(($Opt | ForEach-Object { "opt.$_" }) -join "`r`n")
ab=$AB
abblock=$ABBlock
breakdown=$(-not $NoBreakdown)
label=$Label
quit=true
$(($Portrait -split '\|' | Where-Object { $_ } | ForEach-Object { "portrait.$($_.Trim())" }) -join "`r`n")
"@ | Set-Content -Encoding ASCII "$data\ptbench.txt"

$resultFile = "$data\ptbench-result.txt"
$randTraceFile = "$data\ptbench-randtrace.txt"
$dumpFile = "$data\ptbench-dump.txt"
$log = "$data\Player.log"
Remove-Item $resultFile, $randTraceFile, $dumpFile, $log -ErrorAction SilentlyContinue

# Heavy background programs to wait for (up to 15 minutes) so they don't skew the measurement.
$noisy = $WaitFor
$waitUntil = (Get-Date).AddMinutes(15)
while (-not $NoWait -and $noisy.Count -gt 0 -and (Get-Process -Name $noisy -ErrorAction SilentlyContinue) -and (Get-Date) -lt $waitUntil) {
    Write-Host "Waiting for $($noisy -join '/') to exit..."
    Start-Sleep -Seconds 20
}

function Get-CpuByProcess { $t = @{}; Get-Process | ForEach-Object { if ($_.CPU) { $t["$($_.Id)"] = @($_.Name, $_.CPU) } }; $t }

Write-Host "Launching RimWorld ($Label): warmup $Warmup, measure $Ticks ticks..."
$cpuBefore = Get-CpuByProcess
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath (Join-Path $GameDir "RimWorldWin64.exe") -WorkingDirectory $GameDir -PassThru `
    -ArgumentList "-savedatafolder=$data", "-logFile", $log
# Keep background apps from stealing time from the measurement.
try { $proc.PriorityClass = [Diagnostics.ProcessPriorityClass]::High } catch { Write-Host "Could not raise priority: $_" }
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    Stop-Process -Id $proc.Id -Force
    Write-Host "Timed out after $TimeoutMinutes minutes."
}
$elapsed = $sw.Elapsed.TotalSeconds
Write-Host ("Game exited after {0:N0} s" -f $elapsed)
if ((Get-Item $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { cmd /c rmdir "$link" | Out-Null }
# Which copy of the mod did the game load? (The Workshop copy has the same packageId.)
$loadedFrom = Select-String -Path $log -Pattern "\[Free Performance\] Loaded from (.*)" | Select-Object -First 1
if ($loadedFrom -and $loadedFrom.Matches[0].Groups[1].Value -notlike "*\Mods\$(Split-Path $link -Leaf)\*") {
    Write-Host "WARNING: the game loaded another copy of the mod: $($loadedFrom.Matches[0].Groups[1].Value)"
}

# CPU used by every other process during the run, in "cores busy on average".
$cpuAfter = Get-CpuByProcess
$others = foreach ($id in $cpuAfter.Keys) {
    $name, $cpu = $cpuAfter[$id]
    if ($name -in @("RimWorldWin64", "Idle", "System")) { continue }
    $prev = if ($cpuBefore.ContainsKey($id)) { $cpuBefore[$id][1] } else { 0 }
    [pscustomobject]@{ Name = $name; Cores = ($cpu - $prev) / $elapsed }
}
$busy = $others | Group-Object Name | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Cores = ($_.Group | Measure-Object Cores -Sum).Sum } } |
    Sort-Object Cores -Descending
$total = ($busy | Measure-Object Cores -Sum).Sum
$top = ($busy | Select-Object -First 3 | ForEach-Object { "{0} {1:F2}" -f $_.Name, $_.Cores }) -join ", "
$loadLine = "background load:  {0:F2} cores busy on average (top: {1}){2}" -f $total, $top, $(if ($total -gt 0.75) { "  <-- NOISY RUN" } else { "" })

if (Test-Path $resultFile) {
    Add-Content -Path $resultFile -Value "`r`n$loadLine"
    $out = Join-Path $results ("{0:yyyyMMdd-HHmmss}-{1}.txt" -f (Get-Date), $Label)
    Copy-Item $resultFile $out
    if (Test-Path $randTraceFile) { Copy-Item $randTraceFile ($out -replace '\.txt$', '.randtrace.txt') }
    if (Test-Path $dumpFile) { Copy-Item $dumpFile ($out -replace '\.txt$', '.dump.txt') }
    if ($ExtraMods.Count -gt 0 -and (Test-Path $log)) { Copy-Item $log ($out -replace '\.txt$', '.log.txt') }
    Get-Content $resultFile
    if ($SaveAs) {
        $saveFile = "$data\Saves\$SaveAs.rws"
        if (Test-Path $saveFile) {
            $hits = Select-String -Path $saveFile -Pattern "ParallelTick" -SimpleMatch
            Write-Host ("save file check:  {0} ({1:N0} KB): {2}" -f $saveFile, ((Get-Item $saveFile).Length / 1KB),
                $(if ($hits) { "mentions ParallelTick: " + (($hits | Select-Object -First 3 | ForEach-Object { $_.Line.Trim() }) -join " | ") } else { "no trace of ParallelTick" }))
        } else { Write-Host "save file check:  $saveFile was not written" }
    }
    if ($Portrait) { Write-Host "portraits:        $data\portraits" }
    if ($Record -gt 0) { Write-Host "frames:           $datarames" }
    Write-Host "`nSaved: $out"
} else {
    Write-Host "No result file. Errors from the game log:"
    if (Test-Path $log) { Select-String -Path $log -Pattern "Free Performance|ParallelTick|Exception|Error" | Select-Object -Last 40 | ForEach-Object { $_.Line } }
    exit 1
}
