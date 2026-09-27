using RimWorld;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// testcomp=inert|disrupt (bench and play modes only): adds a ticking comp to every pawn race, standing in for mods
    /// like Facial Animation that do this, to test the idle-pawn skip with comps it doesn't know.
    /// inert: keeps a per-tick counter of its own (changes nothing else).
    /// disrupt: also staggers its pawn now and then, so the skip has to notice the change after the comps and run the
    /// rest of the tick as vanilla.
    /// </summary>
    public class CompBenchTest : ThingComp
    {
        public int Ticks;

        public override void CompTick()
        {
            Ticks++;
            if (BenchConfig.TestComp != "disrupt" || !(parent is Pawn pawn) || !pawn.Spawned || pawn.Dead)
                return;
            if ((Find.TickManager.TicksGame + pawn.thingIDNumber) % 97 == 0)
                pawn.stances?.stagger?.StaggerFor(20);
        }
    }

    public static class BenchTestComp
    {
        public static int Added;

        /// <summary>Called at startup (defs are loaded); pawns get the comp when they are loaded from the save.</summary>
        public static void Apply()
        {
            if (BenchConfig.TestComp == "")
                return;
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.race == null)
                    continue;
                def.comps.Add(new CompProperties { compClass = typeof(CompBenchTest) });
                Added++;
            }
            Log.Message($"[Free Performance] Test comp ({BenchConfig.TestComp}) added to {Added} pawn races.");
        }
    }
}
