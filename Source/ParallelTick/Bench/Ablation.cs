using HarmonyLib;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Diagnostic only: skips one void method entirely while Skip is true. Used through the in-process A/B
    /// ("ab=ablate:Namespace.Type.Method"), the difference between blocks is that method's true cost without any
    /// profiler timers. The game misbehaves while the method is skipped, which is fine for a throwaway benchmark run.
    /// </summary>
    public static class Ablation
    {
        public static bool Skip;
        public static string Target;

        public static void Apply(Harmony harmony, string target)
        {
            var dot = target.LastIndexOf('.');
            var type = AccessTools.TypeByName(target.Substring(0, dot));
            var method = type == null ? null : AccessTools.Method(type, target.Substring(dot + 1));
            if (method == null)
            {
                Log.Error($"[Free Performance] Ablation target not found: {target}");
                return;
            }
            if (method is System.Reflection.MethodInfo mi && mi.ReturnType != typeof(void))
            {
                Log.Error($"[Free Performance] Ablation target must return void: {target}");
                return;
            }
            Target = target;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Ablation), nameof(Prefix)) { priority = Priority.First });
        }

        public static bool Prefix() => !Skip;
    }
}
