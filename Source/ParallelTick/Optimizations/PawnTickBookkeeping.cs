using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Pawn.Tick and Pawn.TickInterval do some bookkeeping for every pawn, every time, that has a fixed answer for pawns
    /// standing on a map:
    /// - Suspended and IsWorldPawn look the pawn up in the world-pawn sets (3 hash lookups each). A spawned pawn is never
    ///   a world pawn: Pawn.SpawnSetup removes it from WorldPawns and WorldPawns.PassToWorld refuses spawned pawns. So
    ///   for a spawned pawn of the plain Pawn type both are false.
    /// - BloodRainTick asks whether the pawn is exposed to blood rain; without an active blood-rain condition on the map
    ///   it cannot be (checked once per map per tick).
    /// - GeneTrackerTick calls Active and Tick on every gene; Gene.Tick is empty and no gene type overrides it, so
    ///   the loop does nothing. Skipped only if no loaded gene type overrides Tick or Active.
    /// Exact.
    /// </summary>
    public static class PawnTickBookkeeping
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "pawnbook",
            Label = "Faster pawn bookkeeping  (exact)",
            Description = "Skips per-tick lookups whose answer is fixed for pawns on a map (\"is this a world pawn?\", " +
                          "\"is there blood rain?\") and the gene tick loop, which does nothing. Same result.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private static bool genesSkippable;
        private static long suspendedHits, worldPawnHits, bloodRainSkips, geneSkips;

        private static void Patch(Harmony harmony)
        {
            genesSkippable = GenTypes.AllSubclasses(typeof(Gene)).All(t =>
                AccessTools.Method(t, nameof(Gene.Tick))?.DeclaringType == typeof(Gene) &&
                (AccessTools.PropertyGetter(t, nameof(Gene.Active))?.DeclaringType == typeof(Gene) ||
                 AccessTools.PropertyGetter(t, nameof(Gene.Active))?.DeclaringType == typeof(Gene_ChemicalDependency)));
            var transpiler = new HarmonyMethod(typeof(PawnTickBookkeeping), nameof(Transpiler));
            harmony.Patch(AccessTools.Method(typeof(Pawn), "Tick"), transpiler: transpiler);
            harmony.Patch(AccessTools.Method(typeof(Pawn), "TickInterval"), transpiler: transpiler);
        }

        private static void Reset()
        {
            suspendedHits = worldPawnHits = bloodRainSkips = geneSkips = 0;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  Suspended answered: {suspendedHits:N0}, IsWorldPawn answered: {worldPawnHits:N0}, " +
                         $"blood rain checks skipped: {bloodRainSkips:N0}, gene ticks skipped: {geneSkips:N0} (genes skippable: {genesSkippable})";
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase __originalMethod)
        {
            var suspended = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Suspended));
            var isWorldPawn = AccessTools.Method(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn));
            var bloodRain = AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.BloodRainTick));
            var geneTick = AccessTools.Method(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.GeneTrackerTick));
            string Helper(CodeInstruction ins) =>
                ins.Calls(suspended) ? nameof(FastSuspended) :
                ins.Calls(isWorldPawn) ? nameof(FastIsWorldPawn) :
                ins.Calls(bloodRain) ? nameof(FastBloodRainTick) :
                ins.Calls(geneTick) ? nameof(FastGeneTrackerTick) : null;
            var expected = __originalMethod.Name == "Tick" ? 5 : 2;
            return SafeTranspile.Replace(instructions, expected, $"Pawn bookkeeping shortcut (Pawn.{__originalMethod.Name})",
                ins => Helper(ins) != null,
                ins =>
                {
                    var name = Helper(ins);
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(PawnTickBookkeeping), name);
                    return new[] { ins };
                });
        }

        private static bool guardChecked, guardBlocked;

        /// <summary>Stays off if another mod patches the methods whose answers are assumed.</summary>
        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            foreach (var m in new[]
                     {
                         AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Suspended)),
                         AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Suspended)),
                         AccessTools.Method(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn)),
                         AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.Contains)),
                         AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.GetSituation)),
                         AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.BloodRainTick)),
                         AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.ExposedToBloodRain)),
                         AccessTools.Method(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.GeneTrackerTick)),
                         AccessTools.Method(typeof(Gene), nameof(Gene.Tick)),
                         AccessTools.PropertyGetter(typeof(Gene), nameof(Gene.Active)),
                     })
            {
                var owners = m == null ? null : Harmony.GetPatchInfo(m)?.Owners.Where(o => o != ParallelTickMod.Id).ToList();
                if (owners != null && owners.Count > 0)
                {
                    Log.Message($"[Free Performance] Pawn bookkeeping shortcut stays off: {m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", owners)}.");
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        private static bool Use => (Info.Active || Info.Verifying) && !Blocked();

        public static bool FastSuspended(Thing t)
        {
            if (!Use || !(t is Pawn p) || p.GetType() != typeof(Pawn) || !p.Spawned)
                return t.Suspended;
            if (Info.Verifying)
            {
                var vanilla = t.Suspended;
                Info.Stats.Checks++;
                if (vanilla)
                    Info.Stats.Mismatch(() => $"{t}: spawned but Suspended");
                return vanilla;
            }
            suspendedHits++;
            return false;
        }

        public static bool FastIsWorldPawn(Pawn p)
        {
            if (!Use || !p.Spawned)
                return WorldPawnsUtility.IsWorldPawn(p);
            if (Info.Verifying)
            {
                var vanilla = WorldPawnsUtility.IsWorldPawn(p);
                Info.Stats.Checks++;
                if (vanilla)
                    Info.Stats.Mismatch(() => $"{p}: spawned but a world pawn");
                return vanilla;
            }
            worldPawnHits++;
            return false;
        }

        public static void FastBloodRainTick(Pawn p)
        {
            if (!Use || !ModsConfig.AnomalyActive || BloodRainCache.Possible(p.MapHeld))
            {
                BloodRainUtility.BloodRainTick(p);
                return;
            }
            if (Info.Verifying)
            {
                var hediffs = p.health.hediffSet.hediffs.Count;
                Info.Stats.Checks++;
                if (BloodRainUtility.ExposedToBloodRain(p))
                    Info.Stats.Mismatch(() => $"{p}: exposed to blood rain without a blood rain condition");
                BloodRainUtility.BloodRainTick(p);
                if (p.health.hediffSet.hediffs.Count != hediffs)
                    Info.Stats.Mismatch(() => $"{p}: blood rain tick changed hediffs");
                return;
            }
            bloodRainSkips++;
        }

        public static void FastGeneTrackerTick(Pawn_GeneTracker genes)
        {
            if (!Use || !genesSkippable || Info.Verifying)
            {
                genes.GeneTrackerTick();
                return;
            }
            geneSkips++;
        }
    }
}
