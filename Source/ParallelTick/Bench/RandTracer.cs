using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Records every outermost call into Verse.Rand (tick, RNG iteration, thread, caller chain) so two runs can be
    /// diffed to find the first call that differs. Diagnostic only: stack traces make it very slow.
    /// </summary>
    public static class RandTracer
    {
        private static readonly HashSet<string> Skip = new HashSet<string>
        {
            "PushState", "PopState", "EnsureStateStackEmpty", "get_StateCompressed", "set_StateCompressed",
            "set_Seed", "get_Seed", "Block",
        };

        private static readonly AccessTools.FieldRef<uint> Iterations =
            AccessTools.StaticFieldRefAccess<uint>(AccessTools.Field(typeof(Rand), "iterations"));

        public static readonly List<string> Lines = new List<string>();
        public static bool Active;

        // Set by the benchmark around each DoSingleTick. Rendering between ticks also rolls Rand, but the
        // benchmark restores the simulation's RNG state before every chunk, so those calls are not recorded.
        public static bool InTick;
        private static int depth;
        private static readonly List<MethodBase> patched = new List<MethodBase>();

        public static void Attach(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(RandTracer), nameof(Prefix));
            var finalizer = new HarmonyMethod(typeof(RandTracer), nameof(Finalizer));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Rand)))
            {
                if (!m.IsStatic || m.IsGenericMethodDefinition || Skip.Contains(m.Name) || m.GetMethodBody() == null)
                    continue;
                harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                patched.Add(m);
            }
        }

        public static void Detach(Harmony harmony)
        {
            Active = false;
            foreach (var m in patched)
                harmony.Unpatch(m, HarmonyPatchType.All, harmony.Id);
            patched.Clear();
        }

        public static void Prefix(MethodBase __originalMethod)
        {
            if (!UnityData.IsInMainThread)
            {
                if (Active)
                    Lines.Add($"{Find.TickManager?.TicksGame} {Iterations()} WORKER-THREAD {__originalMethod.Name} <- {Callers()}");
                return;
            }
            if (depth++ > 0 || !Active || !InTick || DeterminismShims.IsolationDepth > 0)
                return;
            Lines.Add($"{Find.TickManager.TicksGame} {Iterations()} {__originalMethod.Name} <- {Callers()}");
        }

        public static void Finalizer()
        {
            if (UnityData.IsInMainThread)
                depth--;
        }

        private static string Callers()
        {
            var frames = new StackTrace(1, false).GetFrames();
            if (frames == null)
                return "?";
            var sb = new StringBuilder();
            var n = 0;
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                var type = m?.DeclaringType;
                if (m == null || type == null || type == typeof(Rand) || type == typeof(RandTracer) || m.Name.StartsWith("DMD<"))
                    continue;
                if (n++ > 0)
                    sb.Append(" <- ");
                sb.Append(type.Name).Append('.').Append(m.Name);
                if (n == 6)
                    break;
            }
            return sb.ToString();
        }
    }
}
