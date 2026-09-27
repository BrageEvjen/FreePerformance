using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Two exact savings in the "what needs hauling" bookkeeping (ListerHaulables), which re-checks stored items every
    /// tick and asks, for each, whether better storage exists.
    ///
    /// 1. StoreUtility.TryFindBestBetterNonSlotGroupStorageFor walks every haul destination in priority order and, as
    ///    its first test, skips every one that is a stockpile or shelf (ISlotGroupParent) without touching any state.
    ///    In a big base that is nearly all of them (~410 of ~430 in the benchmark save), for every stored item checked.
    ///    The list is replaced with a cached copy that holds only the non-slot-group destinations, in the same order,
    ///    rebuilt whenever the source list changes (reference or List._version). Everything else is still read live.
    ///
    /// 2. ListerHaulables.CellsCheckTick recalculates 4 cells of one storage group per tick, cycling through its cells,
    ///    so a 1-cell shelf is recalculated 4 times and a 2-cell shelf twice each, in the same tick. Recalculating a cell
    ///    only adds or removes its things from the haulables set according to ShouldBeHaulable, which does not change
    ///    between two calls in the same tick, so repeats are skipped. The cycle counters advance exactly as vanilla.
    /// </summary>
    public static class HaulablesFastPath
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "haulables",
            Label = "Faster hauling bookkeeping  (exact)",
            Description = "When checking whether stored items should be moved to better storage, stockpiles and shelves " +
                          "are no longer scanned in a list where they are always skipped, and a storage cell is only " +
                          "re-checked once per tick instead of up to 4 times. Same result.",
            Patch = Patch,
            Guarded = () => new MethodBase[]
            {
                AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor)),
                AccessTools.Method(typeof(ListerHaulables), "CellsCheckTick"),
                AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.RecalcAllInCell)),
                AccessTools.PropertyGetter(typeof(HaulDestinationManager), nameof(HaulDestinationManager.AllHaulDestinationsListInPriorityOrder)),
            },
            Reset = Reset,
            ReportLines = Report,
        };

        private static readonly Dictionary<List<IHaulDestination>, Filtered> filtered =
            new Dictionary<List<IHaulDestination>, Filtered>(RefEq<List<IHaulDestination>>.Instance);

        private sealed class Filtered
        {
            public int Version = -1;
            public readonly List<IHaulDestination> List = new List<IHaulDestination>();
        }

        private static readonly IntVec3[] recalculated = new IntVec3[4];
        private static int recalculatedCount;
        private static long cellRuns, cellSkips, rebuilds, filteredCalls;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor)),
                transpiler: new HarmonyMethod(typeof(HaulablesFastPath), nameof(DestinationsTranspiler)));
            var cellsCheckTick = AccessTools.Method(typeof(ListerHaulables), "CellsCheckTick");
            harmony.Patch(cellsCheckTick,
                prefix: new HarmonyMethod(typeof(HaulablesFastPath), nameof(CellsCheckStart)),
                transpiler: new HarmonyMethod(typeof(HaulablesFastPath), nameof(CellsTranspiler)));
        }

        private static void Reset()
        {
            filtered.Clear();
            recalculatedCount = 0;
            cellRuns = cellSkips = rebuilds = filteredCalls = 0;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  non-slot-group list: {filteredCalls} calls, {rebuilds} rebuilds";
            yield return $"  storage cells recalculated: {cellRuns}, repeats skipped: {cellSkips}";
        }

        // ---- Part 1 ----

        public static IEnumerable<CodeInstruction> DestinationsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(HaulDestinationManager), nameof(HaulDestinationManager.AllHaulDestinationsListInPriorityOrder));
            return SafeTranspile.Replace(instructions, 1, "Haul destination filter (AllHaulDestinationsListInPriorityOrder)",
                ins => ins.Calls(getter),
                ins => new[]
                {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HaulablesFastPath), nameof(NonSlotGroupDestinations)))
                        .MoveLabelsFrom(ins).MoveBlocksFrom(ins),
                });
        }

        public static List<IHaulDestination> NonSlotGroupDestinations(HaulDestinationManager manager)
        {
            var source = manager.AllHaulDestinationsListInPriorityOrder;
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread)
                return source;
            var version = ListVersion<IHaulDestination>.Of(source);
            if (!filtered.TryGetValue(source, out var f))
                filtered[source] = f = new Filtered();
            if (f.Version != version)
            {
                f.List.Clear();
                foreach (var d in source)
                    if (!(d is ISlotGroupParent))
                        f.List.Add(d);
                f.Version = version;
                rebuilds++;
            }
            filteredCalls++;
            if (!Info.Verifying)
                return f.List;

            // Verify: the cached list must equal a fresh filter of the source; vanilla still gets the source list.
            Info.Stats.Checks++;
            var i = 0;
            var ok = true;
            foreach (var d in source)
            {
                if (d is ISlotGroupParent)
                    continue;
                if (i >= f.List.Count || f.List[i] != d)
                    ok = false;
                i++;
            }
            if (!ok || i != f.List.Count)
                Info.Stats.Mismatch(() => $"non-slot-group destinations: cached {f.List.Count}, fresh {i}");
            return source;
        }

        // ---- Part 2 ----

        public static void CellsCheckStart() => recalculatedCount = 0;

        public static IEnumerable<CodeInstruction> CellsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var recalc = AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.RecalcAllInCell));
            // Stack: [this, cell] -> RecalcOnce(this, cell).
            return SafeTranspile.Replace(instructions, 1, "Storage cell recheck (RecalcAllInCell in CellsCheckTick)",
                ins => ins.Calls(recalc),
                ins => new[]
                {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HaulablesFastPath), nameof(RecalcOnce)))
                        .MoveLabelsFrom(ins).MoveBlocksFrom(ins),
                });
        }

        private static readonly List<bool> membershipBefore = new List<bool>();

        public static void RecalcOnce(ListerHaulables lister, IntVec3 cell)
        {
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread)
            {
                lister.RecalcAllInCell(cell);
                return;
            }
            var repeat = false;
            for (var i = 0; i < recalculatedCount; i++)
                if (recalculated[i] == cell)
                    repeat = true;
            if (!repeat)
            {
                if (recalculatedCount < recalculated.Length)
                    recalculated[recalculatedCount++] = cell;
                cellRuns++;
                lister.RecalcAllInCell(cell);
                return;
            }
            if (Info.Active)
            {
                cellSkips++;
                return;
            }

            // Verify: a repeat must not change which of the cell's things are listed as haulable.
            var things = cell.GetThingList(lister_map(lister));
            membershipBefore.Clear();
            var haulables = lister_haulables(lister);
            foreach (var t in things)
                membershipBefore.Add(haulables.Contains(t));
            var countBefore = things.Count;
            lister.RecalcAllInCell(cell);
            Info.Stats.Checks++;
            if (things.Count != countBefore)
            {
                Info.Stats.Mismatch(() => $"cell {cell}: thing count changed during recalculation");
                return;
            }
            for (var i = 0; i < things.Count; i++)
                if (haulables.Contains(things[i]) != membershipBefore[i])
                {
                    var t = things[i];
                    Info.Stats.Mismatch(() => $"{t} at {cell}: haulable {!haulables.Contains(t)} -> {haulables.Contains(t)} on a repeat check");
                }
        }

        private static readonly AccessTools.FieldRef<ListerHaulables, Map> lister_map = AccessTools.FieldRefAccess<ListerHaulables, Map>("map");
        private static readonly AccessTools.FieldRef<ListerHaulables, HashSet<Thing>> lister_haulables =
            AccessTools.FieldRefAccess<ListerHaulables, HashSet<Thing>>("haulables");
    }
}
