using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ParallelTick.Optimizations
{
    /// <summary>Counters every cache-style optimization reports in the benchmark.</summary>
    public sealed class OptStats
    {
        public long Hits, Misses, Checks, Mismatches;
        public readonly List<string> Examples = new List<string>();

        public void Mismatch(Func<string> example)
        {
            Mismatches++;
            if (Examples.Count < 10)
                Examples.Add(example());
        }

        public void Reset()
        {
            Hits = Misses = Checks = Mismatches = 0;
            Examples.Clear();
        }
    }

    /// <summary>One optimization: how to patch it, its per-game state hooks, and its runtime switches.</summary>
    public sealed class Optimization
    {
        public string Key, Label, Description;

        /// <summary>
        /// Approximate optimizations may change *when* something happens by a few ticks (they are not bit-identical
        /// to vanilla); their verify mode counts how often that happens.
        /// </summary>
        public bool Approximate;

        /// <summary>Whether the optimization is on in normal play when the player hasn't touched its checkbox.</summary>
        public bool DefaultOn = true;

        public Action<Harmony> Patch;

        /// <summary>Optional extra lines for the benchmark report (e.g. per-stat breakdowns).</summary>
        public Func<IEnumerable<string>> ReportLines = () => Enumerable.Empty<string>();

        public Action Reset = () => { };
        public Action<int> Prune = _ => { };
        public readonly OptStats Stats = new OptStats();

        /// <summary>Off = not patched. On/Verify = patched; see OptMode.</summary>
        public OptMode Mode { get; private set; }

        /// <summary>Runtime switch (settings, in-process A/B); when false the patched code behaves exactly like vanilla.</summary>
        public bool Enabled = true;

        /// <summary>True when the optimized path should answer (not Off, not Verify, not switched off).</summary>
        public bool Active => Enabled && Mode == OptMode.On;

        /// <summary>True when the optimized path should run alongside vanilla and compare.</summary>
        public bool Verifying => Enabled && Mode == OptMode.Verify;

        public void Apply(Harmony harmony, OptMode mode)
        {
            Mode = mode;
            if (mode != OptMode.Off)
                Patch(harmony);
        }
    }

    public static class OptimizationRegistry
    {
        public static readonly List<Optimization> All = new List<Optimization>
        {
            TemperatureRangeCache.Info,
            SolarRoofCache.Info,
            StatCache.Info,
            MusicListeningShortcut.Info,
            DeliveryEarlyOut.Info,
            MergeIndex.Info,
            WindSwayDeferral.Info,
            OverseerStateCache.Info,
            HaulablesFastPath.Info,
            PawnTimeDilation.Info,
            PawnTickBookkeeping.Info,
            HediffTickPlan.Info,
            GasGridFastPath.Info,
            ReservationDedup.Info,
            ContentsTickSkip.Info,
            PawnPreDrawSkip.Info,
            QuestReserveFilter.Info,
            UnspawnedPawnsMemo.Info,
            AlertEarlyOut.Info,
            SettlementFastTick.Info,
            WealthRecountMemo.Info,
        };

        public static Optimization Get(string key) =>
            All.FirstOrDefault(o => o.Key == key) ?? throw new ArgumentException($"Unknown optimization '{key}'");
    }
}
