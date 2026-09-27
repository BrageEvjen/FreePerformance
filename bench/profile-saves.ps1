<#
Profiles several saves in a row (vanilla, all optimizations off, with the instrumented breakdown) to see what grows
with colony size. Waits for any running RimWorld first. Each save runs on its own copy in bench\data.

  powershell -ExecutionPolicy Bypass -File bench\profile-saves.ps1
#>
param(
    [string[]]$Saves = @(
        "Northwest Finlium League (Permadeath)",
        "Southern Itora (Permadeath)",
        "Hinlia (Permadeath)",
        "Union of Thiistan (Permadeath)",
        "The New Hope (Permadeath)",
        "Space Peace Keepers (SPK) (Permadeath)",
        "Youin (Permadeath)",
        "East Kaum (Permadeath)"
    ),
    [int]$Warmup = 600,
    [int]$Ticks = 2000
)
$Saves = @($Saves | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

foreach ($save in $Saves) {
    while (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 20 }
    $label = "profile-" + (($save -replace '\(Permadeath\)', '' -replace '[^A-Za-z0-9]+', '-').Trim('-'))
    Write-Host "=== $save"
    & (Join-Path $PSScriptRoot "run-bench.ps1") -Save $save -Label $label -Warmup $Warmup -Ticks $Ticks -NoWait -TimeoutMinutes 30 |
        Select-String -Pattern "mean:|Saved:|No result|Timed out" | ForEach-Object { $_.Line }
}
