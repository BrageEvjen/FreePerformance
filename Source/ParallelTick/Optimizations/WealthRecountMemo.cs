using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Every 5000 ticks the colony's wealth is recounted (WealthWatcher.ForceRecount): the market value of every player
    /// building, item and floor. In a big base that is thousands of walls, conduits and floors, and for each one
    /// StatWorker_MarketValue.CalculatedBaseMarketValue(def, stuff) works out the value from the cost list, the work to
    /// build and the stuff's value — the same answer for every granite wall. That shows as a stutter.
    ///
    /// CalculatedBaseMarketValue is static and reads only its two arguments and game data; nothing else runs while the
    /// recount does, so within one recount each (def, stuff) pair is computed once and reused. The memo is thrown away
    /// when the recount ends. Exact.
    /// </summary>
    public static class WealthRecountMemo
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "wealthmemo",
            Label = "Faster colony wealth recount  (exact)",
            Description = "Every ~80 seconds of game time the colony's wealth is recounted, working out the value of every wall, " +
                          "conduit and floor one by one. Now each kind (e.g. granite wall) is worked out once per recount. Same result.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private static int depth;
        private static long recounts;
        private static readonly Dictionary<(BuildableDef, ThingDef), float> memo = new Dictionary<(BuildableDef, ThingDef), float>();

        private static MethodInfo Recount => AccessTools.Method(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount));
        private static MethodInfo BaseValue => AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculatedBaseMarketValue));

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(Recount,
                prefix: new HarmonyMethod(typeof(WealthRecountMemo), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(WealthRecountMemo), nameof(Exit)));
            harmony.Patch(BaseValue,
                prefix: new HarmonyMethod(typeof(WealthRecountMemo), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(WealthRecountMemo), nameof(Postfix)));
        }

        private static void Reset()
        {
            depth = 0;
            memo.Clear();
            recounts = 0;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  recounts: {recounts:N0}, base values computed: {Info.Stats.Misses:N0}, reused: {Info.Stats.Hits:N0}";
        }

        private static bool guardChecked, guardBlocked;

        /// <summary>
        /// Another mod patching these could make the base value depend on which thing is being valued (e.g. by stashing
        /// the request's thing in a prefix); then reuse across things would be wrong, so the memo stays off.
        /// CalculableRecipe is not on the list: it takes only the def, and Performance Optimizer caches it per def.
        /// </summary>
        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            var methods = new List<MethodBase>
            {
                Recount,
                BaseValue,
                AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.GetValueUnfinalized)),
            };
            foreach (var m in methods)
            {
                var owners = PatchGuard.ForeignOwners(m);
                if (m == null || owners != null && owners.Count > 0)
                {
                    Log.Message($"[Free Performance] Wealth recount memo stays off: {m?.DeclaringType?.Name}.{m?.Name} is " +
                                (m == null ? "missing." : $"patched by {string.Join(", ", owners)}."));
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        public static void Enter()
        {
            if (!UnityData.IsInMainThread)
                return;
            if (depth++ == 0)
                recounts++;
        }

        public static void Exit()
        {
            if (!UnityData.IsInMainThread)
                return;
            if (--depth <= 0)
            {
                depth = 0;
                memo.Clear();
            }
        }

        public static bool Prefix(BuildableDef def, ThingDef stuffDef, ref float __result, out bool __state)
        {
            __state = false;
            if (depth <= 0 || def == null || !Info.Active && !Info.Verifying || !UnityData.IsInMainThread || Blocked())
                return true;
            __state = true;
            if (Info.Active && memo.TryGetValue((def, stuffDef), out var value))
            {
                Info.Stats.Hits++;
                __result = value;
                __state = false;
                return false;
            }
            return true;
        }

        public static void Postfix(BuildableDef def, ThingDef stuffDef, float __result, bool __state)
        {
            if (!__state || depth <= 0)
                return;
            if (Info.Verifying && memo.TryGetValue((def, stuffDef), out var saved))
            {
                Info.Stats.Checks++;
                if (saved != __result)
                    Info.Stats.Mismatch(() => $"{def.defName} / {stuffDef?.defName ?? "no stuff"}: memo {saved:R}, vanilla {__result:R}");
                return;
            }
            Info.Stats.Misses++;
            memo[(def, stuffDef)] = __result;
        }
    }
}
