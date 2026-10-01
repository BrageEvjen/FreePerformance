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

If another mod patches a method an optimization reasons about, the part it patches runs as vanilla (for the idle-pawn skip
and the hediff plan) or that optimization switches itself off (the others), and a line starting with `[Free Performance]`
names the mod. The mod writes nothing into save files.

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
- `workshop/` – extra pictures for the Workshop page (added by hand under Edit > Add / Edit Images & Videos); the main picture is `About/Preview.png`
- `Source/ParallelTick/` – C# source (`dotnet build -c Release` writes the DLL into `1.6/Assemblies`)
- `bench/run-bench.ps1` – runs a benchmark on a copy of a save
- `bench/make-release.ps1` – builds the clean Workshop folder (see above)
- `bench/check-allocations.ps1` – fails if the optimizations allocate far more garbage than vanilla (see Stutter)
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
- `-AB <key> -Ticks 10000` – in-process A/B of one optimization (or `all`, `defaults`, `a+b+c` with `-Opt` for the
  rest); reports time and allocation per tick, off against on
- `-AB "ablate:Verse.AI.JobDriver.DriverTick"` – skips a void method in the "on" blocks, so the difference is its true
  cost without profiler timers; `...?asleep` (JobDriver methods only) skips it for sleeping pawns' drivers only.
  The game misbehaves meanwhile; for measuring only

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

**Other mods' patches: only that part runs as vanilla (1.0.3)**: until 1.0.2, another mod patching any method the
idle-pawn skip reasons about switched the whole skip off, so heavily modded games lost most of the gain. Now each part is
handled on its own (`PawnTimeDilation.Part`):
- a patch on a part of `Pawn.Tick`'s head (path follower, verbs, roping, flight, native verbs, stances and what they
  call), on `ThingWithComps.Tick`, or on a comp type's `CompTick` makes that part run for real on skipped ticks, exactly
  where vanilla calls it. After a part ran for real, what the rest of the head's skip relies on is re-checked before the
  next part is skipped (`Head`/`Rest`); if it no longer holds, the head runs exactly as vanilla from that step
  (`VanillaHead`, which keeps vanilla's single `Spawned` check for roping, flight and native verbs).
- a patch on `Suspended`, `IsWorldPawn`, `IsHiddenFromPlayer`, `BloodRainTick` or the effecter ticks makes the skip ask or
  call them on every skipped tick, where vanilla does.
- other mods' prefixes, postfixes and finalizers on `Pawn.Tick` itself are fine: the skip is now the last prefix
  (`Priority.Last`), so their prefixes run before it just as before vanilla's body (one that skips the tick skips this
  too, since Harmony leaves out later bool prefixes), and their postfixes run after it. Only a transpiler on `Pawn.Tick`, a
  prefix that runs after this one, or a patch that reads `__runOriginal` still switches the skip off
  (`PatchGuard.ReplacedBodyProblem`).

