using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// ReservationManager.CanReserve, which every job search and bed search calls many times, scans the map's whole
    /// reservation list with IsAlreadyReserved(claimant, target, layer, count) twice with the same arguments: at IL 0x00C8
    /// (returning true if it matches) and again at 0x0180. The second call is only reached from 0x00D8/0x00EB/0x00FB,
    /// right after the first returned false, with only pure reads (target thing, building, interaction-cell flag) in
    /// between, so it always returns false. It is replaced with false. Exact.
    /// </summary>
    public static class ReservationDedup
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "reservedup",
            Label = "Skip a repeated reservation check  (exact)",
            Description = "CanReserve looked through all reservations on the map twice with the same question; the second " +
                          "answer is always the same as the first, so it is no longer asked.",
            Patch = Patch,
            Reset = () => Info.Stats.Reset(),
        };

        private static readonly Func<ReservationManager, Pawn, LocalTargetInfo, ReservationLayerDef, int, bool> isAlreadyReserved =
            AccessTools.MethodDelegate<Func<ReservationManager, Pawn, LocalTargetInfo, ReservationLayerDef, int, bool>>(
                AccessTools.Method(typeof(ReservationManager), "IsAlreadyReserved"));

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ReservationManager), nameof(ReservationManager.CanReserve)),
                transpiler: new HarmonyMethod(typeof(ReservationDedup), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(ReservationManager), "IsAlreadyReserved");
            var list = new List<CodeInstruction>(instructions);
            var sites = new List<int>();
            for (var i = 0; i < list.Count; i++)
                if (list[i].Calls(target))
                    sites.Add(i);
            if (sites.Count != 2)
            {
                Log.Message($"[Free Performance] ReservationDedup expected 2 IsAlreadyReserved calls in CanReserve, found {sites.Count}; not patched.");
                return list;
            }
            list[sites[1]].operand = AccessTools.Method(typeof(ReservationDedup), nameof(SecondCheck));
            return list;
        }

        /// <summary>Same stack as IsAlreadyReserved (this, claimant, target, layer, count).</summary>
        public static bool SecondCheck(ReservationManager manager, Pawn claimant, LocalTargetInfo target, ReservationLayerDef layer, int count)
        {
            if (Info.Active && UnityData.IsInMainThread)
            {
                Info.Stats.Hits++;
                return false;
            }
            var result = isAlreadyReserved(manager, claimant, target, layer, count);
            if (Info.Verifying)
            {
                Info.Stats.Checks++;
                if (result)
                    Info.Stats.Mismatch(() => $"{claimant} / {target}: second IsAlreadyReserved returned true");
            }
            return result;
        }
    }
}
