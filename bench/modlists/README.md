Mod lists for `run-bench.ps1 -ExtraMods` (comma-separated Workshop packageIds, loaded after the DLCs in this order).
The mods must be subscribed on Steam; the bench loads them from the Workshop folder.

- `compat50.txt`: the 50-mod compatibility list (VEF, Performance Optimizer, Replace Stuff, Blood Animations, ...).
- `heavy-ce.txt`: 12 heavy mods with Combat Extended (Facial Animation, Melee Animation, Humanoid Alien Races + NewRatkinPlus,
  Big and Small, Dubs Bad Hygiene, Pick Up And Haul, Common Sense, Alpha Animals, Vanilla Psycasts Expanded, VEF).
- `heavy-yayo.txt`: the same with Yayo's Combat 3 instead of Combat Extended (the two don't go together).

Example: `powershell -File bench\run-bench.ps1 -Label x -ExtraMods (Get-Content bench\modlists\heavy-ce.txt)`
