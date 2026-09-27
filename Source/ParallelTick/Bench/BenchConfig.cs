using System;
using System.Collections.Generic;
using System.IO;
using ParallelTick.Optimizations;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Reads ptbench.txt (key=value lines) from the save data folder. The file only exists in the
    /// isolated folder the bench script launches the game with, so normal play never triggers it.
    /// </summary>
    public static class BenchConfig
    {
        public static bool Active;
        public static string SaveName;

        /// <summary>"bench" = controlled tick measurement; "play" = real play at max speed with rendering (PlayComponent).</summary>
        public static string Mode = "bench";
        public static int PlaySeconds = 180;
        /// <summary>Play mode: true = optimizations as in normal play (settings defaults), false = all off.</summary>
        public static bool PlayOpts = true;
        /// <summary>Play mode: attach FrameProfiler and report where each frame's time goes.</summary>
        public static bool FrameProfile;
        /// <summary>Benchmark: ticks run per rendered frame (real play at Superfast runs ~4 per frame).</summary>
        public static int TicksPerFrame = 60;
        /// <summary>Play mode: per-frame simulation budget in ms (0 = from settings / vanilla).</summary>
        public static float TickBudgetMs;
        /// <summary>Play mode A/B: "opts" (all optimizations), an optimization key, or "budget:&lt;ms&gt;"; "" = none.</summary>
        public static string PlayAb = "";
        public static int PlayAbSeconds = 10;

        /// <summary>If set, the game is saved under this name (in the bench data folder) when the run ends.</summary>
        public static string SaveAs = "";

        /// <summary>Play mode: frames per second to save as JPGs for comparison videos (0 = off); see FrameRecorder.</summary>
        public static float RecordFps;

        /// <summary>mode=portrait: portrait.&lt;key&gt;=value lines (see PortraitComponent).</summary>
        public static readonly Dictionary<string, string> Portrait = new Dictionary<string, string>();
        public static int WarmupTicks = 2500;
        public static int MeasureTicks = 5000;
        public static bool Breakdown = true;
        public static bool Quit = true;
        public static int Seed = 12345;
        public static int TraceInterval;
        public static int RandTraceTicks;
        public static int DumpFrom = -1, DumpTo = -1;
        /// <summary>Mode per optimization key from "opt.&lt;key&gt;=off|on|verify" lines; unlisted optimizations are off.</summary>
        public static readonly Dictionary<string, OptMode> OptModes = new Dictionary<string, OptMode>();

        /// <summary>Optimization to A/B inside one process ("" = none), switching every AbBlockTicks during measurement.</summary>
        public static string AbTarget = "";
        public static int AbBlockTicks = 250;
        public static string Label = "";
        public static string ResultPath;

        public static void Load()
        {
            var path = Path.Combine(GenFilePaths.SaveDataFolderPath, "ptbench.txt");
            if (!File.Exists(path))
                return;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                var eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "save": SaveName = value; break;
                    case "mode": Mode = value.ToLowerInvariant(); break;
                    case "playseconds": PlaySeconds = int.Parse(value); break;
                    case "playopts": PlayOpts = bool.Parse(value); break;
                    case "frameprofile": FrameProfile = bool.Parse(value); break;
                    case "ticksperframe": TicksPerFrame = int.Parse(value); break;
                    case "tickbudget": TickBudgetMs = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "playab": PlayAb = value.ToLowerInvariant(); break;
                    case "playabseconds": PlayAbSeconds = int.Parse(value); break;
                    case "saveas": SaveAs = value; break;
                    case "record": RecordFps = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "warmup": WarmupTicks = int.Parse(value); break;
                    case "ticks": MeasureTicks = int.Parse(value); break;
                    case "breakdown": Breakdown = bool.Parse(value); break;
                    case "quit": Quit = bool.Parse(value); break;
                    case "seed": Seed = int.Parse(value); break;
                    case "trace": TraceInterval = int.Parse(value); break;
                    case "randtrace": RandTraceTicks = int.Parse(value); break;
                    case "dumpfrom": DumpFrom = int.Parse(value); break;
                    case "dumpto": DumpTo = int.Parse(value); break;
                    // Ablation targets keep their case (type and method names).
                    case "ab": AbTarget = value.StartsWith("ablate:") ? value : value.ToLowerInvariant(); break;
                    case "abblock": AbBlockTicks = int.Parse(value); break;
                    case "label": Label = value; break;
                    default:
                        if (key.StartsWith("portrait."))
                            Portrait[key.Substring("portrait.".Length)] = value;
                        else if (key.StartsWith("opt."))
                            OptModes[key.Substring(4)] = (OptMode)Enum.Parse(typeof(OptMode), value, ignoreCase: true);
                        else
                            Log.Warning($"[Free Performance] Unknown ptbench.txt key '{key}'");
                        break;
                }
            }

            // The A/B target must be patched in On mode; the benchmark flips its runtime switch. "all" = every optimization.
            if (AbTarget == "all" || AbTarget == "defaults")
            {
                foreach (var opt in OptimizationRegistry.All)
                    if (AbTarget == "all" || opt.DefaultOn)
                        OptModes[opt.Key] = OptMode.On;
            }
            else if (AbTarget != "" && !AbTarget.StartsWith("ablate:"))
            {
                OptModes[AbTarget] = OptMode.On;
            }

            ResultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "ptbench-result.txt");
            Active = !string.IsNullOrEmpty(SaveName);
        }
    }
}
