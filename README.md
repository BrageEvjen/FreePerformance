# Free Performance

A RimWorld 1.6 mod that makes large, late-game colonies run faster without changing how the game plays.
[Steam Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3808956030)

Every optimization gives exactly the same result as the unmodified game. The mod skips work that provably does nothing
(a sleeping pawn still runs its path follower, verbs, stances and comps every tick in vanilla) or reuses answers that
provably have not changed. Nothing is approximated, delayed or run on other threads.

How that is checked: every optimization has a **verify mode** that runs the original game code next to the optimized path
in the same process and counts differences. All of them show 0 differences, also with 50 other mods loaded. Speed is
measured by switching the optimizations on and off every few seconds in the same session, so background load on the PC
can't skew the comparison. Details and numbers are below.

The code was written with AI (Claude). That is why every optimization is checked against vanilla's own code as described
above, and why the source is open, so you can check it yourself.

If another mod patches a method an optimization reasons about, that optimization switches itself off and logs a line
starting with `[Free Performance]`. The mod writes nothing into save files.

The project folder and assembly are still called `ParallelTick` (the original working name); the packageId is
`brage.paralleltick`.

## Building

Requires the .NET SDK and RimWorld 1.6 with Harmony.

```
cd Source/ParallelTick
dotnet build -c Release -p:RimWorldDir="<RimWorld folder>" -p:HarmonyDir="<Harmony>/Current/Assemblies"
```

This writes `1.6/Assemblies/ParallelTick.dll`. Put (or link) this folder into RimWorld's `Mods` folder.

The code is in `Source/ParallelTick/Optimizations` (one file per optimization, each with its own guard and verify mode) and
`Source/ParallelTick/Bench` (the benchmark and test harness; it only does anything when a `ptbench.txt` file exists in the
save data folder, which only the bench scripts create).

## Releasing to the Steam Workshop

The game's uploader sends the mod's whole folder (`SteamUGC.SetItemContent` on the mod directory, no exclusions), and a
working copy also holds `bench/data` (copies of saves), results and source. So never upload the working folder:

