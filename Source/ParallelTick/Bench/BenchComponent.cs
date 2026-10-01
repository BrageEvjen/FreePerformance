using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using ParallelTick.Optimizations;
using RimWorld;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Drives the benchmark once the save has loaded: warmup, timed measurement, optional instrumented
    /// breakdown, then writes ptbench-result.txt and quits. The game stays paused; ticks are run by calling
    /// TickManager.DoSingleTick directly, a fixed number per frame so frame boundaries land on the same
    /// ticks every run.
    /// </summary>
    public class BenchComponent : GameComponent
    {
        private enum Phase { WaitForLoad, Warmup, Measure, Breakdown, Done }

        private const int SettleFrames = 60;
        private static int TicksPerFrame => BenchConfig.TicksPerFrame;

        // Rand's push/pop stack is emptied by Root.Update every frame, so the state is saved here instead.
        private static readonly Func<ulong> GetRandState =
            AccessTools.MethodDelegate<Func<ulong>>(AccessTools.PropertyGetter(typeof(Rand), "StateCompressed"));
        private static readonly Action<ulong> SetRandState =
            AccessTools.MethodDelegate<Action<ulong>>(AccessTools.PropertySetter(typeof(Rand), "StateCompressed"));

        private Phase phase = Phase.WaitForLoad;
        private int framesWaited, ticksLeft;
        private ulong simRandState;
        private bool randStashed;
        private int warmupStartTick, measureStartTick, measureEndTick, randTraceEndTick;
        private StateHash.Parts hashAtWarmupStart, hashAtMeasureEnd;
        private readonly List<string> trace = new List<string>();
        private readonly List<double> gcTickMs = new List<double>();
        private long allocatedBytes;
        /// <summary>KB allocated by each measured tick (NaN when a collection ran during it), in tick order like measureMs.</summary>
        private readonly List<double> allocKb = new List<double>();
        private readonly List<double> measureMs = new List<double>();
        private readonly List<double> breakdownMs = new List<double>();
        private readonly Stopwatch wall = new Stopwatch();

        public BenchComponent(Game game)
        {
        }

        public override void GameComponentUpdate()
        {
            if (!BenchConfig.Active || BenchConfig.Mode != "bench" || phase == Phase.Done)
                return;
            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting)
                return;

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Finish("FAILED: " + e);
            }
        }

        private static bool ilDumped;

        /// <summary>dumpil: each named method's instructions as they run now, with every mod's transpilers applied.</summary>
        private static void DumpIl()
        {
            if (ilDumped || BenchConfig.DumpIl == "")
                return;
            ilDumped = true;
            foreach (var name in BenchConfig.DumpIl.Split(';'))
            {
                var method = AccessTools.Method(name.Trim());
                if (method == null)
                {
                    Log.Message($"[Free Performance] dumpil: {name} not found");
                    continue;
                }
                var owners = Harmony.GetPatchInfo(method)?.Owners;
                var sb = new StringBuilder();
                sb.AppendLine($"[Free Performance] dumpil {name} (patched by {(owners == null ? "nobody" : string.Join(", ", owners))}):");
                var i = 0;
                foreach (var ins in PatchProcessor.GetCurrentInstructions(method))
                    sb.AppendLine($"  {i++,4} {ins}");
                Log.Message(sb.ToString());
            }
        }

        private void Step()
        {
            DumpIl();
            switch (phase)
            {
                case Phase.WaitForLoad:
                    if (++framesWaited < SettleFrames)
                        return;
                    Rand.Seed = BenchConfig.Seed;
                    warmupStartTick = Find.TickManager.TicksGame;
                    hashAtWarmupStart = StateHash.Compute();
                    if (BenchConfig.RandTraceTicks > 0)
                    {
                        RandTracer.Attach(ParallelTickMod.Harmony);
                        RandTracer.Active = true;
                        randTraceEndTick = warmupStartTick + BenchConfig.RandTraceTicks;
                    }
                    StashRand();
                    phase = Phase.Warmup;
                    ticksLeft = BenchConfig.WarmupTicks;
                    wall.Start();
                    return;

                case Phase.Warmup:
                    RunChunk(null);
                    if (ticksLeft > 0)
                        return;
                    phase = Phase.Measure;
                    ticksLeft = BenchConfig.MeasureTicks;
                    measureStartTick = Find.TickManager.TicksGame;
                    return;

                case Phase.Measure:
                    RunChunk(measureMs);
                    if (ticksLeft > 0)
                        return;
                    measureEndTick = Find.TickManager.TicksGame;
                    if (AbActive)
                        SetAb(true);
                    UnstashRand();
                    hashAtMeasureEnd = StateHash.Compute();
                    StashRand();
                    if (!BenchConfig.Breakdown)
                    {
                        Finish(null);
                        return;
                    }
                    BenchProfiler.Attach(ParallelTickMod.Harmony);
                    phase = Phase.Breakdown;
                    ticksLeft = BenchConfig.MeasureTicks;
                    return;

                case Phase.Breakdown:
                    RunChunk(breakdownMs);
                    if (ticksLeft > 0)
                        return;
                    Finish(null);
                    return;
            }
        }

        private void RunChunk(List<double> record)
        {
            // Rendering between frames also draws from Rand; restore the simulation's RNG state so that
            // frame timing cannot change the outcome, then stash it again before handing the frame back.
            UnstashRand();
            var tm = Find.TickManager;
            var n = Math.Min(TicksPerFrame, ticksLeft);
            var toMs = 1000.0 / Stopwatch.Frequency;
            var tracing = BenchConfig.TraceInterval > 0 && phase != Phase.Breakdown;
            var ab = AbActive && phase != Phase.Breakdown;
            for (var i = 0; i < n; i++)
            {
                // In-process A/B: alternate off/on every AbBlockTicks (warmup included, so both paths are JIT-warm).
                // Adjacent off/on blocks see nearly the same game state, which cancels most of the drift.
                if (ab)
                {
                    var done = (phase == Phase.Warmup ? BenchConfig.WarmupTicks : BenchConfig.MeasureTicks) - ticksLeft + i;
                    SetAb((done / BenchConfig.AbBlockTicks) % 2 == 1);
                }
                long[] slotsBefore = null;
                if (phase == Phase.Breakdown)
                {
                    slotsBefore = BenchProfiler.SnapshotSlots();
                    BenchProfiler.MaxThingElapsed = 0;
                    BenchProfiler.MaxThing = null;
                }
                RandTracer.InTick = true;
                var measuring = phase == Phase.Measure;
                var heapBefore = measuring ? Profiler.GetMonoUsedSizeLong() : 0;
                var gcBefore = measuring ? GC.CollectionCount(0) : 0;
                var start = Stopwatch.GetTimestamp();
                tm.DoSingleTick();
                var ms = (Stopwatch.GetTimestamp() - start) * toMs;
                record?.Add(ms);
                RandTracer.InTick = false;
                if (slotsBefore != null)
                    RecordSpike(tm.TicksGame, ms, slotsBefore);
                if (measuring)
                {
                    // Heap growth during a tick is its allocation, unless a collection ran inside the tick.
                    if (GC.CollectionCount(0) != gcBefore)
                    {
                        gcTickMs.Add(ms);
                        allocKb.Add(double.NaN);
                    }
                    else
                    {
                        var grown = Math.Max(0, Profiler.GetMonoUsedSizeLong() - heapBefore);
                        allocatedBytes += grown;
                        allocKb.Add(grown / 1024.0);
                    }
                }
                if (tracing && tm.TicksGame % BenchConfig.TraceInterval == 0)
                    trace.Add($"{tm.TicksGame} {StateHash.Compute()}");
                if (tm.TicksGame >= BenchConfig.DumpFrom && tm.TicksGame <= BenchConfig.DumpTo)
                    File.AppendAllLines(Path.Combine(GenFilePaths.SaveDataFolderPath, "ptbench-dump.txt"), StateHash.Describe());
                if (RandTracer.Active && tm.TicksGame >= randTraceEndTick)
                {
                    RandTracer.Detach(ParallelTickMod.Harmony);
                    File.WriteAllLines(Path.Combine(GenFilePaths.SaveDataFolderPath, "ptbench-randtrace.txt"), RandTracer.Lines);
                }
            }
            ticksLeft -= n;
            StashRand();
        }

        private const int SpikesKept = 20;
        private readonly List<(double ms, string line)> spikes = new List<(double, string)>();

        /// <summary>Keeps the slowest breakdown ticks with the profiler slots that grew most during each.</summary>
        private void RecordSpike(int tick, double ms, long[] slotsBefore)
        {
            if (spikes.Count >= SpikesKept && ms <= spikes[spikes.Count - 1].ms)
                return;
            var toMs = 1000.0 / Stopwatch.Frequency;
            var slots = BenchProfiler.Slots;
            var top = Enumerable.Range(0, slotsBefore.Length)
                .Select(i => (slot: slots[i], delta: (slots[i].Elapsed - slotsBefore[i]) * toMs))
                .Where(x => x.delta > 0.05 && x.slot.Group != "Tick sections")
                .OrderByDescending(x => x.delta)
                .Take(6)
                .Select(x => $"{x.slot.Label} {x.delta:F1}");
            var thing = BenchProfiler.MaxThing;
            var line = $"tick {tick}  {ms,6:F1} ms | {string.Join(", ", top)}" +
                       (thing == null ? "" : $" | slowest thing: {thing.LabelShort} ({thing.def.defName}) {BenchProfiler.MaxThingElapsed * toMs:F1} ms");
            spikes.Add((ms, line));
            spikes.Sort((a, b) => b.ms.CompareTo(a.ms));
            if (spikes.Count > SpikesKept)
                spikes.RemoveAt(spikes.Count - 1);
        }

        private static bool AbActive => BenchConfig.AbTarget != "";

        private static void SetAb(bool on)
        {
            if (BenchConfig.AbTarget.StartsWith("ablate:"))
                Ablation.Skip = on;
            else if (BenchConfig.AbTarget == "all" || BenchConfig.AbTarget == "defaults")
                foreach (var opt in OptimizationRegistry.All)
                    opt.Enabled = on && (BenchConfig.AbTarget == "all" || opt.DefaultOn);
            else if (BenchConfig.AbTarget.Contains("+"))
                foreach (var key in BenchConfig.AbTarget.Split('+'))
                    OptimizationRegistry.Get(key).Enabled = on;
            else
                OptimizationRegistry.Get(BenchConfig.AbTarget).Enabled = on;
        }

        /// <summary>Paired comparison of adjacent off/on blocks from the measurement phase.</summary>
        private void AppendAbReport(StringBuilder sb)
        {
            var block = BenchConfig.AbBlockTicks;
            var blockMeans = new List<double>();
            for (var start = 0; start + block <= measureMs.Count; start += block)
                blockMeans.Add(measureMs.Skip(start).Take(block).Average());
            var diffs = new List<double>();
            var offMeans = new List<double>();
            for (var k = 0; k + 1 < blockMeans.Count; k += 2)
            {
                offMeans.Add(blockMeans[k]);
                diffs.Add(blockMeans[k + 1] - blockMeans[k]);
            }
            if (diffs.Count < 2)
            {
                sb.AppendLine("A/B: not enough blocks (need at least 4)");
                return;
            }
            var offMean = offMeans.Average();
            var d = diffs.Average();
            var sd = Math.Sqrt(diffs.Sum(x => (x - d) * (x - d)) / (diffs.Count - 1));
            var se = sd / Math.Sqrt(diffs.Count);
            sb.AppendLine($"== In-process A/B: {BenchConfig.AbTarget}, {diffs.Count} off/on block pairs of {block} ticks");
            sb.AppendLine($"off mean:         {offMean:F3} ms");
            sb.AppendLine($"on mean:          {offMean + d:F3} ms");
            sb.AppendLine($"difference:       {d:+0.000;-0.000} ms ({100 * d / offMean:+0.0;-0.0}%), 95% CI +/-{1.96 * se:F3} ms (+/-{100 * 1.96 * se / offMean:F1}%)");
            sb.AppendLine($"on faster in:     {diffs.Count(x => x < 0)} of {diffs.Count} pairs");

            var allocOff = new List<double>();
            var allocDiffs = new List<double>();
            for (var k = 0; k + 1 < blockMeans.Count; k += 2)
            {
                var off = BlockAllocKb(k * block, block);
                var on = BlockAllocKb((k + 1) * block, block);
                if (double.IsNaN(off) || double.IsNaN(on))
                    continue;
                allocOff.Add(off);
                allocDiffs.Add(on - off);
            }
            if (allocDiffs.Count >= 2)
            {
                var ad = allocDiffs.Average();
                var asd = Math.Sqrt(allocDiffs.Sum(x => (x - ad) * (x - ad)) / (allocDiffs.Count - 1));
                var ase = asd / Math.Sqrt(allocDiffs.Count);
                sb.AppendLine($"allocation:       off {allocOff.Average():F1} KB/tick, on {allocOff.Average() + ad:F1} KB/tick, " +
                              $"difference {ad:+0.0;-0.0} KB/tick (95% CI +/-{1.96 * ase:F1}), {allocDiffs.Count} pairs without a collection");
            }
            sb.AppendLine();
        }

        private double BlockAllocKb(int start, int count)
        {
            var sum = 0.0;
            var n = 0;
            for (var i = start; i < start + count && i < allocKb.Count; i++)
            {
                if (double.IsNaN(allocKb[i]))
                    continue;
                sum += allocKb[i];
                n++;
            }
            return n < count / 2 ? double.NaN : sum / n;
        }

        private static readonly Func<WealthWatcher, float> WealthItemsOf =
            AccessTools.MethodDelegate<Func<WealthWatcher, float>>(AccessTools.Method(typeof(WealthWatcher), "CalculateWealthItems"));

        private static readonly Func<WealthWatcher, float> WealthFloorsOf =
            AccessTools.MethodDelegate<Func<WealthWatcher, float>>(AccessTools.Method(typeof(WealthWatcher), "CalculateWealthFloors"));

        /// <summary>
        /// Times WealthWatcher.ForceRecount, the colony-wealth recount the game runs every 5000 ticks (a visible
        /// stutter in big colonies), alternating every optimization off and on, and checks the result is identical.
        /// Runs after the measurement, so it can't affect it.
        /// </summary>
        private static void AppendWealthRecount(StringBuilder sb)
        {
            const int Pairs = 8;
            var enabled = OptimizationRegistry.All.ToDictionary(o => o, o => o.Enabled);
            sb.AppendLine();
            sb.AppendLine($"== Wealth recount (ForceRecount, {Pairs} off/on pairs per map, median ms)");
            try
            {
                foreach (var map in Find.Maps)
                {
                    var ww = map.wealthWatcher;
                    var total = new[] { new List<double>(), new List<double>() };
                    var items = new[] { new List<double>(), new List<double>() };
                    var floors = new[] { new List<double>(), new List<double>() };
                    var results = new[] { new HashSet<string>(), new HashSet<string>() };
                    for (var i = 0; i < Pairs * 2; i++)
                    {
                        var on = i % 2;
                        foreach (var opt in OptimizationRegistry.All)
                            opt.Enabled = on == 1 && enabled[opt];
                        var sw = Stopwatch.StartNew();
                        ww.ForceRecount(true);
                        total[on].Add(sw.Elapsed.TotalMilliseconds);
                        results[on].Add($"{ww.WealthItems:R} / {ww.WealthBuildings:R} / {ww.WealthPawns:R} / {ww.WealthFloorsOnly:R}");
                        sw.Restart();
                        WealthItemsOf(ww);
                        items[on].Add(sw.Elapsed.TotalMilliseconds);
                        sw.Restart();
                        WealthFloorsOf(ww);
                        floors[on].Add(sw.Elapsed.TotalMilliseconds);
                    }
                    double Median(List<double> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);
                    string Row(List<double>[] xs) => $"off {Median(xs[0]),7:F2}  on {Median(xs[1]),7:F2}";
                    sb.AppendLine($"map {map.uniqueID}: total {Row(total)} | items {Row(items)} | floors {Row(floors)} | " +
                                  $"{map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).Count(t => t.Faction == Faction.OfPlayer)} player buildings");
                    var same = results[0].Count == 1 && results[0].SetEquals(results[1]);
                    sb.AppendLine($"  wealth items/buildings/pawns/floors: {string.Join(" | ", results[0])}" +
                                  (same ? "  (identical with optimizations on)" : $"  MISMATCH, on: {string.Join(" | ", results[1])}"));

                    // Where the building part goes, vanilla: market value time by building def.
                    foreach (var opt in OptimizationRegistry.All)
                        opt.Enabled = false;
                    var byDef = new Dictionary<ThingDef, (double ms, double baseMs, int count)>();
                    var clock = new Stopwatch();
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                    {
                        if (t.Faction != Faction.OfPlayer)
                            continue;
                        clock.Restart();
                        t.GetStatValue(StatDefOf.MarketValueIgnoreHp, true, -1);
                        var ms = clock.Elapsed.TotalMilliseconds;
                        clock.Restart();
                        StatWorker_MarketValue.CalculatedBaseMarketValue(t.def, t.Stuff);
                        var baseMs = clock.Elapsed.TotalMilliseconds;
                        byDef.TryGetValue(t.def, out var e);
                        byDef[t.def] = (e.ms + ms, e.baseMs + baseMs, e.count + 1);
                    }
                    sb.AppendLine($"  building market values, vanilla: {byDef.Values.Sum(v => v.ms):F2} ms " +
                                  $"(CalculatedBaseMarketValue alone: {byDef.Values.Sum(v => v.baseMs):F2} ms); top defs (ms total / base part): " +
                                  string.Join(", ", byDef.OrderByDescending(kv => kv.Value.ms).Take(12)
                                      .Select(kv => $"{kv.Key.defName} x{kv.Value.count} {kv.Value.ms:F2}/{kv.Value.baseMs:F2}")));
                }
            }
            finally
            {
                foreach (var kv in enabled)
                    kv.Key.Enabled = kv.Value;
            }
            // The optimization report above was written before these recounts ran.
            var memo = WealthRecountMemo.Info;
            if (memo.Mode != OptMode.Off)
            {
                sb.AppendLine($"  wealthmemo ({memo.Mode}) including these recounts: hits {memo.Stats.Hits:N0}, computed {memo.Stats.Misses:N0}, " +
                              $"checks {memo.Stats.Checks:N0}, mismatches {memo.Stats.Mismatches:N0}");
                foreach (var ex in memo.Stats.Examples)
                    sb.AppendLine("    " + ex);
            }
        }

        private void StashRand()
        {
            simRandState = GetRandState();
            randStashed = true;
        }

        private void UnstashRand()
        {
            if (!randStashed)
                return;
            SetRandState(simRandState);
            randStashed = false;
        }

        private void Finish(string error)
        {
            phase = Phase.Done;
            try
            {
                if (BenchProfiler.Slots.Count > 0)
                    BenchProfiler.Detach(ParallelTickMod.Harmony);
                UnstashRand();
                // A failure in a later phase shouldn't throw away the measurement that already finished.
                var report = error == null ? BuildReport() : measureMs.Count > 0 ? error + "\n\n" + BuildReport() : error;
                File.WriteAllText(BenchConfig.ResultPath, report);
            }
            catch (Exception e)
            {
                Log.Error("[Free Performance] Could not finish benchmark: " + e);
                try { File.WriteAllText(BenchConfig.ResultPath, "FAILED while finishing: " + e + "\n\nOriginal: " + error); }
                catch { }
            }
            BenchLauncher.SaveIfAsked();
            if (BenchConfig.Quit)
                Root.Shutdown();
        }

        private string BuildReport()
        {
            var sb = new StringBuilder();
            var sorted = measureMs.OrderBy(x => x).ToList();
            double Pct(double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
            var mean = measureMs.Average();

            sb.AppendLine("ParallelTick benchmark");
            sb.AppendLine($"label:            {BenchConfig.Label}");
            sb.AppendLine($"save:             {BenchConfig.SaveName}");
            sb.AppendLine($"game version:     {VersionControl.CurrentVersionStringWithRev}");
            sb.AppendLine($"active mods:      {string.Join(", ", LoadedModManager.RunningMods.Select(m => m.PackageId))}");
            sb.AppendLine($"cpu threads:      {Environment.ProcessorCount}");
            sb.AppendLine($"seed:             {BenchConfig.Seed}");
            sb.AppendLine($"determinism shims: {(DeterminismShims.Applied.Count == 0 ? "none" : string.Join("; ", DeterminismShims.Applied))}");
            sb.AppendLine($"warmup:           {BenchConfig.WarmupTicks} ticks from tick {warmupStartTick}");
            sb.AppendLine($"measured:         {measureMs.Count} ticks ({measureStartTick} -> {measureEndTick})");
            sb.AppendLine($"wall time:        {wall.Elapsed.TotalSeconds:F1} s");
            sb.AppendLine();
            sb.AppendLine("== Simulation cost per tick (no instrumentation, rendering excluded)");
            sb.AppendLine($"mean:             {mean:F3} ms");
            sb.AppendLine($"median:           {Pct(0.50):F3} ms");
            sb.AppendLine($"p95:              {Pct(0.95):F3} ms");
            sb.AppendLine($"p99:              {Pct(0.99):F3} ms");
            sb.AppendLine($"max:              {sorted.Last():F3} ms");
            sb.AppendLine($"max possible TPS: {1000.0 / mean:F0}  (targets: 1x=60, 3x=180, 6x=360; rendering not included)");
            sb.AppendLine();
            sb.AppendLine("== Colony size");
            sb.AppendLine($"game day:         {GenDate.DaysPassed}");
            foreach (var map in Find.Maps)
                sb.AppendLine($"map {map.uniqueID}:            {map.Size.x}x{map.Size.z}, {map.mapPawns.AllPawnsSpawnedCount} pawns spawned " +
                              $"({map.mapPawns.FreeColonistsSpawnedCount} free colonists, {map.mapPawns.SpawnedColonyAnimals.Count} colony animals), " +
                              $"{map.listerThings.AllThings.Count} things, {map.listerBuildings.allBuildingsColonist.Count} colony buildings");
            int? CountOf(string field)
            {
                var value = AccessTools.Field(typeof(RimWorld.Planet.WorldPawns), field)?.GetValue(Find.WorldPawns);
                return value == null ? (int?)null : (int)AccessTools.Property(value.GetType(), "Count").GetValue(value);
            }
            var ticked = CountOf("pawnsAlive");
            var mothballed = CountOf("pawnsMothballed");
            sb.AppendLine($"world pawns:      {ticked} ticked every tick, {mothballed} mothballed, {Find.WorldPawns.AllPawnsDead.Count} dead");
            sb.AppendLine($"world objects:    {Find.WorldObjects.AllWorldObjects.Count}, factions: {Find.FactionManager.AllFactionsListForReading.Count}, " +
                          $"ideologies: {Find.IdeoManager?.IdeosListForReading.Count ?? 0}, quests: {Find.QuestManager.QuestsListForReading.Count}");
            sb.AppendLine();
            sb.AppendLine("== Memory / garbage collection (measured ticks)");
            var cleanTicks = measureMs.Count - gcTickMs.Count;
            sb.AppendLine($"allocated:        {(cleanTicks == 0 ? 0 : allocatedBytes / 1024.0 / cleanTicks):F1} KB per tick (ticks without a collection)");
            sb.AppendLine($"collections:      {gcTickMs.Count} during {measureMs.Count} ticks (incremental GC: {GarbageCollector.isIncremental})");
            if (gcTickMs.Count > 0)
            {
                var p99 = Pct(0.99);
                sb.AppendLine($"ticks with a GC:  mean {gcTickMs.Average():F1} ms, max {gcTickMs.Max():F1} ms " +
                              $"({gcTickMs.Count(x => x >= p99)} of the {measureMs.Count(x => x >= p99)} slowest-1% ticks had a GC)");
            }
            sb.AppendLine();
            if (AbActive)
                AppendAbReport(sb);
            sb.AppendLine("== Optimizations");
            foreach (var opt in OptimizationRegistry.All)
            {
                sb.AppendLine($"{opt.Key}: {opt.Mode}");
                if (opt.Mode == OptMode.Off)
                    continue;
                var st = opt.Stats;
                var verify = opt.Mode == OptMode.Verify;
                var answered = verify ? st.Checks : st.Hits;
                var total = answered + st.Misses;
                sb.AppendLine($"  {(verify ? "would-be hits" : "hits")}: {answered:N0} / {total:N0} calls ({(total == 0 ? 0 : 100.0 * answered / total):F1}%)");
                if (verify)
                {
                    sb.AppendLine($"  mismatches: {st.Mismatches:N0}");
                    foreach (var ex in st.Examples)
                        sb.AppendLine("    " + ex);
                }
                foreach (var line in opt.ReportLines())
                    sb.AppendLine(line);
            }

            sb.AppendLine();
            sb.AppendLine("== Determinism");
            sb.AppendLine($"hash at warmup start: {hashAtWarmupStart}");
            sb.AppendLine($"hash at measure end:  {hashAtMeasureEnd}");
            AppendWealthRecount(sb);

            sb.AppendLine();
            sb.AppendLine("== Compatibility report (what the settings button copies)");
            sb.Append(CompatReport.Build());

            if (breakdownMs.Count > 0)
            {
                var ticks = breakdownMs.Count;
                var instrumentedMean = breakdownMs.Average();
                var toMs = 1000.0 / Stopwatch.Frequency;
                string Row(string label, long elapsed, long calls) =>
                    $"  {label,-44} {elapsed * toMs / ticks,8:F3} ms  {100.0 * elapsed * toMs / ticks / instrumentedMean,5:F1}%  {(double)calls / ticks,9:F1} calls/tick";

                sb.AppendLine();
                sb.AppendLine($"== Breakdown over another {ticks} ticks, instrumented (mean {instrumentedMean:F3} ms/tick incl. profiler overhead)");
                sb.AppendLine("   Percentages are of the instrumented tick. Rows within a group can nest inside other groups.");
                foreach (var group in BenchProfiler.Slots.GroupBy(s => s.Group))
                {
                    sb.AppendLine();
                    sb.AppendLine($"-- {group.Key}");
                    foreach (var s in group.Where(s => s.Calls > 0).OrderByDescending(s => s.Elapsed).Take(25))
                        sb.AppendLine(Row(s.Label, s.Elapsed, s.Calls));
                }

                sb.AppendLine();
                sb.AppendLine("-- Thing ticks by category (outermost DoTick only)");
                foreach (var kv in BenchProfiler.ByCategory.OrderByDescending(kv => kv.Value.Elapsed))
                    sb.AppendLine(Row(kv.Key, kv.Value.Elapsed, kv.Value.Calls));

                sb.AppendLine();
                sb.AppendLine("-- Top 25 thing defs by tick cost");
                foreach (var kv in BenchProfiler.ByDef.OrderByDescending(kv => kv.Value.Elapsed).Take(25))
                    sb.AppendLine(Row(kv.Key.defName, kv.Value.Elapsed, kv.Value.Calls));

                sb.AppendLine();
                sb.AppendLine("-- Pawn work: every-tick vs interval, by pawn category");
                foreach (var kv in BenchProfiler.PawnParts.OrderByDescending(kv => kv.Value.Elapsed))
                    sb.AppendLine(Row($"{kv.Key.category.Replace("Pawn: ", "")} / {kv.Key.part}", kv.Value.Elapsed, kv.Value.Calls));

                sb.AppendLine();
                sb.AppendLine("-- Job driver ticks by pawn category and job (top 30)");
                foreach (var kv in BenchProfiler.JobDrivers.OrderByDescending(kv => kv.Value.Elapsed).Take(30))
                    sb.AppendLine(Row($"{kv.Key.category.Replace("Pawn: ", "")} / {kv.Key.job.defName}", kv.Value.Elapsed, kv.Value.Calls));

                sb.AppendLine();
                sb.AppendLine("-- Work searches (JobGiver_Work) by pawn category and outcome");
                foreach (var kv in BenchProfiler.WorkSearches.OrderByDescending(kv => kv.Value.Elapsed))
                {
                    var avg = kv.Value.Calls == 0 ? 0 : kv.Value.Elapsed * toMs / kv.Value.Calls;
                    sb.AppendLine(Row($"{kv.Key.category.Replace("Pawn: ", "")} / {kv.Key.outcome}", kv.Value.Elapsed, kv.Value.Calls) + $"   avg {avg:F2} ms/search");
                }

                sb.AppendLine();
                sb.AppendLine("-- Situational thought checks by thought (ThoughtWorker.CurrentState, top 30)");
                foreach (var kv in BenchProfiler.Thoughts.OrderByDescending(kv => kv.Value.Elapsed).Take(30))
                {
                    var avg = kv.Value.Calls == 0 ? 0 : kv.Value.Elapsed * toMs * 1000 / kv.Value.Calls;
                    sb.AppendLine(Row(kv.Key, kv.Value.Elapsed, kv.Value.Calls) + $"   avg {avg:F1} us each");
                }

                sb.AppendLine();
                sb.AppendLine("-- Time inside work searches by pawn category and work giver (top 30)");
                foreach (var kv in BenchProfiler.WorkGiverTimes.OrderByDescending(kv => kv.Value.Elapsed).Take(30))
                {
                    var avg = kv.Value.Calls == 0 ? 0 : kv.Value.Elapsed * toMs / kv.Value.Calls;
                    sb.AppendLine(Row($"{kv.Key.category.Replace("Pawn: ", "")} / {kv.Key.giver}", kv.Value.Elapsed, kv.Value.Calls) + $"   avg {avg:F2} ms each");
                }

                sb.AppendLine();
                sb.AppendLine("-- Closest-thing searches (outermost GenClosest call) by who started them (top 25)");
                foreach (var kv in BenchProfiler.Searches.OrderByDescending(kv => kv.Value.Elapsed).Take(25))
                {
                    var avg = kv.Value.Calls == 0 ? 0 : kv.Value.Elapsed * toMs / kv.Value.Calls;
                    sb.AppendLine(Row(kv.Key, kv.Value.Elapsed, kv.Value.Calls) + $"   avg {avg:F2} ms/search");
                }

                sb.AppendLine();
                sb.AppendLine($"-- Slowest {spikes.Count} ticks (instrumented): biggest profiler slots in ms, and the slowest single thing");
                foreach (var (_, line) in spikes)
                    sb.AppendLine("  " + line);

                sb.AppendLine();
                sb.AppendLine("-- Job fail/end conditions by method (top 25)");
                foreach (var kv in BenchProfiler.FailConditions.OrderByDescending(kv => kv.Value.Elapsed).Take(25))
                    sb.AppendLine(Row(kv.Key, kv.Value.Elapsed, kv.Value.Calls));

                sb.AppendLine();
                sb.AppendLine("-- Stat requests by stat (outermost request, includes dependent stats; top 25)");
                foreach (var kv in BenchProfiler.Stats.OrderByDescending(kv => kv.Value.Elapsed).Take(25))
                    sb.AppendLine(Row(kv.Key?.defName ?? "?", kv.Value.Elapsed, kv.Value.Calls));
            }

            if (trace.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"== State trace (every {BenchConfig.TraceInterval} ticks)");
                foreach (var line in trace)
                    sb.AppendLine("trace " + line);
            }
            return sb.ToString();
        }
    }
}
