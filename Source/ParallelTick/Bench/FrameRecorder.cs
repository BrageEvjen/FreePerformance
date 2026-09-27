using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// record=&lt;fps&gt; in play mode: saves what is on screen as JPGs (bench data folder, frames\) plus frames.csv with each
    /// frame's real time and the game ticks since the start, for side-by-side comparison videos. Frames are grabbed at
    /// the end of a rendered frame, so the picture is exactly what the player sees; files are written off the main thread.
    /// </summary>
    public static class FrameRecorder
    {
        private static bool active;
        private static string dir;
        private static int startTick, index;
        private static Stopwatch clock;
        private static StringBuilder meta;
        private static readonly List<Task> writes = new List<Task>();

        public static string Folder => Path.Combine(Path.GetDirectoryName(BenchConfig.ResultPath) ?? ".", "frames");

        public static void Start(int tick)
        {
            if (BenchConfig.RecordFps <= 0f || active)
                return;
            dir = Folder;
            // Empty the folder rather than deleting it: a shell sitting in it would make the delete fail.
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir))
                File.Delete(old);
            startTick = tick;
            index = 0;
            meta = new StringBuilder("frame,seconds,ticks\n");
            clock = Stopwatch.StartNew();
            active = true;
            Find.CameraDriver.StartCoroutine(Loop(1.0 / BenchConfig.RecordFps));
        }

        private static IEnumerator Loop(double interval)
        {
            var endOfFrame = new WaitForEndOfFrame();
            var next = 0.0;
            while (active)
            {
                yield return endOfFrame;
                if (!active)
                    yield break;
                var t = clock.Elapsed.TotalSeconds;
                if (t < next)
                    continue;
                next = System.Math.Max(next + interval, t);
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var bytes = tex.EncodeToJPG(90);
                Object.Destroy(tex);
                var path = Path.Combine(dir, $"frame_{index:D5}.jpg");
                meta.AppendLine($"{index},{t:F3},{Find.TickManager.TicksGame - startTick}");
                index++;
                writes.Add(Task.Run(() => File.WriteAllBytes(path, bytes)));
            }
        }

        public static void Stop()
        {
            if (!active)
                return;
            active = false;
            Task.WaitAll(writes.ToArray());
            writes.Clear();
            File.WriteAllText(Path.Combine(dir, "frames.csv"), meta.ToString());
            Log.Message($"[Free Performance] Recorded {index} frames to {dir}");
        }
    }
}
