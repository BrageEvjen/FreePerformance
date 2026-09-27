using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ParallelTick.Optimizations
{
    /// <summary>
    /// WindManagerTick ends by calling Material.SetFloat(SwayHead, plantSwayHead) on every plant material ever created
    /// (one native Unity call each, every tick, for the visible map). The value is only a shader parameter that drawing
    /// reads, and all of a frame's ticks run before the map is drawn, so only the last value of the frame is ever seen.
    /// This stores the value and applies it once per frame, before the map is drawn (Map.MapUpdate prefix) and after the
    /// frame's ticks (TickManagerUpdate postfix). plantSwayHead itself is still computed every tick; nothing in the
    /// simulation reads the shader value, so the game state is identical. Visual only: every frame is drawn with the same
    /// sway as vanilla.
    /// </summary>
    public static class WindSwayDeferral
    {
        public static readonly Optimization Info = new Optimization
        {
            Key = "windsway",
            Label = "Plant sway once per frame  (exact)",
            Description = "Plants' wind sway is sent to the graphics card once per drawn frame instead of once per game tick. " +
                          "Only the last value before drawing is ever visible, so it looks the same.",
            Patch = Patch,
            Guarded = () => new MethodBase[] { AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick)) },
            Reset = Reset,
            ReportLines = Report,
        };

        private static List<Material> plantMaterials;
        private static int swayHeadId;
        private static bool dirty;
        private static float pending;
        private static float lastFlushedValue = float.NaN;
        private static int lastFlushedCount = -1;
        private static long flushes, verifyLoopTicks, verifyLoops;
        private static readonly Stopwatch watch = new Stopwatch();

        private static void Patch(Harmony harmony)
        {
            plantMaterials = (List<Material>)AccessTools.Field(typeof(WindManager), "plantMaterials").GetValue(null);
            swayHeadId = (int)AccessTools.Field(typeof(ShaderPropertyIDs), "SwayHead").GetValue(null);
            harmony.Patch(AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick)),
                transpiler: new HarmonyMethod(typeof(WindSwayDeferral), nameof(Transpiler)));
            harmony.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)),
                prefix: new HarmonyMethod(typeof(WindSwayDeferral), nameof(Flush)));
            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)),
                postfix: new HarmonyMethod(typeof(WindSwayDeferral), nameof(Flush)));
        }

        private static void Reset()
        {
            // Runs from a GameComponent constructor, which can be on the loading thread: no Unity calls here. The next
            // tick sets the value again.
            dirty = false;
            lastFlushedValue = float.NaN;
            lastFlushedCount = -1;
            flushes = verifyLoopTicks = verifyLoops = 0;
            Info.Stats.Reset();
        }

        /// <summary>
        /// Inserts `if (TryDefer(this.plantSwayHead)) return;` right after vanilla's `Find.CurrentMap == map` test, i.e.
        /// in front of the SetFloat loop. Patches nothing unless the expected shape is found exactly once.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var getCurrentMap = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
            var mapField = AccessTools.Field(typeof(WindManager), "map");
            var swayField = AccessTools.Field(typeof(WindManager), "plantSwayHead");
            var setFloat = AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(int), typeof(float) });

            var setFloatCalls = 0;
            var site = -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(setFloat))
                    setFloatCalls++;
                // call Find.get_CurrentMap; ldarg.0; ldfld WindManager.map; bne.un
                if (i >= 3 && list[i - 3].Calls(getCurrentMap) && list[i - 2].opcode == OpCodes.Ldarg_0 && list[i - 1].LoadsField(mapField) &&
                    (list[i].opcode == OpCodes.Bne_Un || list[i].opcode == OpCodes.Bne_Un_S))
                    site = site == -1 ? i : -2;
            }
            if (site < 0 || setFloatCalls != 1)
            {
                Log.Message($"[Free Performance] WindSwayDeferral: unexpected WindManagerTick IL (site {site}, SetFloat calls {setFloatCalls}); not patched.");
                return list;
            }
            // The branch skips the loop and goes to `ret`; reuse its target.
            var skipLoop = (Label)list[site].operand;
            list.InsertRange(site + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, swayField),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WindSwayDeferral), nameof(TryDefer))),
                new CodeInstruction(OpCodes.Brtrue, skipLoop),
            });
            return list;
        }

        /// <summary>Called once per tick for the visible map. True = the SetFloat loop was handled here.</summary>
        public static bool TryDefer(float value)
        {
            if (Info.Active)
            {
                pending = value;
                dirty = true;
                Info.Stats.Hits++;
                return true;
            }
            // Vanilla sets the value now; a flush of an older deferred value would overwrite it.
            dirty = false;
            if (Info.Verifying)
            {
                // Same SetFloat calls as vanilla, timed: this is what Active saves per tick.
                watch.Restart();
                for (var i = 0; i < plantMaterials.Count; i++)
                    plantMaterials[i].SetFloat(swayHeadId, value);
                watch.Stop();
                verifyLoopTicks += watch.ElapsedTicks;
                verifyLoops++;
                return true;
            }
            return false;
        }

        public static void Flush()
        {
            if (!dirty)
                return;
            dirty = false;
            if (pending == lastFlushedValue && plantMaterials.Count == lastFlushedCount)
                return;
            for (var i = 0; i < plantMaterials.Count; i++)
                plantMaterials[i].SetFloat(swayHeadId, pending);
            lastFlushedValue = pending;
            lastFlushedCount = plantMaterials.Count;
            flushes++;
        }

        private static IEnumerable<string> Report()
        {
            yield return $"  plant materials: {plantMaterials?.Count ?? 0}, flushes: {flushes}, deferred ticks: {Info.Stats.Hits}";
            if (verifyLoops > 0)
                yield return $"  vanilla SetFloat loop: {verifyLoopTicks * 1000.0 / Stopwatch.Frequency / verifyLoops:F4} ms per tick ({verifyLoops} ticks timed)";
        }
    }
}
