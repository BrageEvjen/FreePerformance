using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// After ticking a thing that holds other things, Thing.DoTick walks its contents: it checks whether the holder is
    /// suspended (a walk up the holder chain to the world, with a world-object lookup), collects the child holders, and
    /// calls DoTick on every contained thing. Every pawn does this each tick for its apparel, equipment, inventory and
    /// carried thing, and so does every construction frame and bookcase. Almost all contained things do nothing when
    /// ticked this way: apparel, weapons and resources never tick, books tick rarely (every 250 ticks by their hash).
    ///
    /// For pawns, frames and bookcases (exact types), this checks the contents first and skips the walk when every thing
    /// it would tick does nothing this tick: not destroyed, not spawned, held by that owner, not itself a holder, and a
    /// Never ticker, or a Rare/Long ticker not on its 250/2000-tick hash. Frames and bookcases also need no holder comps
    /// on their items (those would be ticked as child holders). Otherwise vanilla runs. Exact.
    /// </summary>
    public static class ContentsTickSkip
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "contentsskip",
            Label = "Skip idle contents ticking  (exact)",
            Description = "Pawns' clothes, weapons and inventory, and construction sites' materials, are checked every tick in " +
                          "case they need ticking. When none of them do anything this tick, the check is skipped.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private static readonly AccessTools.FieldRef<ThingOwner, bool> dontTickContents = AccessTools.FieldRefAccess<ThingOwner, bool>("dontTickContents");
        private static bool[] alwaysTicks; // by ThingDef.index: Normal ticker, or a holder type (ticked as a child holder)
        private static long pawnHits, pawnMisses, frameHits, frameMisses, bookcaseHits, bookcaseMisses;
        private static readonly Dictionary<string, long> fallbackDefs = new Dictionary<string, long>();

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
                transpiler: new HarmonyMethod(typeof(ContentsTickSkip), nameof(Transpiler)));
            if (Info.Mode == OptMode.Verify)
                harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
                    prefix: new HarmonyMethod(typeof(VerifyPatch), nameof(VerifyPatch.Prefix)),
                    postfix: new HarmonyMethod(typeof(VerifyPatch), nameof(VerifyPatch.Postfix)));
        }

        private static void Reset()
        {
            pawnHits = pawnMisses = frameHits = frameMisses = bookcaseHits = bookcaseMisses = 0;
            fallbackDefs.Clear();
            alwaysTicks = null;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  pawns: {pawnHits:N0} skipped / {pawnMisses:N0} ticked, frames: {frameHits:N0} / {frameMisses:N0}, bookcases: {bookcaseHits:N0} / {bookcaseMisses:N0}";
            foreach (var kv in fallbackDefs.OrderByDescending(kv => kv.Value).Take(8))
                yield return $"  contents ticked because of {kv.Key}: {kv.Value:N0}";
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.ContentsSuspended));
            return SafeTranspile.Replace(instructions, 1, "Idle-contents skipping (ContentsSuspended in Thing.DoTick)",
                ins => ins.Calls(target),
                ins =>
                {
                    ins.operand = AccessTools.Method(typeof(ContentsTickSkip), nameof(SkipContents));
                    return new[] { ins };
                });
        }

        private static bool guardChecked, guardBlocked;

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            var methods = new List<MethodBase>
            {
                AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
                AccessTools.Method(typeof(ThingOwner), nameof(ThingOwner.DoTick)),
                AccessTools.Method(typeof(Pawn), nameof(Pawn.GetChildHolders)),
                AccessTools.Method(typeof(Frame), nameof(Frame.GetChildHolders)),
                AccessTools.Method(typeof(Building_Bookcase), nameof(Building_Bookcase.GetChildHolders)),
                AccessTools.Method(typeof(Pawn), nameof(Pawn.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Frame), nameof(Frame.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Building_Bookcase), nameof(Building_Bookcase.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.GetDirectlyHeldThings)),
                AccessTools.Method(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.AppendThingHoldersFromThings)),
            };
            foreach (var m in methods)
            {
                var info = m == null ? null : Harmony.GetPatchInfo(m);
                var owners = info?.Owners.Where(o => o != ParallelTickMod.Id).ToList();
                if (owners != null && owners.Count > 0)
                {
                    Log.Message($"[Free Performance] Idle-contents skipping stays off: {m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", owners)}.");
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        private static void BuildTable()
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading;
            alwaysTicks = new bool[defs.Max(d => d.index) + 1];
            foreach (var d in defs)
                alwaysTicks[d.index] = d.tickerType == TickerType.Normal || d.thingClass != null && typeof(IThingHolder).IsAssignableFrom(d.thingClass);
        }

        /// <summary>Replaces ContentsSuspended(holder) in Thing.DoTick; true makes DoTick return before the contents walk.</summary>
        public static bool SkipContents(IThingHolder holder)
        {
            if (!Info.Active && !Info.Verifying || Blocked())
                return ThingOwnerUtility.ContentsSuspended(holder);
            if (alwaysTicks == null)
                BuildTable();
            var now = Find.TickManager.TicksGame;
            bool inert;
            var type = holder.GetType();
            if (type == typeof(Pawn))
            {
                var p = (Pawn)holder;
                // Pawn.GetChildHolders: its own (null) container, then inventory, carryTracker, equipment, apparel.
                inert = Inert(p.apparel?.GetDirectlyHeldThings(), now, false) && Inert(p.equipment?.GetDirectlyHeldThings(), now, false) &&
                        Inert(p.carryTracker?.innerContainer, now, false) && Inert(p.inventory?.innerContainer, now, false);
                if (inert) pawnHits++; else pawnMisses++;
            }
            else if (type == typeof(Frame))
            {
                inert = Inert(((Frame)holder).resourceContainer, now, true);
                if (inert) frameHits++; else frameMisses++;
            }
            else if (type == typeof(Building_Bookcase))
            {
                inert = Inert(holder.GetDirectlyHeldThings(), now, true);
                if (inert) bookcaseHits++; else bookcaseMisses++;
            }
            else
                return ThingOwnerUtility.ContentsSuspended(holder);

            if (!inert)
                return ThingOwnerUtility.ContentsSuspended(holder);
            if (Info.Verifying)
            {
                // Vanilla walks the contents; VerifyPatch checks every contained DoTick changes nothing.
                var suspended = ThingOwnerUtility.ContentsSuspended(holder);
                if (!suspended)
                    VerifyPatch.Begin((Thing)holder);
                return suspended;
            }
            Info.Stats.Hits++;
            return true;
        }

        /// <summary>
        /// True when ThingOwner.DoTick on this owner does nothing this tick. checkComps: the owner's holder uses
        /// AppendThingHoldersFromThings, which also ticks holder comps of its items.
        /// </summary>
        private static bool Inert(ThingOwner owner, int now, bool checkComps)
        {
            if (owner == null || dontTickContents(owner))
                return true;
            for (var i = owner.Count - 1; i >= 0; i--)
            {
                var t = owner[i];
                var def = t.def;
                if (def.index >= alwaysTicks.Length || alwaysTicks[def.index] || t.Destroyed || t.Spawned || t.holdingOwner != owner || t is IThingHolder)
                    return Fallback(def);
                switch (def.tickerType)
                {
                    case TickerType.Never:
                        break;
                    case TickerType.Rare:
                        if (unchecked(now + Gen.HashOffset(t)) % 250 == 0)
                            return false;
                        break;
                    case TickerType.Long:
                        if (unchecked(now + Gen.HashOffset(t)) % 2000 == 0)
                            return false;
                        break;
                    default:
                        return Fallback(def);
                }
                if (checkComps && t is ThingWithComps twc)
                {
                    var comps = twc.AllComps;
                    for (var c = 0; c < comps.Count; c++)
                        if (comps[c] is IThingHolder)
                            return Fallback(def);
                }
            }
            return true;
        }

        private static bool Fallback(ThingDef def)
        {
            fallbackDefs.TryGetValue(def.defName, out var n);
            fallbackDefs[def.defName] = n + 1;
            return false;
        }

        /// <summary>Verify: while vanilla walks contents predicted to be idle, every nested DoTick must change nothing.</summary>
        public static class VerifyPatch
        {
            private static Thing holder;
            private static readonly FieldInfo randIterations = AccessTools.Field(typeof(Rand), "iterations");

            public static void Begin(Thing h) => holder = h;

            private static bool IgnoredField(string name) => name.StartsWith("cached") || name == "tmpHolders";

            public static void Prefix(Thing __instance, out object[] __state)
            {
                __state = null;
                if (holder != null && __instance != holder)
                    __state = new object[] { ShallowSnapshot.Take(__instance), randIterations.GetValue(null), __instance.holdingOwner?.Count };
            }

            public static void Postfix(Thing __instance, object[] __state)
            {
                if (__instance == holder)
                {
                    holder = null;
                    return;
                }
                if (__state == null)
                    return;
                Info.Stats.Checks++;
                var diff = ShallowSnapshot.Diff(__instance, (object[])__state[0], IgnoredField);
                if (diff != null)
                    Info.Stats.Mismatch(() => $"{__instance} in {holder}: {diff}");
                if (!Equals(randIterations.GetValue(null), __state[1]))
                    Info.Stats.Mismatch(() => $"{__instance} in {holder}: used the random number generator");
                if (!Equals(__instance.holdingOwner?.Count, __state[2]))
                    Info.Stats.Mismatch(() => $"{__instance} in {holder}: owner count changed");
            }
        }
    }
}
