using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Combat Extended's transpiler on VerbTracker.VerbsTick (Harmony_VerbTracker_Modify_VerbsTick, read in the running
    /// game's code): after each verb's VerbTick it calls VerbTickCE on verbs that are Verb_LaunchProjectileCE. That method is
    /// empty in Verb_LaunchProjectileCE; subclasses such as Verb_ShootCE (aiming, bipods) and Verb_MarkForArtillery
    /// override it. So VerbsTick is still a no-op for verbs that are idle by vanilla's rules and whose type doesn't
    /// override VerbTickCE.
    ///
    /// The idle-pawn skip accepts the transpiler only when VerbsTick's current code is exactly vanilla's loop plus that
    /// call (checked once, so a changed CE version or another mod's change is not accepted), and then counts a verb as
    /// idle only if its type doesn't override VerbTickCE (Quiet).
    /// </summary>
    public static class CeVerbs
    {
        private const string PatchType = "CombatExtended.HarmonyCE.Harmony_VerbTracker_Modify_VerbsTick";
        private static bool? shapeOk;
        private static Type launchVerb;
        private static readonly Dictionary<Type, bool> quiet = new Dictionary<Type, bool>();

        public static bool IsCePatch(Patch patch) => patch?.PatchMethod?.DeclaringType?.FullName == PatchType;

        /// <summary>For PatchGuard.ForeignOwners on VerbsTick: CE's transpiler, if the resulting code has the known shape.</summary>
        public static bool Accepts(Patch patch)
        {
            if (!IsCePatch(patch))
                return false;
            if (shapeOk == null)
            {
                try
                {
                    shapeOk = ShapeOk();
                }
                catch (Exception e)
                {
                    Log.Message($"[Free Performance] Could not read Combat Extended's VerbsTick change ({e.GetType().Name}: {e.Message}); verbs run as vanilla.");
                    shapeOk = false;
                }
            }
            return shapeOk.Value;
        }

        /// <summary>True when the verb's VerbTickCE is the empty one (or it is not a CE projectile verb).</summary>
        public static bool Quiet(Verb verb)
        {
            var type = verb.GetType();
            if (!quiet.TryGetValue(type, out var q))
                quiet[type] = q = launchVerb == null || !launchVerb.IsAssignableFrom(type) ||
                                  AccessTools.Method(type, "VerbTickCE")?.DeclaringType == launchVerb;
            return q;
        }

        /// <summary>
        /// VerbsTick as it runs now: vanilla's loop over verbs calling VerbTick, plus isinst Verb_LaunchProjectileCE and a
        /// call to its VerbTickCE; nothing else (no other calls, fields, or stores besides locals).
        /// </summary>
        private static bool ShapeOk()
        {
            launchVerb = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
            var tickCe = launchVerb == null ? null : AccessTools.Method(launchVerb, "VerbTickCE");
            if (tickCe == null)
                return false;
            var verbsField = AccessTools.Field(typeof(VerbTracker), "verbs");
            var calls = new List<MethodBase>();
            foreach (var ins in PatchProcessor.GetCurrentInstructions(AccessTools.Method(typeof(VerbTracker), nameof(VerbTracker.VerbsTick))))
            {
                var op = ins.opcode;
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                    calls.Add(ins.operand as MethodBase);
                else if (op == OpCodes.Isinst)
                {
                    if (!Equals(ins.operand, launchVerb))
                        return false;
                }
                else if (op == OpCodes.Ldfld)
                {
                    if (!Equals(ins.operand, verbsField))
                        return false;
                }
                else if (!(op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch || op == OpCodes.Ret ||
                           op == OpCodes.Nop || op == OpCodes.Ldarg_0 || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1 ||
                           op == OpCodes.Add || ins.IsLdloc() || ins.IsStloc()))
                    return false;
            }
            var names = calls.Select(m => m == null ? "?" : $"{m.DeclaringType?.Name}.{m.Name}").ToList();
            var expected = new[] { "List`1.get_Item", "Verb.VerbTick", "Verb_LaunchProjectileCE.VerbTickCE", "List`1.get_Count" };
            if (!names.SequenceEqual(expected))
                return false;
            Log.Message("[Free Performance] Combat Extended's VerbsTick change checked: only CE projectile verbs get an extra VerbTickCE call.");
            return true;
        }
    }
}
