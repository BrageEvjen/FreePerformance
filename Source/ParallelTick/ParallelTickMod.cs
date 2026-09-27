using HarmonyLib;
using ParallelTick.Bench;
using ParallelTick.Optimizations;
using RimWorld;
using Verse;

namespace ParallelTick
{
    [StaticConstructorOnStartup]
    public static class ParallelTickMod
    {
        public const string Id = "brage.paralleltick";
        public static readonly Harmony Harmony = new Harmony(Id);

        static ParallelTickMod()
        {
            BenchConfig.Load();
            SaveCompat.Patch(Harmony);
            if (!BenchConfig.Active)
            {
                // Normal play: verified optimizations are always patched and switched on/off by the settings at runtime.
                foreach (var opt in OptimizationRegistry.All)
                    opt.Apply(Harmony, OptMode.On);
                ParallelTickModEntry.Settings.ApplyRuntime();
                return;
            }

            if (BenchConfig.Mode == "play")
            {
                // Real-play test: optimizations exactly as a player would have them (default settings), or all off.
                foreach (var opt in OptimizationRegistry.All)
                    opt.Apply(Harmony, BenchConfig.PlayOpts || BenchConfig.OptModes.TryGetValue(opt.Key, out var m) && m != OptMode.Off
                        ? OptMode.On : OptMode.Off);
                ParallelTickModEntry.Settings.ApplyRuntime();
                // Explicit opt.<key>=on|off lines override the settings defaults in play tests.
                foreach (var kv in BenchConfig.OptModes)
                    OptimizationRegistry.Get(kv.Key).Enabled = kv.Value != OptMode.Off;
                // Test-only: a larger frame budget (more ticks per frame, fewer frames); not offered to players.
                if (BenchConfig.TickBudgetMs > 0 || BenchConfig.PlayAb.StartsWith("budget:"))
                    TickBudget.Patch(Harmony);
                if (BenchConfig.TickBudgetMs > 0)
                    TickBudget.BudgetMs = BenchConfig.TickBudgetMs;
                LogCounter.Apply(Harmony);
                if (BenchConfig.FrameProfile)
                    FrameProfiler.Attach(Harmony);
                Log.Message($"[Free Performance] Play test: save '{BenchConfig.SaveName}', {BenchConfig.PlaySeconds} s, optimizations {(BenchConfig.PlayOpts ? "on" : "off")}");
            }
            else
            {
                // Benchmark: ptbench.txt decides each optimization's mode; player settings are ignored.
                foreach (var opt in OptimizationRegistry.All)
                    opt.Apply(Harmony, BenchConfig.OptModes.TryGetValue(opt.Key, out var mode) ? mode : OptMode.Off);
                if (BenchConfig.AbTarget.StartsWith("ablate:"))
                    Ablation.Apply(Harmony, BenchConfig.AbTarget.Substring("ablate:".Length));
                DeterminismShims.Apply(Harmony);
                Log.Message($"[Free Performance] Benchmark mode: save '{BenchConfig.SaveName}', " +
                            $"warmup {BenchConfig.WarmupTicks}, measure {BenchConfig.MeasureTicks}, seed {BenchConfig.Seed}");
            }
            Harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.Init)),
                postfix: new HarmonyMethod(typeof(BenchLauncher), nameof(BenchLauncher.MainMenuInit_Postfix)));
            Harmony.Patch(AccessTools.Method(typeof(Autosaver), nameof(Autosaver.AutosaverTick)),
                prefix: new HarmonyMethod(typeof(BenchLauncher), nameof(BenchLauncher.SkipAutosave_Prefix)));
        }
    }
}