The hediff plan works the same way: it is the last prefix on `HealthTick` (other mods' prefixes are fine), and hediff and
hediff-comp types whose `Tick`, `PostTick` or `CompPostTick` another mod patches tick as vanilla. The settings list the
parts that run as vanilla and which mod patches them.

Verify mode takes the same decisions at the same points (`Verifier.HeadCheck`): a part the skip leaves out is checked as a
no-op, a part that runs for real marks the head for a re-check, and the re-check decides whether what follows is still
checked. Tested with `-TestPatches mode:group+group` (patches under another Harmony id; `disrupt` also staggers the pawn
now and then from the patched parts, and sometimes skips the whole `Pawn.Tick` or `HealthTick`), four runs so that every
part is both patched in one run and checked right after a patched part in another:
- path follower, native verbs, `Pawn.Tick` prefix, `Suspended`, `IsWorldPawn`, egg layer comp: 0 mismatches, 140k
  would-skip ticks (38%; the staggers keep many pawns busy, and mechs were left out in this run);
- flight, `ThingWithComps.Tick`, hidden, blood rain, effecters, `HealthTick` prefix and postfix, tend comp: 0 mismatches
  in dilation (302k would-skip ticks, 82.5%) and in the hediff plan (1.32M checks);
- verbs, native verbs, stances and their handlers, `Pawn.Tick` prefix: 0 mismatches, 296k would-skip ticks (81%);
- roping, `FullBodyBusy` (so path follower and stances run for real): 0 mismatches, 315k would-skip ticks (86%).

In each run a few thousand ticks had a patched part change something the rest relies on; those ran the rest of the head
as vanilla, and the part right after was not checked for them (e.g. 3,368 of 314,672 native-verb ticks after roping).

Speed (bench A/B of all default optimizations, 20 pairs of 250 ticks, noisy PC): vanilla -13.3% +/- 2.7% (1.0.2: -14.0%,
the same within noise; the extra checks cost nothing measurable); with another mod's (inert) patches on verbs and stances
-10.2% +/- 2.8%, where 1.0.2 would have switched the pawn skip off; with every part patched and disrupting (the worst case)
-2.5% +/- 7.2%, no errors, 63% of pawn ticks still skipped and 62,880 fallbacks to vanilla.

**Known-harmless patches** (`PatchGuard.Harmless`, each read in the other mod's code): Performance Optimizer's "Faster
GetComp methods replacement" (returns the same comp through a cache) and wind transpiler (returns early only when plant
sway is off, before the loop the sway deferral replaces), and Minify Everything's `ThingOwner.DoTick` prefix (only skips
ticking minified things' contents). Per optimization (`Optimization.Accepts`, `GuardedAllowPostfixes`): the stat caches
accept VEF's gear-factor transpiler (gear is tracked) and animal-gene postfix (pawns in VEF's gene table are never
cached); the mech-control cache accepts other mods' postfixes on `State` (its own postfix runs first, so theirs adjust
vanilla's value every time, e.g. Fortified Features); the merge index accepts VEF's gourmet meal `CanStackWith` (same def
required); the delivery shortcut accepts VEF's work-giver prefix (only ever answers "no job") and no longer guards
`GenConstruct.CanConstruct` (in the state where it answers early, vanilla returns false whatever CanConstruct says, so
Replace Stuff's and VEF's patches to it can't matter); pawn bookkeeping accepts Vehicle Framework's `GetSituation`
postfix (it only changes free world pawns; the fast answers are for spawned pawns); the wealth memo accepts Big and
Small's prefix and postfix on `ForceRecount` (they only set a flag that its `Thing.MarketValue` postfix reads to change
pawns' final value; the base value per def and stuff that is reused is untouched). With the 50-mod list nothing stays
off any more; 50-mod verify (3000 ticks): 0 mismatches in all 18 verified optimizations. Measured cost of the guards before these exceptions: bench A/B with 50 mods -3.4% with
guards vs -11.1% ignoring them (`-NoGuards`, test only). The settings window shows, under each optimization, why it is off in the
current game.

**Heavy mods (1.0.4)**: 12 popular mods that change a lot (Combat Extended or Yayo's Combat 3, Facial Animation, Melee
Animation, Humanoid Alien Races with NewRatkinPlus, Big and Small, Dubs Bad Hygiene, Pick Up And Haul, Common Sense,
Alpha Animals, Vanilla Psycasts Expanded, VEF) added to the test save. Verify (1500 ticks, every optimization): 0
mismatches in all 21 with either combat mod. The idle-pawn skip applies to 88.6% of pawn ticks with Combat Extended (its
`VerbsTick` patch makes verbs run as vanilla) and 90.4% with Yayo's (vanilla save: 89.8%); only the wealth memo stayed off
(Big and Small, now accepted). Bench A/B of all default optimizations with the Combat Extended list: -8.5% +/- 3.0%
(18 of 20 pairs; off 20.0 ms, on 18.4 ms per tick; vanilla save -13.3%, off 17.2 ms), so the mods add about 3 ms per tick
of their own and the mod still takes 1.7 ms off.

Combat Extended's transpiler on `VerbTracker.VerbsTick` (read from the running game with `-DumpIl`) only adds, after each
verb's `VerbTick`, a call to `VerbTickCE` for `Verb_LaunchProjectileCE` verbs; that method is empty unless a subclass
overrides it (`Verb_ShootCE` while aiming or with a bipod, `Verb_MarkForArtillery`). The idle-pawn skip now accepts it
(`CeVerbs`) when `VerbsTick`'s current code is exactly vanilla's loop plus that call (checked once at startup, so a changed
CE version or another mod's change to the method is not accepted), and counts a verb as idle only if its type doesn't
override `VerbTickCE`. Verify with the Combat Extended list: 0 mismatches, `VerbsTick` checked as a no-op 325,606 times
with CE's code in place, 89.1% of pawn ticks skippable. Bench A/B with the CE list: -8.1% +/- 3.6% (before: -8.5% +/-
3.0%); the verbs part was a small cost, so the difference is within the noise. Final 1.0.4 build with the CE list, 3000 ticks, every
optimization in verify mode: 0 mismatches in all 21, nothing switched off, 87.8% of pawn ticks skippable.

Real play with the 1.0.4 build (Superfast with rendering, all optimizations switched on and off every 10 s, 10 minutes,
same night, ~1.3 cores of background load): the CE list 53.3 -> 60.6 TPS, +13.6% +/- 5.0% (24 of 29 pairs); vanilla
49.4 -> 56.1 TPS, +13.7% +/- 6.0% (26 of 29). Heavily modded games now gain as much as vanilla (1.0.2 with the 50-mod list:
+2.1% +/- 6.7%). Both are lower than the earlier +23% (80 -> 99 TPS): the PC ran this save much slower that night, and
with ~11 FPS rendering takes a larger share of each frame.

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

Garbage collection: vanilla allocates ~11–13 KB per tick and had 0–1 collections in 5000 ticks (Unity incremental GC is
on), so it is not a source of spikes. **1.0.5 was**: with every optimization on it allocated 88–99 KB per tick, 5–7
collections in 5000 ticks of ~15 ms each. The cause was verify-only lambdas inside methods that run thousands of times
per tick (`GasGridFastPath.Prefix` in its per-cell loop, `PawnTickBookkeeping.FastSuspended`/`FastIsWorldPawn`/
`FastBloodRainTick`, `HediffTickPlan` prefix and `FastEffecter`): a lambda that captures a variable makes the compiler
allocate a closure on entry to the scope that declares it, whether or not the lambda ever runs. In 1.0.6 the lambdas
live in their own methods. Bisected by running groups of optimizations alone (gas grid 81.7 → 11.7 KB/tick, hediff plan
34.8 → 11.7, pawn bookkeeping 26.2 → 12.4); all three still show 0 mismatches in verify mode.

With every optimization on, 1.0.6 allocates 13–29 KB per tick and has 0–2 collections in 5000 ticks. That spread is the
game, not the mod: even the off blocks of one in-process A/B vary 14–25 KB between runs, and the A/B finds no added
allocation (all optimizations, 20 pairs of 250 ticks: on −9.3 KB/tick ± 7.9 against off). So `check-allocations.ps1`
compares vanilla with all-on across launches with a wide threshold (default 40 KB/tick: 1.0.5 was +76 to +87, 1.0.6 at
most +17) and catches a leak of that size. For a smaller one use the A/B, which is blind only to allocations made in
both the on and off blocks (a closure at the entry of a patched method).

## What is left (1.0.6, same save, 20-thread PC, all optimizations on)

True costs measured with `-AB "ablate:..."` (20 pairs of 250 ticks, noisy PC with 2–2.4 cores of other load; paired, so
the drift cancels):

- Skipping every `JobDriver.DriverTick`: 3.27 → 2.39 ms/tick, **−27%** (±5%).
- Skipping it only for sleeping pawns' drivers (`?asleep`): 3.40 → 2.71 ms/tick, **−20%** (±6%). This is an upper bound
  for an exact skip of what a sleeping pawn's job tick does (about 108 pawns per tick on this save).
- The breakdown's "Job fail/end conditions" (16% of the instrumented tick) is mostly the profiler itself: its timer
  evaluates every condition delegate again and reads the closure's fields by reflection. Don't take it as a target.

## Compatibility report

Options → Mod settings → Free Performance → "Copy compatibility report to clipboard" copies, as text: the mod and game
versions, for each optimization whether it is on, off (and because of which mod's patch) or partly vanilla, and the active
mod list in load order. Nothing is sent anywhere. In the main menu the "partly vanilla" lines are still empty (they are
found when a game first uses an optimization), so ask for the report from a running game. The same text is appended to
every benchmark result.

## Reading the report

- **mean ms/tick** – simulation cost per tick with no instrumentation. Superfast (6x) needs 360 ticks/s, i.e. under 2.8 ms
  per tick *including* rendering.
- **hash at measure end** – fingerprint of the game state. RimWorld 1.6 is not repeatable across launches (see Findings),
  so hashes are only a rough check; exactness is established with verify mode.
- **breakdown** – where the time goes, measured with Harmony timers (adds overhead, so compare percentages, not totals).
