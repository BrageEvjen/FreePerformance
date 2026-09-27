using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// GenTemperature.ComfortableTemperatureRange(Pawn) reads ComfyTemperatureMin/Max through the stat system with a
    /// one-tick cache, so heatstroke/hypothermia checks and danger checks recompute both from apparel, genes and
    /// hediffs on nearly every call. Those inputs rarely change. This keeps the range per pawn until PawnVersions says
    /// the pawn changed, and for at most MaxAgeTicks as a safety net for anything missed.
    /// </summary>
    public static class TemperatureRangeCache
    {
        public const int MaxAgeTicks = 250;

        public static readonly Optimization Info = new Optimization
        {
            Key = "tempcache",
            Label = "Cache comfortable temperature range  (~2% faster)",
            Description = "Keeps each pawn's comfortable temperature range until their apparel, health, genes, traits or life " +
                          "stage change, instead of recalculating it on nearly every heatstroke, hypothermia and danger check.",
            Patch = Patch,
            Guarded = () => new MethodBase[]
            {
                AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.ComfortableTemperatureRange), new[] { typeof(Pawn) }),
            }.Concat(StatGuard.Methods(new[] { StatDefOf.ComfyTemperatureMin, StatDefOf.ComfyTemperatureMax })),
            BlockReason = () => StatGuard.ForeignParts(StatDefOf.ComfyTemperatureMin) ?? StatGuard.ForeignParts(StatDefOf.ComfyTemperatureMax),
            Accepts = VefStats.Accepts,
            Reset = Reset,
            Prune = Prune,
        };

        private struct Entry
        {
            public int Tick, Version;
            public FloatRange Range;
        }

        private static readonly Dictionary<Pawn, Entry> cache = new Dictionary<Pawn, Entry>();

        private static void Patch(Harmony harmony)
        {
            PawnVersions.EnsurePatched(harmony);
            harmony.Patch(AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.ComfortableTemperatureRange), new[] { typeof(Pawn) }),
                prefix: new HarmonyMethod(typeof(TemperatureRangeCache), nameof(RangePrefix)),
                postfix: new HarmonyMethod(typeof(TemperatureRangeCache), nameof(RangePostfix)));
        }

        private static void Reset()
        {
            cache.Clear();
            Info.Stats.Reset();
        }

        /// <summary>Drops entries for pawns that are gone or long unused, so the dictionary doesn't grow forever.</summary>
        private static void Prune(int now)
        {
            foreach (var p in cache.Where(kv => kv.Key.Destroyed || now - kv.Value.Tick > MaxAgeTicks).Select(kv => kv.Key).ToList())
                cache.Remove(p);
        }

        public static bool RangePrefix(Pawn p, ref FloatRange __result, out bool __state)
        {
            __state = false;
            // Worker threads and non-game contexts (e.g. world generation) always take the vanilla path.
            if (!Info.Active && !Info.Verifying || p == null || !UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing)
                return true;
            // VEF adjusts these pawns' stats from its gene table, which this cache can't watch.
            if (VefStats.HasAnimalGenes(p))
                return true;

            if (Info.Active && TryGetFresh(p, out var entry))
            {
                Info.Stats.Hits++;
                __result = entry.Range;
                return false;
            }
            // Miss, or Verify mode: run vanilla, then store or compare in the postfix.
            __state = true;
            return true;
        }

        public static void RangePostfix(Pawn p, FloatRange __result, bool __state)
        {
            if (!__state)
                return;
            if (Info.Verifying && TryGetFresh(p, out var entry))
            {
                // The cache would have answered; check that it would have answered correctly. The entry is left alone
                // so it ages exactly as it would when active.
                Info.Stats.Checks++;
                if (entry.Range.min != __result.min || entry.Range.max != __result.max)
                    Info.Stats.Mismatch(() => $"{p.LabelShort} ({p.def.defName}) age {Find.TickManager.TicksGame - entry.Tick} ticks: " +
                                              $"cached {entry.Range.min:F2}..{entry.Range.max:F2}, fresh {__result.min:F2}..{__result.max:F2}");
                return;
            }
            Info.Stats.Misses++;
            cache[p] = new Entry { Tick = Find.TickManager.TicksGame, Version = PawnVersions.Get(p), Range = __result };
        }

        private static bool TryGetFresh(Pawn p, out Entry entry)
        {
            if (!cache.TryGetValue(p, out entry))
                return false;
            var age = Find.TickManager.TicksGame - entry.Tick;
            return entry.Version == PawnVersions.Get(p) && age >= 0 && age < MaxAgeTicks;
        }
    }
}
