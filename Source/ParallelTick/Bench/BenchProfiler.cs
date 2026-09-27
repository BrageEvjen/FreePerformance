using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Inclusive wall-clock timers attached with Harmony during the breakdown pass only.
    /// Each slot has its own depth counter so recursive/nested calls into the same slot are not double counted.
    /// Timings from different slots do overlap (e.g. "Work scanning" is inside "Job search").
    /// </summary>
    public static class BenchProfiler
    {
        public class Slot
        {
            public string Group, Label;
            public long Elapsed, Calls;
            public int Depth;
        }

        public class Acc
        {
            public long Elapsed, Calls;
        }

        private static readonly (string group, string type, string method, string label)[] Targets =
        {
            ("Tick sections", "Verse.Map", "MapPreTick", "Map pre-tick"),
            ("Tick sections", "Verse.TickList", "Tick", "Thing ticks (all tick lists)"),
            ("Tick sections", "Verse.Map", "MapPostTick", "Map post-tick"),
            ("Tick sections", "RimWorld.Planet.World", "WorldTick", "World tick"),
            ("Tick sections", "RimWorld.Planet.World", "WorldPostTick", "World post-tick"),
            ("Tick sections", "RimWorld.Storyteller", "StorytellerTick", "Storyteller"),
            ("Tick sections", "RimWorld.QuestManager", "QuestManagerTick", "Quests"),
            ("Tick sections", "RimWorld.StoryWatcher", "StoryWatcherTick", "Story watcher"),
            ("Tick sections", "RimWorld.TaleManager", "TaleManagerTick", "Tales"),
            ("Tick sections", "RimWorld.History", "HistoryTick", "History"),
            ("Tick sections", "Verse.GameComponentUtility", "GameComponentTick", "Game components"),

            ("Map pre-tick", "Verse.PathFinder", "PathFinderTick", "Pathfinder batch (multithreaded in 1.6)"),
            ("Map pre-tick", "RimWorld.ListerHaulables", "ListerHaulablesTick", "Haulables lister"),
            ("Map pre-tick", "RimWorld.ItemAvailability", "Tick", "Item availability"),
            ("Map pre-tick", "Verse.MapTemperature", "MapTemperatureTick", "Temperature"),

            ("Map post-tick", "Verse.MapComponentUtility", "MapComponentTick", "Map components"),
            ("Map post-tick", "Verse.AI.Group.LordManager", "LordManagerTick", "Lords (group AI)"),
            ("Map post-tick", "RimWorld.PowerNetManager", "PowerNetsTick", "Power nets"),
            ("Map post-tick", "RimWorld.SteadyEnvironmentEffects", "SteadyEnvironmentEffectsTick", "Environment effects"),
            ("Map post-tick", "RimWorld.WildPlantSpawner", "WildPlantSpawnerTick", "Wild plant spawner"),
            ("Map post-tick", "RimWorld.WeatherManager", "WeatherManagerTick", "Weather"),

            ("World tick", "RimWorld.Planet.WorldPawns", "WorldPawnsTick", "World pawns"),
            ("World tick", "RimWorld.Planet.WorldObjectsHolder", "WorldObjectsHolderTick", "World objects"),
            ("World tick", "RimWorld.FactionManager", "FactionManagerTick", "Factions"),
            ("World tick", "RimWorld.Planet.WorldPathGrid", "WorldPathGridTick", "World path grid"),
            ("World tick", "RimWorld.Planet.WorldComponentUtility", "WorldComponentTick", "World components"),
            ("World tick", "RimWorld.IdeoManager", "IdeoManagerTick", "Ideologies"),
            ("World tick", "RimWorld.Planet.WorldPawns", "GetSituation", "World pawn situation checks (all callers)"),
            ("Cross-cutting (nested in the above)", "Verse.MapPawns", "get_AllPawnsUnspawned", "Unspawned-pawn search (walks every container on the map)"),
            ("Wealth recount", "RimWorld.WealthWatcher", "ForceRecount", "Wealth recount (total)"),
            ("Wealth recount", "RimWorld.WealthWatcher", "CalculateWealthItems", "Items"),
            ("Wealth recount", "RimWorld.WealthWatcher", "CalculateWealthFloors", "Floors"),
            ("Wealth recount", "RimWorld.StatWorker_MarketValue", "CalculatedBaseMarketValue", "Base market value from cost"),
            ("Wealth recount", "RimWorld.StatWorker_MarketValue", "CalculableRecipe", "Recipe lookup (scans all recipes)"),

            ("Inside pawns", "Verse.AI.Pawn_JobTracker", "JobTrackerTick", "Job tracker (every tick)"),
            ("Inside pawns", "Verse.AI.Pawn_JobTracker", "JobTrackerTickInterval", "Job tracker (interval)"),
            ("Inside pawns", "Verse.AI.Pawn_JobTracker", "TryFindAndStartJob", "Job search (think tree)"),
            ("Inside pawns", "Verse.AI.Pawn_JobTracker", "DetermineNextConstantThinkTreeJob", "Constant think tree (every 30 ticks)"),
            ("Inside pawns", "Verse.AI.JobDriver", "DriverTickInterval", "Job driver interval (toil interval actions)"),
            ("Inside pawns", "Verse.AI.JobDriver", "DriverTick", "Job driver tick"),
            ("Inside pawns", "Verse.AI.JobDriver", "CheckCurrentToilEndOrFail", "Job fail/end conditions"),
            ("Inside pawns", "RimWorld.JobGiver_Work", "TryIssueJobPackage", "Work scanning (JobGiver_Work)"),
            ("Inside pawns", "Verse.AI.Pawn_PathFollower", "PatherTick", "Path following"),
            ("Inside pawns", "Verse.Pawn_HealthTracker", "HealthTick", "Health (every tick)"),
            ("Inside pawns", "Verse.Pawn_HealthTracker", "HealthTickInterval", "Health (interval)"),
            ("Inside pawns", "RimWorld.Pawn_NeedsTracker", "NeedsTrackerTickInterval", "Needs"),
            ("Inside pawns", "Verse.AI.Pawn_MindState", "MindStateTickInterval", "Mind state"),
            ("Inside pawns", "RimWorld.Pawn_InteractionsTracker", "InteractionsTrackerTickInterval", "Social interactions"),
            ("Inside pawns", "Verse.Pawn_StanceTracker", "StanceTrackerTick", "Stances"),
            ("Inside pawns", "Verse.VerbTracker", "VerbsTick", "Verbs"),
            ("Inside pawns", "Verse.PawnRenderer", "EffectersTick", "Effecters"),

            ("Pawn sub-systems", "Verse.ThingWithComps", "Tick", "Comps every tick (all things)"),
            ("Pawn sub-systems", "Verse.ThingWithComps", "TickInterval", "Comps interval (all things)"),
            ("Pawn sub-systems", "RimWorld.Pawn_GeneTracker", "GeneTrackerTick", "Genes (every tick)"),
            ("Pawn sub-systems", "RimWorld.Pawn_GeneTracker", "GeneTrackerTickInterval", "Genes (interval)"),
            ("Pawn sub-systems", "RimWorld.Pawn_AbilityTracker", "AbilitiesTick", "Abilities"),
            ("Pawn sub-systems", "Verse.Pawn_EquipmentTracker", "EquipmentTrackerTick", "Equipment"),
            ("Pawn sub-systems", "Verse.Pawn_InventoryTracker", "InventoryTrackerTick", "Inventory"),
            ("Pawn sub-systems", "RimWorld.Pawn_ApparelTracker", "ApparelTrackerTickInterval", "Apparel"),
            ("Pawn sub-systems", "RimWorld.Pawn_SkillTracker", "SkillsTickInterval", "Skills"),
            ("Pawn sub-systems", "Verse.Pawn_AgeTracker", "AgeTickInterval", "Age"),
            ("Pawn sub-systems", "RimWorld.Pawn_RecordsTracker", "RecordsTickInterval", "Records"),
            ("Pawn sub-systems", "RimWorld.Pawn_IdeoTracker", "IdeoTrackerTickInterval", "Ideology"),
            ("Pawn sub-systems", "RimWorld.Pawn_StyleObserverTracker", "StyleObserverTickInterval", "Style observer"),
            ("Pawn sub-systems", "RimWorld.Pawn_SurroundingsTracker", "SurroundingsTrackerTickInterval", "Surroundings"),
            ("Pawn sub-systems", "RimWorld.Pawn_RelationsTracker", "RelationsTrackerTickInterval", "Relations"),
            ("Pawn sub-systems", "RimWorld.Pawn_GuestTracker", "GuestTrackerTickInterval", "Guest"),
            ("Pawn sub-systems", "Verse.Pawn_CarryTracker", "CarryHandsTickInterval", "Carry"),
            ("Pawn sub-systems", "Verse.VacuumUtility", "PawnVacuumTickInterval", "Vacuum"),
            ("Pawn sub-systems", "Verse.GasUtility", "PawnGasEffectsTickInterval", "Gas"),
            ("Pawn sub-systems", "Verse.PollutionUtility", "PawnPollutionTickInterval", "Pollution"),
            ("Pawn sub-systems", "Verse.ToxicUtility", "PawnToxicTickInterval", "Toxic"),
            ("Pawn sub-systems", "RimWorld.Pawn_MutantTracker", "MutantTrackerTick", "Mutant"),

            ("Mood", "RimWorld.SituationalThoughtHandler", "SituationalThoughtInterval", "Situational thoughts interval"),
            ("Mood", "RimWorld.SituationalThoughtHandler", "UpdateAllMoodThoughts", "Recalculate mood thoughts"),
            ("Mood", "RimWorld.ThoughtHandler", "TotalMoodOffset", "Total mood offset"),

            ("Sleeping", "RimWorld.RestUtility", "ShouldWakeUp", "Should wake up?"),
            ("Sleeping", "RimWorld.RestUtility", "CanFallAsleep", "Can fall asleep?"),
            ("Sleeping", "RimWorld.Toils_LayDown", "ApplyBedRelatedEffects", "Bed effects (rest, comfort, flecks)"),
            ("Sleeping", "RimWorld.PawnUtility", "GainComfortFromCellIfPossible", "Comfort from cell"),

            ("Visual", "Verse.Effecter", "EffectTick", "Effecters"),
            ("Visual", "RimWorld.FleckMaker", "ThrowMetaIcon", "Meta icons (Z, hearts...)"),

            ("Work search", "Verse.GenClosest", "ClosestThing_Global", "ClosestThing_Global (no reachability)"),
            ("Work search", "Verse.GenClosest", "ClosestThing_Global_Reachable", "ClosestThing_Global_Reachable"),
            ("Work search", "Verse.GenClosest", "ClosestThingReachable", "ClosestThingReachable"),
            ("Work search", "Verse.GenClosest", "RegionwiseBFSWorker", "RegionwiseBFSWorker"),

            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "ResourceDeliverJobFor", "ResourceDeliverJobFor (per blueprint/frame)"),
            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "FindNearbyNeeders", "FindNearbyNeeders"),
            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "FindAvailableNearbyResources", "FindAvailableNearbyResources"),
            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "NotifyNoReachableResourceFound", "No reachable resource (count)"),
            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "NoReachableResourceFailedThisTick", "Failed-this-tick cache lookup"),
            ("Construction delivery", "RimWorld.WorkGiver_ConstructDeliverResources", "ResourceValidator_NewTemp", "Resource validator"),
            ("Construction delivery", "RimWorld.ItemAvailability", "ThingsAvailableAnywhere", "ThingsAvailableAnywhere"),

            ("Movement", "Verse.AI.Pawn_PathFollower", "TryEnterNextPathCell", "Enter next path cell"),
            ("Movement", "Verse.AI.Pawn_PathFollower", "CostToMoveIntoCell", "Cost to move into cell"),

            ("Cross-cutting (nested in the above)", "RimWorld.StatWorker", "GetValue", "Stat calculation"),
            ("Cross-cutting (nested in the above)", "Verse.Reachability", "CanReach", "Reachability checks"),
            ("Cross-cutting (nested in the above)", "Verse.PathFinder", "FindPathNow", "Immediate pathfinding"),
        };

        /// <summary>The fixed targets plus NeedInterval of every Need subclass that overrides it.</summary>
        private static IEnumerable<(string group, string type, string method, string label)> AllTargets()
        {
            foreach (var t in Targets)
                yield return t;
            foreach (var needType in typeof(Need).AllSubclassesNonAbstract().OrderBy(t => t.Name))
                if (AccessTools.DeclaredMethod(needType, nameof(Need.NeedInterval)) != null)
                    yield return ("Needs (interval)", needType.FullName, nameof(Need.NeedInterval), needType.Name.Replace("Need_", ""));
            foreach (var compType in typeof(ThingComp).AllSubclasses().OrderBy(t => t.Name))
                if (AccessTools.DeclaredMethod(compType, nameof(ThingComp.CompTick)) != null)
                    yield return ("Comp ticks by type", compType.FullName, nameof(ThingComp.CompTick), compType.Name);
            foreach (var giverType in typeof(WorkGiver_Scanner).AllSubclasses().Concat(new[] { typeof(WorkGiver_Scanner) }).OrderBy(t => t.Name))
                foreach (var method in new[] { "HasJobOnThing", "JobOnThing", "HasJobOnCell", "JobOnCell" })
                    if (AccessTools.DeclaredMethod(giverType, method) != null)
                        yield return ("Work givers (per candidate thing/cell)", giverType.FullName, method, $"{giverType.Name.Replace("WorkGiver_", "")}.{method}");
            foreach (var buildingType in typeof(Building).AllSubclasses().Concat(new[] { typeof(Building) }).OrderBy(t => t.Name))
                if (AccessTools.DeclaredMethod(buildingType, "Tick") != null)
                    yield return ("Building ticks by type (own Tick, includes base/comps)", buildingType.FullName, "Tick", buildingType.Name);
        }

        public static readonly List<Slot> Slots = new List<Slot>();
        public static readonly Dictionary<string, Acc> ByCategory = new Dictionary<string, Acc>();
        public static readonly Dictionary<ThingDef, Acc> ByDef = new Dictionary<ThingDef, Acc>();

        // Deeper pawn breakdown: every-tick vs interval work per pawn category, job driver ticks per job,
        // and stat requests by stat (outermost request only, inclusive of the stats it depends on).
        public static readonly Dictionary<(string category, string part), Acc> PawnParts = new Dictionary<(string, string), Acc>();
        public static readonly Dictionary<(string category, JobDef job), Acc> JobDrivers = new Dictionary<(string, JobDef), Acc>();
        public static readonly Dictionary<StatDef, Acc> Stats = new Dictionary<StatDef, Acc>();
        public static readonly Dictionary<string, Acc> FailConditions = new Dictionary<string, Acc>();
        public static readonly Dictionary<(string category, string outcome), Acc> WorkSearches = new Dictionary<(string, string), Acc>();
        public static readonly Dictionary<string, Acc> Searches = new Dictionary<string, Acc>();
        public static readonly Dictionary<(string category, string giver), Acc> WorkGiverTimes = new Dictionary<(string, string), Acc>();
        public static readonly Dictionary<string, Acc> Thoughts = new Dictionary<string, Acc>();

        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> JobTrackerPawn =
            AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<StatWorker, StatDef> WorkerStat =
            AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");

        private static readonly Dictionary<MethodBase, Slot> slotByMethod = new Dictionary<MethodBase, Slot>();
        private static readonly List<MethodBase> patched = new List<MethodBase>();
        private static int thingDepth, pawnTickDepth, pawnIntervalDepth, statDepth;

        public static void Attach(Harmony harmony)
        {
            var slotPrefix = new HarmonyMethod(typeof(BenchProfiler), nameof(SlotPrefix));
            var slotFinalizer = new HarmonyMethod(typeof(BenchProfiler), nameof(SlotFinalizer));
            foreach (var (group, typeName, methodName, label) in AllTargets())
            {
                var type = AccessTools.TypeByName(typeName);
                var methods = type == null
                    ? new List<MethodInfo>()
                    : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName && !m.IsAbstract).ToList();
                if (methods.Count == 0)
                {
                    Log.Warning($"[Free Performance] Profiler target not found: {typeName}.{methodName}");
                    continue;
                }
                // All overloads share one slot (and one depth counter), so overloads calling each other count once.
                var slot = new Slot { Group = group, Label = label };
                Slots.Add(slot);
                foreach (var m in methods)
                {
                    slotByMethod[m] = slot;
                    harmony.Patch(m, prefix: slotPrefix, finalizer: slotFinalizer);
                    patched.Add(m);
                }
            }

            // Harmony shares __state between all patches declared in the same class on the same method, so each
            // timer that can land on an already-timed method lives in its own class.
            PatchWith(harmony, AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)), typeof(BenchProfiler), nameof(DoTickPrefix), nameof(DoTickFinalizer));
            PatchWith(harmony, AccessTools.Method(typeof(Pawn), "Tick"), typeof(PawnTickTimer));
            PatchWith(harmony, AccessTools.Method(typeof(Pawn), "TickInterval"), typeof(PawnIntervalTimer));
            PatchWith(harmony, AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick)), typeof(JobTimer));
            PatchWith(harmony, AccessTools.Method(typeof(JobDriver), "CheckCurrentToilEndOrFail"), typeof(FailConditionTimer));
            foreach (var name in new[] { nameof(GenClosest.ClosestThing_Global), nameof(GenClosest.ClosestThingReachable), nameof(GenClosest.ClosestThing_Global_Reachable) })
                PatchWith(harmony, AccessTools.Method(typeof(GenClosest), name), typeof(SearchTimer));
            var tryIssue = AccessTools.Method(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage));
            harmony.Patch(tryIssue,
                prefix: new HarmonyMethod(typeof(WorkSearchTimer), nameof(WorkSearchTimer.Prefix)),
                postfix: new HarmonyMethod(typeof(WorkSearchTimer), nameof(WorkSearchTimer.Postfix)));
            patched.Add(tryIssue);
            harmony.Patch(tryIssue,
                prefix: new HarmonyMethod(typeof(WorkGiverSegments), nameof(WorkGiverSegments.SearchPrefix)),
                finalizer: new HarmonyMethod(typeof(WorkGiverSegments), nameof(WorkGiverSegments.SearchFinalizer)));
            PatchWith(harmony, AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver"), typeof(WorkGiverSegments),
                prefix: nameof(WorkGiverSegments.GiverPrefix), finalizer: null);
            PatchWith(harmony, AccessTools.Method(typeof(ThoughtWorker), nameof(ThoughtWorker.CurrentState)), typeof(ThoughtTimer));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(StatWorker)).Where(m => m.Name == nameof(StatWorker.GetValue)))
                PatchWith(harmony, m, typeof(StatTimer));
        }

        private static void PatchWith(Harmony harmony, MethodBase method, Type patchClass, string prefix = "Prefix", string finalizer = "Finalizer")
        {
            harmony.Patch(method,
                prefix: prefix == null ? null : new HarmonyMethod(patchClass, prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(patchClass, finalizer));
            patched.Add(method);
        }

        public static void Detach(Harmony harmony)
        {
            foreach (var m in patched)
                harmony.Unpatch(m, HarmonyPatchType.All, harmony.Id);
            patched.Clear();
        }

        public static void SlotPrefix(MethodBase __originalMethod, out long __state)
        {
            // 1.6 runs some work on worker threads; only the main thread is measured.
            if (!UnityData.IsInMainThread)
            {
                __state = -2;
                return;
            }
            var slot = slotByMethod[__originalMethod];
            slot.Calls++;
            __state = slot.Depth++ == 0 ? Stopwatch.GetTimestamp() : -1;
        }

        public static void SlotFinalizer(MethodBase __originalMethod, long __state)
        {
            if (__state == -2)
                return;
            var slot = slotByMethod[__originalMethod];
            slot.Depth--;
            if (__state >= 0)
                slot.Elapsed += Stopwatch.GetTimestamp() - __state;
        }

        // Things tick their contents (e.g. pawn inventories) from inside DoTick; only the outermost call is timed.
        public static void DoTickPrefix(out long __state)
        {
            __state = thingDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;
        }

        /// <summary>The single most expensive thing tick since the last reset (for spike analysis).</summary>
        public static long MaxThingElapsed;
        public static Thing MaxThing;

        public static long[] SnapshotSlots()
        {
            var a = new long[Slots.Count];
            for (var i = 0; i < a.Length; i++)
                a[i] = Slots[i].Elapsed;
            return a;
        }

        public static void DoTickFinalizer(Thing __instance, long __state)
        {
            thingDepth--;
            if (__state < 0)
                return;
            var elapsed = Stopwatch.GetTimestamp() - __state;
            if (elapsed > MaxThingElapsed)
            {
                MaxThingElapsed = elapsed;
                MaxThing = __instance;
            }
            Add(ByCategory, Categorize(__instance), elapsed);
            if (__instance.def != null)
                Add(ByDef, __instance.def, elapsed);
        }

        private static class PawnTickTimer
        {
            public static void Prefix(out long __state) =>
                __state = pawnTickDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;

            public static void Finalizer(Pawn __instance, long __state)
            {
                pawnTickDepth--;
                if (__state >= 0)
                    Add(PawnParts, (Categorize(__instance), "every tick"), Stopwatch.GetTimestamp() - __state);
            }
        }

        private static class PawnIntervalTimer
        {
            public static void Prefix(out long __state) =>
                __state = pawnIntervalDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;

            public static void Finalizer(Pawn __instance, long __state)
            {
                pawnIntervalDepth--;
                if (__state >= 0)
                    Add(PawnParts, (Categorize(__instance), "interval"), Stopwatch.GetTimestamp() - __state);
            }
        }

        private static class JobTimer
        {
            // The job is captured before the tick because the driver may end it during the tick.
            public static void Prefix(Pawn_JobTracker __instance, out (long start, JobDef job) __state) =>
                __state = (Stopwatch.GetTimestamp(), __instance.curJob?.def);

            public static void Finalizer(Pawn_JobTracker __instance, (long start, JobDef job) __state)
            {
                var pawn = JobTrackerPawn(__instance);
                if (pawn != null && __state.job != null)
                    Add(JobDrivers, (Categorize(pawn), __state.job), Stopwatch.GetTimestamp() - __state.start);
            }
        }

        /// <summary>
        /// Times each job fail/end condition delegate separately, keyed by the method behind it. Diagnostic only: the
        /// conditions are evaluated here and then again by the original method (they are meant to be pure checks).
        /// </summary>
        private static class FailConditionTimer
        {
            private static readonly AccessTools.FieldRef<JobDriver, List<Func<JobCondition>>> GlobalFail =
                AccessTools.FieldRefAccess<JobDriver, List<Func<JobCondition>>>("globalFailConditions");
            private static readonly AccessTools.FieldRef<JobDriver, List<Toil>> Toils = AccessTools.FieldRefAccess<JobDriver, List<Toil>>("toils");
            private static readonly AccessTools.FieldRef<JobDriver, int> CurToilIndex = AccessTools.FieldRefAccess<JobDriver, int>("curToilIndex");

            public static void Prefix(JobDriver __instance)
            {
                if (!UnityData.IsInMainThread)
                    return;
                var global = GlobalFail(__instance);
                if (global != null)
                    foreach (var cond in global)
                        Time(cond, "global");
                var toils = Toils(__instance);
                var index = CurToilIndex(__instance);
                if (toils != null && index >= 0 && index < toils.Count && toils[index].endConditions != null)
                    foreach (var cond in toils[index].endConditions)
                        Time(cond, "toil");
            }

            private static void Time(Func<JobCondition> cond, string kind)
            {
                var start = Stopwatch.GetTimestamp();
                cond();
                var elapsed = Stopwatch.GetTimestamp() - start;
                // Generic wrappers such as ToilFailConditions.FailOn(Func<bool>) capture the real predicate in their
                // closure; label by that inner delegate when there is one.
                Delegate labelled = cond;
                var wrapped = false;
                if (cond.Target != null)
                    foreach (var f in cond.Target.GetType().GetFields())
                        if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(cond.Target) is Delegate inner)
                        {
                            labelled = inner;
                            wrapped = true;
                            break;
                        }
                var m = labelled.Method;
                var owner = m.DeclaringType?.DeclaringType ?? m.DeclaringType;
                Add(FailConditions, $"{kind}: {owner?.Name}.{m.Name}{(wrapped ? " (wrapped)" : "")}", elapsed);
            }

            public static void Finalizer() { }
        }

        /// <summary>
        /// Attributes the outermost GenClosest search to whoever started it: the work giver captured in the validator's
        /// closure when there is one (JobGiver_Work passes closures holding the scanner), otherwise the calling methods.
        /// </summary>
        private static class SearchTimer
        {
            private static int depth;

            public static void Prefix(Predicate<Thing> validator, out (long start, string key) __state)
            {
                if (!UnityData.IsInMainThread || depth++ > 0)
                {
                    __state = (-1, null);
                    return;
                }
                __state = (Stopwatch.GetTimestamp(), Describe(validator));
            }

            public static void Finalizer((long start, string key) __state)
            {
                if (!UnityData.IsInMainThread)
                    return;
                depth--;
                if (__state.start >= 0)
                    Add(Searches, __state.key, Stopwatch.GetTimestamp() - __state.start);
            }

            private static string Describe(Delegate validator)
            {
                // Walk the closure (and closures it captures) looking for the work giver behind this search.
                var target = validator?.Target;
                for (var hop = 0; target != null && hop < 3; hop++)
                {
                    object next = null;
                    foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var v = f.GetValue(target);
                        if (v is WorkGiver wg)
                            return "work giver: " + wg.def.defName;
                        if (v is WorkGiverDef wgd)
                            return "work giver: " + wgd.defName;
                        if (next == null && v != null && v.GetType().Name.Contains("DisplayClass"))
                            next = v;
                    }
                    target = next;
                }
                var frames = new StackTrace(2, false).GetFrames() ?? new StackFrame[0];
                var callers = frames.Select(fr => fr.GetMethod())
                    .Where(m => m?.DeclaringType != null && m.DeclaringType != typeof(GenClosest) && !m.Name.StartsWith("DMD<") &&
                                m.DeclaringType != typeof(SearchTimer))
                    .Take(2)
                    .Select(m => $"{(m.DeclaringType.DeclaringType ?? m.DeclaringType).Name}.{m.Name}");
                return "caller: " + string.Join(" <- ", callers);
            }
        }

        /// <summary>
        /// Splits the time inside a work search by work giver. JobGiver_Work calls PawnCanUseWorkGiver once at the start
        /// of each work giver it processes, so the time between two such calls (or until the search ends) belongs to the
        /// earlier work giver.
        /// </summary>
        private static class WorkGiverSegments
        {
            private static bool inSearch;
            private static Pawn searchPawn;
            private static WorkGiver current;
            private static long segmentStart;

            public static void SearchPrefix(Pawn pawn)
            {
                if (!UnityData.IsInMainThread || inSearch)
                    return;
                inSearch = true;
                searchPawn = pawn;
                current = null;
                segmentStart = Stopwatch.GetTimestamp();
            }

            public static void SearchFinalizer(Pawn pawn)
            {
                if (!inSearch || pawn != searchPawn)
                    return;
                Close();
                inSearch = false;
            }

            public static void GiverPrefix(WorkGiver giver)
            {
                if (!inSearch || !UnityData.IsInMainThread)
                    return;
                Close();
                current = giver;
            }

            private static void Close()
            {
                var now = Stopwatch.GetTimestamp();
                Add(WorkGiverTimes, (Categorize(searchPawn), current?.def.defName ?? "(setup)"), now - segmentStart);
                segmentStart = now;
            }
        }

        /// <summary>Times each thought worker's CurrentState by thought def (outermost only).</summary>
        private static class ThoughtTimer
        {
            private static int depth;

            public static void Prefix(out long __state)
            {
                __state = UnityData.IsInMainThread && depth++ == 0 ? Stopwatch.GetTimestamp() : -1;
                if (!UnityData.IsInMainThread)
                    __state = -2;
            }

            public static void Finalizer(ThoughtWorker __instance, long __state)
            {
                if (__state == -2)
                    return;
                depth--;
                if (__state >= 0)
                    Add(Thoughts, __instance.def?.defName ?? __instance.GetType().Name, Stopwatch.GetTimestamp() - __state);
            }
        }

        /// <summary>Splits work searches (JobGiver_Work) by pawn category and whether they found a job.</summary>
        private static class WorkSearchTimer
        {
            public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            public static void Postfix(Pawn pawn, ThinkResult __result, long __state)
            {
                if (UnityData.IsInMainThread && pawn != null)
                    Add(WorkSearches, (Categorize(pawn), __result.Job != null ? "found a job" : "found nothing"), Stopwatch.GetTimestamp() - __state);
            }
        }

        private static class StatTimer
        {
            public static void Prefix(out long __state)
            {
                if (!UnityData.IsInMainThread)
                {
                    __state = -2;
                    return;
                }
                __state = statDepth++ == 0 ? Stopwatch.GetTimestamp() : -1;
            }

            public static void Finalizer(StatWorker __instance, long __state)
            {
                if (__state == -2)
                    return;
                statDepth--;
                if (__state >= 0)
                    Add(Stats, WorkerStat(__instance), Stopwatch.GetTimestamp() - __state);
            }
        }

        private static void Add<TKey>(Dictionary<TKey, Acc> dict, TKey key, long elapsed)
        {
            if (!dict.TryGetValue(key, out var acc))
                dict[key] = acc = new Acc();
            acc.Elapsed += elapsed;
            acc.Calls++;
        }

        public static string Categorize(Thing t)
        {
            if (t is Pawn p)
            {
                var ofPlayer = p.Faction != null && p.Faction == Faction.OfPlayer;
                if (p.RaceProps.IsMechanoid)
                    return ofPlayer ? "Pawn: colony mech" : "Pawn: other mech";
                if (p.RaceProps.Animal)
                    return ofPlayer ? "Pawn: colony animal" : p.Faction == null ? "Pawn: wild animal" : "Pawn: other animal";
                if (ofPlayer)
                    return p.IsSlave ? "Pawn: slave" : "Pawn: colonist";
                if (p.IsPrisonerOfColony)
                    return "Pawn: prisoner";
                return "Pawn: other (visitors, raiders, entities)";
            }
            if (t is Plant) return "Plants";
            if (t is Corpse) return "Corpses";
            if (t is Filth) return "Filth";
            if (t is Blueprint || t is Frame) return "Blueprints/frames";
            if (t is Building) return "Buildings";
            if (t.def.category == ThingCategory.Item) return "Items";
            return "Other things";
        }
    }
}
