using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// A per-pawn counter that goes up whenever something that feeds pawn stats changes: apparel, hediffs, genes,
    /// life stage, traits, ideology role. Caches store the counter with each entry and treat a changed counter as stale.
    /// </summary>
    public static class PawnVersions
    {
        private static readonly Dictionary<Pawn, int> versions = new Dictionary<Pawn, int>();
        private static bool patched;

        private static readonly AccessTools.FieldRef<Pawn_ApparelTracker, Pawn> ApparelPawn = AccessTools.FieldRefAccess<Pawn_ApparelTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<HediffSet, Pawn> HediffPawn = AccessTools.FieldRefAccess<HediffSet, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<Pawn_GeneTracker, Pawn> GenePawn = AccessTools.FieldRefAccess<Pawn_GeneTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<Pawn_AgeTracker, Pawn> AgePawn = AccessTools.FieldRefAccess<Pawn_AgeTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<TraitSet, Pawn> TraitPawn = AccessTools.FieldRefAccess<TraitSet, Pawn>("pawn");

        public static int Get(Pawn p)
        {
            versions.TryGetValue(p, out var v);
            return v;
        }

        public static void Bump(Pawn p)
        {
            if (p == null)
                return;
            versions.TryGetValue(p, out var v);
            versions[p] = v + 1;
        }

        /// <summary>Patches the change notifications once, however many optimizations use the counters.</summary>
        public static void EnsurePatched(Harmony harmony)
        {
            if (patched)
                return;
            patched = true;

            var apparelChanged = new HarmonyMethod(typeof(PawnVersions), nameof(ApparelChanged));
            foreach (var name in new[] { "Notify_ApparelAdded", "Notify_ApparelRemoved", "Notify_ApparelChanged" })
                harmony.Patch(AccessTools.Method(typeof(Pawn_ApparelTracker), name), postfix: apparelChanged);
            harmony.Patch(AccessTools.Method(typeof(HediffSet), nameof(HediffSet.DirtyCache)),
                postfix: new HarmonyMethod(typeof(PawnVersions), nameof(HediffsChanged)));
            harmony.Patch(AccessTools.Method(typeof(Pawn_GeneTracker), "Notify_GenesChanged"),
                postfix: new HarmonyMethod(typeof(PawnVersions), nameof(GenesChanged)));
            harmony.Patch(AccessTools.Method(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.PostResolveLifeStageChange)),
                postfix: new HarmonyMethod(typeof(PawnVersions), nameof(LifeStageChanged)));
            var traitsChanged = new HarmonyMethod(typeof(PawnVersions), nameof(TraitsChanged));
            harmony.Patch(AccessTools.Method(typeof(TraitSet), nameof(TraitSet.GainTrait)), postfix: traitsChanged);
            harmony.Patch(AccessTools.Method(typeof(TraitSet), nameof(TraitSet.RemoveTrait)), postfix: traitsChanged);
        }

        public static void Reset() => versions.Clear();

        public static void Prune()
        {
            foreach (var p in versions.Keys.Where(p => p.Destroyed).ToList())
                versions.Remove(p);
        }

        public static void ApparelChanged(Pawn_ApparelTracker __instance) => Bump(ApparelPawn(__instance));
        public static void HediffsChanged(HediffSet __instance) => Bump(HediffPawn(__instance));
        public static void GenesChanged(Pawn_GeneTracker __instance) => Bump(GenePawn(__instance));
        public static void LifeStageChanged(Pawn_AgeTracker __instance) => Bump(AgePawn(__instance));
        public static void TraitsChanged(TraitSet __instance) => Bump(TraitPawn(__instance));
    }
}
