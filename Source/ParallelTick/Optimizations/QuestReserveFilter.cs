using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// Every off-map (world) pawn asks each tick whether it is suspended, which goes through WorldPawns.GetSituation. For
    /// a pawn that is not a faction leader, kidnapped or in a caravan that ends in QuestManager.IsReservedByAnyQuest and
    /// QuestUtility.IsBorrowedByAnyFaction, which call QuestPartReserves on every part of every active quest (and look
    /// for lend-colonists parts). Most quest part types don't override QuestPartReserves; the base returns false with no
    /// side effects. This keeps, per quest, the parts whose type does override it (and the lend-colonists parts), rebuilt
    /// when the quest's part list changes (List._version), and loops over only those, in the same order. Exact.
    /// </summary>
    public static class QuestReserveFilter
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "questreserve",
            Label = "Faster quest reservation checks  (exact)",
            Description = "Off-map pawns check every tick whether a quest reserves them, by asking every part of every " +
                          "active quest. Parts that can never reserve anyone are no longer asked. Same result.",
            Patch = Patch,
            Reset = Reset,
            ReportLines = Report,
        };

        private sealed class Filtered
        {
            public List<QuestPart> Parts;
            public int Version = -1;
            public QuestPart[] PawnReservers;
            public QuestPart_LendColonistsToFaction[] Lenders;
        }

        private static readonly Dictionary<Quest, Filtered> filtered = new Dictionary<Quest, Filtered>(RefEq<Quest>.Instance);
        private static readonly Dictionary<Type, bool> overrides = new Dictionary<Type, bool>();
        private static readonly AccessTools.FieldRef<Quest, List<QuestPart>> questParts = AccessTools.FieldRefAccess<Quest, List<QuestPart>>("parts");
        private static long reserveCalls, partsAsked, partsSkipped;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Quest), nameof(Quest.QuestReserves), new[] { typeof(Pawn) }),
                prefix: new HarmonyMethod(typeof(QuestReserveFilter), nameof(ReservesPrefix)));
            harmony.Patch(AccessTools.Method(typeof(QuestUtility), nameof(QuestUtility.IsBorrowedByAnyFaction)),
                prefix: new HarmonyMethod(typeof(QuestReserveFilter), nameof(BorrowedPrefix)));
        }

        private static void Reset()
        {
            filtered.Clear();
            reserveCalls = partsAsked = partsSkipped = 0;
            guardChecked = false;
            Info.Stats.Reset();
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  Quest.QuestReserves(Pawn) calls: {reserveCalls:N0}, parts asked: {partsAsked:N0}, skipped: {partsSkipped:N0}";
        }

        private static bool guardChecked, guardBlocked;

        private static bool Blocked()
        {
            if (guardChecked)
                return guardBlocked;
            guardChecked = true;
            var methods = new List<MethodBase>
            {
                AccessTools.Method(typeof(Quest), nameof(Quest.QuestReserves), new[] { typeof(Pawn) }),
                AccessTools.Method(typeof(QuestPart), nameof(QuestPart.QuestPartReserves), new[] { typeof(Pawn) }),
                AccessTools.Method(typeof(QuestUtility), nameof(QuestUtility.IsBorrowedByAnyFaction)),
                AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.Historical)),
            };
            foreach (var m in methods)
            {
                var owners = PatchGuard.ForeignOwners(m);
                if (owners != null && owners.Count > 0)
                {
                    Log.Message($"[Free Performance] Quest reservation shortcut stays off: {m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", owners)}.");
                    guardBlocked = true;
                }
            }
            return guardBlocked;
        }

        private static bool Overrides(Type type)
        {
            if (!overrides.TryGetValue(type, out var result))
                overrides[type] = result =
                    AccessTools.Method(type, nameof(QuestPart.QuestPartReserves), new[] { typeof(Pawn) })?.DeclaringType != typeof(QuestPart);
            return result;
        }

        private static Filtered For(Quest quest)
        {
            var parts = questParts(quest);
            if (!filtered.TryGetValue(quest, out var f))
                filtered[quest] = f = new Filtered();
            var version = parts == null ? -1 : ListVersion<QuestPart>.Of(parts);
            if (f.Parts != parts || f.Version != version)
            {
                f.Parts = parts;
                f.Version = version;
                f.PawnReservers = parts?.Where(p => p != null && Overrides(p.GetType())).ToArray() ?? Array.Empty<QuestPart>();
                f.Lenders = parts?.OfType<QuestPart_LendColonistsToFaction>().ToArray() ?? Array.Empty<QuestPart_LendColonistsToFaction>();
            }
            return f;
        }

        private static bool Use => (Info.Active || Info.Verifying) && UnityData.IsInMainThread && !Blocked();

        /// <summary>Quest.QuestReserves(Pawn): Historical -> false, else any part that reserves the pawn.</summary>
        public static bool ReservesPrefix(Quest __instance, Pawn p, ref bool __result)
        {
            if (!Use || questParts(__instance) == null)
                return true;
            reserveCalls++;
            if (__instance.Historical)
            {
                __result = false;
                return false;
            }
            var f = For(__instance);
            var result = false;
            var reservers = f.PawnReservers;
            for (var i = 0; i < reservers.Length; i++)
            {
                if (reservers[i].QuestPartReserves(p))
                {
                    result = true;
                    break;
                }
            }
            partsAsked += reservers.Length;
            partsSkipped += f.Parts.Count - reservers.Length;
            if (Info.Verifying)
            {
                // Vanilla loop over every part.
                var vanilla = f.Parts.Any(part => part.QuestPartReserves(p));
                Info.Stats.Checks++;
                if (vanilla != result)
                    Info.Stats.Mismatch(() => $"{__instance} / {p}: filtered {result}, vanilla {vanilla}");
                __result = vanilla;
                return false;
            }
            Info.Stats.Hits++;
            __result = result;
            return false;
        }

        /// <summary>QuestUtility.IsBorrowedByAnyFaction: an ongoing quest with a lend-colonists part that lent this pawn.</summary>
        public static bool BorrowedPrefix(Pawn pawn, ref bool __result)
        {
            if (!Use || Info.Verifying)
                return true;
            var quests = Find.QuestManager.ActiveQuestsListForReading;
            for (var i = 0; i < quests.Count; i++)
            {
                var quest = quests[i];
                if (quest.State != QuestState.Ongoing || questParts(quest) == null)
                    continue;
                var lenders = For(quest).Lenders;
                for (var j = 0; j < lenders.Length; j++)
                    if (lenders[j].LentColonistsListForReading.Contains(pawn))
                    {
                        __result = true;
                        return false;
                    }
            }
            __result = false;
            return false;
        }
    }
}
