using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ParallelTick.Optimizations;
using RimWorld;
using UnityEngine;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Real-play test ("mode=play"): loads the save, plays it at Superfast with normal rendering for PlaySeconds of real
    /// time, and reports ticks per second, frame times and every error/warning logged meanwhile. Unlike the benchmark,
    /// this includes rendering and uses the optimizations exactly as a player gets them.
    /// </summary>
    public class PlayComponent : GameComponent
    {
        private const int SettleFrames = 60;

        private int framesWaited, startTick;
        private bool running, done;
        private readonly Stopwatch wall = new Stopwatch();
        private readonly List<float> frameMs = new List<float>();

        // In-play A/B: the setting alternates every PlayAbSeconds; each block records its ticks, frames and seconds.
        private sealed class Block
        {
            public bool On;
            public int Ticks, Frames;
            public double Seconds;
        }

        private readonly List<Block> blocks = new List<Block>();
        private bool abOn;
        private int blockStartTick, blockFrames;
        private double blockStartSeconds;
        private Dictionary<Optimization, bool> abBaseline;

        public PlayComponent(Game game)
        {
        }

        public override void GameComponentUpdate()
        {
            if (!BenchConfig.Active || BenchConfig.Mode != "play" || done)
                return;
            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting)
                return;

            if (!running)
            {
                if (++framesWaited < SettleFrames)
                    return;
                running = true;
                startTick = Find.TickManager.TicksGame;
                LogCounter.Counting = true;
                FrameProfiler.Reset();
                wall.Start();
                FrameRecorder.Start(startTick);
                if (BenchConfig.PlayAb != "")
                {
                    abBaseline = OptimizationRegistry.All.ToDictionary(o => o, o => o.Enabled);
                    SetAb(false);
                    blockStartTick = startTick;
                }
                return;
            }

            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (BenchConfig.PlayAb != "")
            {
                blockFrames++;
                var now = wall.Elapsed.TotalSeconds;
                if (now - blockStartSeconds >= BenchConfig.PlayAbSeconds)
                {
                    blocks.Add(new Block { On = abOn, Ticks = Find.TickManager.TicksGame - blockStartTick, Frames = blockFrames, Seconds = now - blockStartSeconds });
                    SetAb(!abOn);
                    blockStartTick = Find.TickManager.TicksGame;
                    blockStartSeconds = now;
                    blockFrames = 0;
                }
            }
            // Keep playing at max normal speed: close anything that forces a pause (letters, dialogs).
            foreach (var w in Find.WindowStack.Windows.Where(w => w.forcePause).ToList())
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast)
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;

            if (wall.Elapsed.TotalSeconds >= BenchConfig.PlaySeconds)
                Finish();
        }

        /// <summary>Off = baseline for the A/B subject; on = the subject switched on (opts: as configured; key: enabled; budget).</summary>
        private void SetAb(bool on)
        {
            abOn = on;
            var spec = BenchConfig.PlayAb;
            if (spec.StartsWith("budget:"))
            {
                TickBudget.BudgetMs = on ? float.Parse(spec.Substring(7), System.Globalization.CultureInfo.InvariantCulture) : TickBudget.VanillaMs;
                return;
            }
            foreach (var opt in OptimizationRegistry.All)
            {
                if (spec == "opts")
                    opt.Enabled = on && abBaseline[opt];
                else if (opt.Key == spec)
                    opt.Enabled = on;
            }
        }

        private void AppendAbReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine($"== In-play A/B: {BenchConfig.PlayAb}, blocks of {BenchConfig.PlayAbSeconds} s");
            // Pair each block with the next one of the other state.
            var diffs = new List<double>();
            var fpsOff = new List<double>();
            var fpsOn = new List<double>();
            var tpsOff = new List<double>();
            var tpsOn = new List<double>();
            foreach (var b in blocks.Skip(1)) // first block includes settling after the switch
                (b.On ? tpsOn : tpsOff).Add(b.Ticks / b.Seconds);
            foreach (var b in blocks.Skip(1))
                (b.On ? fpsOn : fpsOff).Add(b.Frames / b.Seconds);
            for (var i = 1; i + 1 < blocks.Count; i += 2)
            {
                var a = blocks[i];
                var b = blocks[i + 1];
                if (a.On == b.On)
                    continue;
                var off = a.On ? b : a;
                var on = a.On ? a : b;
                diffs.Add(on.Ticks / on.Seconds - off.Ticks / off.Seconds);
            }
            sb.AppendLine($"TPS off:          {(tpsOff.Count == 0 ? 0 : tpsOff.Average()):F1}   ({tpsOff.Count} blocks)");
            sb.AppendLine($"TPS on:           {(tpsOn.Count == 0 ? 0 : tpsOn.Average()):F1}   ({tpsOn.Count} blocks)");
            sb.AppendLine($"FPS off / on:     {(fpsOff.Count == 0 ? 0 : fpsOff.Average()):F1} / {(fpsOn.Count == 0 ? 0 : fpsOn.Average()):F1}");
            if (diffs.Count >= 2)
            {
                var mean = diffs.Average();
                var sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1));
                var ci = 1.96 * sd / Math.Sqrt(diffs.Count);
                var baseTps = tpsOff.Count == 0 ? 1 : tpsOff.Average();
                sb.AppendLine($"TPS difference:   {mean:+0.0;-0.0} ({100 * mean / baseTps:+0.0;-0.0}%), 95% CI +/-{ci:F1} ({100 * ci / baseTps:F1}%), on faster in {diffs.Count(d => d > 0)} of {diffs.Count} pairs");
            }
        }

        private void Finish()
        {
            done = true;
            FrameRecorder.Stop();
            LogCounter.Counting = false;
            try
            {
                File.WriteAllText(BenchConfig.ResultPath, BuildReport());
            }
            catch (Exception e)
            {
                Log.Error("[Free Performance] Could not write play report: " + e);
            }
            BenchLauncher.SaveIfAsked();
            Root.Shutdown();
        }

        private string BuildReport()
        {
            var seconds = wall.Elapsed.TotalSeconds;
            var ticks = Find.TickManager.TicksGame - startTick;
            var sorted = frameMs.Skip(5).OrderBy(x => x).ToList(); // first frames include the speed change
            float Pct(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

            var sb = new StringBuilder();
            sb.AppendLine("ParallelTick play test");
            sb.AppendLine($"label:            {BenchConfig.Label}");
            sb.AppendLine($"save:             {BenchConfig.SaveName}");
            sb.AppendLine($"game version:     {VersionControl.CurrentVersionStringWithRev}");
            sb.AppendLine($"optimizations:    {(BenchConfig.PlayOpts ? string.Join(", ", OptimizationRegistry.All.Where(o => o.Active).Select(o => o.Key)) : "off")}");
            sb.AppendLine($"played:           {seconds:F0} s real time at Superfast (target 360 TPS), from tick {startTick}");
            sb.AppendLine();
            sb.AppendLine("== Speed (includes rendering)");
            sb.AppendLine($"ticks per second: {ticks / seconds:F1}   ({ticks} ticks)");
            sb.AppendLine($"frames per second:{frameMs.Count / seconds,6:F1}");
            sb.AppendLine($"frame time:       median {Pct(0.5):F1} ms, p95 {Pct(0.95):F1} ms, p99 {Pct(0.99):F1} ms, max {(sorted.Count == 0 ? 0 : sorted.Last()):F1} ms");
            if (FrameProfiler.Slots.Count > 0 && frameMs.Count > 0)
            {
                var frames = frameMs.Count;
                var toMs = 1000.0 / Stopwatch.Frequency;
                var meanFrame = frameMs.Average();
                double PerFrame(FrameProfiler.Slot s) => s.Elapsed * toMs / frames;
                sb.AppendLine();
                sb.AppendLine($"== Where a frame's time goes (instrumented, per frame; mean frame {meanFrame:F1} ms)");
                foreach (var s in FrameProfiler.Slots)
                    sb.AppendLine($"  {s.Label,-52} {PerFrame(s),7:F2} ms  {100 * PerFrame(s) / meanFrame,5:F1}%  {(double)s.Calls / frames,6:F1} calls/frame");
                var scripted = FrameProfiler.Slots.Where(s => s.Label.StartsWith("Root_Play.Update") || s.Label.StartsWith("UIRoot_Play.UIRootOnGUI")).Sum(PerFrame);
                sb.AppendLine($"  {"Everything else (Unity rendering, GPU wait, engine)",-52} {meanFrame - scripted,7:F2} ms  {100 * (meanFrame - scripted) / meanFrame,5:F1}%");
                sb.AppendLine();
                sb.AppendLine("-- Alert recalculation by alert (top 15)");
                foreach (var a in FrameProfiler.Alerts.Values.OrderByDescending(a => a.Elapsed).Take(15))
                    sb.AppendLine($"  {a.Label,-52} {PerFrame(a),7:F3} ms/frame  {a.Elapsed * toMs / System.Math.Max(1, a.Calls):F3} ms each  {(double)a.Calls / frames,5:F2} calls/frame");
            }

            if (BenchConfig.PlayAb != "")
                AppendAbReport(sb);

            sb.AppendLine();
            sb.AppendLine("== Log during play");
            sb.AppendLine($"errors:           {LogCounter.Errors}");
            sb.AppendLine($"warnings:         {LogCounter.Warnings}");
            foreach (var ex in LogCounter.Examples)
                sb.AppendLine("  " + ex);
            return sb.ToString();
        }
    }
}
