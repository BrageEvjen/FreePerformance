using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// CompOverseerSubject.State ("is this mech controlled by its mechanitor?") is asked every tick by each mech's
    /// CompTick (twice, through CanGoFeral) and by IsColonyMechPlayerControlled, which forbidden checks and many
    /// others call. Each time it scans the mech's relations for its overseer and then searches the mechanitor's
    /// controlled-mech list.
    ///
    /// The answer depends only on: the mech's direct-relation list (the DirectPawnRelation objects are fixed after
    /// construction), the overseer's mechanitor tracker, and that tracker's controlled-pawn list. The cache is reused
    /// only while both lists are the same objects with the same List._version and the tracker is the same, so it is
    /// exact. Nothing is cached while a save is loading (cross-references are filled in without a version change).
    ///
    /// CompTick fast path: when the mech is overseen (or not spawned) and has no "needs overseer" effecter, vanilla
    /// CompTick does nothing (CanGoFeral is false), so it is skipped.
    /// </summary>
    public static class OverseerStateCache
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "overseer",
            Label = "Cache mech control state  (exact)",
            Description = "Mechs check every tick whether their mechanitor still controls them. This remembers the answer " +
                          "until the mech's relations or the mechanitor's list of mechs actually change.",
            Patch = Patch,
            Guarded = () => new MethodBase[]
            {
                AccessTools.PropertyGetter(typeof(CompOverseerSubject), nameof(CompOverseerSubject.State)),
                AccessTools.Method(typeof(CompOverseerSubject), nameof(CompOverseerSubject.CompTick)),
                AccessTools.Method(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.GetFirstDirectRelationPawn)),
                AccessTools.PropertyGetter(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.ControlledPawns)),
            },
            Reset = Reset,
            Prune = Prune,
            ReportLines = Report,
        };

        private sealed class Entry
        {
            public List<DirectPawnRelation> Relations;
            public int RelationsVersion;
            public Pawn Overseer;
            public Pawn_MechanitorTracker Tracker;
            public List<Pawn> Controlled;
            public int ControlledVersion;
            public OverseerSubjectState State;
        }

        private static readonly Dictionary<CompOverseerSubject, Entry> cache =
            new Dictionary<CompOverseerSubject, Entry>(RefEq<CompOverseerSubject>.Instance);

        private static readonly AccessTools.FieldRef<CompOverseerSubject, Effecter> effect =
            AccessTools.FieldRefAccess<CompOverseerSubject, Effecter>("effect");
        private static readonly AccessTools.FieldRef<CompOverseerSubject, int> delayUntilFeralCheck =
            AccessTools.FieldRefAccess<CompOverseerSubject, int>("delayUntilFeralCheck");
        private static readonly Func<Pawn, bool> canGoFeral =
            AccessTools.MethodDelegate<Func<Pawn, bool>>(AccessTools.Method(typeof(CompOverseerSubject), "CanGoFeral"));

        private static long tickSkips, tickRuns;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompOverseerSubject), nameof(CompOverseerSubject.State)),
                prefix: new HarmonyMethod(typeof(StatePatch), nameof(StatePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(StatePatch), nameof(StatePatch.Postfix)));
            harmony.Patch(AccessTools.Method(typeof(CompOverseerSubject), nameof(CompOverseerSubject.CompTick)),
                prefix: new HarmonyMethod(typeof(TickPatch), nameof(TickPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(TickPatch), nameof(TickPatch.Postfix)));
        }

        private static void Reset()
        {
            cache.Clear();
            tickSkips = tickRuns = 0;
            Info.Stats.Reset();
        }

        private static void Prune(int now)
        {
            foreach (var comp in cache.Keys.Where(c => c.parent.Destroyed).ToList())
                cache.Remove(comp);
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  CompTick skipped: {tickSkips}, run: {tickRuns}";
        }

        /// <summary>For PawnTimeDilation: true when this comp's CompTick would do nothing this tick.</summary>
        public static bool TickIsNoOp(CompOverseerSubject comp) => (Info.Active || Info.Verifying) && CanUse && TickPatch.NoOp(comp);

        private static bool CanUse => Scribe.mode == LoadSaveMode.Inactive && UnityData.IsInMainThread;

        private static bool TryGetValid(CompOverseerSubject comp, out OverseerSubjectState state)
        {
            state = default;
            if (!cache.TryGetValue(comp, out var e))
                return false;
            var relations = (comp.parent as Pawn)?.relations;
            if (relations == null)
                return false;
            var list = relations.DirectRelations;
            if (list != e.Relations || ListVersion<DirectPawnRelation>.Of(list) != e.RelationsVersion)
                return false;
            var tracker = e.Overseer?.mechanitor;
            if (tracker != e.Tracker)
                return false;
            if (tracker != null)
            {
                var controlled = tracker.ControlledPawns;
                if (controlled != e.Controlled || ListVersion<Pawn>.Of(controlled) != e.ControlledVersion)
                    return false;
            }
            state = e.State;
            return true;
        }

        private static void Record(CompOverseerSubject comp, OverseerSubjectState state)
        {
            var relations = (comp.parent as Pawn)?.relations;
            if (relations == null)
                return;
            var list = relations.DirectRelations;
            var overseer = relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Overseer);
            var tracker = overseer?.mechanitor;
            var controlled = tracker?.ControlledPawns;
            if (list == null || tracker != null && controlled == null)
                return;
            cache[comp] = new Entry
            {
                Relations = list,
                RelationsVersion = ListVersion<DirectPawnRelation>.Of(list),
                Overseer = overseer,
                Tracker = tracker,
                Controlled = controlled,
                ControlledVersion = controlled == null ? 0 : ListVersion<Pawn>.Of(controlled),
                State = state,
            };
        }

        public static class StatePatch
        {
            public static bool Prefix(CompOverseerSubject __instance, ref OverseerSubjectState __result, out bool __state)
            {
                __state = false;
                if (!Info.Active && !Info.Verifying || !CanUse)
                    return true;
                if (Info.Active && TryGetValid(__instance, out var cached))
                {
                    Info.Stats.Hits++;
                    __result = cached;
                    return false;
                }
                __state = true;
                return true;
            }

            public static void Postfix(CompOverseerSubject __instance, OverseerSubjectState __result, bool __state)
            {
                if (!__state)
                    return;
                if (Info.Verifying && TryGetValid(__instance, out var cached))
                {
                    Info.Stats.Checks++;
                    if (cached != __result)
                        Info.Stats.Mismatch(() => $"{__instance.parent}: cached {cached}, fresh {__result}");
                    return;
                }
                Info.Stats.Misses++;
                Record(__instance, __result);
            }
        }

        public static class TickPatch
        {
            /// <summary>
            /// True when vanilla CompTick is known to do nothing: no effecter to clean up or tick, and CanGoFeral is
            /// false because the mech is not spawned, has no overseer subject, or is overseen.
            /// </summary>
            internal static bool NoOp(CompOverseerSubject comp)
            {
                if (CompOverseerSubject.debugDisableNeedsOverseerEffect || effect(comp) != null || !(comp.parent is Pawn pawn))
                    return false;
                if (!pawn.Spawned)
                    return true;
                var subject = pawn.OverseerSubject;
                if (subject == null)
                    return true;
                return TryGetValid(subject, out var state) && state == OverseerSubjectState.Overseen;
            }

            public static bool Prefix(CompOverseerSubject __instance, out int __state)
            {
                __state = int.MinValue;
                if (!Info.Active && !Info.Verifying || !CanUse)
                    return true;
                if (!NoOp(__instance))
                {
                    tickRuns++;
                    return true;
                }
                if (Info.Active)
                {
                    tickSkips++;
                    return false;
                }
                __state = delayUntilFeralCheck(__instance);
                return true;
            }

            public static void Postfix(CompOverseerSubject __instance, int __state)
            {
                if (__state == int.MinValue)
                    return;
                // Verify: vanilla ran on a tick predicted to be a no-op.
                Info.Stats.Checks++;
                var pawn = (Pawn)__instance.parent;
                if (effect(__instance) != null || delayUntilFeralCheck(__instance) != __state || canGoFeral(pawn))
                    Info.Stats.Mismatch(() => $"{pawn}: CompTick predicted no-op, but effect {effect(__instance) != null}, " +
                                              $"delay {__state} -> {delayUntilFeralCheck(__instance)}, canGoFeral {canGoFeral(pawn)}");
            }
        }
    }
}
