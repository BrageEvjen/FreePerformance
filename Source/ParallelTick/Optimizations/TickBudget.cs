using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// TickManager.TickManagerUpdate stops running ticks once 45.45 ms (1000/22) have passed in the current frame, then
    /// the frame is drawn. In a large colony at high speed a frame is ~45 ms of ticks plus ~40 ms of drawing, so game
    /// speed is capped by how often frames happen. This replaces that constant with a player setting: a larger budget
    /// runs more ticks per frame (faster game time) at a lower frame rate. The simulation itself is unchanged.
    /// </summary>
    public static class TickBudget
    {
        public const float VanillaMs = 45.454544f;

        /// <summary>Budget in ms; set from settings (or ptbench.txt in play tests).</summary>
        public static float BudgetMs = VanillaMs;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)),
                transpiler: new HarmonyMethod(typeof(TickBudget), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(TickBudget), nameof(BudgetMs));
            return SafeTranspile.Replace(instructions, 1, "Frame budget (45.45 ms constant in TickManagerUpdate)",
                ins => ins.opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && ins.operand is float f && System.Math.Abs(f - VanillaMs) < 0.001f,
                ins => new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ldsfld, field).MoveLabelsFrom(ins).MoveBlocksFrom(ins) });
        }
    }
}
