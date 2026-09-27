using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// WorkGiver_Merge.JobOnThing is evaluated for every partial stack in storage during a work search, and for each one
    /// it walks every thing held by that stack's storage group looking for a stack it CanStackWith. That is
    /// partial stacks x stored items per search (~10 ms in the benchmark save).
    ///
    /// Every CanStackWith implementation (Thing, ThingWithComps, MinifiedThing, Book) requires the same def, so only
    /// same-def things can ever match. This transpiles the single ISlotGroup.HeldThings call in JobOnThing into a lookup
    /// that returns just the same-def things, in the original order, from an index built once per storage group per
    /// work search. Storage cannot change during a search (it only evaluates), so the first valid partner, and the job,
    /// are the same as vanilla. Outside JobGiver_Work searches the vanilla enumeration is used. Exact.
    /// </summary>
    public static class MergeIndex
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "mergeindex",
            Label = "Faster 'merge stacks' search  (exact)",
            Description = "When looking for stacks to merge, only items of the same type in the stockpile are checked, " +
                          "instead of every stored item for every partial stack. Same result.",
            Patch = Patch,
            // Only same-def things are looked at because every CanStackWith in the game requires the same def.
            Guarded = () => new MethodBase[] { AccessTools.Method(typeof(WorkGiver_Merge), nameof(WorkGiver_Merge.JobOnThing)) }
                .Concat(new[] { typeof(Thing) }.Concat(typeof(Thing).AllSubclasses())
                    .Select(t => AccessTools.DeclaredMethod(t, nameof(Thing.CanStackWith)))),
            BlockReason = () => typeof(Thing).AllSubclasses()
                .Where(t => t.Assembly != typeof(Thing).Assembly && AccessTools.DeclaredMethod(t, nameof(Thing.CanStackWith)) != null)
                .Select(t => $"{t.FullName} ({t.Assembly.GetName().Name}) has its own CanStackWith").FirstOrDefault(),
            Reset = Reset,
        };

        private static bool inWorkSearch;
        private static readonly Dictionary<ISlotGroup, Dictionary<ThingDef, List<Thing>>> index =
            new Dictionary<ISlotGroup, Dictionary<ThingDef, List<Thing>>>();
        private static readonly List<Thing> Empty = new List<Thing>();

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_Merge), nameof(WorkGiver_Merge.JobOnThing)),
                transpiler: new HarmonyMethod(typeof(MergeIndex), nameof(Transpiler)));
            harmony.Patch(AccessTools.Method(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage)),
                prefix: new HarmonyMethod(typeof(MergeIndex), nameof(SearchStart)),
                finalizer: new HarmonyMethod(typeof(MergeIndex), nameof(SearchEnd)));
        }

        private static void Reset()
        {
            index.Clear();
            inWorkSearch = false;
            Info.Stats.Reset();
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getHeld = AccessTools.PropertyGetter(typeof(ISlotGroup), nameof(ISlotGroup.HeldThings));
            var helper = AccessTools.Method(typeof(MergeIndex), nameof(HeldThingsFor));
            // Stack: [slotGroup] -> push t (arg 2 of the instance method JobOnThing(pawn, t, forced)) -> helper.
            return SafeTranspile.Replace(instructions, 1, "Merge search index (HeldThings in WorkGiver_Merge.JobOnThing)",
                ins => ins.Calls(getHeld),
                ins => new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_2).MoveLabelsFrom(ins).MoveBlocksFrom(ins),
                    new CodeInstruction(OpCodes.Call, helper),
                });
        }

        public static void SearchStart()
        {
            if (!UnityData.IsInMainThread)
                return;
            index.Clear();
            inWorkSearch = true;
        }

        public static void SearchEnd()
        {
            if (UnityData.IsInMainThread)
                inWorkSearch = false;
        }

        public static IEnumerable<Thing> HeldThingsFor(ISlotGroup group, Thing t)
        {
            if (!inWorkSearch || !UnityData.IsInMainThread || !Info.Active && !Info.Verifying)
                return group.HeldThings;

            if (Info.Verifying)
            {
                // Vanilla list; check the assumption that only same-def things can stack.
                Info.Stats.Checks++;
                foreach (var h in group.HeldThings)
                    if (h.def != t.def && h.CanStackWith(t))
                        Info.Stats.Mismatch(() => $"{h.LabelShort} ({h.def.defName}) can stack with {t.LabelShort} ({t.def.defName})");
                return group.HeldThings;
            }

            if (!index.TryGetValue(group, out var byDef))
            {
                Info.Stats.Misses++;
                byDef = new Dictionary<ThingDef, List<Thing>>();
                foreach (var h in group.HeldThings)
                {
                    if (!byDef.TryGetValue(h.def, out var list))
                        byDef[h.def] = list = new List<Thing>();
                    list.Add(h);
                }
                index[group] = byDef;
            }
            else
            {
                Info.Stats.Hits++;
            }
            return byDef.TryGetValue(t.def, out var same) ? same : Empty;
        }
    }
}
