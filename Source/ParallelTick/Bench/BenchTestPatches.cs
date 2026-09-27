using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace ParallelTick.Bench
{
    /// <summary>
    /// testpatches=inert|disrupt (bench and play modes only): patches, as another mod would (its own Harmony id), every
    /// method the idle-pawn skip reasons about, to test that each part then runs as vanilla while the rest keeps skipping.
    /// inert: the patches only count their calls (a Pawn.Tick prefix too).
    /// disrupt: the patches on parts that run for real also stagger their pawn now and then, so the skip has to notice
    /// the change and run the rest of the head as vanilla; the Pawn.Tick prefix sometimes skips the whole tick (as mods
    /// that slow pawn ticks do).
    /// </summary>
    public static class BenchTestPatches
    {
        public const string Owner = "bench.testpatches";
        public static long Calls, Staggers, SkippedTicks;
        private static readonly Dictionary<Type, FieldInfo> pawnFields = new Dictionary<Type, FieldInfo>();
        // Each disrupting method staggers a pawn once every 97 ticks, at its own evenly spread phase, so that most
        // staggers land on a pawn that was idle.
        private static readonly Dictionary<IntPtr, int> phases = new Dictionary<IntPtr, int>();

        private static string Mode => BenchConfig.TestPatches.Split(':')[0];

        /// <summary>
        /// Groups of methods, by the part of the skip they belong to. "disrupt" methods run for real when patched and may
        /// stagger their pawn; the others only count.
        /// </summary>
        private static IEnumerable<(string group, MethodBase method, bool disrupt)> Methods()
        {
            yield return ("comps", AccessTools.Method(typeof(ThingWithComps), "Tick"), true);
            yield return ("egglayer", AccessTools.Method(typeof(CompEggLayer), nameof(CompEggLayer.CompTick)), true);
            yield return ("pather", AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick)), true);
            yield return ("pather", AccessTools.Method(typeof(Pawn_PathFollower), "WillCollideWithPawnAt"), false);
            yield return ("pather", AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.ShouldCollideWithPawns)), false);
            yield return ("busy", AccessTools.PropertyGetter(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.FullBodyBusy)), false);
            yield return ("verbs", AccessTools.Method(typeof(VerbTracker), nameof(VerbTracker.VerbsTick)), true);
            yield return ("verbs", AccessTools.Method(typeof(Verb), nameof(Verb.VerbTick)), false);
            yield return ("roping", AccessTools.Method(typeof(Pawn_RopeTracker), nameof(Pawn_RopeTracker.RopingTick)), true);
            yield return ("flight", AccessTools.Method(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.FlightTick)), true);
            yield return ("natives", AccessTools.Method(typeof(Pawn_NativeVerbs), nameof(Pawn_NativeVerbs.NativeVerbsTick)), true);
            yield return ("stances", AccessTools.Method(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerTick)), true);
            yield return ("stances", AccessTools.Method(typeof(Stance), nameof(Stance.StanceTick)), false);
            yield return ("stances", AccessTools.Method(typeof(StunHandler), nameof(StunHandler.StunHandlerTick)), false);
            yield return ("stances", AccessTools.PropertyGetter(typeof(StunHandler), nameof(StunHandler.Hypnotized)), false);
            yield return ("stances", AccessTools.Method(typeof(StaggerHandler), nameof(StaggerHandler.StaggerHandlerTick)), false);
            yield return ("suspended", AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Suspended)), false);
            yield return ("suspended", AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Suspended)), false);
            yield return ("suspended", AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.GetSituation)), false);
            yield return ("worldpawn", AccessTools.Method(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn)), false);
            yield return ("worldpawn", AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.Contains)), false);
            yield return ("hidden", AccessTools.Method(typeof(InvisibilityUtility), nameof(InvisibilityUtility.IsHiddenFromPlayer)), false);
            yield return ("bloodrain", AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.BloodRainTick)), false);
            yield return ("effecters", AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.EffectersTick)), false);
            yield return ("effecters", AccessTools.Method(AccessTools.TypeByName("Verse.PawnStatusEffecters"), "EffectersTick"), false);
            yield return ("health", AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)), false);
            yield return ("tendcomp", AccessTools.Method(typeof(HediffComp_TendDuration), nameof(HediffComp.CompPostTick)), false);
        }

        /// <summary>
        /// Called at startup, before the game loads. testpatches=mode[:group+group...] (default all groups; "tick" is a
        /// Pawn.Tick prefix and postfix, "healthprefix" a HealthTick prefix).
        /// </summary>
        public static void Apply()
        {
            if (BenchConfig.TestPatches == "")
                return;
            var parts = BenchConfig.TestPatches.Split(':');
            var groups = parts.Length > 1 ? new HashSet<string>(parts[1].Split('+')) : null;
            bool Wanted(string g) => groups == null || groups.Contains(g);
            var harmony = new Harmony(Owner);
            var count = 0;
            var disrupting = Methods().Where(m => m.disrupt && Wanted(m.group)).Select(m => m.method).ToList();
            for (var i = 0; i < disrupting.Count; i++)
                phases[disrupting[i].MethodHandle.Value] = i * 97 / disrupting.Count;
            foreach (var (group, method, disrupt) in Methods())
            {
                if (!Wanted(group))
                    continue;
                var patch = disrupt ? nameof(DisruptPostfix) : method.IsStatic ? nameof(StaticPostfix) : nameof(InstancePostfix);
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(BenchTestPatches), patch));
                count++;
            }
            if (Wanted("healthprefix"))
            {
                harmony.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)),
                    prefix: new HarmonyMethod(typeof(BenchTestPatches), nameof(HealthPrefix)));
                count++;
            }
            if (Wanted("tick"))
            {
                harmony.Patch(AccessTools.Method(typeof(Pawn), "Tick"),
                    prefix: new HarmonyMethod(typeof(BenchTestPatches), nameof(TickPrefix)),
                    postfix: new HarmonyMethod(typeof(BenchTestPatches), nameof(InstancePostfix)));
                count++;
            }
            Log.Message($"[Free Performance] Test patches ({BenchConfig.TestPatches}) on {count} methods.");
        }

        public static void InstancePostfix() => Calls++;

        public static void StaticPostfix() => Calls++;

        public static void DisruptPostfix(object __instance, MethodBase __originalMethod)
        {
            Calls++;
            if (Mode != "disrupt")
                return;
            var pawn = PawnOf(__instance);
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.stances?.stagger == null)
                return;
            phases.TryGetValue(__originalMethod.MethodHandle.Value, out var phase);
            if ((Find.TickManager.TicksGame + pawn.thingIDNumber + phase) % 97 != 0)
                return;
            pawn.stances.stagger.StaggerFor(5);
            Staggers++;
        }

        public static bool TickPrefix(Pawn __instance)
        {
            Calls++;
            if (Mode != "disrupt" || (Find.TickManager.TicksGame + __instance.thingIDNumber) % 101 != 0)
                return true;
            SkippedTicks++;
            return false;
        }

        private static readonly AccessTools.FieldRef<Pawn_HealthTracker, Pawn> healthPawn = AccessTools.FieldRefAccess<Pawn_HealthTracker, Pawn>("pawn");

        /// <summary>Another mod's prefix on HealthTick; with disrupt it sometimes skips the health tick.</summary>
        public static bool HealthPrefix(Pawn_HealthTracker __instance)
        {
            Calls++;
            var pawn = healthPawn(__instance);
            if (Mode != "disrupt" || pawn == null || (Find.TickManager.TicksGame + pawn.thingIDNumber) % 89 != 0)
                return true;
            SkippedTicks++;
            return false;
        }

        private static Pawn PawnOf(object o)
        {
            switch (o)
            {
                case Pawn p: return p;
                case ThingComp c: return c.parent as Pawn;
                case VerbTracker t: return t.directOwner as Pawn;
                case null: return null;
            }
            var type = o.GetType();
            if (!pawnFields.TryGetValue(type, out var field))
                pawnFields[type] = field = AccessTools.Field(type, "pawn");
            return field?.GetValue(o) as Pawn;
        }
    }
}
