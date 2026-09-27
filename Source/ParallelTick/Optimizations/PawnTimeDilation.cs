using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Every pawn runs the full Pawn.Tick every tick, but for a pawn standing still (asleep, idle, working at a bench)
    /// most of it is checks that find nothing to do: path follower with no path, idle verbs, idle stun/stagger handlers,
    /// comps with nothing to do, world-pawn lookups, effecter and blood-rain checks. The "interval" work (needs, mind,
    /// think tree, job expiry) is not in Pawn.Tick: Thing.DoTick calls TickInterval separately and it is left alone.
    ///
    /// For such a pawn this runs a copy of vanilla Pawn.Tick in which each sub-system that is provably a no-op in the
    /// pawn's current state is skipped, and everything else is called exactly as vanilla calls it, in the same order:
    /// comps that do something (milk/wool growth), flight, the real job tracker tick (whatever the job does), health,
    /// equipment, abilities, inventory, genes, blood rain and sounds when possible, effecters when something changed.
    /// The no-op conditions are re-checked on every tick before anything runs; if any fails, or on TickRare's 250-tick
    /// hash, vanilla Pawn.Tick runs instead. So every change happens on the same tick as in vanilla.
    ///
    /// Verify mode runs vanilla on the ticks that would have been skipped and checks each skipped call really changed
    /// nothing (its object's fields, the pawn's fields and the random number generator).
    /// </summary>
    public static class PawnTimeDilation
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "dilation",
            Label = "Idle pawns skip empty ticks  (exact)",
            Description = "Pawns that stand still (sleeping, waiting, working at a bench) skip the parts of their per-tick " +
                          "update that have nothing to do. Anything with an effect (the job itself, needs, growth, health, " +
                          "waking up, reacting) still happens on exactly the same tick.",
            Patch = Patch,
            Reset = Reset,
            Prune = Prune,
            ReportLines = Report,
        };

        /// <summary>
        /// How a comp's CompTick is handled on a skipped tick. Foreign: a comp this mod knows nothing about (usually from
        /// another mod, e.g. facial animation); it ticks exactly as in vanilla, and whatever it changes is re-checked.
        /// </summary>
        private enum CompKind : byte { NoOp, Replay, Attach, Explosive, Platform, Overseer, Dormant, Mechanoid, Foreign }

        private sealed class DilState
        {
            public bool Eligible;
            public int Category; // 0 animal, 1 humanlike, 2 mechanoid, 3 other
            public int Hash;
            public int LastOffTick = -1, VerifyTick = -1;

            public Map Map;
            public int Version;

            public List<ThingComp> CompList;
            public int CompCount;
            public ThingComp[] Comps;
            public CompKind[] Kinds;
            public bool CompsOk, HasForeign;
            public int HediffsCheckedVersion = -1;
            public List<Hediff> HediffsCheckedList;
            public bool HediffsOk;
            public List<Verb> Verbs, NativeVerbs;
        }

        private static readonly Dictionary<Pawn, DilState> states = new Dictionary<Pawn, DilState>(RefEq<Pawn>.Instance);
        private static readonly Dictionary<Type, CompKind?> compKinds = new Dictionary<Type, CompKind?>();
        private static readonly Dictionary<string, long> forced = new Dictionary<string, long>();
        private static long offTicks, sleepOffTicks, fullTicks, qualified, movingOffTicks, remainderFallbacks, foreignCompTicks, foreignFallbacks, foreignPawnOffTicks, verifyForeignChanged, foreignPartCalls;
        private static readonly Dictionary<string, long> foreignTypes = new Dictionary<string, long>();
        private static readonly long[] offByCategory = new long[4], fullByCategory = new long[4];
        private static readonly string[] CategoryNames = { "animals", "humanlikes", "mechs", "other" };

        private static int CategoryOf(Pawn p) =>
            p.RaceProps == null ? 3 : p.RaceProps.Animal ? 0 : p.RaceProps.Humanlike ? 1 : p.RaceProps.IsMechanoid ? 2 : 3;

        // Field access
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, bool> moving = AccessTools.FieldRefAccess<Pawn_PathFollower, bool>("moving");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, bool> cachedWillCollideNextCell = AccessTools.FieldRefAccess<Pawn_PathFollower, bool>("cachedWillCollideNextCell");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> lastBlocker = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("lastBlocker");
        private static readonly AccessTools.FieldRef<StunHandler, int> stunTicksLeft = AccessTools.FieldRefAccess<StunHandler, int>("stunTicksLeft");
        private static readonly AccessTools.FieldRef<StunHandler, Effecter> empEffecter = AccessTools.FieldRefAccess<StunHandler, Effecter>("empEffecter");
        private static readonly AccessTools.FieldRef<StunHandler, bool> stunFromEMP = AccessTools.FieldRefAccess<StunHandler, bool>("stunFromEMP");
        private static readonly AccessTools.FieldRef<StunHandler, bool> disableRotation = AccessTools.FieldRefAccess<StunHandler, bool>("disableRotation");
        private static readonly AccessTools.FieldRef<StunHandler, ICollection> adaptationTicksLeft = AccessTools.FieldRefAccess<StunHandler, ICollection>("adaptationTicksLeft");
        private static readonly AccessTools.FieldRef<StaggerHandler, int> staggerTicksLeft = AccessTools.FieldRefAccess<StaggerHandler, int>("staggerTicksLeft");
        private static readonly AccessTools.FieldRef<StaggerHandler, int> staggerEffectTicksLeft = AccessTools.FieldRefAccess<StaggerHandler, int>("staggerEffectTicksLeft");
        private static readonly AccessTools.FieldRef<VerbTracker, List<Verb>> trackerVerbs = AccessTools.FieldRefAccess<VerbTracker, List<Verb>>("verbs");
        private static readonly AccessTools.FieldRef<Verb, ICollection> maintainedEffecters = AccessTools.FieldRefAccess<Verb, ICollection>("maintainedEffecters");
        private static readonly AccessTools.FieldRef<CompExplosive, int> countdownTicksLeft = AccessTools.FieldRefAccess<CompExplosive, int>("countdownTicksLeft");
        private static readonly AccessTools.FieldRef<CompMechanoid, bool> mechActive = AccessTools.FieldRefAccess<CompMechanoid, bool>("active");
        private static readonly AccessTools.FieldRef<GameComponent_Anomaly, IDictionary> hypnotisedPawns = AccessTools.FieldRefAccess<GameComponent_Anomaly, IDictionary>("hypnotisedPawns");
        private static readonly AccessTools.FieldRef<Pawn, Pawn_DrawTracker> drawer = AccessTools.FieldRefAccess<Pawn, Pawn_DrawTracker>("drawer");
        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerAmbient = AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerAmbient");
        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerMoving = AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerMoving");
        private static readonly FieldInfo rendererEffecters = AccessTools.Field(typeof(PawnRenderer), "effecters");
        private static readonly AccessTools.FieldRef<ThingWithComps, List<ThingComp>> thingComps = AccessTools.FieldRefAccess<ThingWithComps, List<ThingComp>>("comps");
        private static readonly FieldInfo effecterPairs = AccessTools.Field(AccessTools.TypeByName("Verse.PawnStatusEffecters"), "pairs");

        // ---- Setup ----

        private static void Patch(Harmony harmony)
        {
            PawnVersions.EnsurePatched(harmony);
            harmony.Patch(AccessTools.Method(typeof(Pawn), "Tick"),
                prefix: new HarmonyMethod(typeof(TickPatch), nameof(TickPatch.Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(TickPatch), nameof(TickPatch.Postfix)) { priority = Priority.Last });
            if (Info.Mode == OptMode.Verify)
                Verifier.Patch(harmony);
        }

        private static void Reset()
        {
            states.Clear();
            forced.Clear();
            blockers.Clear();
            offTicks = sleepOffTicks = fullTicks = qualified = movingOffTicks = remainderFallbacks = foreignCompTicks = foreignFallbacks = foreignPawnOffTicks = verifyForeignChanged = foreignPartCalls = 0;
            foreignTypes.Clear();
            // Which comps tick as vanilla depends on other mods' patches, checked again for each game.
            compKinds.Clear();
            Array.Clear(offByCategory, 0, 4);
            Array.Clear(fullByCategory, 0, 4);
            BloodRainCache.Reset();
            guardChecked = false;
            healthTickForeign = false;
            foreignParts = Part.None;
            Verifier.Reset();
            Info.Stats.Reset();
        }

        private static void Prune(int now)
        {
            foreach (var p in states.Keys.Where(p => p.Destroyed).ToList())
                states.Remove(p);
        }

        private static IEnumerable<string> Report()
        {
            var total = offTicks + fullTicks;
            yield return $"  pawn ticks: {total:N0}, {(Info.Mode == OptMode.Verify ? "would skip" : "skipped")} {offTicks:N0} ({(total == 0 ? 0 : 100.0 * offTicks / total):F1}%), of which asleep {sleepOffTicks:N0}";
            yield return $"  of which moving (real PatherTick): {movingOffTicks:N0}, rest of the head run as vanilla after a real call: {remainderFallbacks:N0}";
            if (foreignParts != Part.None)
                yield return $"  parts other mods patch ({foreignParts}), called for real on skipped ticks: {foreignPartCalls:N0}";
            yield return $"  full-tick qualifications: {qualified:N0}";
            yield return $"  comps from other mods ticked as in vanilla on skipped ticks: {foreignCompTicks:N0}, rest of the tick run as vanilla after them: {foreignFallbacks:N0}";
            yield return $"  {(Info.Mode == OptMode.Verify ? "would-skip" : "skipped")} ticks of pawns with such comps: {foreignPawnOffTicks:N0}" +
                         (Info.Mode == OptMode.Verify ? $", of which a comp changed something the skip relies on (rest verified as vanilla): {verifyForeignChanged:N0}" : "");
            foreach (var kv in foreignTypes.OrderByDescending(kv => kv.Value).Take(10))
                yield return $"  pawns qualified with comp {kv.Key}: {kv.Value:N0}";
            for (var c = 0; c < 4; c++)
                if (offByCategory[c] + fullByCategory[c] > 0)
                    yield return $"  {CategoryNames[c]}: {offByCategory[c]:N0} of {offByCategory[c] + fullByCategory[c]:N0} ticks skipped ({100.0 * offByCategory[c] / (offByCategory[c] + fullByCategory[c]):F1}%)";
            foreach (var kv in forced.OrderByDescending(kv => kv.Value))
                yield return $"  full tick because {kv.Key}: {kv.Value:N0}";
            foreach (var kv in blockers.OrderByDescending(kv => kv.Value).Take(10))
                yield return $"  not eligible because of comp {kv.Key}: {kv.Value:N0} checks";
            if (Bench.BenchConfig.TestPatches != "")
                yield return $"  test patches ({Bench.BenchConfig.TestPatches}): {Bench.BenchTestPatches.Calls:N0} calls, {Bench.BenchTestPatches.Staggers:N0} staggers, {Bench.BenchTestPatches.SkippedTicks:N0} pawn ticks skipped by the test prefix";
            foreach (var line in Verifier.ReportLines())
                yield return line;
        }

        private static readonly Dictionary<string, long> blockers = new Dictionary<string, long>();

        private static void Blocker(string what)
        {
            if (!blockers.TryGetValue(what, out var n))
                Log.Message($"[Free Performance] Idle-pawn tick skipping leaves out pawns with {what} (when it acts can't be told from outside).");
            blockers[what] = n + 1;
        }

        private static void Count(string reason)
        {
            forced.TryGetValue(reason, out var n);
            forced[reason] = n + 1;
        }

        // ---- Other mods' patches: the part they change runs as vanilla, the rest of the skip keeps working ----

        private static bool guardChecked, guardBlocked;

        /// <summary>
        /// HealthTick is skipped for a pawn without hediffs, where vanilla's does nothing. A mod patching it may do
        /// something even then (Blood Animations draws a random number for every pawn), so it is then always called.
        /// </summary>
        private static bool healthTickForeign;

        /// <summary>
        /// Parts of Pawn.Tick that MicroTick skips or makes assumptions about. When another mod patches a method in a
        /// part, that part is called for real on skipped ticks, exactly where vanilla calls it, and what it changed is
        /// re-checked before anything after it is skipped. Comps whose CompTick another mod patches tick for real the
        /// same way (see KindOf).
        /// </summary>
        [Flags]
        private enum Part
        {
            None = 0,
            Comps = 1,       // ThingWithComps.Tick runs for real (every comp ticks)
            Suspended = 2,   // Pawn.Suspended is asked after the comps, as vanilla does
            Pather = 4,
            Verbs = 8,
            Roping = 16,
            Flight = 32,     // always called; what it changed is re-checked
            Natives = 64,
            Stances = 128,
            WorldPawn = 256, // IsWorldPawn is asked where vanilla asks it
            Hidden = 512,
            BloodRain = 1024,
            Effecters = 2048,
        }

        private static Part foreignParts;
        private static bool Has(Part part) => (foreignParts & part) != 0;

        private static readonly (Part part, Type type, string method)[] PartMethods =
        {
            (Part.Comps, typeof(ThingWithComps), "Tick"),
            (Part.Suspended, typeof(Thing), "get_Suspended"), (Part.Suspended, typeof(Pawn), "get_Suspended"),
            (Part.Suspended, typeof(WorldPawns), "GetSituation"),
            (Part.Pather, typeof(Pawn_PathFollower), "PatherTick"), (Part.Pather, typeof(Pawn_PathFollower), "WillCollideWithPawnAt"),
            (Part.Pather, typeof(PawnUtility), "ShouldCollideWithPawns"),
            (Part.Pather | Part.Stances, typeof(Pawn_StanceTracker), "get_FullBodyBusy"),
            (Part.Verbs | Part.Natives, typeof(VerbTracker), "VerbsTick"), (Part.Verbs | Part.Natives, typeof(Verb), "VerbTick"),
            (Part.Roping, typeof(Pawn_RopeTracker), "RopingTick"),
            (Part.Flight, typeof(Pawn_FlightTracker), "FlightTick"),
            (Part.Natives, typeof(Pawn_NativeVerbs), "NativeVerbsTick"),
            (Part.Stances, typeof(Pawn_StanceTracker), "StanceTrackerTick"), (Part.Stances, typeof(Stance), "StanceTick"),
            (Part.Stances, typeof(StunHandler), "StunHandlerTick"), (Part.Stances, typeof(StunHandler), "get_Hypnotized"),
            (Part.Stances, typeof(StaggerHandler), "StaggerHandlerTick"),
            (Part.WorldPawn, typeof(WorldPawnsUtility), "IsWorldPawn"), (Part.WorldPawn, typeof(WorldPawns), "Contains"),
            (Part.Hidden, typeof(InvisibilityUtility), "IsHiddenFromPlayer"),
            (Part.BloodRain, typeof(BloodRainUtility), "BloodRainTick"),
            (Part.Effecters, typeof(PawnRenderer), "EffectersTick"),
        };

        /// <summary>ThingWithComps.Tick called non-virtually (base.Tick() in Pawn.Tick), with other mods' patches.</summary>
        private static Action<ThingWithComps> baseTick;

        /// <summary>
        /// Vehicle Framework's postfix on WorldPawns.GetSituation only changes the answer for world pawns (see
        /// PawnTickBookkeeping), never for the spawned pawns skipped here.
        /// </summary>
        private static bool Accepts(HarmonyLib.Patch patch) => PawnTickBookkeeping.Accepts(patch);

        /// <summary>Checked on first use, so mods that patch after this one are seen too. True when the skip can't be used at all.</summary>
        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            guardBlocked = false;
            foreignParts = Part.None;
            healthTickForeign = false;
            if (Bench.BenchConfig.NoGuards)
                return false;
            try
            {
                // Other mods' patches on Pawn.Tick itself: MicroTick is the last prefix and a copy of the body.
                var problem = PatchGuard.ReplacedBodyProblem(AccessTools.Method(typeof(Pawn), "Tick"),
                    AccessTools.Method(typeof(TickPatch), nameof(TickPatch.Prefix)));
                if (problem != null)
                {
                    Info.LogBlocked(problem);
                    return guardBlocked = true;
                }
                var methods = PartMethods.Select(g => (g.part, method: (MethodBase)AccessTools.Method(g.type, g.method))).ToList();
                methods.Add((Part.Effecters, AccessTools.Method(AccessTools.TypeByName("Verse.PawnStatusEffecters"), "EffectersTick")));
                foreach (var (part, method) in methods)
                {
                    var owners = PatchGuard.ForeignOwners(method, accepts: Accepts);
                    if (owners.Count == 0)
                        continue;
                    foreignParts |= part;
                    Info.LogPartlyVanilla($"{method.DeclaringType?.Name}.{method.Name} runs as vanilla (patched by {string.Join(", ", owners)})");
                }
                if (Has(Part.Comps))
                    baseTick = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Tick"), null, false, null);
                var healthOwners = PatchGuard.ForeignOwners(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)));
                if (healthOwners.Count > 0)
                {
                    healthTickForeign = true;
                    Info.LogPartlyVanilla($"Pawn_HealthTracker.HealthTick runs for every pawn (patched by {string.Join(", ", healthOwners)})");
                }
            }
            catch (Exception e)
            {
                Info.LogBlocked($"could not check other mods' patches ({e.GetType().Name}: {e.Message})");
                guardBlocked = true;
            }
            return guardBlocked;
        }

        // ---- Per-tick decision ----

        public static class TickPatch
        {
            /// <summary>The last prefix: other mods' prefixes on Pawn.Tick run first, as before vanilla's body (see PatchGuard.ReplacedBodyProblem).</summary>
            [HarmonyPriority(Priority.Last)]
            public static bool Prefix(Pawn __instance)
            {
                if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread)
                    return true;
                if (!states.TryGetValue(__instance, out var s) || !s.Eligible || Blocked())
                    return true;
                var now = Find.TickManager.TicksGame;
                if (!CanSkip(__instance, s, now))
                    return true;
                offByCategory[s.Category]++;
                offTicks++;
                if (s.HasForeign)
                    foreignPawnOffTicks++;
                if (__instance.jobs?.curDriver?.asleep == true)
                    sleepOffTicks++;
                if (Info.Verifying)
                {
                    // Vanilla runs; the Verifier checks the calls MicroTick would have skipped.
                    s.VerifyTick = now;
                    Verifier.Begin(__instance);
                    return true;
                }
                s.LastOffTick = now;
                Info.Stats.Hits++;
                MicroTick(__instance, s);
                return false;
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix(Pawn __instance)
            {
                if (!UnityData.IsInMainThread)
                    return;
                if (!Info.Active && !Info.Verifying)
                {
                    // Switched off at runtime: nothing is requalified meanwhile, so drop eligibility.
                    if (states.Count > 0 && states.TryGetValue(__instance, out var stale))
                        stale.Eligible = false;
                    return;
                }
                var now = Find.TickManager.TicksGame;
                states.TryGetValue(__instance, out var s);
                if (s != null && s.VerifyTick == now)
                    Verifier.End(__instance);
                else if (s != null && s.LastOffTick == now)
                    return;
                else
                    fullTicks++;
                if (s == null)
                    states[__instance] = s = new DilState { Hash = Gen.HashOffset(__instance), Category = CategoryOf(__instance) };
                if (s.VerifyTick != now)
                    fullByCategory[s.Category]++;
                Qualify(__instance, s);
            }
        }

        /// <summary>True when every sub-system MicroTick skips is a no-op this tick. Changes nothing.</summary>
        private static bool CanSkip(Pawn pawn, DilState s, int now)
        {
            // TickRare runs inside Pawn.Tick on the pawn's 250-tick hash (and comps act on 250/2500-tick hashes with
            // the same offset): those ticks run vanilla.
            if (unchecked(now + s.Hash) % 250 == 0)
            {
                Count("TickRare tick");
                return false;
            }
            if (!StillIdle(pawn, s))
            {
                Count("state changed");
                s.Eligible = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The no-op conditions of every skipped sub-system, re-checked each tick (cheap field reads). The map and the
        /// hediff/apparel version were taken when the pawn qualified.
        /// </summary>
        private static bool StillIdle(Pawn pawn, DilState s)
        {
            if (DebugSettings.noAnimals || !pawn.Spawned || pawn.Map != s.Map || pawn.Downed || pawn.mutant != null)
                return false;
            if (PawnVersions.Get(pawn) != s.Version)
                return false;

            var mind = pawn.mindState;
            if (mind == null || mind.anyCloseHostilesRecently || mind.mentalStateHandler.CurState != null)
                return false;

            if (!RemainderIdle(pawn, s))
                return false;

            // ThingWithComps.Tick: comps whose CompTick is skipped must be idle.
            return CompsIdle(pawn, s);
        }

        /// <summary>
        /// Pawn_PathFollower.PatherTick is a no-op when not moving and without a path: no pawn collisions
        /// (ShouldCollideWithPawns needs close hostiles, checked, or a shambler, not eligible), so it only rewrites fields
        /// that already hold these values. Otherwise the real PatherTick runs.
        /// </summary>
        private static bool PatherIdle(Pawn_PathFollower pather) =>
            !moving(pather) && pather.curPath == null && !cachedWillCollideNextCell(pather) && lastBlocker(pather) == null;

        /// <summary>No-op conditions of the sub-systems vanilla runs after PatherTick: verbs, roping, natives, stances.</summary>
        private static bool RemainderIdle(Pawn pawn, DilState s)
        {
            if (!pawn.Spawned)
                return false;
            // Pawn_StanceTracker.StanceTrackerTick: stun/stagger counters idle, not hypnotized, plain mobile stance.
            var stances = pawn.stances;
            var stun = stances.stunner;
            if (stunTicksLeft(stun) > 0 || empEffecter(stun) != null || stunFromEMP(stun) || disableRotation(stun) ||
                adaptationTicksLeft(stun)?.Count > 0)
                return false;
            if (ModsConfig.AnomalyActive && Find.Anomaly != null)
            {
                var hypnotised = hypnotisedPawns(Find.Anomaly);
                if (hypnotised != null && hypnotised.Count > 0 && hypnotised.Contains(pawn))
                    return false;
            }
            var stagger = stances.stagger;
            if (staggerTicksLeft(stagger) > 0 || staggerEffectTicksLeft(stagger) > 0)
                return false;
            if (stances.curStance == null || stances.curStance.GetType() != typeof(Stance_Mobile))
                return false;

            // VerbTracker.VerbsTick (own and native verbs): Verb.VerbTick only acts while bursting or maintaining effecters.
            if (pawn.verbTracker != null && trackerVerbs(pawn.verbTracker) != s.Verbs ||
                pawn.natives?.verbTracker != null && trackerVerbs(pawn.natives.verbTracker) != s.NativeVerbs)
                return false;
            if (!VerbsIdle(s.Verbs) || !VerbsIdle(s.NativeVerbs))
                return false;

            // Pawn_RopeTracker.RopingTick returns at once without ropes; FlightTick is replayed on the grounded path only.
            if (pawn.roping != null && pawn.roping.HasAnyRope)
                return false;
            if (pawn.flight != null && pawn.flight.Flying)
                return false;
            return true;
        }

        private static bool CompsIdle(Pawn pawn, DilState s)
        {
            if (thingComps(pawn) != s.CompList || s.CompList.Count != s.CompCount)
                return false;
            var comps = s.Comps;
            for (var i = 0; i < comps.Length; i++)
                if (!CompIdle(comps[i], s.Kinds[i]))
                    return false;
            return true;
        }

        /// <summary>True when the comp's CompTick does nothing right now. NoOp comps never act on a skipped tick; Replay and Foreign comps tick anyway.</summary>
        private static bool CompIdle(ThingComp comp, CompKind kind)
        {
            switch (kind)
            {
                case CompKind.Attach:
                    return !(((CompAttachBase)comp).attachments?.Count > 0);
                case CompKind.Explosive:
                    var explosive = (CompExplosive)comp;
                    return !explosive.wickStarted && countdownTicksLeft(explosive) <= 0;
                case CompKind.Platform:
                    var platform = (CompHoldingPlatformTarget)comp;
                    return platform.targetHolder == null && !platform.isEscaping;
                case CompKind.Overseer:
                    return OverseerStateCache.TickIsNoOp((CompOverseerSubject)comp);
                // CompCanBeDormant.CompTick acts only while a wake-up is scheduled (and on the 250-tick hash);
                // CompMechanoid adds "go dormant if deactivated while active".
                case CompKind.Dormant:
                    return ((CompCanBeDormant)comp).wakeUpOnTick == int.MinValue;
                case CompKind.Mechanoid:
                    var mech = (CompMechanoid)comp;
                    return mech.wakeUpOnTick == int.MinValue && !(mechActive(mech) && mech.Deactivated);
                default:
                    return true;
            }
        }

        /// <summary>
        /// After comps from other mods ticked: everything the rest of the skip relies on still holds (what StillIdle checks,
        /// apart from the comps themselves, which have ticked by now).
        /// </summary>
        private static bool IdleAfterComps(Pawn pawn, DilState s)
        {
            if (pawn.Dead || !pawn.Spawned || pawn.Map != s.Map || pawn.Downed || pawn.mutant != null || PawnVersions.Get(pawn) != s.Version)
                return false;
            var mind = pawn.mindState;
            if (mind == null || mind.anyCloseHostilesRecently || mind.mentalStateHandler.CurState != null)
                return false;
            return thingComps(pawn) == s.CompList && s.CompList.Count == s.CompCount && RemainderIdle(pawn, s);
        }

        private static bool VerbsIdle(List<Verb> verbs)
        {
            if (verbs == null)
                return true;
            for (var i = 0; i < verbs.Count; i++)
            {
                var v = verbs[i];
                if (v.state == VerbState.Bursting || maintainedEffecters(v)?.Count > 0)
                    return false;
            }
            return true;
        }

        // ---- The copy of Pawn.Tick for an eligible, spawned, non-suspended pawn ----

        private static void MicroTick(Pawn pawn, DilState s)
        {
            var version = PawnVersions.Get(pawn);
            if (!TickComps(pawn, s))
            {
                foreignFallbacks++;
                return;
            }
            // TickRare: not a 250-tick. Suspended: false for a spawned pawn (checked when qualifying); asked as vanilla
            // does when another mod patches it.
            if (Has(Part.Suspended) && pawn.Suspended)
            {
                foreignFallbacks++;
                VanillaAfterRare(pawn, true);
                return;
            }
            Head(pawn, s);
            Tail(pawn, version);
        }

        /// <summary>
        /// ThingWithComps.Tick: comps in order, with the count read once as vanilla does. Idle ones are skipped (checked in
        /// StillIdle). Comps from other mods (or whose CompTick another mod patches) tick exactly as in vanilla; they may
        /// change anything, so each comp after one is re-checked where it stands, and so is everything else before the
        /// rest of the tick is skipped. When another mod patches ThingWithComps.Tick itself, it runs for real. Returns
        /// false when the rest of Pawn.Tick had to run as vanilla (it has then run).
        /// </summary>
        private static bool TickComps(Pawn pawn, DilState s)
        {
            var list = s.CompList;
            if (Has(Part.Comps))
            {
                var before = ListVersion<ThingComp>.Of(list);
                foreignCompTicks++;
                baseTick(pawn);
                if (thingComps(pawn) == list && ListVersion<ThingComp>.Of(list) == before && IdleAfterComps(pawn, s))
                    return true;
                VanillaAfterComps(pawn);
                return false;
            }
            var comps = s.Comps;
            var listVersion = s.HasForeign ? ListVersion<ThingComp>.Of(list) : 0;
            var ranForeign = false;
            for (var i = 0; i < comps.Length; i++)
            {
                if (ranForeign && (thingComps(pawn) != list || ListVersion<ThingComp>.Of(list) != listVersion))
                {
                    // A comp changed the comp list: vanilla keeps indexing the live list up to the count it read at the
                    // start; the rest of Pawn.Tick then runs exactly as vanilla.
                    for (var j = i; j < comps.Length; j++)
                        thingComps(pawn)[j].CompTick();
                    VanillaAfterComps(pawn);
                    return false;
                }
                var kind = s.Kinds[i];
                if (kind == CompKind.Replay)
                    comps[i].CompTick();
                else if (kind == CompKind.Foreign)
                {
                    foreignCompTicks++;
                    ranForeign = true;
                    comps[i].CompTick();
                }
                else if (ranForeign && !CompIdle(comps[i], kind))
                    comps[i].CompTick();
            }
            if (!ranForeign || thingComps(pawn) == list && ListVersion<ThingComp>.Of(list) == listVersion && IdleAfterComps(pawn, s))
                return true;
            VanillaAfterComps(pawn);
            return false;
        }

        // Pawn.Tick's head, in vanilla's order.
        private const int StepPather = 0, StepVerbs = 1, StepRoping = 2, StepFlight = 3, StepNatives = 4, StepStances = 5, StepJobs = 6;

        /// <summary>
        /// Pawn.Tick from PatherTick to JobTrackerTick (IL 0045 to 00CC) for a spawned pawn that is not suspended. Idle
        /// parts are skipped (PatherIdle, RemainderIdle). A moving pawn's path follower, and a part another mod patches,
        /// run for real; what they changed is re-checked before the next part (dirty), and if the rest is no longer idle
        /// the head runs exactly as vanilla from there. FlightTick and the job tick always run (FlightTick of a grounded
        /// pawn changes nothing the rest relies on; verify mode checks the parts after it).
        /// </summary>
        private static void Head(Pawn pawn, DilState s)
        {
            var dirty = false;
            if (Has(Part.Pather) || !PatherIdle(pawn.pather))
            {
                if (PatherIdle(pawn.pather))
                    foreignPartCalls++;
                else
                    movingOffTicks++;
                pawn.pather.PatherTick();
                // Moving can change what the next systems see (a trap staggers the pawn, it dies, a door...).
                dirty = true;
            }
            if (Rest(pawn, s, ref dirty, StepVerbs))
                return;
            if (Has(Part.Verbs))
            {
                foreignPartCalls++;
                pawn.verbTracker.VerbsTick();
                dirty = true;
            }
            // Vanilla checks Spawned once for roping, flight and natives; it holds here (RemainderIdle).
            if (Rest(pawn, s, ref dirty, StepRoping))
                return;
            if (Has(Part.Roping))
            {
                foreignPartCalls++;
                pawn.roping?.RopingTick();
                dirty = true;
            }
            if (Rest(pawn, s, ref dirty, StepFlight))
                return;
            pawn.flight?.FlightTick();
            dirty |= Has(Part.Flight);
            if (Rest(pawn, s, ref dirty, StepNatives))
                return;
            if (Has(Part.Natives))
            {
                foreignPartCalls++;
                pawn.natives.NativeVerbsTick();
                dirty = true;
            }
            if (Rest(pawn, s, ref dirty, StepStances))
                return;
            if (Has(Part.Stances))
            {
                foreignPartCalls++;
                pawn.stances.StanceTrackerTick();
                dirty = true;
            }
            if (Rest(pawn, s, ref dirty, StepJobs))
                return;
            // IsWorldPawn: false for a spawned pawn; asked as vanilla does when another mod patches it.
            if (!Has(Part.WorldPawn) || !WorldPawnsUtility.IsWorldPawn(pawn))
                pawn.jobs?.JobTrackerTick();
        }

        /// <summary>
        /// If a part ran for real since the last check (dirty): when what the rest of the head's skip relies on no longer
        /// holds, runs the head from the given step exactly as vanilla and returns true.
        /// </summary>
        private static bool Rest(Pawn pawn, DilState s, ref bool dirty, int from)
        {
            if (!dirty)
                return false;
            dirty = false;
            if (RemainderIdle(pawn, s))
                return false;
            remainderFallbacks++;
            VanillaHead(pawn, from);
            return true;
        }

        /// <summary>
        /// Pawn.Tick's head from the given step to JobTrackerTick, exactly as vanilla, for a pawn that was not suspended.
        /// Vanilla checks Spawned once before roping, flight and natives: callers starting at flight or natives got there
        /// with the pawn spawned at that point, so those run without checking again.
        /// </summary>
        private static void VanillaHead(Pawn pawn, int from)
        {
            if (from <= StepPather && pawn.Spawned)
                pawn.pather.PatherTick();
            if (from <= StepVerbs && pawn.Spawned)
                pawn.verbTracker.VerbsTick();
            if (from <= StepRoping ? pawn.Spawned : from <= StepNatives)
            {
                if (from <= StepRoping)
                    pawn.roping?.RopingTick();
                if (from <= StepFlight)
                    pawn.flight?.FlightTick();
                pawn.natives.NativeVerbsTick();
            }
            if (from <= StepStances && pawn.Spawned)
                pawn.stances.StanceTrackerTick();
            if (!WorldPawnsUtility.IsWorldPawn(pawn))
                pawn.jobs?.JobTrackerTick();
        }

        /// <summary>
        /// Pawn.Tick after ThingWithComps.Tick (IL 0025 to the end), exactly as vanilla: used when a comp from another
        /// mod changed something the skip relies on.
        /// </summary>
        private static void VanillaAfterComps(Pawn pawn)
        {
            if (pawn.IsHashIntervalTick(250))
                pawn.TickRare();
            VanillaAfterRare(pawn, pawn.Suspended);
        }

        /// <summary>Pawn.Tick after reading Suspended (IL 003F to the end), exactly as vanilla.</summary>
        private static void VanillaAfterRare(Pawn pawn, bool suspended)
        {
            if (!suspended)
            {
                VanillaHead(pawn, StepPather);
                pawn.health.HealthTick();
                if (pawn.Spawned && InvisibilityUtility.IsHiddenFromPlayer(pawn) && Find.Selector.IsSelected(pawn))
                    Find.Selector.Deselect(pawn);
                pawn.equipment?.EquipmentTrackerTick();
                pawn.abilities?.AbilitiesTick();
                pawn.inventory?.InventoryTrackerTick();
                pawn.genes?.GeneTrackerTick();
                if (ModsConfig.AnomalyActive && pawn.Spawned)
                {
                    pawn.mutant?.MutantTrackerTick();
                    BloodRainUtility.BloodRainTick(pawn);
                }
            }
            if (pawn.Spawned)
                Sounds(pawn);
            drawer(pawn)?.renderer.EffectersTick(suspended || WorldPawnsUtility.IsWorldPawn(pawn));
        }

        /// <summary>Pawn.Tick from HealthTick (IL 00CC) to the end, for a pawn that was not suspended.</summary>
        private static void Tail(Pawn pawn, int version)
        {
            // The rest of Pawn.Tick, as vanilla, with known no-ops skipped. The job or health tick may have changed the
            // pawn (killed it, started a mental state, added a hediff, started moving); the checks below follow that.
            // Parts another mod patches are always called.
            if (healthTickForeign || pawn.health.hediffSet.hediffs.Count > 0)
                pawn.health.HealthTick();
            var changed = Changed(pawn, version);
            // IsHiddenFromPlayer is false without an invisibility hediff (none when qualified; hediff changes bump the version).
            if ((changed || Has(Part.Hidden)) && pawn.Spawned && InvisibilityUtility.IsHiddenFromPlayer(pawn) && Find.Selector.IsSelected(pawn))
                Find.Selector.Deselect(pawn);
            pawn.equipment?.EquipmentTrackerTick();
            pawn.abilities?.AbilitiesTick();
            pawn.inventory?.InventoryTrackerTick();
            pawn.genes?.GeneTrackerTick();
            if (ModsConfig.AnomalyActive && pawn.Spawned)
            {
                pawn.mutant?.MutantTrackerTick();
                if (changed || Has(Part.BloodRain) || BloodRainCache.Possible(pawn.Map))
                    BloodRainUtility.BloodRainTick(pawn);
            }
            if (pawn.Spawned && (pawn.RaceProps.soundAmbience != null || pawn.pather.Moving))
                Sounds(pawn);
            // PawnStatusEffecters.EffectersTick has nothing to do without effecter hediffs, a mental state or live
            // effecters (none when qualified), for a pawn that is not a world pawn.
            var draw = drawer(pawn);
            if (draw == null)
                return;
            if (Has(Part.WorldPawn))
            {
                var world = WorldPawnsUtility.IsWorldPawn(pawn);
                if (world || changed || Has(Part.Effecters) || Changed(pawn, version))
                    draw.renderer.EffectersTick(world);
            }
            else if (changed || Has(Part.Effecters) || Changed(pawn, version))
                draw.renderer.EffectersTick(WorldPawnsUtility.IsWorldPawn(pawn));
        }

        private static bool Changed(Pawn pawn, int version) =>
            PawnVersions.Get(pawn) != version || pawn.mindState?.mentalStateHandler.CurState != null || !pawn.Spawned || pawn.Dead;

        /// <summary>Pawn.Tick's ambient and movement sound block.</summary>
        private static void Sounds(Pawn pawn)
        {
            if (pawn.Position.Fogged(pawn.Map))
                return;
            var props = pawn.RaceProps;
            if (props.soundAmbience != null)
            {
                ref var ambient = ref sustainerAmbient(pawn);
                if (ambient == null || ambient.Ended)
                    ambient = props.soundAmbience.TrySpawnSustainer(SoundInfo.InMap(pawn, MaintenanceType.PerTick));
                ambient?.Maintain();
            }
            if (pawn.pather != null && pawn.pather.Moving && props.soundMoving != null)
            {
                ref var movingSound = ref sustainerMoving(pawn);
                if (movingSound == null || movingSound.Ended)
                    movingSound = props.soundMoving.TrySpawnSustainer(SoundInfo.InMap(pawn, MaintenanceType.PerTick));
                movingSound?.Maintain();
            }
        }

        // ---- Qualification after a full tick ----

        private static void Qualify(Pawn pawn, DilState s)
        {
            s.Eligible = false;
            // Cheap, common rejections first.
            if (!pawn.Spawned || pawn.pather == null || pawn.GetType() != typeof(Pawn) ||
                pawn.jobs == null || pawn.jobs.GetType() != typeof(Pawn_JobTracker) || pawn.RaceProps == null ||
                pawn.RaceProps.soundAmbience != null || pawn.Dead || pawn.Downed || pawn.mutant != null || pawn.stances == null)
                return;
            // Other mods' patches decide which parts run as vanilla (and the comp kinds below).
            if (Blocked())
                return;

            // Comp classification, cached per comp list (comps are only added when a thing is made).
            var comps = thingComps(pawn);
            if (comps == null)
                return;
            if (s.CompList != comps || s.CompCount != comps.Count)
            {
                s.CompList = comps;
                s.CompCount = comps.Count;
                s.Comps = comps.ToArray();
                s.Kinds = new CompKind[comps.Count];
                s.CompsOk = true;
                for (var i = 0; i < comps.Count && s.CompsOk; i++)
                {
                    var kind = KindOf(comps[i].GetType());
                    if (kind == null || comps[i] is CompActivity)
                    {
                        Blocker(comps[i].GetType().Name);
                        s.CompsOk = false;
                    }
                    else
                        s.Kinds[i] = kind.Value;
                }
                s.HasForeign = s.CompsOk && s.Kinds.Contains(CompKind.Foreign);
            }
            if (!s.CompsOk)
                return;

            // No effecter or invisibility hediffs (their per-tick effects are skipped when nothing changed); cached per
            // hediff version.
            var version = PawnVersions.Get(pawn);
            if (s.HediffsCheckedVersion != version || s.HediffsCheckedList != pawn.health.hediffSet.hediffs)
            {
                s.HediffsCheckedVersion = version;
                s.HediffsCheckedList = pawn.health.hediffSet.hediffs;
                s.HediffsOk = true;
                foreach (var h in pawn.health.hediffSet.hediffs)
                    if (h is HediffWithComps hc && hc.comps.Any(c => c is HediffComp_Effecter || c is HediffComp_Invisibility))
                        s.HediffsOk = false;
            }
            if (!s.HediffsOk)
                return;

            s.Map = pawn.Map;
            s.Version = version;
            s.Verbs = pawn.verbTracker == null ? null : trackerVerbs(pawn.verbTracker);
            s.NativeVerbs = pawn.natives?.verbTracker == null ? null : trackerVerbs(pawn.natives.verbTracker);

            // The per-tick checks must hold now as well; then the costlier one-time facts.
            if (!StillIdle(pawn, s))
                return;
            // Suspended and IsWorldPawn are asked on every skipped tick instead when another mod patches them.
            if (drawer(pawn)?.renderer == null || !Has(Part.Suspended) && pawn.Suspended ||
                !Has(Part.WorldPawn) && WorldPawnsUtility.IsWorldPawn(pawn) || EffecterPairs(pawn) > 0)
                return;
            s.Eligible = true;
            qualified++;
            if (s.HasForeign)
                for (var i = 0; i < s.Kinds.Length; i++)
                    if (s.Kinds[i] == CompKind.Foreign)
                    {
                        var name = s.Comps[i].GetType().Name;
                        foreignTypes.TryGetValue(name, out var n);
                        foreignTypes[name] = n + 1;
                    }
        }

        private static int EffecterPairs(Pawn pawn)
        {
            var effecters = rendererEffecters.GetValue(drawer(pawn).renderer);
            return (effecterPairs.GetValue(effecters) as ICollection)?.Count ?? 0;
        }

        /// <summary>How a comp type's CompTick is handled on a skipped tick (unknown types tick as in vanilla: Foreign).</summary>
        private static CompKind? KindOf(Type type)
        {
            if (compKinds.TryGetValue(type, out var kind))
                return kind;
            var declaring = AccessTools.Method(type, nameof(ThingComp.CompTick))?.DeclaringType;
            if (CompTickPatched(type))
                kind = CompKind.Foreign;
            else if (declaring == typeof(ThingComp))
                kind = CompKind.NoOp;
            else if (declaring == typeof(CompEggLayer))
                kind = CompKind.NoOp; // acts only on IsHashIntervalTick(parent, 2500)
            else if (declaring == typeof(CompHasGatherableBodyResource))
                kind = CompKind.Replay;
            else if (declaring == typeof(CompAttachBase))
                kind = CompKind.Attach;
            else if (declaring == typeof(CompExplosive))
                kind = CompKind.Explosive;
            else if (declaring == typeof(CompHoldingPlatformTarget))
                kind = CompKind.Platform;
            else if (declaring == typeof(CompOverseerSubject))
                kind = CompKind.Overseer;
            else if (declaring == typeof(CompWakeUpDormant))
                kind = CompKind.NoOp; // acts only on IsHashIntervalTick(parent, 250)
            else if (declaring == typeof(CompCanBeDormant))
                kind = CompKind.Dormant;
            else if (declaring == typeof(CompMechanoid))
                kind = CompKind.Mechanoid;
            else
                kind = CompKind.Foreign;
            compKinds[type] = kind;
            return kind;
        }

        /// <summary>
        /// True when another mod patches a CompTick this type runs (its own or a base class's, which it may call): its
        /// comps then tick as vanilla, like comps from other mods.
        /// </summary>
        private static bool CompTickPatched(Type type)
        {
            if (Bench.BenchConfig.NoGuards)
                return false;
            var patched = PatchGuard.PatchedInHierarchy(type, nameof(ThingComp.CompTick), Accepts);
            foreach (var line in patched)
                Info.LogPartlyVanilla(line);
            return patched.Count > 0;
        }

        // ---- Verify mode ----

        /// <summary>
        /// On ticks that would have been skipped, vanilla runs and every call the skip would have left out is checked to
        /// be a no-op: the fields of its object and of the pawn, and the random number generator, are unchanged.
        /// </summary>
        public static class Verifier
        {
            private static Pawn current;
            private static DilState currentState;
            private static bool patherPredictedIdle;
            private static readonly Dictionary<string, long> checkedCalls = new Dictionary<string, long>();
            private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
            private static readonly FieldInfo randSeed = AccessTools.Field(typeof(Rand), "seed");
            private static readonly FieldInfo randIterations = AccessTools.Field(typeof(Rand), "iterations");
            private static int versionAtBegin;
            // MicroTick's decisions, taken at the same points of the vanilla tick. restSkippable: false once the rest of
            // Pawn.Tick runs as vanilla (a comp from another mod changed something the skip relies on, or the pawn is
            // suspended); nothing after is checked. headSkippable: false once the head runs as vanilla from some step
            // (after a part ran for real, the rest was no longer idle); the tail is still checked. dirty: a part of the
            // head ran for real since MicroTick's last re-check.
            private static bool restSkippable, headSkippable, compsDone, dirty, suspendedSeen;
            private static int compListVersion, lastStep;

            public static void Reset()
            {
                current = null;
                checkedCalls.Clear();
            }

            public static IEnumerable<string> ReportLines()
            {
                foreach (var kv in checkedCalls.OrderBy(kv => kv.Key))
                    yield return $"  verified no-op: {kv.Key} x{kv.Value:N0}";
            }

            public static void Begin(Pawn pawn)
            {
                current = pawn;
                currentState = states[pawn];
                patherPredictedIdle = PatherIdle(pawn.pather);
                versionAtBegin = PawnVersions.Get(pawn);
                restSkippable = headSkippable = true;
                compsDone = dirty = suspendedSeen = false;
                lastStep = -1;
                compListVersion = ListVersion<ThingComp>.Of(currentState.CompList);
            }

            public static void End(Pawn pawn)
            {
                current = null;
                Info.Stats.Checks++;
            }

            public static void Patch(Harmony harmony)
            {
                var check = new HarmonyMethod(typeof(NoOpCheck), nameof(NoOpCheck.Prefix));
                var after = new HarmonyMethod(typeof(NoOpCheck), nameof(NoOpCheck.Postfix));
                foreach (var m in new[]
                         {
                             AccessTools.Method(typeof(CompEggLayer), nameof(CompEggLayer.CompTick)),
                             AccessTools.Method(typeof(CompAttachBase), nameof(CompAttachBase.CompTick)),
                             AccessTools.Method(typeof(CompExplosive), nameof(CompExplosive.CompTick)),
                             AccessTools.Method(typeof(CompHoldingPlatformTarget), nameof(CompHoldingPlatformTarget.CompTick)),
                             AccessTools.Method(typeof(CompOverseerSubject), nameof(CompOverseerSubject.CompTick)),
                             AccessTools.Method(typeof(CompWakeUpDormant), nameof(CompWakeUpDormant.CompTick)),
                             AccessTools.Method(typeof(CompCanBeDormant), nameof(CompCanBeDormant.CompTick)),
                             AccessTools.Method(typeof(CompMechanoid), nameof(CompMechanoid.CompTick)),
                         })
                    harmony.Patch(m, prefix: check, postfix: after);
                var head = new HarmonyMethod(typeof(HeadCheck), nameof(HeadCheck.Prefix));
                var headAfter = new HarmonyMethod(typeof(HeadCheck), nameof(HeadCheck.Postfix));
                foreach (var m in new[]
                         {
                             AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick)),
                             AccessTools.Method(typeof(VerbTracker), nameof(VerbTracker.VerbsTick)),
                             AccessTools.Method(typeof(Pawn_RopeTracker), nameof(Pawn_RopeTracker.RopingTick)),
                             AccessTools.Method(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.FlightTick)),
                             AccessTools.Method(typeof(Pawn_NativeVerbs), nameof(Pawn_NativeVerbs.NativeVerbsTick)),
                             AccessTools.Method(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerTick)),
                         })
                    harmony.Patch(m, prefix: head, postfix: headAfter);
                harmony.Patch(AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.EffectersTick)),
                    prefix: new HarmonyMethod(typeof(EffectersCheck), nameof(EffectersCheck.Prefix)),
                    postfix: new HarmonyMethod(typeof(EffectersCheck), nameof(EffectersCheck.Postfix)));
                harmony.Patch(AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.BloodRainTick)),
                    prefix: new HarmonyMethod(typeof(BloodRainCheck), nameof(BloodRainCheck.Prefix)),
                    postfix: new HarmonyMethod(typeof(BloodRainCheck), nameof(BloodRainCheck.Postfix)));
                harmony.Patch(AccessTools.Method(typeof(InvisibilityUtility), nameof(InvisibilityUtility.IsHiddenFromPlayer)),
                    postfix: new HarmonyMethod(typeof(Verifier), nameof(HiddenPostfix)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Suspended)),
                    postfix: new HarmonyMethod(typeof(Verifier), nameof(SuspendedPostfix)));
                harmony.Patch(AccessTools.Method(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn)),
                    postfix: new HarmonyMethod(typeof(Verifier), nameof(WorldPawnPostfix)));
                harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.TickRare)),
                    prefix: new HarmonyMethod(typeof(Verifier), nameof(TickRarePrefix)));
                harmony.Patch(AccessTools.Method(typeof(ThingWithComps), "Tick"),
                    postfix: new HarmonyMethod(typeof(Verifier), nameof(CompsDonePostfix)));
            }

            private static void Note(string what)
            {
                checkedCalls.TryGetValue(what, out var n);
                checkedCalls[what] = n + 1;
            }

            private static FieldInfo[] FieldsOf(Type t)
            {
                if (!fieldCache.TryGetValue(t, out var fields))
                    fieldCache[t] = fields = AccessTools.GetDeclaredFields(t).Concat(t.BaseType == null || t.BaseType == typeof(object)
                            ? Enumerable.Empty<FieldInfo>() : FieldsOf(t.BaseType))
                        .Where(f => !f.IsStatic).ToArray();
                return fields;
            }

            public sealed class Snapshot
            {
                public object[] Objects;
                public List<object>[] Values;
                public object Seed, Iterations;
            }

            private static List<object> Values(object o)
            {
                var list = new List<object>();
                if (o == null)
                    return list;
                foreach (var f in FieldsOf(o.GetType()))
                {
                    var v = f.GetValue(o);
                    list.Add(v);
                    if (v is ICollection c)
                        list.Add(c.Count);
                }
                return list;
            }

            public static Snapshot Take(params object[] objects) => new Snapshot
            {
                Objects = objects,
                Values = objects.Select(Values).ToArray(),
                Seed = randSeed.GetValue(null),
                Iterations = randIterations.GetValue(null),
            };

            public static void Compare(Snapshot before, string what)
            {
                Note(what);
                if (!Equals(randSeed.GetValue(null), before.Seed) || !Equals(randIterations.GetValue(null), before.Iterations))
                    Info.Stats.Mismatch(() => $"{what} on {current}: used the random number generator");
                for (var i = 0; i < before.Objects.Length; i++)
                {
                    var now = Values(before.Objects[i]);
                    var was = before.Values[i];
                    for (var j = 0; j < Math.Min(now.Count, was.Count); j++)
                    {
                        var a = was[j];
                        var b = now[j];
                        var same = a == null || a.GetType().IsValueType || a is string ? Equals(a, b) : ReferenceEquals(a, b);
                        if (!same)
                        {
                            var obj = before.Objects[i];
                            var index = j;
                            Info.Stats.Mismatch(() => $"{what} on {current}: {obj.GetType().Name} value #{index} changed {a} -> {b}");
                            break;
                        }
                    }
                }
            }

            /// <summary>Comps MicroTick skips: checked as no-ops where vanilla ticks them.</summary>
            public static class NoOpCheck
            {
                public static void Prefix(ThingComp __instance, out Snapshot __state)
                {
                    __state = null;
                    if (current == null || compsDone || __instance.parent != current || Has(Part.Comps))
                        return;
                    // Comps from other mods (or whose CompTick another mod patches) and replayed ones tick for real; after
                    // one from another mod has, a comp is only skipped if it is idle where it stands.
                    var index = Array.IndexOf(currentState.Comps, __instance);
                    if (index >= 0)
                    {
                        var kind = currentState.Kinds[index];
                        if (kind == CompKind.Foreign || kind == CompKind.Replay || !CompIdle(__instance, kind))
                            return;
                    }
                    __state = Take(__instance, current);
                }

                public static void Postfix(MethodBase __originalMethod, Snapshot __state)
                {
                    if (__state != null)
                        Compare(__state, $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
                }
            }

            private static int StepOf(object o)
            {
                var p = current;
                return o == p.pather ? StepPather : o == p.verbTracker ? StepVerbs : o == p.roping ? StepRoping :
                    o == p.flight ? StepFlight : o == p.natives ? StepNatives : o == p.stances ? StepStances : -1;
            }

            /// <summary>Whether MicroTick calls the step for real (moving, or another mod patches it).</summary>
            private static bool RealStep(int step)
            {
                switch (step)
                {
                    case StepPather: return Has(Part.Pather) || !patherPredictedIdle;
                    case StepVerbs: return Has(Part.Verbs);
                    case StepRoping: return Has(Part.Roping);
                    case StepNatives: return Has(Part.Natives);
                    case StepStances: return Has(Part.Stances);
                    default: return false;
                }
            }

            /// <summary>What a step may change: its object, the pawn, and the verbs and stance handlers it ticks.</summary>
            private static object[] StepObjects(int step, object o)
            {
                var list = new List<object> { o, current };
                switch (step)
                {
                    case StepVerbs:
                        list.AddRange(trackerVerbs((VerbTracker)o) ?? new List<Verb>());
                        break;
                    case StepNatives:
                        var tracker = ((Pawn_NativeVerbs)o).verbTracker;
                        list.Add(tracker);
                        if (tracker != null)
                            list.AddRange(trackerVerbs(tracker) ?? new List<Verb>());
                        break;
                    case StepStances:
                        var stances = (Pawn_StanceTracker)o;
                        list.Add(stances.stunner);
                        list.Add(stances.stagger);
                        list.Add(stances.curStance);
                        break;
                }
                return list.ToArray();
            }

            private static readonly Snapshot RealCall = new Snapshot(), PlainCall = new Snapshot();

            /// <summary>
            /// The head's parts, in vanilla's order: skipped ones are checked as no-ops; after one that runs for real in
            /// MicroTick, the same re-check decides whether the rest of the head is still skipped.
            /// </summary>
            public static class HeadCheck
            {
                public static void Prefix(object __instance, out Snapshot __state)
                {
                    __state = null;
                    if (current == null || !compsDone || !restSkippable)
                        return;
                    var step = StepOf(__instance);
                    // Only the head's own call of each step (not one made from inside another part or the job).
                    if (step < 0 || step <= lastStep)
                        return;
                    lastStep = step;
                    if (!headSkippable)
                        return;
                    if (step == StepFlight)
                        __state = Has(Part.Flight) ? RealCall : PlainCall; // always called
                    else if (RealStep(step))
                        __state = RealCall;
                    else
                        __state = Take(StepObjects(step, __instance));
                }

                public static void Postfix(MethodBase __originalMethod, Snapshot __state)
                {
                    if (__state == null)
                        return;
                    if (__state == RealCall)
                        dirty = true;
                    else if (__state != PlainCall)
                        Compare(__state, $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
                    // MicroTick re-checks before the next part when one ran for real since its last check.
                    if (dirty && headSkippable)
                    {
                        dirty = false;
                        if (!RemainderIdle(current, currentState))
                            headSkippable = false;
                    }
                }
            }

            public static class EffectersCheck
            {
                public static void Prefix(PawnRenderer __instance, out Snapshot __state)
                {
                    __state = null;
                    if (current != null && restSkippable && !Has(Part.Effecters) && drawer(current)?.renderer == __instance &&
                        PawnVersions.Get(current) == versionAtBegin && current.mindState.mentalStateHandler.CurState == null)
                        __state = Take(current, EffecterPairsList(current));
                }

                public static void Postfix(Snapshot __state)
                {
                    if (__state != null)
                        Compare(__state, "PawnRenderer.EffectersTick");
                }
            }

            private static object EffecterPairsList(Pawn pawn) => effecterPairs.GetValue(rendererEffecters.GetValue(drawer(pawn).renderer));

            public static class BloodRainCheck
            {
                public static void Prefix(Pawn pawn, out Snapshot __state)
                {
                    __state = null;
                    if (current != null && restSkippable && !Has(Part.BloodRain) && pawn == current && PawnVersions.Get(current) == versionAtBegin &&
                        pawn.Spawned && !BloodRainCache.Possible(pawn.Map))
                        __state = Take(pawn, pawn.health.hediffSet);
                }

                public static void Postfix(Snapshot __state)
                {
                    if (__state != null)
                        Compare(__state, "BloodRainUtility.BloodRainTick");
                }
            }

            public static void HiddenPostfix(Pawn pawn, bool __result)
            {
                if (current == null || !compsDone || !restSkippable || Has(Part.Hidden) || pawn != current || PawnVersions.Get(current) != versionAtBegin)
                    return;
                Note("InvisibilityUtility.IsHiddenFromPlayer");
                if (__result)
                    Info.Stats.Mismatch(() => $"{current}: hidden from player on a predicted off-tick");
            }

            /// <summary>Vanilla's own read, right after the comps (TickRare never runs on a predicted off-tick).</summary>
            public static void SuspendedPostfix(Pawn __instance, bool __result)
            {
                if (current == null || !compsDone || suspendedSeen || __instance != current)
                    return;
                suspendedSeen = true;
                if (!restSkippable)
                    return;
                if (Has(Part.Suspended))
                {
                    // MicroTick asks too, and runs the rest as vanilla when suspended.
                    if (__result)
                        restSkippable = false;
                    return;
                }
                Note("Pawn.Suspended");
                if (__result)
                    Info.Stats.Mismatch(() => $"{current}: suspended on a predicted off-tick");
            }

            public static void WorldPawnPostfix(Pawn p, bool __result)
            {
                if (current == null || !compsDone || !restSkippable || !headSkippable || Has(Part.WorldPawn) || p != current)
                    return;
                Note("WorldPawnsUtility.IsWorldPawn");
                if (__result)
                    Info.Stats.Mismatch(() => $"{current}: world pawn on a predicted off-tick");
            }

            /// <summary>Right after the comps: what MicroTick decides at the same point.</summary>
            public static void CompsDonePostfix(ThingWithComps __instance)
            {
                if (current == null || __instance != current || compsDone)
                    return;
                compsDone = true;
                if (currentState.HasForeign || Has(Part.Comps))
                {
                    var list = currentState.CompList;
                    restSkippable = thingComps(current) == list && ListVersion<ThingComp>.Of(list) == compListVersion &&
                                    IdleAfterComps(current, currentState);
                    if (!restSkippable)
                        verifyForeignChanged++;
                }
                patherPredictedIdle = PatherIdle(current.pather);
            }

            /// <summary>TickRare runs inside Pawn.Tick; a predicted off-tick must never be one.</summary>
            public static void TickRarePrefix(Pawn __instance)
            {
                if (current == null || __instance != current)
                    return;
                Info.Stats.Mismatch(() => $"{__instance}: TickRare ran on a predicted off-tick");
            }
        }
    }
}
