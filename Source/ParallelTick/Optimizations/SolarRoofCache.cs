using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// CompPowerPlantSolar recomputes RoofedPowerOutputFactor every tick by checking the roof over every cell it
    /// occupies (16 for a vanilla solar panel). Roofs only change through RoofGrid.SetRoof and RoofGrid.RemoveRoofUnsafe
    /// (the only writers of RoofGrid.roofGrid besides its constructor), so a per-grid change counter makes the cached
    /// factor exact: it is reused only while the panel's map, position, rotation and the grid's counter are unchanged.
    /// </summary>
    public static class SolarRoofCache
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "solar",
            Label = "Cache solar panel roof coverage  (exact)",
            Description = "Solar panels check the roof over each of their tiles every tick. This remembers the result until " +
                          "a roof on that map actually changes or the panel moves. Gives exactly the same power output.",
            Patch = Patch,
            Reset = Reset,
            Prune = Prune,
        };

        private struct Entry
        {
            public RoofGrid Grid;
            public int Version;
            public IntVec3 Position;
            public Rot4 Rotation;
            public float Factor;
        }

        private static readonly Dictionary<CompPowerPlantSolar, Entry> cache = new Dictionary<CompPowerPlantSolar, Entry>();
        private static readonly Dictionary<RoofGrid, int> roofVersions = new Dictionary<RoofGrid, int>();

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompPowerPlantSolar), "RoofedPowerOutputFactor"),
                prefix: new HarmonyMethod(typeof(SolarRoofCache), nameof(FactorPrefix)),
                postfix: new HarmonyMethod(typeof(SolarRoofCache), nameof(FactorPostfix)));
            var roofChanged = new HarmonyMethod(typeof(SolarRoofCache), nameof(RoofChanged));
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)), postfix: roofChanged);
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.RemoveRoofUnsafe)), postfix: roofChanged);
        }

        private static void Reset()
        {
            cache.Clear();
            roofVersions.Clear();
            Info.Stats.Reset();
        }

        private static void Prune(int now)
        {
            foreach (var comp in cache.Keys.Where(c => c.parent.Destroyed).ToList())
                cache.Remove(comp);
            foreach (var grid in roofVersions.Keys.Where(g => !cache.Values.Any(e => e.Grid == g)).ToList())
                roofVersions.Remove(grid);
        }

        public static void RoofChanged(RoofGrid __instance)
        {
            roofVersions.TryGetValue(__instance, out var v);
            roofVersions[__instance] = v + 1;
        }

        public static bool FactorPrefix(CompPowerPlantSolar __instance, ref float __result, out bool __state)
        {
            __state = false;
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread || !__instance.parent.Spawned)
                return true;
            if (Info.Active && TryGetFresh(__instance, out var entry))
            {
                Info.Stats.Hits++;
                __result = entry.Factor;
                return false;
            }
            __state = true;
            return true;
        }

        public static void FactorPostfix(CompPowerPlantSolar __instance, float __result, bool __state)
        {
            if (!__state)
                return;
            if (Info.Verifying && TryGetFresh(__instance, out var entry))
            {
                Info.Stats.Checks++;
                if (entry.Factor != __result)
                    Info.Stats.Mismatch(() => $"{__instance.parent} at {__instance.parent.Position}: cached {entry.Factor}, fresh {__result}");
                return;
            }
            Info.Stats.Misses++;
            var parent = __instance.parent;
            var grid = parent.Map.roofGrid;
            roofVersions.TryGetValue(grid, out var version);
            cache[__instance] = new Entry { Grid = grid, Version = version, Position = parent.Position, Rotation = parent.Rotation, Factor = __result };
        }

        private static bool TryGetFresh(CompPowerPlantSolar comp, out Entry entry)
        {
            if (!cache.TryGetValue(comp, out entry))
                return false;
            var parent = comp.parent;
            var grid = parent.Map.roofGrid;
            roofVersions.TryGetValue(grid, out var version);
            return entry.Grid == grid && entry.Version == version && entry.Position == parent.Position && entry.Rotation == parent.Rotation;
        }
    }
}
