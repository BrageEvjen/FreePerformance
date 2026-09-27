using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Five alerts build their list of culprits from pawn lists that walk every container on every map (~2.5 ms each,
    /// one alert per frame, so a spike every few frames): low baby food, abandoned baby, cube withdrawal, ghoul
    /// hypothermia, starving animals. Each can only list a pawn with a specific property (a baby, in cube withdrawal, a
    /// ghoul, starving). This keeps a registry of every Pawn object created (a postfix on the Pawn constructor, weak
    /// references), and when no live pawn anywhere has that property the vanilla list would come out empty: the alert's
    /// result is set exactly as vanilla leaves it for "none found", without the walk. Any doubt (a pawn whose life stage
    /// is not yet computed, an exception) runs vanilla. UI only; exact.
    /// </summary>
    public static class AlertEarlyOut
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "alertskip",
            Label = "Skip alert searches that can't find anything  (exact)",
            Description = "A few alerts search every pawn on the map, including ones carried or in containers, for babies, " +
                          "ghouls, starving animals or cube withdrawal. When no such pawn exists anywhere the search is skipped.",
            Patch = Patch,
            Guarded = () => new MethodBase[]
            {
                AccessTools.Method(typeof(Alert_LowBabyFood), "LowBabyFoodNutrition"),
                AccessTools.Method(typeof(Alert_AbandonedBaby), "AbandonedBabies"),
                AccessTools.PropertyGetter(typeof(Alert_CubeWithdrawal), "Withdrawal"),
                AccessTools.PropertyGetter(typeof(Alert_GhoulHypothermia), "HypothermiaDangerGhouls"),
                AccessTools.PropertyGetter(typeof(Alert_StarvationAnimals), "StarvingAnimals"),
            },
            Reset = () => Info.Stats.Reset(),
            ReportLines = Report,
        };

        private static readonly List<System.WeakReference<Pawn>> registry = new List<System.WeakReference<Pawn>>();
        private static readonly object registryLock = new object();
        private static bool registryComplete;

        private static readonly AccessTools.FieldRef<Pawn_AgeTracker, int> cachedLifeStageIndex = AccessTools.FieldRefAccess<Pawn_AgeTracker, int>("cachedLifeStageIndex");
        private static readonly Func<ThingWithComps, bool> inCubeWithdrawal =
            AccessTools.MethodDelegate<Func<ThingWithComps, bool>>(AccessTools.Method(typeof(Alert_CubeWithdrawal), "InWithdrawal"));
        private static readonly AccessTools.FieldRef<Alert_GhoulHypothermia, List<Pawn>> ghoulResult = AccessTools.FieldRefAccess<Alert_GhoulHypothermia, List<Pawn>>("hypothermiaDangerGhoulsResult");
        private static readonly AccessTools.FieldRef<Alert_StarvationAnimals, List<Pawn>> starvingResult = AccessTools.FieldRefAccess<Alert_StarvationAnimals, List<Pawn>>("starvingAnimalsResult");
        private static readonly AccessTools.FieldRef<Alert_CubeWithdrawal, List<Pawn>> withdrawalResult = AccessTools.FieldRefAccess<Alert_CubeWithdrawal, List<Pawn>>("inWithdrawal");
        private static readonly AccessTools.FieldRef<List<Pawn>> tmpAllBabies = AccessTools.StaticFieldRefAccess<List<Pawn>>(AccessTools.Field(typeof(Alert_AbandonedBaby), "tmpAllBabiesList"));
        private static readonly AccessTools.FieldRef<List<Pawn>> tmpAbandonedBabies = AccessTools.StaticFieldRefAccess<List<Pawn>>(AccessTools.Field(typeof(Alert_AbandonedBaby), "tmpAbandonedBabiesList"));

        private static long skipped, ran;
        private static bool expectEmpty;

        public static void VerifyListPostfix(List<Pawn> __result, System.Reflection.MethodBase __originalMethod)
        {
            if (!expectEmpty)
                return;
            expectEmpty = false;
            Info.Stats.Checks++;
            if (__result != null && __result.Count > 0)
                Info.Stats.Mismatch(() => $"{__originalMethod.DeclaringType?.Name}: predicted no candidates, vanilla found {__result.Count}");
        }

        public static void VerifyBoolPostfix(bool __result, System.Reflection.MethodBase __originalMethod)
        {
            if (!expectEmpty)
                return;
            expectEmpty = false;
            Info.Stats.Checks++;
            if (__result)
                Info.Stats.Mismatch(() => $"{__originalMethod.DeclaringType?.Name}: predicted no candidates, vanilla alert active");
        }

        private static void Patch(Harmony harmony)
        {
            // Must see every pawn ever made; optimizations are patched at startup, before any game exists.
            harmony.Patch(AccessTools.Constructor(typeof(Pawn), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(PawnCreated)));
            registryComplete = Current.Game == null;
            var verifyList = new HarmonyMethod(typeof(AlertEarlyOut), nameof(VerifyListPostfix));
            harmony.Patch(AccessTools.Method(typeof(Alert_LowBabyFood), "LowBabyFoodNutrition"),
                prefix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(LowBabyFoodPrefix)),
                postfix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(VerifyBoolPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Alert_AbandonedBaby), "AbandonedBabies"),
                prefix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(AbandonedBabiesPrefix)), postfix: verifyList);
            harmony.Patch(AccessTools.PropertyGetter(typeof(Alert_CubeWithdrawal), "Withdrawal"),
                prefix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(WithdrawalPrefix)), postfix: verifyList);
            harmony.Patch(AccessTools.PropertyGetter(typeof(Alert_GhoulHypothermia), "HypothermiaDangerGhouls"),
                prefix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(GhoulPrefix)), postfix: verifyList);
            harmony.Patch(AccessTools.PropertyGetter(typeof(Alert_StarvationAnimals), "StarvingAnimals"),
                prefix: new HarmonyMethod(typeof(AlertEarlyOut), nameof(StarvingPrefix)), postfix: verifyList);
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  alert searches skipped: {skipped:N0}, run: {ran:N0}, pawns registered: {registry.Count:N0}";
        }

        public static void PawnCreated(Pawn __instance)
        {
            lock (registryLock)
                registry.Add(new System.WeakReference<Pawn>(__instance));
        }

        private enum Answer { None, Some, Unsure }

        /// <summary>None: no live pawn anywhere matches. Prunes collected pawns as it goes.</summary>
        private static Answer AnyPawn(Func<Pawn, Answer> test)
        {
            if (!registryComplete)
                return Answer.Unsure;
            var result = Answer.None;
            lock (registryLock)
            {
                for (var i = registry.Count - 1; i >= 0; i--)
                {
                    if (!registry[i].TryGetTarget(out var p))
                    {
                        registry[i] = registry[registry.Count - 1];
                        registry.RemoveAt(registry.Count - 1);
                        continue;
                    }
                    Answer a;
                    try
                    {
                        a = test(p);
                    }
                    catch
                    {
                        a = Answer.Unsure;
                    }
                    if (a == Answer.Some)
                        return Answer.Some;
                    if (a == Answer.Unsure)
                        result = Answer.Unsure;
                }
            }
            return result;
        }

        /// <summary>ChildcareUtility.CanSuckle needs a live humanlike in a baby life stage.</summary>
        private static Answer MaybeBaby(Pawn p)
        {
            if (p.health == null || p.Dead || p.RaceProps == null || !p.RaceProps.Humanlike)
                return Answer.None;
            var age = p.ageTracker;
            if (age == null)
                return Answer.None; // DevelopmentalStage is Adult without an age tracker
            if (cachedLifeStageIndex(age) < 0)
                return Answer.Unsure; // reading it would compute it
            return p.DevelopmentalStage.Baby() ? Answer.Some : Answer.None;
        }

        private static bool Skip(Func<Pawn, Answer> test)
        {
            if (!Info.Active && !Info.Verifying || !UnityData.IsInMainThread)
                return false;
            if (AnyPawn(test) != Answer.None)
            {
                ran++;
                return false;
            }
            if (Info.Verifying)
            {
                expectEmpty = true;
                return false;
            }
            skipped++;
            Info.Stats.Hits++;
            return true;
        }

        public static bool LowBabyFoodPrefix(List<Pawn> lowFoodBabiesOut, ref float? babyFoodNutrition, ref bool __result)
        {
            if (!ModsConfig.BiotechActive || !Skip(MaybeBaby))
                return true;
            // Vanilla with no babies: every map clears the list and finds none; then null and false.
            if (Find.Maps.Count > 0)
                lowFoodBabiesOut.Clear();
            babyFoodNutrition = null;
            __result = false;
            return false;
        }

        public static bool AbandonedBabiesPrefix(ref List<Pawn> __result)
        {
            if (!ModsConfig.BiotechActive || !Skip(MaybeBaby))
                return true;
            tmpAllBabies().Clear();
            tmpAbandonedBabies().Clear();
            __result = tmpAbandonedBabies();
            return false;
        }

        public static bool WithdrawalPrefix(Alert_CubeWithdrawal __instance, ref List<Pawn> __result)
        {
            if (!Skip(p => p.health != null && !p.Dead && inCubeWithdrawal(p) ? Answer.Some : Answer.None))
                return true;
            withdrawalResult(__instance).Clear();
            __result = withdrawalResult(__instance);
            return false;
        }

        public static bool GhoulPrefix(Alert_GhoulHypothermia __instance, ref List<Pawn> __result)
        {
            if (!Skip(p => p.health != null && !p.Dead && p.IsGhoul ? Answer.Some : Answer.None))
                return true;
            ghoulResult(__instance).Clear();
            __result = ghoulResult(__instance);
            return false;
        }

        public static bool StarvingPrefix(Alert_StarvationAnimals __instance, ref List<Pawn> __result)
        {
            // Vanilla lists a pawn only if it starved for over 30000 ticks, or over 5000 while pregnant.
            if (!Skip(p => p.health != null && !p.Dead && p.needs?.food != null && p.needs.food.TicksStarving > 5000 ? Answer.Some : Answer.None))
                return true;
            starvingResult(__instance).Clear();
            __result = starvingResult(__instance);
            return false;
        }
    }
}
