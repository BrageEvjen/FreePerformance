using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Benchmark-only patches that make vanilla repeatable across process launches, so a state hash can be
    /// compared between runs. Each entry is visual-only code that draws from the simulation RNG at moments that
    /// differ between launches; it gets a private copy of the RNG state (push before, pop after), which leaves
    /// its own behavior alone but stops it from shifting every later random roll in the simulation.
    /// </summary>
    public static class DeterminismShims
    {
        private static readonly (string type, string method, string why)[] IsolateRand =
        {
            // Spawn timing is staggered by WaterBody.GetHashCode(), which mixes in Map's identity hash code.
            ("Verse.FishShadowComponent", "MapComponentTick", "fish shadow flecks timed by identity hash"),
        };

        public static readonly List<string> Applied = new List<string>();

        public static void Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(DeterminismShims), nameof(PushRand));
            var finalizer = new HarmonyMethod(typeof(DeterminismShims), nameof(PopRand));
            foreach (var (typeName, methodName, why) in IsolateRand)
            {
                var method = AccessTools.Method(AccessTools.TypeByName(typeName), methodName);
                if (method == null)
                {
                    Log.Warning($"[Free Performance] Determinism shim target not found: {typeName}.{methodName}");
                    continue;
                }
                harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                Applied.Add($"{typeName}.{methodName} ({why})");
            }
        }

        /// <summary>Greater than zero while inside an isolated method; its Rand calls don't affect the simulation.</summary>
        public static int IsolationDepth;

        public static void PushRand()
        {
            Rand.PushState();
            IsolationDepth++;
        }

        public static void PopRand()
        {
            IsolationDepth--;
            Rand.PopState();
        }
    }
}
