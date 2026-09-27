using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

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

        /// <summary>
        /// Game methods whose behaviour this optimization reasons about. If another mod patches any of them, the
        /// optimization switches itself off and logs which mod; its reasoning may no longer hold, and a cached answer
        /// could skip (or repeat) the other mod's change. Checked on first use, so patches applied later are seen too.
        /// </summary>
        public Func<IEnumerable<MethodBase>> Guarded;

        /// <summary>Optional extra check: a reason to stay off (e.g. another mod overrides a method in a subclass), or null.</summary>
        public Func<string> BlockReason;

        private bool guardChecked, guardBlocked;

        /// <summary>Why this optimization is off in the current game (another mod's patch), shown in the settings; null when not.</summary>
        public string BlockedReason { get; private set; }

        /// <summary>Records and logs why the optimization stays off (the first reason is kept for the settings window).</summary>
        public void LogBlocked(string reason)
        {
            BlockedReason = BlockedReason ?? reason;
            Log.Message($"[Free Performance] {Label?.Replace("  (exact)", "") ?? Key} stays off: {reason}.");
        }

        /// <summary>True when another mod changes something this optimization relies on (see Guarded).</summary>
        public bool Blocked
        {
            get
            {
                if (!guardChecked)
                    CheckGuard();
                return guardBlocked;
            }
        }

        /// <summary>Re-check on the next use (each new or loaded game).</summary>
        public void ResetGuard()
        {
            guardChecked = false;
            BlockedReason = null;
        }

        private void CheckGuard()
        {
            guardChecked = true;
            guardBlocked = false;
            try
            {
                foreach (var method in Guarded?.Invoke() ?? Enumerable.Empty<MethodBase>())
                {
                    if (method == null)
                        continue;
                    var owners = PatchGuard.ForeignOwners(method);
                    if (owners == null || owners.Count == 0)
                        continue;
                    LogBlocked($"{method.DeclaringType?.Name}.{method.Name} is patched by {string.Join(", ", owners)}");
                    guardBlocked = true;
                }
                var reason = BlockReason?.Invoke();
                if (reason != null)
                {
                    LogBlocked(reason);
                    guardBlocked = true;
                }
            }
            catch (Exception e)
            {
                LogBlocked($"could not check other mods' patches ({e.GetType().Name}: {e.Message})");
                guardBlocked = true;
            }
        }

        public Action Reset = () => { };
        public Action<int> Prune = _ => { };
        public readonly OptStats Stats = new OptStats();

        /// <summary>Off = not patched. On/Verify = patched; see OptMode.</summary>
        public OptMode Mode { get; private set; }

        /// <summary>Runtime switch (settings, in-process A/B); when false the patched code behaves exactly like vanilla.</summary>
        public bool Enabled = true;

        /// <summary>True when the optimized path should answer (not Off, not Verify, not switched off).</summary>
        public bool Active => Enabled && Mode == OptMode.On && !Blocked;

        /// <summary>True when the optimized path should run alongside vanilla and compare.</summary>
        public bool Verifying => Enabled && Mode == OptMode.Verify && !Blocked;

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
