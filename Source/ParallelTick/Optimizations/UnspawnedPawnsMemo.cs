using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// MapPawns.AllPawnsUnspawned (behind AllPawns, FreeColonists, PawnsInFaction and friends) walks every container on
    /// the map — every pawn's apparel, inventory and carried thing, every corpse, frame and minified thing — to find
    /// pawns held inside things: ~3 ms in a big colony. The colonist bar walks it twice each time it rebuilds its list
    /// (FreeColonists, then ColonySubhumansControllable), and several alerts walk it twice per recalculation, which shows
    /// as frame spikes.
    ///
    /// Inside those two UI updates (ColonistBar.CheckRecacheEntries, AlertsReadout.AlertsReadoutUpdate) nothing changes
    /// what is held where, so the first walk's result is reused for later calls in the same update, as long as the
    /// shared result list still holds exactly that result. Visual/UI only; exact.
    /// </summary>
    public static class UnspawnedPawnsMemo
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "unspawnedmemo",
            Label = "Reuse the held-pawn search within one UI update  (exact)",
            Description = "Finding pawns carried inside things walks every container on the map. The colonist bar and some " +
                          "alerts did that twice in the same update; now the second time reuses the first answer.",
            Patch = Patch,
            Reset = Reset,
        };

        private static int depth;
        private static readonly Dictionary<MapPawns, Pawn[]> memo = new Dictionary<MapPawns, Pawn[]>(RefEq<MapPawns>.Instance);
        private static readonly AccessTools.FieldRef<MapPawns, List<Pawn>> result = AccessTools.FieldRefAccess<MapPawns, List<Pawn>>("allPawnsUnspawnedResult");

        private static void Patch(Harmony harmony)
        {
            var enter = new HarmonyMethod(typeof(UnspawnedPawnsMemo), nameof(Enter));
            var exit = new HarmonyMethod(typeof(UnspawnedPawnsMemo), nameof(Exit));
            harmony.Patch(AccessTools.Method(typeof(ColonistBar), "CheckRecacheEntries"), prefix: enter, finalizer: exit);
            harmony.Patch(AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate)), prefix: enter, finalizer: exit);
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawnsUnspawned)),
                prefix: new HarmonyMethod(typeof(UnspawnedPawnsMemo), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(UnspawnedPawnsMemo), nameof(Postfix)));
        }

        private static void Reset()
        {
            depth = 0;
            memo.Clear();
            Info.Stats.Reset();
        }

        public static void Enter()
        {
            if (UnityData.IsInMainThread)
                depth++;
        }

        public static void Exit()
        {
            if (!UnityData.IsInMainThread)
                return;
            if (--depth <= 0)
            {
                depth = 0;
                memo.Clear();
            }
        }

        private static bool Matches(List<Pawn> list, Pawn[] saved)
        {
            if (list.Count != saved.Length)
                return false;
            for (var i = 0; i < saved.Length; i++)
                if (list[i] != saved[i])
                    return false;
            return true;
        }

        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result, out bool __state)
        {
            __state = false;
            if (depth <= 0 || !Info.Active && !Info.Verifying || !UnityData.IsInMainThread)
                return true;
            __state = true;
            if (Info.Active && memo.TryGetValue(__instance, out var saved) && Matches(result(__instance), saved))
            {
                Info.Stats.Hits++;
                __result = result(__instance);
                __state = false;
                return false;
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, List<Pawn> __result, bool __state)
        {
            if (!__state || __result == null)
                return;
            if (Info.Verifying && memo.TryGetValue(__instance, out var saved))
            {
                Info.Stats.Checks++;
                if (!Matches(__result, saved))
                    Info.Stats.Mismatch(() => $"held pawns changed within one UI update: {saved.Length} -> {__result.Count}");
            }
            Info.Stats.Misses++;
            memo[__instance] = __result.ToArray();
        }
    }
}
