using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Many callers read stats with StatExtension.GetStatValue and the default one-tick cache, so stats that only
    /// change when the pawn's gear or body changes are recomputed from scratch many times per second. For a whitelist
    /// of such stats this keeps the value per (thing, stat) until PawnVersions reports a change for pawns, and for at
    /// most MaxAgeTicks in any case. The whitelist is settled by verify mode: a stat stays on it only while it shows
    /// zero mismatches.
    /// </summary>
    public static class StatCache
    {
        public const int MaxAgeTicks = 250;

        public static readonly Optimization Info = new Optimization
        {
            Key = "statcache",
            Label = "Cache rarely-changing stats",
            Description = "Keeps stats like max nutrition and mental break threshold per pawn until their apparel, health, " +
                          "genes, traits or life stage change, and furniture stats like comfort and bed rest, instead of " +
                          "recalculating them several times a second.",
            Patch = Patch,
            Guarded = () => StatGuard.Methods(cachedStats),
            Accepts = VefStats.Accepts,
            Reset = Reset,
            Prune = Prune,
            ReportLines = ReportLines,
        };

        // Per-stat hit/check/mismatch counts, so the whitelist can be settled stat by stat.
        private static readonly Dictionary<StatDef, long[]> perStat = new Dictionary<StatDef, long[]>();

        private static long[] Counts(StatDef stat)
        {
            if (!perStat.TryGetValue(stat, out var c))
                perStat[stat] = c = new long[4]; // hits, misses, checks, mismatches
            return c;
        }

        private static IEnumerable<string> ReportLines() =>
            perStat.OrderByDescending(kv => kv.Value[0] + kv.Value[1] + kv.Value[2])
                .Select(kv => $"  {kv.Key.defName,-24} hits {kv.Value[0],9:N0}  misses {kv.Value[1],7:N0}  checks {kv.Value[2],9:N0}  mismatches {kv.Value[3],6:N0}");

        /// <summary>
        /// Stats read from pawns whose inputs are tracked by PawnVersions. Tried and removed after verify runs:
        /// MoveSpeed (changes 0.8x/1.25x through something untracked, 589 mismatches) and VacuumResistance (0.68 -> 0.98
        /// on one colonist without any tracked change, likely a vacsuit toggle).
        /// </summary>
        private static readonly string[] PawnStatNames =
        {
            "MaxNutrition", "MentalBreakThreshold", "PsychicSensitivity",
            "GlobalLearningFactor", "WastepacksPerRecharge", "MeleeDamageFactor", "MeleeCooldownFactor",
        };

        /// <summary>
        /// Stats read from buildings that depend only on the building itself (def, stuff, quality). Beauty was tried and
        /// removed: containers such as bookcases change beauty with their contents.
        /// </summary>
        private static readonly string[] ThingStatNames = { "DoorOpenSpeed", "BedRestEffectiveness", "Comfort" };

        private struct Entry
        {
            public int Tick, Version;
            public float Value;
        }

        // Indexed by StatDef.index, so the check for "is this stat cached at all" costs one array read.
        private static bool[] cachedStat = new bool[0];
        private static readonly List<StatDef> cachedStats = new List<StatDef>();
        private static readonly Dictionary<(Thing, StatDef, bool), Entry> cache = new Dictionary<(Thing, StatDef, bool), Entry>();

        private static void Patch(Harmony harmony)
        {
            PawnVersions.EnsurePatched(harmony);
            cachedStat = new bool[DefDatabase<StatDef>.DefCount];
            foreach (var name in PawnStatNames.Concat(ThingStatNames))
            {
                var stat = DefDatabase<StatDef>.GetNamedSilentFail(name);
                if (stat == null)
                    continue;
                // A worker or part from another mod can depend on anything; such a stat isn't cached.
                var foreign = StatGuard.ForeignParts(stat);
                if (foreign != null)
                {
                    Log.Message($"[Free Performance] Stat cache leaves {stat.defName} alone: {foreign}.");
                    continue;
                }
                cachedStat[stat.index] = true;
                cachedStats.Add(stat);
            }
            harmony.Patch(AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue)),
                prefix: new HarmonyMethod(typeof(StatCache), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(StatCache), nameof(Postfix)));
        }

        private static void Reset()
        {
            cache.Clear();
            perStat.Clear();
            Info.Stats.Reset();
        }

        private static void Prune(int now)
        {
            foreach (var key in cache.Where(kv => kv.Key.Item1.Destroyed || now - kv.Value.Tick > MaxAgeTicks).Select(kv => kv.Key).ToList())
                cache.Remove(key);
        }

        public static bool Prefix(Thing thing, StatDef stat, bool applyPostProcess, ref float __result, out bool __state)
        {
            __state = false;
            if (stat == null || stat.index >= cachedStat.Length || !cachedStat[stat.index] || thing == null)
                return true;
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing)
                return true;
            if (thing is Pawn && VefStats.HasAnimalGenes(thing))
                return true;

            if (Info.Active && TryGetFresh(thing, stat, applyPostProcess, out var entry))
            {
                Info.Stats.Hits++;
                Counts(stat)[0]++;
                __result = entry.Value;
                return false;
            }
            __state = true;
            return true;
        }

        public static void Postfix(Thing thing, StatDef stat, bool applyPostProcess, float __result, bool __state)
        {
            if (!__state)
                return;
            if (Info.Verifying && TryGetFresh(thing, stat, applyPostProcess, out var entry))
            {
                Info.Stats.Checks++;
                Counts(stat)[2]++;
                if (entry.Value != __result)
                {
                    Counts(stat)[3]++;
                    Info.Stats.Mismatch(() => $"{stat.defName} on {thing.LabelShort} ({thing.def.defName}) age " +
                                              $"{Find.TickManager.TicksGame - entry.Tick}: cached {entry.Value}, fresh {__result}");
                }
                return;
            }
            Info.Stats.Misses++;
            Counts(stat)[1]++;
            cache[(thing, stat, applyPostProcess)] = new Entry
            {
                Tick = Find.TickManager.TicksGame,
                Version = thing is Pawn p ? PawnVersions.Get(p) : 0,
                Value = __result,
            };
        }

        private static bool TryGetFresh(Thing thing, StatDef stat, bool applyPostProcess, out Entry entry)
        {
            if (!cache.TryGetValue((thing, stat, applyPostProcess), out entry))
                return false;
            var age = Find.TickManager.TicksGame - entry.Tick;
            var version = thing is Pawn p ? PawnVersions.Get(p) : 0;
            return entry.Version == version && age >= 0 && age < MaxAgeTicks;
        }
    }
}
