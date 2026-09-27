using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>Play-test only: counts errors and warnings logged while the test is running, with a few examples.</summary>
    public static class LogCounter
    {
        public static bool Counting;
        public static int Errors, Warnings;
        public static readonly List<string> Examples = new List<string>();
        private static readonly HashSet<string> seen = new HashSet<string>();

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(LogCounter), nameof(ErrorPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(LogCounter), nameof(WarningPrefix)));
        }

        public static void ErrorPrefix(string text) => Record(ref Errors, "ERROR", text);

        public static void WarningPrefix(string text) => Record(ref Warnings, "warning", text);

        private static void Record(ref int counter, string kind, string text)
        {
            if (!Counting)
                return;
            counter++;
            var firstLine = (text ?? "").Split('\n')[0];
            if (firstLine.Length > 200)
                firstLine = firstLine.Substring(0, 200);
            if (Examples.Count < 15 && seen.Add(firstLine))
                Examples.Add($"{kind}: {firstLine}");
        }
    }
}
