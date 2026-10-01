# Builds the clean folder that gets uploaded to the Steam Workshop: only About\ and 1.6\Assemblies\.
# The game's uploader sends a mod's whole folder, and this dev folder also holds bench data (copies of saves),
# results and source code, so never upload the dev folder itself.
#
#   powershell -File bench\make-release.ps1            build E:\RimWorldMods\FreePerformance
#   powershell -File bench\make-release.ps1 -Install   also make the game load the release folder instead of the
#                                                       dev folder (needed to upload it from the in-game mod list)
#   powershell -File bench\make-release.ps1 -Uninstall  switch the game back to the dev folder
param(
    [switch]$Install,
    [switch]$Uninstall,
    [string]$Out = "E:\RimWorldMods\FreePerformance",
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\RimWorld"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$devLink = Join-Path $GameDir "Mods\ParallelTick"
$releaseLink = Join-Path $GameDir "Mods\FreePerformance"

function Remove-Junction($path) {
    # rmdir on a junction removes only the link, never the folder it points to.
    if (Test-Path $path) {
        if (-not ((Get-Item $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$path is not a junction; not touching it." }
        cmd /c rmdir "$path" | Out-Null
    }
}

if ($Uninstall) {
    Remove-Junction $releaseLink
    Write-Host "Release folder removed from Mods (the bench script links the dev folder only while it runs)."
    return
}

if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw "Close RimWorld first." }

Push-Location (Join-Path $root "Source\ParallelTick")
try {
    # The csproj defaults point at E:\SteamLibrary; derive the game and Harmony folders from -GameDir so any Steam library works.
    $steamApps = Split-Path (Split-Path $GameDir -Parent) -Parent
    $harmonyDir = Join-Path $steamApps "workshop\content\294100\2009463077\Current\Assemblies"
    dotnet build -c Release "-p:RimWorldDir=$GameDir" "-p:HarmonyDir=$harmonyDir" | Select-String -Pattern "error|Build succeeded" | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
} finally { Pop-Location }

# Keep the Workshop id the game writes after the first upload, so later uploads update the same item.
$publishedId = Join-Path $Out "About\PublishedFileId.txt"
$savedId = if (Test-Path $publishedId) { Get-Content $publishedId -Raw } else { $null }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force "$Out\About", "$Out\1.6\Assemblies" | Out-Null
Copy-Item "$root\About\About.xml", "$root\About\Preview.png" "$Out\About\"
Copy-Item "$root\1.6\Assemblies\ParallelTick.dll" "$Out\1.6\Assemblies\"
if ($savedId) { Set-Content -Path $publishedId -Value $savedId.Trim() -NoNewline }
elseif (Test-Path "$root\About\PublishedFileId.txt") { Copy-Item "$root\About\PublishedFileId.txt" "$Out\About\" }

Write-Host "`nRelease folder: $Out"
Get-ChildItem $Out -Recurse -File | ForEach-Object { "  {0}  ({1:N0} KB)" -f $_.FullName.Substring($Out.Length + 1), ($_.Length / 1KB) }

if ($Install) {
    # Both folders have the same packageId, so only one may be in the Mods folder at a time.
    Remove-Junction $devLink
    if (-not (Test-Path $releaseLink)) { New-Item -ItemType Junction -Path $releaseLink -Target $Out | Out-Null }
    Write-Host "`nThe game now loads the release folder. Upload it from Mods (dev mode on) -> Free Performance -> Upload."
    Write-Host "Afterwards: copy About\PublishedFileId.txt back to the dev folder, then run with -Uninstall."
}
