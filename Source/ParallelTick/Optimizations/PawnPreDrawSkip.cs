using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// PawnRenderTree.Draw calls worker.PreDraw and then worker.GetMaterialPropertyBlock for every draw request (every
    /// body part, apparel layer and so on of every visible pawn, every frame). The base PreDraw only sets the node's
    /// property-block colour to tint * material.color (three native calls, one with string marshalling), and
    /// GetMaterialPropertyBlock then always overwrites that same colour, or returns null so the block is not used for
    /// this draw. Only these two methods touch the node's block. So the base PreDraw's write is never seen, and it is
    /// skipped; a worker type that overrides PreDraw (none in vanilla) still gets its call. Visual only, identical.
    /// </summary>
    public static class PawnPreDrawSkip
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "predraw",
            Label = "Skip a dead colour write when drawing pawns  (exact)",
            Description = "Each body part and piece of clothing set a colour before drawing that was always overwritten " +
                          "right after. It is no longer set twice. Looks the same.",
            Patch = Patch,
            Reset = () => Info.Stats.Reset(),
        };

        private static readonly Dictionary<Type, bool> overridesPreDraw = new Dictionary<Type, bool>();
        private static bool guardChecked, guardBlocked;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PawnRenderTree), nameof(PawnRenderTree.Draw)),
                transpiler: new HarmonyMethod(typeof(PawnPreDrawSkip), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var preDraw = AccessTools.Method(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.PreDraw));
            // Stack: worker, node, material, parms -> the same arguments to a static helper.
            return SafeTranspile.Replace(instructions, 1, "Pawn PreDraw skip (PawnRenderTree.Draw)",
                ins => ins.Calls(preDraw),
                ins =>
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(PawnPreDrawSkip), nameof(PreDraw));
                    return new[] { ins };
                });
        }

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            foreach (var m in new[]
                     {
                         AccessTools.Method(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.PreDraw)),
                         AccessTools.Method(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.GetMaterialPropertyBlock)),
                         AccessTools.PropertyGetter(typeof(PawnRenderNode), nameof(PawnRenderNode.MatPropBlock)),
                     })
            {
                var owners = PatchGuard.ForeignOwners(m);
                if (owners != null && owners.Count > 0)
                {
                    Info.LogBlocked($"{m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", owners)}");
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        public static void PreDraw(PawnRenderNodeWorker worker, PawnRenderNode node, Material mat, PawnDrawParms parms)
        {
            if (!Info.Active || !UnityData.IsInMainThread || Blocked() || OverridesPreDraw(worker.GetType()))
            {
                worker.PreDraw(node, mat, parms);
                return;
            }
            Info.Stats.Hits++;
        }

        private static bool OverridesPreDraw(Type type)
        {
            if (!overridesPreDraw.TryGetValue(type, out var result))
                overridesPreDraw[type] = result =
                    AccessTools.Method(type, nameof(PawnRenderNodeWorker.PreDraw))?.DeclaringType != typeof(PawnRenderNodeWorker);
            return result;
        }
    }
}
