using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Every world object is ticked every tick, and a late game has hundreds of settlements (250 on the test save).
    /// WorldObject.DoTick for a settlement calls Tick (a loop over its comps, whose CompTick is WorldObjectComp's empty
    /// one), counts tickDelta, runs TickInterval on its 15-tick interval, and walks its contents: its own things
    /// (MapParent: none), its map (skipped by DoTick, owner is the Map) and its trader's stock.
    ///
    /// On a tick that is not the settlement's interval tick, with only empty-CompTick comps and no trade stock, all of
    /// that does nothing except tickDelta++; this does exactly that. Interval ticks, settlements with stock or other
    /// comps, and every other world object type run vanilla. Exact.
    /// </summary>
    public static class SettlementFastTick
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "settlementtick",
            Label = "Skip idle settlement ticks  (exact)",
            Description = "Hundreds of world-map settlements are ticked every tick even though almost all of them have " +
                          "nothing to do between their regular 15-tick updates. Those empty ticks are skipped.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private sealed class CompsState
        {
            public List<WorldObjectComp> List;
            public int Count;
            public bool Idle;
        }

        private static readonly Dictionary<WorldObject, CompsState> comps = new Dictionary<WorldObject, CompsState>(RefEq<WorldObject>.Instance);
        private static readonly Dictionary<System.Type, bool> emptyCompTick = new Dictionary<System.Type, bool>();
        private static readonly AccessTools.FieldRef<WorldObject, int> tickDelta = AccessTools.FieldRefAccess<WorldObject, int>("tickDelta");
        private static readonly AccessTools.FieldRef<WorldObject, List<WorldObjectComp>> objectComps = AccessTools.FieldRefAccess<WorldObject, List<WorldObjectComp>>("comps");
        private static long skipped, ran;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.DoTick)),
                prefix: new HarmonyMethod(typeof(SettlementFastTick), nameof(Prefix)) { priority = Priority.First });
        }

        private static void Reset()
        {
            comps.Clear();
            skipped = ran = 0;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  settlement ticks skipped: {skipped:N0}, run: {ran:N0}";
        }

        private static bool guardChecked, guardBlocked;

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            var methods = new List<MethodBase>
            {
                AccessTools.Method(typeof(WorldObject), nameof(WorldObject.DoTick)),
                AccessTools.Method(typeof(WorldObject), "Tick"),
                AccessTools.Method(typeof(WorldObject), "TickInterval"),
                AccessTools.Method(typeof(WorldObjectComp), nameof(WorldObjectComp.CompTick)),
                AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTicks"),
                AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTickOffset"),
                AccessTools.Method(typeof(Settlement), nameof(Settlement.GetChildHolders)),
                AccessTools.Method(typeof(MapParent), nameof(MapParent.GetChildHolders)),
                AccessTools.Method(typeof(MapParent), nameof(MapParent.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(ThingOwner), nameof(ThingOwner.DoTick)),
                AccessTools.PropertyGetter(typeof(WorldRendererUtility), nameof(WorldRendererUtility.WorldSelected)),
            };
            foreach (var m in methods)
            {
                var owners = PatchGuard.ForeignOwners(m);
                if (owners != null && owners.Count > 0)
                {
                    Info.LogBlocked($"{m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", owners)}");
                    guardBlocked = true;
                }
            }
            if (AccessTools.Method(typeof(Settlement), "Tick")?.DeclaringType != typeof(WorldObject) ||
                AccessTools.PropertyGetter(typeof(Settlement), "UpdateRateTicks")?.DeclaringType != typeof(WorldObject) ||
                AccessTools.PropertyGetter(typeof(Settlement), "UpdateRateTickOffset")?.DeclaringType != typeof(WorldObject))
                guardBlocked = true;
            return guardBlocked;
        }

        private static bool CompsIdle(WorldObject wo)
        {
            var list = objectComps(wo);
            if (list == null)
                return true;
            if (!comps.TryGetValue(wo, out var c))
                comps[wo] = c = new CompsState();
            if (c.List != list || c.Count != list.Count)
            {
                c.List = list;
                c.Count = list.Count;
                c.Idle = list.All(comp => EmptyCompTick(comp.GetType()));
            }
            return c.Idle;
        }

        private static bool EmptyCompTick(System.Type type)
        {
            if (!emptyCompTick.TryGetValue(type, out var empty))
                emptyCompTick[type] = empty = AccessTools.Method(type, nameof(WorldObjectComp.CompTick))?.DeclaringType == typeof(WorldObjectComp);
            return empty;
        }

        public static bool Prefix(WorldObject __instance)
        {
            if (!Info.Active || __instance.GetType() != typeof(Settlement) || !UnityData.IsInMainThread || Blocked())
                return true;
            var settlement = (Settlement)__instance;
            // WorldObject.UpdateRateTicks / UpdateRateTickOffset (not overridden by Settlement, checked in Blocked).
            var rate = WorldRendererUtility.WorldSelected ? 1 : 15;
            ref var delta = ref tickDelta(settlement);
            if (delta + 1 >= rate || GenTicks.IsTickInterval(Gen.HashOffset(settlement), rate))
            {
                ran++;
                return true;
            }
            // Contents: the trader's stock is the only thing DoTick would tick.
            var stock = settlement.trader?.GetDirectlyHeldThings();
            if (stock != null && stock.Count > 0 || !CompsIdle(settlement))
            {
                ran++;
                return true;
            }
            delta++;
            skipped++;
            Info.Stats.Hits++;
            return false;
        }
    }
}
