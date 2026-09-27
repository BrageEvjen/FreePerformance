using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// WorkGiver_ConstructDeliverResourcesTo{Frames,Blueprints}.HasJobOnThing is the validator of the closest-thing
    /// search over every frame/blueprint on the map. For each candidate it runs GenConstruct.CanConstruct (reachability,
    /// reservations, skills, ideology) before GenConstruct.CanGetResources_NewTemp (are the materials available anywhere,
    /// cached per tick). In a base with many blueprints waiting for materials, most candidates pass the expensive check
    /// and then fail the cheap one, which makes every hauler's work search take several milliseconds.
    ///
    /// For automatic (non-forced) searches this answers "no job" up front when vanilla is certain to answer "no job":
    /// no blocking thing (which would produce a clearing job), no floor-removal step and no install blueprint (which don't
    /// need resources), a non-empty material cost, and CanGetResources_NewTemp false. Every vanilla path in that state
    /// ends in "false", so the result is identical. Exact.
    /// </summary>
    public static class DeliveryEarlyOut
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "deliveryearly",
            Label = "Skip blueprints whose materials don't exist  (exact)",
            Description = "When haulers look for construction work, blueprints and frames whose materials aren't available " +
                          "anywhere on the map are ruled out with a cheap check first, instead of after the expensive " +
                          "reachability and reservation checks. Same result, much shorter work searches in big bases.",
            Patch = Patch,
            Reset = () => Info.Stats.Reset(),
        };

        private static readonly System.Func<Pawn, Blueprint, bool> ShouldRemoveExistingFloorFirst =
            AccessTools.MethodDelegate<System.Func<Pawn, Blueprint, bool>>(
                AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources), "ShouldRemoveExistingFloorFirst"));

        private static void Patch(Harmony harmony)
        {
            foreach (var type in new[] { typeof(WorkGiver_ConstructDeliverResourcesToFrames), typeof(WorkGiver_ConstructDeliverResourcesToBlueprints) })
                harmony.Patch(AccessTools.Method(type, nameof(WorkGiver_Scanner.HasJobOnThing)),
                    prefix: new HarmonyMethod(typeof(DeliveryEarlyOut), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(DeliveryEarlyOut), nameof(Postfix)));
        }

        /// <summary>True when vanilla HasJobOnThing is certain to return false for this candidate.</summary>
        private static bool CertainlyNoJob(Pawn pawn, Thing t)
        {
            if (t.Faction != pawn.Faction)
                return false; // vanilla also says no, but leave the uncommon paths to vanilla
            if (t is Frame)
            {
                // Cheap (cached per tick) check first; only then the blocker check.
                return !GenConstruct.CanGetResources_NewTemp(t, pawn, false) && GenConstruct.FirstBlockingThing(t, pawn) == null;
            }
            if (t is Blueprint blueprint && !(blueprint is Blueprint_Install))
            {
                var cost = blueprint.TotalMaterialCost();
                if (cost == null || cost.Count == 0)
                    return false;
                return !GenConstruct.CanGetResources_NewTemp(t, pawn, false)
                       && GenConstruct.FirstBlockingThing(t, pawn) == null
                       && !ShouldRemoveExistingFloorFirst(pawn, blueprint);
            }
            return false;
        }

        public static bool Prefix(Pawn pawn, Thing t, bool forced, ref bool __result, out bool __state)
        {
            __state = false;
            if (!Info.Active && !Info.Verifying || forced || pawn == null || t == null || !UnityData.IsInMainThread)
                return true;
            if (!CertainlyNoJob(pawn, t))
            {
                Info.Stats.Misses++;
                return true;
            }
            if (Info.Active)
            {
                Info.Stats.Hits++;
                __result = false;
                return false;
            }
            __state = true;
            return true;
        }

        public static void Postfix(Pawn pawn, Thing t, bool __result, bool __state)
        {
            if (!__state)
                return;
            Info.Stats.Checks++;
            if (__result)
                Info.Stats.Mismatch(() => $"{pawn.LabelShort} -> {t.LabelShort}: early-out said no job, vanilla found one");
        }
    }
}
