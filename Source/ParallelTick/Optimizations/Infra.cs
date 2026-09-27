using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>Which other mods' Harmony patches on a method matter to an optimization's reasoning.</summary>
    public static class PatchGuard
    {
        /// <summary>
        /// Patches known not to affect what the optimizations reason about (each one read in the other mod's code):
        /// - Performance Optimizer's "Faster GetComp methods replacement" transpiles methods that look up comps so they use
        ///   its cached lookup, which returns the same comp.
        /// - Minify Everything's ThingOwner.DoTick prefix only skips ticking the contents of minified things (a setting);
        ///   it never makes a tick do more, and the contents/settlement skips never skip a minified thing's contents.
        /// - Performance Optimizer's WindManagerTick transpiler returns early when plant sway is off in the options, before
        ///   the material loop the sway deferral replaces; with sway on the method runs as vanilla (and is deferred).
        /// </summary>
        public static bool Harmless(Patch patch)
        {
            var method = patch?.PatchMethod;
            var type = method?.DeclaringType?.FullName;
            if (type == null)
                return false;
            return type.StartsWith("PerformanceOptimizer.Optimization_FasterGetCompReplacement") ||
                   type == "PerformanceOptimizer.Optimization_WindManager_WindManagerTick" ||
                   type == "MinifyEverything.MinifyEverything" && method.Name == "ThingOwnerTickPrefix";
        }

        /// <summary>
        /// Owners of patches on the method other than this mod, ignoring harmless ones (see Harmless). With
        /// allowPostfixes, postfixes are ignored too: for a cache whose own postfix runs first (Priority.First), a later
        /// postfix sees and adjusts the same value it would after vanilla, as long as it doesn't ask whether the
        /// original ran (__runOriginal).
        /// </summary>
        public static List<string> ForeignOwners(System.Reflection.MethodBase method, bool allowPostfixes = false, Func<Patch, bool> accepts = null)
        {
            var info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null)
                return new List<string>();
            var known = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).ToList();
            bool Ok(Patch p) => Harmless(p) || accepts?.Invoke(p) == true || allowPostfixes && info.Postfixes.Contains(p) &&
                                p.PatchMethod.GetParameters().All(a => a.Name != "__runOriginal");
            return info.Owners.Where(o => o != ParallelTickMod.Id)
                .Where(o => !(known.Any(p => p.owner == o) && known.Where(p => p.owner == o).All(Ok)))
                .Distinct().ToList();
        }
    }

    /// <summary>
    /// Vanilla Expanded Framework's changes to StatWorker.GetValueUnfinalized, read in its code: a transpiler that applies
    /// stat factors from worn and equipped gear (which pawn stat caches already track), and a postfix that applies
    /// animal-gene offsets to pawns registered in its own table (WorldComponent_AnimalGenes). The stat caches accept both
    /// and never cache a pawn that is in that table.
    /// </summary>
    public static class VefStats
    {
        public static bool Accepts(Patch patch)
        {
            var type = patch?.PatchMethod?.DeclaringType?.FullName;
            return type == "VEF.Apparels.VanillaExpandedFramework_StatWorker_GetValueUnfinalized_Transpiler" ||
                   type == "VEF.AnimalGenes.VEF_AnimalGenes_StatWorker_GetValueUnfinalized_Patch";
        }

        private static bool resolved;
        private static System.Reflection.FieldInfo instanceField, tableField;

        /// <summary>True when VEF adjusts this thing's stats from its animal-gene table (then it must not be cached).</summary>
        public static bool HasAnimalGenes(object thing)
        {
            if (!resolved)
            {
                resolved = true;
                var type = AccessTools.TypeByName("VEF.AnimalGenes.WorldComponent_AnimalGenes");
                instanceField = type == null ? null : AccessTools.Field(type, "Instance");
                tableField = type == null ? null : AccessTools.Field(type, "pawnToCompAnimalGenes");
            }
            if (instanceField == null || tableField == null)
                return false;
            var instance = instanceField.GetValue(null);
            return instance != null && tableField.GetValue(instance) is System.Collections.IDictionary table && table.Contains(thing);
        }
    }

    /// <summary>What the stat caches rely on: the stat system's own methods, and stats built only from the game's own parts.</summary>
    public static class StatGuard
    {
        /// <summary>
        /// The stat pipeline (GetStatValue -> StatWorker.GetValue -> GetValueUnfinalized / FinalizeValue), including
        /// the given stats' own worker overrides. A patch on any of them can add inputs a cache doesn't track.
        /// </summary>
        public static IEnumerable<System.Reflection.MethodBase> Methods(IEnumerable<RimWorld.StatDef> stats)
        {
            yield return AccessTools.Method(typeof(RimWorld.StatExtension), nameof(RimWorld.StatExtension.GetStatValue));
            string[] names = { "GetValue", "GetValueUnfinalized", "FinalizeValue", "GetBaseValueFor" };
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(RimWorld.StatWorker)).Where(m => names.Contains(m.Name)))
                yield return m;
            foreach (var stat in stats)
            {
                var type = stat?.Worker?.GetType();
                if (type == null || type == typeof(RimWorld.StatWorker))
                    continue;
                foreach (var m in AccessTools.GetDeclaredMethods(type).Where(m => names.Contains(m.Name)))
                    yield return m;
            }
        }

        /// <summary>Why a stat can't be cached (its worker or a part comes from another mod, with inputs we can't see), or null.</summary>
        public static string ForeignParts(RimWorld.StatDef stat)
        {
            if (stat == null)
                return null;
            var vanilla = typeof(RimWorld.StatWorker).Assembly;
            if (stat.workerClass != null && stat.workerClass.Assembly != vanilla)
                return $"{stat.defName} uses {stat.workerClass.FullName}";
            var part = stat.parts?.FirstOrDefault(p => p != null && p.GetType().Assembly != vanilla);
            return part == null ? null : $"{stat.defName} has part {part.GetType().FullName}";
        }
    }

    /// <summary>
    /// Transpiler helper: rewrites a method only when it has exactly as many matching instructions as the unmodified
    /// game. Otherwise another mod has probably changed that method, so it is left untouched and the optimization's
    /// rewrite is skipped (an info line in the log, not an error).
    /// </summary>
    public static class SafeTranspile
    {
        public static List<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, int expected, string what,
            Func<CodeInstruction, bool> match, Func<CodeInstruction, IEnumerable<CodeInstruction>> replace)
        {
            var list = instructions.ToList();
            var sites = list.Count(match);
            if (sites != expected)
            {
                Log.Message($"[Free Performance] {what}: found {sites} matching places instead of {expected} " +
                            "(another mod may have changed this code); left as is.");
                return list;
            }
            var result = new List<CodeInstruction>(list.Count + expected);
            foreach (var ins in list)
            {
                if (match(ins))
                    result.AddRange(replace(ins));
                else
                    result.Add(ins);
            }
            return result;
        }
    }

    /// <summary>
    /// Identity comparer for caches keyed by game objects: Thing.GetHashCode changes when an ID is assigned and
    /// Pawn.Equals compares defs and IDs, neither of which a cache key should depend on.
    /// </summary>
    public sealed class RefEq<T> : IEqualityComparer<T> where T : class
    {
        public static readonly RefEq<T> Instance = new RefEq<T>();
        public bool Equals(T a, T b) => ReferenceEquals(a, b);
        public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
    }

    /// <summary>Verify-mode helper: the values of an object's instance fields, to check a call left it unchanged.</summary>
    public static class ShallowSnapshot
    {
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo[]> fields = new Dictionary<System.Type, System.Reflection.FieldInfo[]>();

        private static System.Reflection.FieldInfo[] FieldsOf(System.Type t)
        {
            if (!fields.TryGetValue(t, out var f))
            {
                var list = new List<System.Reflection.FieldInfo>();
                for (var type = t; type != null && type != typeof(object); type = type.BaseType)
                    foreach (var fi in AccessTools.GetDeclaredFields(type))
                        if (!fi.IsStatic)
                            list.Add(fi);
                fields[t] = f = list.ToArray();
            }
            return f;
        }

        public static object[] Take(object o)
        {
            var f = FieldsOf(o.GetType());
            var values = new object[f.Length * 2];
            for (var i = 0; i < f.Length; i++)
            {
                var v = f[i].GetValue(o);
                values[2 * i] = v;
                values[2 * i + 1] = (v as System.Collections.ICollection)?.Count;
            }
            return values;
        }

        /// <summary>Name of the first field that differs (value types and strings by value, objects by reference), or null.</summary>
        public static string Diff(object o, object[] before, System.Func<string, bool> ignore = null)
        {
            var f = FieldsOf(o.GetType());
            var now = Take(o);
            for (var i = 0; i < now.Length; i++)
            {
                var a = before[i];
                var b = now[i];
                var same = a == null || a.GetType().IsValueType || a is string ? Equals(a, b) : ReferenceEquals(a, b);
                if (!same && (ignore == null || !ignore(f[i / 2].Name)))
                    return $"{f[i / 2].Name}{(i % 2 == 1 ? ".Count" : "")}: {a} -> {b}";
            }
            return null;
        }
    }

    /// <summary>
    /// Whether a map (or its world parent) has an active GameCondition_BloodRain, looked up once per map per tick.
    /// BloodRainUtility.ExposedToBloodRain needs one that has run for more than 2000 ticks, so a condition started later
    /// in the same tick cannot change the answer, and a removed one only makes the real check run once more.
    /// </summary>
    public static class BloodRainCache
    {
        private static Verse.Map map;
        private static int tick = -1;
        private static bool present;

        public static bool Possible(Verse.Map m)
        {
            if (m == null)
                return true;
            var now = Verse.Find.TickManager.TicksGame;
            if (m != map || now != tick)
            {
                map = m;
                tick = now;
                present = m.gameConditionManager.GetActiveCondition<RimWorld.GameCondition_BloodRain>() != null;
            }
            return present;
        }

        public static void Reset()
        {
            map = null;
            tick = -1;
        }
    }

    /// <summary>
    /// List&lt;T&gt;._version: every mutating List method (Add, Insert, Remove*, Clear, the indexer setter, Sort, Reverse)
    /// increments it, so (list reference, version) identifies the list's contents.
    /// </summary>
    public static class ListVersion<T>
    {
        private static readonly AccessTools.FieldRef<List<T>, int> version = AccessTools.FieldRefAccess<List<T>, int>("_version");

        public static int Of(List<T> list) => version(list);
    }
}