1. `powershell -ExecutionPolicy Bypass -File bench\make-release.ps1 -Install` builds a clean release folder (only `About\`
   and `1.6\Assemblies\`) and makes the game load it instead of the working folder (same packageId, so only one may be
   in `Mods` at a time).
2. In RimWorld: Options → dev mode on → Mods → Free Performance → Advanced... → Upload.
3. Copy `About\PublishedFileId.txt` from the release folder back into this `About\` (later uploads then update the same
   item), then `bench\make-release.ps1 -Uninstall` to switch the game back to the working folder.

## Layout

- `About/`, `1.6/Assemblies/` – the RimWorld mod (linked into the game's `Mods` folder as a junction)
- `Source/ParallelTick/` – C# source (`dotnet build -c Release` writes the DLL into `1.6/Assemblies`)
- `bench/run-bench.ps1` – runs a benchmark on a copy of a save
- `bench/make-release.ps1` – builds the clean Workshop folder (see above)
- `bench/results/` – one text file per benchmark run (not in the repository)
- `tools/Inspect/` – lists types, methods and call graphs in the game's `Assembly-CSharp.dll`

## Running a benchmark

```
powershell -ExecutionPolicy Bypass -File bench\run-bench.ps1 -Label vanilla
```

The script copies the save into `bench/data` and starts RimWorld with `-savedatafolder` pointed there, so your real
config, mod list and saves are never touched. Only Harmony, Core, the DLCs and this mod are active. The game loads the save,
runs a warmup, times a fixed number of ticks with a fixed random seed, runs an instrumented breakdown, writes the report
and quits.

Useful options: `-Save "<save name>"`, `-Ticks 5000`, `-Warmup 2500`, `-NoBreakdown`, `-ExtraMods <packageId>,...`.

Diagnostics (all optional):

- `-Trace 25` – state fingerprint every 25 ticks; compare two runs with `bench\compare-trace.ps1 a.txt b.txt`
- `-RandTrace 120` – every simulation `Rand` call (with caller) for the first 120 ticks; compare with
  `bench\compare-randtrace.ps1 a.randtrace.txt b.randtrace.txt`
- `-DumpFrom <tick> -DumpTo <tick>` – every thing's state for those ticks; compare with
  `bench\compare-dump.ps1 a.dump.txt b.dump.txt`

## Findings so far (Teroum save, Ryzen 5 2600X)

- Vanilla 1.6 simulation cost: ~7.5 ms/tick → ~133 TPS max before rendering (6x speed needs 360).
- 82% of a tick is thing ticks; pawns are ~67% (colonists 29%, colony animals 14% – almost all chickens –,
  mechs 13%, wild animals 10%). Job driver ticks 15%, stat calculation 7%, path following 6%. Work scanning
  (JobGiver_Work) is only ~2%.
- **Vanilla is not repeatable across launches**, even with the same save and RNG seed:
  1. `FishShadowComponent.MapComponentTick` (Odyssey, visual only) staggers spawns by `WaterBody.GetHashCode()`,
     which mixes in `Map`'s identity hash code, and it draws from the simulation `Rand`. Different launch → different
     number of rolls → every later roll shifts. Shimmed in benchmark mode (`DeterminismShims`).
  2. Even with that shimmed, animals pick different wander destinations from identical random rolls ~45 ticks in,
     i.e. candidate ordering differs between launches (likely region/link ordering). Not chased further.
  Consequence: correctness is checked by running each optimization side by side with the vanilla code *in the same
  process* and comparing results (verify mode), not by comparing hashes across launches.
- Separate launches vary ~5% in ms/tick, and background load (other programs) can add far more, so small wins are
  measured with the in-process A/B (`-AB <name> -Ticks 10000`): the optimization is switched off/on every 250 ticks and
  adjacent blocks are compared. With 20 pairs it resolves differences of ~1.7%.
- The instrumented breakdown overstates small, frequently called functions (the timers themselves cost time), so
  candidates are re-checked with the A/B before counting on them.

## What grows with a colony (7 saves compared)

`bench/profile-saves.ps1` profiles several saves in a row and `bench/summarize-profiles.py` puts them side by side.
Across seven of the author's saves (early game to ~700 days, vanilla + DLCs):

- **Pawns are the bulk everywhere** (typically 60–85% of the tick): wild animals (up to 50% on maps full of them),
  colonists (up to ~50% with 24–33 colonists), tame animals (chickens), mechs. Each pawn costs something every tick,
  even standing still — hence `dilation`.
- **The world tick is 6–17%** in several saves: world pawns up to 14% (each off-map pawn checks every tick whether it is
  suspended, which walks factions and every active quest's parts), world objects 2–5%, ideologies 1.5–2.3% (every role
  re-validates its holder every tick).
- Buildings are 2–12%; blueprints/frames and items are small unless there are hundreds.

## Optimizations

In normal play each verified optimization can be toggled under Options → Mod settings → ParallelTick (takes effect
immediately). In benchmark mode the settings are ignored and `ptbench.txt` decides.

| Name | What | Verified | A/B result | Default |
|---|---|---|---|---|
| `tempcache` | Cache `ComfortableTemperatureRange(Pawn)` per pawn until apparel/hediffs/genes/traits/life stage change (max 250 ticks) | 0 mismatches in 111k–183k checks | −1.8% ms/tick (95% CI ±1.7%) | on |
| `solar` | Cache `CompPowerPlantSolar.RoofedPowerOutputFactor` until the map's roof grid changes or the panel moves (exact: `SetRoof`/`RemoveRoofUnsafe` are the only roof writers) | 0 mismatches in 352k checks | −2.0% ±3.7% (noisy runs) | on |
| `statcache` | Cache whitelisted stats per (thing, stat): pawn stats until the pawn changes, furniture stats for 250 ticks | 0 mismatches after removing MoveSpeed, VacuumResistance (untracked changes) and Beauty (bookcases change with contents) | batch | on |
| `musiccheck` | Skip `ThoughtWorker_MusicalInstrumentListeningBase`'s closest-instrument search when no instrument of that def on the map is being played (exact) | 0 mismatches, 96.7% of calls skipped | batch | on |
| `deliveryearly` | In `WorkGiver_ConstructDeliverResourcesTo{Frames,Blueprints}.HasJobOnThing`, answer "no job" up front for non-forced searches when there is no blocker/floor/install step and `CanGetResources_NewTemp` is false (every vanilla path then returns false) | 0 mismatches in 9.5k checks | frame searches 12.4→1.5 ms, blueprint 6.1→2.3 ms | on |
| `mergeindex` | Transpile `WorkGiver_Merge.JobOnThing`'s `HeldThings` loop to iterate only same-def things (all `CanStackWith` overrides require the same def), indexed once per storage group per work search | 0 mismatches in 736 searches | merge searches 10.3→3.7 ms | on |
| `windsway` | `WindManagerTick` calls `Material.SetFloat` on every plant material (344 on the test save) every tick; only the last value before drawing is ever seen, so it is applied once per frame (`Map.MapUpdate` prefix, `TickManagerUpdate` postfix). Simulation untouched | transpiler applied; vanilla loop timed at **0.30–0.32 ms/tick** | pending | on |
| `overseer` | Cache `CompOverseerSubject.State` while the mech's relation list, the overseer's mechanitor and its controlled-pawn list are unchanged (`List._version`); skip `CompTick` when it provably does nothing | 0 mismatches in 1.73M checks (~290 lookups per tick) | pending | on |
| `haulables` | Non-slot-group haul destinations pre-filtered per list version; `CellsCheckTick` recalculates each cell once per call (it visited 1-cell shelves 4×) | 0 mismatches in 110k checks | pending | on |
| `dilation` | Pawns standing still (asleep, idle, working at a bench) run a copy of `Pawn.Tick` that skips sub-systems proven to be no-ops in that state (path follower, verbs, stances, comps, world-pawn lookups, effecters…) and calls everything else exactly as vanilla, in order (the real job tracker tick, health, equipment, abilities…). Checked every tick; any change or a `TickRare` tick runs vanilla | 0 mismatches in 588k skipped pawn-ticks (every skipped call run and compared in verify mode). Moving pawns run the real `PatherTick` and skip the rest; if moving changed anything the later systems depend on, the rest of the head runs as vanilla. Skips 88% of pawn ticks on the test save (animals 87%, humanlikes 92%, mechs 87%) | pending | on |
| `questreserve` | World pawns' per-tick "reserved by a quest?" check asks only the quest parts whose type can reserve anyone (base `QuestPartReserves` returns false), rebuilt per quest when its part list changes | 0 mismatches (few world pawns alive on the test save; Youin: ~100 checks/tick) | pending | on |
| `predraw` | `PawnRenderTree.Draw` skips the base `PreDraw` colour write, which `GetMaterialPropertyBlock` always overwrites (visual only) | transpiler applied; 0 errors in play | frame-side, pending | on |
| `unspawnedmemo` | Within one colonist-bar rebuild or alerts update, `MapPawns.AllPawnsUnspawned` (walks every container on the map, ~3 ms) reuses the first result while the shared result list is unchanged | play test: LowBabyFood 6.0→2.6 ms, CubeWithdrawal 5.5→2.5 ms per recalculation | frame-side | on |
| `alertskip` | Low baby food, abandoned baby, cube withdrawal, ghoul hypothermia and starving animals alerts walk every container on every map (~2.5 ms each, a frame spike every few frames). A registry of every `Pawn` object (constructor postfix, weak references) answers "does any live pawn anywhere have the property at all?"; if not, the alert's empty result is set exactly as vanilla leaves it. Doubt (uncomputed life stage, exception) runs vanilla | verify postfixes check vanilla finds nothing when predicted | frame-side | on |
| `settlementtick` | World settlements (250 on the test save) are ticked every tick; on non-interval ticks, with only empty-`CompTick` comps and no trade stock, `WorldObject.DoTick` only increments `tickDelta` — so that is all that runs | exact by construction (interval ticks and any stock run vanilla) | pending | on |
| `pawnbook` | `Suspended`/`IsWorldPawn` answered false for spawned pawns (never world pawns), blood-rain check skipped without a blood-rain condition, gene tick loop skipped (`Gene.Tick` is empty in every gene type) | 0 mismatches in 2.84M checks | pending | on |
| `hediffplan` | `HealthTick` ticks only hediffs whose comps can do something (injury/scar tends are permanent, most comps have empty `CompPostTick`); effecter lookup answered from a per-def table | 0 mismatches in 2.42M checks | pending | on |
| `gasgrid` | `GasGrid.Tick`'s ~2,900 per-cell calls: the "no gas here" early returns done inline | 0 mismatches in 9.67M cells | pending | on |
| `reservedup` | `CanReserve` scanned all reservations twice with identical arguments; the second (always false) is dropped | 0 mismatches in 18.8k checks | pending | on |
| `contentsskip` | Pawns', frames' and bookcases' contents walk skipped when every contained thing is a no-op this tick (Never tickers, Rare/Long off their hash) | 0 mismatches in 1.1M checks; 95% of pawn and 100% of frame walks skipped | pending | on |
| `wealthmemo` | The colony-wealth recount (every 5000 ticks, a stutter) valued every wall and conduit separately; within one recount `CalculatedBaseMarketValue(def, stuff)` — static, reads only its arguments and game data — is computed once per pair and reused, memo dropped when the recount ends | 0 mismatches in 55.9k checks; recounted wealth bit-identical | recount 63 → 41 ms (−35%, 5,684 buildings) | on |

Batch A/B results (in-process, pairs of 250 ticks):
- `-AB defaults` with all 17 default-on optimizations (10k ticks, 20 pairs): **−13.4% ms/tick** (95% CI ±3.1%, faster in
  19/20 pairs), with ~3 cores of background load. `dilation` skipped 91% of pawn ticks.
- Same, before moving pawns were included (6k ticks, 12 pairs): −11.4% (±4.2%, 12/12), ~6.5 cores of background load.

Older (first eight optimizations, 40 pairs):
- `-AB defaults` with all eight (seven on by default): **−7.0% ms/tick** (95% CI ±4.2%, faster in 28/40 pairs) — but with
  ~4.4 cores of background load (ticks ~16 ms instead of ~7), so treat the size as rough.
- `-AB defaults` (tempcache, solar, statcache, bedcheck, musiccheck): **−3.5% ms/tick** (95% CI ±2.8%, faster in 30/40 pairs;
  ~1.8 cores of background load during the run).
- `-AB all` before musiccheck existed (tempcache, solar, statcache, bedcheck, moodrefresh): −4.6% (±4.2%).

Removed: every optimization is now exact. The approximate ones were dropped — `bedcheck` (bed check every 30 ticks:
−0.4% ± 3.2%, no measurable gain), `moodrefresh` (situational thoughts every 300 ticks) and `camerarate`.

Every exact optimization also checks at first use that no other mod patches the methods it reasons about, and stays off
(with a log line) if one does. Checked together with Performance Optimizer (Taranchuk.PerformanceOptimizer, default
settings): no conflicts reported, all optimizations active, `dilation` verify 0 mismatches in 322k skipped pawn-ticks.

**50-mod compatibility test** (`-ExtraMods`, the test save; Vanilla Expanded Framework, Adaptive Storage, Deep And Deeper,
Replace Stuff, Dead Man's Switch, Ascension Megacorp, RimHUD, Performance Optimizer, Dubs Performance Analyzer, Blood
Animations and 40 more; Multiplayer, RimThreaded and Slower Pawn Tick Rate not included). First run: 0 mismatches, no
errors from this mod, but three optimizations switched themselves off:
- `dilation` and `hediffplan`: Blood Animations patches `HealthTick` with a postfix that calls `Rand.Value` for every
  living flesh pawn, hediffs or not — so skipping `HealthTick` for a pawn without hediffs would shift the random stream.
  Now `dilation` calls `HealthTick` for every pawn when another mod patches it (instead of switching off), and
  `hediffplan` accepts foreign postfixes on `HealthTick` (they run after the replicated body as after vanilla's; Harmony
  runs postfixes when a prefix skips the original), unless they read `__runOriginal`.
- `wealthmemo`: Performance Optimizer caches `CalculableRecipe` per def; that method takes only the def, so it no longer
  blocks the memo.

Second run: all optimizations active, 0 mismatches (dilation 275k skipped pawn-ticks, hediffplan 1.32M, wealthmemo 55.9k
checks). The only load errors come from the save itself: it was once played with FPS+/RimThreaded, whose game components
are still in it.

**Without DLCs**: a freshly generated Core-only colony (`-QuickTest -Dlcs none`, the same path as the main menu's dev
"Quick test" button; the `-quicktest` command line fails in 1.6) ran 60 s at Superfast with 0 errors and 0 warnings, and a
5000-tick verify run showed 0 mismatches in every optimization (dilation 232k skipped pawn-ticks, hediffplan 339k,
contentsskip 402k).

**Guards for every optimization**: each optimization lists the game methods it reasons about (`Optimization.Guarded`,
plus an optional `BlockReason`); if another mod patches one of them, `Active`/`Verifying` turn false and a line starting
with `[Free Performance]` names the mod. Besides reasoning that may no longer hold, this prevents a cached answer from
skipping or repeating another mod's postfix. The stat caches also leave alone any stat whose worker or parts come from
another mod, and the merge index stays off if a mod's Thing subclass has its own `CanStackWith`.

**Comps from other mods (1.0.2)**: a user report showed the idle-pawn skip never applied on a 229-mod list, because mods
like Facial Animation, Melee Animation and TKS Ragdoll put a ticking comp on every pawn and unknown comps made a pawn
ineligible. Now unknown comps (`CompKind.Foreign`) tick exactly as in vanilla, in order, with vanilla's count-read-once
loop; every comp after one is re-checked where it stands, and before the rest of the tick is skipped everything the skip
relies on is re-checked (`IdleAfterComps`). If anything changed, or the comp list itself changed, the rest of `Pawn.Tick`
runs exactly as vanilla (`VanillaAfterComps`, including the suspended branch). Verify mode mirrors this (a postfix on
`ThingWithComps.Tick` decides at the same point). Vanilla save: 0 mismatches, 89.7% of pawn ticks skippable; mechs with
turret guns, shields and carriers now qualify too.

Tested without the mods themselves with `-TestComp` (a ticking comp added to every pawn race, standing in for animation
mods): `inert` only counts ticks; `disrupt` also staggers its pawn every 97 ticks. Verify mode with `disrupt`: 0
mismatches in 265,795 would-skip ticks of pawns with the comp, 3,387 of them with a change the skip relies on (rest
verified as vanilla). Normal mode: 72.6% of pawn ticks skipped, 346k comp ticks run as vanilla, 4,310 vanilla fallbacks,
no errors. 50 real mods (VEF's CompAbilities on pawns): 0 mismatches, skippable pawn ticks 61% -> 74.6%.

**Known-harmless patches** (`PatchGuard.Harmless`, each read in the other mod's code): Performance Optimizer's "Faster
GetComp methods replacement" (returns the same comp through a cache) and Minify Everything's `ThingOwner.DoTick` prefix
(only skips ticking minified things' contents). The settings window shows, under each optimization, why it is off in the
current game.

**Save files**: the mod's game components are taken out of the component list while a game is saved and put back after
(the game recreates missing ones on load), so a save written with the mod has no trace of it except the mod list in the
header. It can be added to or removed from a running save. Checked with `-SaveAs` (saves at the end of a run).

## Frame budget (real play)

At high speed RimWorld runs ticks for at most 45.45 ms per frame, then draws the frame. In the test colony at Superfast a
frame was ~98 ms: ~55 ms of ticks, ~21 ms of drawing, ~12 ms engine, ~7 ms UI, ~2 ms alerts. So game speed is limited by
frames as much as by tick cost. Raising that cap gives more ticks per frame at a lower frame rate (measured below); it is
a trade-off rather than a free gain, so it is only available in the test harness (`-TickBudget`), not as a player setting.

Real-play smoke test with all default optimizations on (60 s at Superfast, ~3 cores of background load): 0 errors,
0 warnings, 47 TPS, 91 ms frames (ticks 54 ms for 4.3 ticks, drawing 18 ms, UI 6 ms).

**In-play A/B** (`-Play -PlayAB <subject>`: one play session at Superfast, the subject switched every 10 s, adjacent
blocks compared; robust against background load drifting between runs). Test save, 300 s each:

| Subject | TPS off → on | FPS off → on | Result |
|---|---|---|---|
| all default optimizations | 80.2 → 98.8 | 12.9 → 13.3 | **+23.2% game speed** (95% CI ±11.1%), faster in 13/14 pairs |
| all optimizations, **with 50 mods** incl. Performance Optimizer (on in both halves) | 82.9 → 95.8 | 12.6 → 13.1 | **+15.5%** (±7.1%), faster in 13/14 pairs, 0 errors, 0 warnings |
| frame budget 45 → 90 ms | 71.2 → 81.6 | 12.1 → 8.0 | +14.6% (±11.8%), but a third fewer frames |

Why a tick costs more in real play than in the benchmark: pawns in view run their interval update every 1–5 ticks
(`GenTicks.GetCameraUpdateRate` = zoom + 1) instead of every 15, so watching the colony multiplies that work for every
colonist and mech on screen. An approximate option that used the off-screen rate everywhere (`camerarate`) gave no
measurable gain in play (−3.2% ± 12.3%) and was removed.

## Stutter (slowest ticks)

The slowest 1% of ticks are mostly a single pawn's successful work search (`JobGiver_Work`): ~12 ms average for mechs
and ~18 ms for colonists in the instrumented run, with material delivery to blueprints (`DeliverResourcesToBlueprints`)
averaging ~17 ms per search. Searches that find nothing are cheap (~0.3–1 ms). Other spike sources: occasional mood
recalculations with an expensive stat, and rare expensive job fail conditions.

GC is not a source of spikes: ~23 KB allocated per tick, 0 collections in 5000 ticks (Unity incremental GC is on).

## Reading the report

- **mean ms/tick** – simulation cost per tick with no instrumentation. Superfast (6x) needs 360 ticks/s, i.e. under 2.8 ms
  per tick *including* rendering.
- **hash at measure end** – fingerprint of the game state. RimWorld 1.6 is not repeatable across launches (see Findings),
  so hashes are only a rough check; exactness is established with verify mode.
- **breakdown** – where the time goes, measured with Harmony timers (adds overhead, so compare percentages, not totals).
