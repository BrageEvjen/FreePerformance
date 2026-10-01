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
    /// Pawn_HealthTracker.HealthTick calls Tick and PostTick on every hediff of every pawn, every tick. Hediff.Tick is
    /// empty in every hediff type, and HediffWithComps.PostTick only calls each comp's CompPostTick and applies a
    /// severity change if one was reported. Most hediffs in a colony are scars and missing parts whose comps do nothing
    /// per tick: HediffComp.CompPostTick is empty, and HediffComp_TendDuration only counts down a non-permanent tend
    /// (injuries' tends are permanent). So their per-tick call is a no-op.
    ///
    /// This builds, per pawn, the list of hediffs that can do something per tick (rebuilt when the hediff list changes,
    /// detected by List._version), and HealthTick ticks only those, in the same order, with vanilla's error handling,
    /// removed-this-tick check and death check, followed by vanilla's ShouldRemove pass over all hediffs. Tend and link
    /// comps that only act while a field is set are checked each tick. Any comp type not known to be a no-op ticks.
    ///
    /// PawnStatusEffecters.EffectersTick looks for an effecter comp on every hediff every tick; hediff comps come only
    /// from the def, so defs without an effecter comp are answered from a table. Exact.
    /// </summary>
    public static class HediffTickPlan
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "hediffplan",
            Label = "Tick only active hediffs  (exact)",
            Description = "Scars, missing body parts and other health conditions that have nothing to do each tick are no " +
                          "longer visited every tick. Conditions that do something per tick still run, in the same order.",
            Patch = Patch,
            Reset = Reset,
            Prune = Prune,
            ReportLines = Report,
        };

        private enum Kind : byte { Skip, Always, Tend, Link }

        private struct Entry
        {
            public Hediff Hediff;
            public Kind Kind;
            public HediffComp Comp; // the conditional comp for Tend/Link
        }

        private sealed class Plan
        {
            public List<Hediff> List;
            public int Version;
            public Entry[] Entries;
            public Pawn Pawn;
        }

        private static readonly Dictionary<int, Plan> plans = new Dictionary<int, Plan>();
        private static readonly Dictionary<Type, Kind> hediffKinds = new Dictionary<Type, Kind>();
        private static readonly Dictionary<Type, Kind> compKinds = new Dictionary<Type, Kind>();
        private static bool[] effecterDefs;
        private static long ticked, skipped, rebuilds, effecterSkips;

        private static readonly AccessTools.FieldRef<Pawn_HealthTracker, Pawn> trackerPawn = AccessTools.FieldRefAccess<Pawn_HealthTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<List<Hediff>> tmpHediffs = AccessTools.StaticFieldRefAccess<List<Hediff>>(AccessTools.Field(typeof(Pawn_HealthTracker), "tmpHediffs"));
        private static readonly AccessTools.FieldRef<HashSet<Hediff>> tmpRemovedHediffs = AccessTools.StaticFieldRefAccess<HashSet<Hediff>>(AccessTools.Field(typeof(Pawn_HealthTracker), "tmpRemovedHediffs"));
        private static readonly AccessTools.FieldRef<Hediff, float> severityInt = AccessTools.FieldRefAccess<Hediff, float>("severityInt");

        private static MethodInfo tryGetEffecter;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)),
                prefix: new HarmonyMethod(typeof(HealthPatch), nameof(HealthPatch.Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(HealthPatch), nameof(HealthPatch.Postfix)));
            tryGetEffecter = AccessTools.Method(typeof(HediffUtility), nameof(HediffUtility.TryGetComp), new[] { typeof(Hediff) })
                ?.MakeGenericMethod(typeof(HediffComp_Effecter));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Verse.PawnStatusEffecters"), "EffectersTick"),
                transpiler: new HarmonyMethod(typeof(HediffTickPlan), nameof(EffectersTranspiler)));
        }

        private static void Reset()
        {
            plans.Clear();
            ticked = skipped = rebuilds = effecterSkips = 0;
            guardChecked = false;
            // Which hediffs and comps tick as vanilla depends on other mods' patches, checked again for each game.
            hediffKinds.Clear();
            compKinds.Clear();
            Info.Stats.Reset();
        }

        private static void Prune(int now)
        {
            foreach (var id in plans.Where(kv => kv.Value.Pawn.Destroyed || kv.Value.Pawn.Discarded).Select(kv => kv.Key).ToList())
                plans.Remove(id);
        }

        private static IEnumerable<string> Report()
        {
            var total = ticked + skipped;
            yield return $"  hediff ticks run: {ticked:N0}, skipped: {skipped:N0} ({(total == 0 ? 0 : 100.0 * skipped / total):F1}%), plan rebuilds: {rebuilds:N0}";
            yield return $"  effecter lookups answered from the def table: {effecterSkips:N0}";
        }

        // ---- Guard ----

        private static bool guardChecked, guardBlocked;

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            guardBlocked = false;
            if (Bench.BenchConfig.NoGuards)
                return false;
            // The prefix is the last one and replaces HealthTick's body with a copy: other mods' prefixes run before it
            // and their postfixes and finalizers after it, as around vanilla's body. Their patches on a hediff's or
            // comp's tick make those hediffs tick as vanilla (Classify).
            var problem = PatchGuard.ReplacedBodyProblem(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)),
                AccessTools.Method(typeof(HealthPatch), nameof(HealthPatch.Prefix)));
            if (problem != null)
            {
                Info.LogBlocked(problem);
                guardBlocked = true;
            }
            return guardBlocked;
        }

        /// <summary>True when another mod patches the method anywhere in the type's hierarchy (logged once per method).</summary>
        private static bool PatchedByOthers(Type type, string method)
        {
            if (Bench.BenchConfig.NoGuards)
                return false;
            var patched = PatchGuard.PatchedInHierarchy(type, method);
            foreach (var line in patched)
                Info.LogPartlyVanilla(line);
            return patched.Count > 0;
        }

        // ---- Classification ----

        /// <summary>What a hediff's Tick + PostTick can do: nothing, always something, or something only while a comp field is set.</summary>
        private static Entry Classify(Hediff h)
        {
            var entry = new Entry { Hediff = h, Kind = HediffKind(h.GetType()) };
            if (entry.Kind != Kind.Skip || !(h is HediffWithComps hc) || hc.comps == null)
                return entry;
            foreach (var comp in hc.comps)
            {
                var kind = CompKind(comp);
                if (kind == Kind.Skip)
                    continue;
                if (kind == Kind.Always || entry.Comp != null)
                    return new Entry { Hediff = h, Kind = Kind.Always };
                entry.Kind = kind;
                entry.Comp = comp;
            }
            return entry;
        }

        /// <summary>Skip = Tick is Hediff's empty one and PostTick is Hediff's or HediffWithComps' (comps decide).</summary>
        private static Kind HediffKind(Type type)
        {
            if (hediffKinds.TryGetValue(type, out var kind))
                return kind;
            var tick = AccessTools.Method(type, nameof(Hediff.Tick))?.DeclaringType;
            var postTick = AccessTools.Method(type, nameof(Hediff.PostTick))?.DeclaringType;
            var patched = PatchedByOthers(type, nameof(Hediff.Tick)) | PatchedByOthers(type, nameof(Hediff.PostTick));
            kind = !patched && tick == typeof(Hediff) && (postTick == typeof(Hediff) || postTick == typeof(HediffWithComps)) ? Kind.Skip : Kind.Always;
            hediffKinds[type] = kind;
            return kind;
        }

        private static Kind CompKind(HediffComp comp)
        {
            var type = comp.GetType();
            if (!compKinds.TryGetValue(type, out var kind))
            {
                var declaring = AccessTools.Method(type, nameof(HediffComp.CompPostTick))?.DeclaringType;
                kind = PatchedByOthers(type, nameof(HediffComp.CompPostTick)) ? Kind.Always
                    : declaring == typeof(HediffComp) ? Kind.Skip
                    : declaring == typeof(HediffComp_TendDuration) ? Kind.Tend
                    : declaring == typeof(HediffComp_Link) ? Kind.Link
                    : Kind.Always;
                compKinds[type] = kind;
            }
            // A permanent tend (injuries: baseTendDurationHours = -1) never counts down.
            if (kind == Kind.Tend && (!(comp.props is HediffCompProperties_TendDuration props) || props.TendIsPermanent))
                return comp.props is HediffCompProperties_TendDuration ? Kind.Skip : Kind.Always;
            return kind;
        }

        private static bool ConditionActive(in Entry e)
        {
            switch (e.Kind)
            {
                case Kind.Tend: return ((HediffComp_TendDuration)e.Comp).tendTicksLeft > 0;
                case Kind.Link: return ((HediffComp_Link)e.Comp).drawConnection;
                default: return true;
            }
        }

        private static Plan GetPlan(Pawn pawn, List<Hediff> list)
        {
            var version = ListVersion<Hediff>.Of(list);
            if (plans.TryGetValue(pawn.thingIDNumber, out var plan) && plan.List == list && plan.Version == version && plan.Pawn == pawn)
                return plan;
            rebuilds++;
            if (plan == null || plan.Pawn != pawn)
                plans[pawn.thingIDNumber] = plan = new Plan { Pawn = pawn };
            plan.List = list;
            plan.Version = version;
            plan.Entries = list.Select(Classify).Where(e => e.Kind != Kind.Skip).ToArray();
            return plan;
        }

        // ---- HealthTick ----

        /// <summary>
        /// Vanilla runs; remember what the plan says it would not tick, and check afterwards it did not change. Its own
        /// method so the lambdas in it don't make the compiler allocate a closure on every call of the prefix.
        /// </summary>
        private static List<(Hediff h, float severity, int tend)> VerifyBefore(Pawn pawn, List<Hediff> list, Plan plan)
        {
            Info.Stats.Checks++;
            var active = new HashSet<Hediff>(plan.Entries.Where(e => ConditionActive(e)).Select(e => e.Hediff));
            var state = new List<(Hediff, float, int)>();
            foreach (var h in list)
                if (!active.Contains(h))
                    state.Add((h, severityInt(h), (h as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>()?.tendTicksLeft ?? 0));
            var fresh = list.Select(Classify).Where(e => e.Kind != Kind.Skip).ToArray();
            if (fresh.Length != plan.Entries.Length || fresh.Where((e, i) => e.Hediff != plan.Entries[i].Hediff || e.Kind != plan.Entries[i].Kind).Any())
                Info.Stats.Mismatch(() => $"{pawn}: cached hediff plan differs from a fresh one");
            return state;
        }

        public static class HealthPatch
        {
            /// <summary>The last prefix: other mods' prefixes on HealthTick run first, as before vanilla's body.</summary>
            [HarmonyPriority(Priority.Last)]
            public static bool Prefix(Pawn_HealthTracker __instance, out List<(Hediff h, float severity, int tend)> __state)
            {
                __state = null;
                if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread || Blocked())
                    return true;
                var pawn = trackerPawn(__instance);
                var list = __instance.hediffSet.hediffs;
                if (pawn == null)
                    return true;
                var plan = GetPlan(pawn, list);

                if (Info.Verifying)
                {
                    __state = VerifyBefore(pawn, list, plan);
                    return true;
                }

                // Vanilla HealthTick with the no-op hediffs left out.
                if (__instance.Dead)
                    return false;
                var removed = tmpRemovedHediffs();
                removed.Clear();
                tmpHediffs().Clear();
                var entries = plan.Entries;
                skipped += list.Count - entries.Length;
                for (var i = 0; i < entries.Length; i++)
                {
                    ref var e = ref entries[i];
                    var h = e.Hediff;
                    if (removed.Count > 0 && removed.Contains(h))
                        continue;
                    try
                    {
                        if (e.Kind != Kind.Always && !ConditionActive(e))
                        {
                            skipped++;
                            continue;
                        }
                        ticked++;
                        h.Tick();
                        h.PostTick();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(string.Format("Exception ticking hediff {0} for pawn {1}. Removing hediff... Exception: {2}", h.ToStringSafe(), pawn.ToStringSafe(), ex));
                        try
                        {
                            __instance.RemoveHediff(h);
                        }
                        catch (Exception arg)
                        {
                            Log.Error(string.Format("Error while removing hediff: {0}", arg));
                        }
                    }
                    if (__instance.Dead)
                        return false;
                }
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var h = list[i];
                    if (h.ShouldRemove)
                        __instance.RemoveHediff(h);
                }
                Info.Stats.Hits++;
                return false;
            }

            public static void Postfix(Pawn_HealthTracker __instance, List<(Hediff h, float severity, int tend)> __state)
            {
                if (__state == null)
                    return;
                foreach (var (h, severity, tend) in __state)
                {
                    var tendNow = (h as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>()?.tendTicksLeft ?? 0;
                    if (severityInt(h) != severity || tendNow != tend)
                        Info.Stats.Mismatch(() => $"{h.pawn}: skipped hediff {h} changed (severity {severity} -> {severityInt(h)}, tend {tend} -> {tendNow})");
                }
            }
        }

        // ---- EffectersTick ----

        public static IEnumerable<CodeInstruction> EffectersTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return SafeTranspile.Replace(instructions, 1, "Hediff effecter lookup (EffectersTick)",
                ins => tryGetEffecter != null && ins.Calls(tryGetEffecter),
                ins =>
                {
                    ins.operand = AccessTools.Method(typeof(HediffTickPlan), nameof(FastEffecter));
                    return new[] { ins };
                });
        }

        public static HediffComp_Effecter FastEffecter(Hediff h)
        {
            var def = h.def;
            if (!Info.Active && !Info.Verifying || def == null || !UnityData.IsInMainThread)
                return h.TryGetComp<HediffComp_Effecter>();
            if (effecterDefs == null)
            {
                var defs = DefDatabase<HediffDef>.AllDefsListForReading;
                effecterDefs = new bool[defs.Max(d => d.index) + 1];
                foreach (var d in defs)
                    effecterDefs[d.index] = d.comps != null && d.comps.Any(c => c.compClass != null && typeof(HediffComp_Effecter).IsAssignableFrom(c.compClass));
            }
            if (def.index >= effecterDefs.Length || effecterDefs[def.index])
                return h.TryGetComp<HediffComp_Effecter>();
            if (Info.Verifying)
            {
                var vanilla = h.TryGetComp<HediffComp_Effecter>();
                Info.Stats.Checks++;
                if (vanilla != null)
                    MismatchEffecter(h);
                return vanilla;
            }
            effecterSkips++;
            return null;
        }

        private static void MismatchEffecter(Hediff h) => Info.Stats.Mismatch(() => $"{h}: has an effecter comp its def does not list");
    }
}
