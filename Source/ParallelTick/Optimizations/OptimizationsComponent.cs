using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>Clears per-game caches when a game is created or loaded and prunes them periodically.</summary>
    public class OptimizationsComponent : GameComponent
    {
        private const int PruneIntervalTicks = 2500;

        public OptimizationsComponent(Game game)
        {
            PawnVersions.Reset();
            foreach (var opt in OptimizationRegistry.All)
                opt.Reset();
        }

        public override void GameComponentTick()
        {
            var now = Find.TickManager.TicksGame;
            if (now % PruneIntervalTicks != 0)
                return;
            PawnVersions.Prune();
            foreach (var opt in OptimizationRegistry.All)
                if (opt.Mode != OptMode.Off)
                    opt.Prune(now);
        }
    }
}
