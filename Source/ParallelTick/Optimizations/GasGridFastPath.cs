using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Once any gas has appeared on a map, GasGrid.Tick visits ~1.6% of the map's cells for dissipation and ~3.1% for
    /// diffusion every tick (about 2,900 calls on a 250x250 map), each through a profiler scope and a method call, even
    /// though almost all cells have no gas. TryDissipateGases returns at once when the cell has no gas (AnyGasAt), and
    /// TryDiffuseGases returns at once when its three diffusing gases sum to less than 17 (before it shuffles anything).
    /// This is the same loop with those two tests done inline, so the calls that would return at once are not made. The
    /// cycle indices advance exactly as vanilla. Exact.
    /// </summary>
    public static class GasGridFastPath
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "gasgrid",
            Label = "Skip empty cells in gas spreading  (exact)",
            Description = "Gas spreading checks thousands of map cells per tick for gas. Cells without gas are now skipped " +
                          "inside the loop instead of through a function call. Same result.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private static readonly AccessTools.FieldRef<GasGrid, Map> map = AccessTools.FieldRefAccess<GasGrid, Map>("map");
        private static readonly AccessTools.FieldRef<GasGrid, uint[]> gasDensity = AccessTools.FieldRefAccess<GasGrid, uint[]>("gasDensity");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleIndexDissipation = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDissipation");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleIndexDiffusion = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDiffusion");
        private static readonly AccessTools.FieldRef<GasGrid, List<IntVec3>> cellsInRandomOrder = AccessTools.FieldRefAccess<GasGrid, List<IntVec3>>("cellsInRandomOrder");
        private static readonly System.Action<GasGrid, int> tryDissipate =
            AccessTools.MethodDelegate<System.Action<GasGrid, int>>(AccessTools.Method(typeof(GasGrid), "TryDissipateGases"));
        private static readonly System.Action<GasGrid, IntVec3> tryDiffuse =
            AccessTools.MethodDelegate<System.Action<GasGrid, IntVec3>>(AccessTools.Method(typeof(GasGrid), "TryDiffuseGases"));
        private static readonly System.Action<GasGrid> recalculateEverHadGas =
            AccessTools.MethodDelegate<System.Action<GasGrid>>(AccessTools.Method(typeof(GasGrid), "RecalculateEverHadGas"));
        private static readonly System.Reflection.FieldInfo randIterations = AccessTools.Field(typeof(Rand), "iterations");

        private static long calls, skippedCalls;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GasGrid), nameof(GasGrid.Tick)),
                prefix: new HarmonyMethod(typeof(GasGridFastPath), nameof(Prefix)));
        }

        private static void Reset()
        {
            calls = skippedCalls = 0;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  cell calls made: {calls:N0}, skipped: {skippedCalls:N0}";
        }

        private static bool guardChecked, guardBlocked;

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            foreach (var name in new[] { "Tick", "TryDissipateGases", "TryDiffuseGases", "AnyGasAt", "DensityAt" })
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(GasGrid)).Where(m => m.Name == name))
            {
                var owners = Harmony.GetPatchInfo(m)?.Owners.Where(o => o != ParallelTickMod.Id).ToList();
                if (owners != null && owners.Count > 0)
                {
                    Log.Message($"[Free Performance] Gas grid shortcut stays off: GasGrid.{name} is patched by {string.Join(", ", owners)}.");
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        /// <summary>Vanilla GasGrid.Tick, with the "no gas here" early returns of its two per-cell calls done inline.</summary>
        public static bool Prefix(GasGrid __instance)
        {
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread || Blocked())
                return true;
            if (!__instance.CalculateGasEffects)
                return false;
            var m = map(__instance);
            var area = m.Area;
            var count = Mathf.CeilToInt(area * 0.015625f);
            var cells = cellsInRandomOrder(__instance) = m.cellsInRandomOrder.GetAll();
            var density = gasDensity(__instance);
            var sizeX = m.Size.x;
            var verifying = Info.Verifying;

            for (var i = 0; i < count; i++)
            {
                if (cycleIndexDissipation(__instance) >= area)
                    cycleIndexDissipation(__instance) = 0;
                var idx = CellIndicesUtility.CellToIndex(cells[cycleIndexDissipation(__instance)], sizeX);
                if (density[idx] != 0)
                {
                    calls++;
                    tryDissipate(__instance, idx);
                }
                else if (verifying)
                    CheckNoOp(__instance, idx, () => tryDissipate(__instance, idx), "dissipation");
                else
                    skippedCalls++;
                cycleIndexDissipation(__instance)++;
            }

            count = Mathf.CeilToInt(area * 0.03125f);
            for (var i = 0; i < count; i++)
            {
                if (cycleIndexDiffusion(__instance) >= area)
                    cycleIndexDiffusion(__instance) = 0;
                var cell = cells[cycleIndexDiffusion(__instance)];
                var v = density[CellIndicesUtility.CellToIndex(cell, sizeX)];
                if (((v >> 8) & 255) + ((v >> 16) & 255) + ((v >> 24) & 255) >= 17)
                {
                    calls++;
                    tryDiffuse(__instance, cell);
                }
                else if (verifying)
                    CheckNoOp(__instance, CellIndicesUtility.CellToIndex(cell, sizeX), () => tryDiffuse(__instance, cell), "diffusion");
                else
                    skippedCalls++;
                cycleIndexDiffusion(__instance)++;
            }

            if (Gen.IsHashIntervalTick(m, 600))
                recalculateEverHadGas(__instance);
            return false;
        }

        /// <summary>Verify: make the vanilla call for a cell predicted to be a no-op and check nothing changed.</summary>
        private static void CheckNoOp(GasGrid grid, int idx, System.Action call, string what)
        {
            // Both calls only write the cell itself and its 4 neighbours.
            var density = gasDensity(grid);
            var sizeX = map(grid).Size.x;
            var around = new[] { idx, idx - 1, idx + 1, idx - sizeX, idx + sizeX };
            var before = around.Select(i => i >= 0 && i < density.Length ? density[i] : 0u).ToArray();
            var rand = randIterations.GetValue(null);
            call();
            Info.Stats.Checks++;
            if (!Equals(randIterations.GetValue(null), rand))
                Info.Stats.Mismatch(() => $"{what} at cell {idx}: used the random number generator");
            if (!around.Select(i => i >= 0 && i < density.Length ? density[i] : 0u).SequenceEqual(before))
                Info.Stats.Mismatch(() => $"{what} at cell {idx}: changed gas density");
        }
    }
}
