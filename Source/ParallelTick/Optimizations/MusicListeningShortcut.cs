using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// ThoughtWorker_MusicalInstrumentListeningBase.CurrentStateInternal runs a reachable-closest-thing search for an
    /// instrument of its def within range whose validator requires IsBeingPlayed, on every mood recalculation of every
    /// colonist (~0.6 searches per tick in the benchmark save). When no instrument of that def on the map is being
    /// played, that search cannot find anything and the result is inactive. Checking the handful of instruments of the
    /// def first gives the same answer without the search. Exact.
    /// </summary>
    public static class MusicListeningShortcut
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "musiccheck",
            Label = "Skip the 'listening to music' search when nobody is playing  (exact)",
            Description = "Every mood recalculation searched the map for a nearby instrument being played. If no instrument " +
                          "of that kind is being played anywhere on the map, the answer is known without searching.",
            Patch = Patch,
            Reset = () => Info.Stats.Reset(),
        };

        // Virtual call, so subclasses' InstrumentDef overrides are used.
        private static readonly System.Func<ThoughtWorker_MusicalInstrumentListeningBase, ThingDef> InstrumentDef =
            AccessTools.MethodDelegate<System.Func<ThoughtWorker_MusicalInstrumentListeningBase, ThingDef>>(
                AccessTools.PropertyGetter(typeof(ThoughtWorker_MusicalInstrumentListeningBase), "InstrumentDef"));

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ThoughtWorker_MusicalInstrumentListeningBase), "CurrentStateInternal"),
                prefix: new HarmonyMethod(typeof(MusicListeningShortcut), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(MusicListeningShortcut), nameof(Postfix)));
        }

        /// <summary>True when no instrument of the worker's def on the pawn's map is being played.</summary>
        private static bool NobodyPlaying(ThoughtWorker_MusicalInstrumentListeningBase worker, Pawn p)
        {
            var def = InstrumentDef(worker);
            if (def == null)
                return false;
            foreach (var t in p.Map.listerThings.ThingsOfDef(def))
                if (t is Building_MusicalInstrument instrument && instrument.IsBeingPlayed)
                    return false;
            return true;
        }

        public static bool Prefix(ThoughtWorker_MusicalInstrumentListeningBase __instance, Pawn p, ref ThoughtState __result, out bool __state)
        {
            __state = false;
            if (!Info.Active && !Info.Verifying || p?.Map == null || !UnityData.IsInMainThread)
                return true;
            if (!NobodyPlaying(__instance, p))
            {
                Info.Stats.Misses++;
                return true;
            }
            if (Info.Active)
            {
                Info.Stats.Hits++;
                __result = ThoughtState.Inactive;
                return false;
            }
            __state = true; // verify: vanilla runs, postfix checks it agrees
            return true;
        }

        public static void Postfix(Pawn p, ThoughtState __result, bool __state)
        {
            if (!__state)
                return;
            Info.Stats.Checks++;
            if (__result.Active)
                Info.Stats.Mismatch(() => $"{p.LabelShort}: listening thought active although no instrument is being played");
        }
    }
}
