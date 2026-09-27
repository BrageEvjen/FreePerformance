using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Play-test frame profile: inclusive main-thread timers on the big per-frame phases (ticking, map drawing, UI
    /// update, alerts, OnGUI), plus alert recalculation by alert type. Reported per frame by PlayComponent.
    /// </summary>
    public static class FrameProfiler
    {
        public class Slot
        {
            public string Label;
            public long Elapsed, Calls;
            public int Depth;
        }

        private static readonly (string type, string method, string label)[] Targets =
        {
            ("Verse.Root_Play", "Update", "Root_Play.Update (all script work in Update)"),
            ("Verse.TickManager", "TickManagerUpdate", "  TickManagerUpdate (ticking)"),
            ("Verse.TickManager", "DoSingleTick", "    DoSingleTick"),
            ("Verse.Map", "MapUpdate", "  Map.MapUpdate (map update + drawing)"),
            ("Verse.MapDrawer", "MapMeshDrawerUpdate_First", "    Map mesh regeneration"),
            ("Verse.MapDrawer", "DrawMapMesh", "    DrawMapMesh (terrain, buildings)"),
            ("Verse.DynamicDrawManager", "DrawDynamicThings", "    DrawDynamicThings (pawns, items)"),
            ("RimWorld.UIRoot_Play", "UIRootUpdate", "  UIRoot_Play.UIRootUpdate"),
            ("RimWorld.AlertsReadout", "AlertsReadoutUpdate", "    AlertsReadoutUpdate (alerts)"),
            ("RimWorld.MapInterface", "MapInterfaceUpdate", "    MapInterfaceUpdate"),
            ("RimWorld.UIRoot_Play", "UIRootOnGUI", "UIRoot_Play.UIRootOnGUI (all GUI events)"),
            ("RimWorld.ColonistBar", "ColonistBarOnGUI", "  ColonistBarOnGUI"),
            ("RimWorld.AlertsReadout", "AlertsReadoutOnGUI", "  AlertsReadoutOnGUI"),
            ("RimWorld.MapInterface", "MapInterfaceOnGUI_BeforeMainTabs", "  MapInterfaceOnGUI_BeforeMainTabs"),
            ("RimWorld.MapInterface", "MapInterfaceOnGUI_AfterMainTabs", "  MapInterfaceOnGUI_AfterMainTabs"),
        };

        public static readonly List<Slot> Slots = new List<Slot>();
        public static readonly Dictionary<string, Slot> Alerts = new Dictionary<string, Slot>();
        private static readonly Dictionary<MethodBase, Slot> byMethod = new Dictionary<MethodBase, Slot>();

        public static void Attach(Harmony harmony)
        {
            foreach (var (typeName, methodName, label) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, methodName);
                if (method == null)
                {
                    Log.Warning($"[Free Performance] Frame profiler target not found: {typeName}.{methodName}");
                    continue;
                }
                var slot = new Slot { Label = label };
                Slots.Add(slot);
                byMethod[method] = slot;
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(FrameProfiler), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(FrameProfiler), nameof(Finalizer)));
            }
            harmony.Patch(AccessTools.Method(typeof(Alert), nameof(Alert.Recalculate)),
                prefix: new HarmonyMethod(typeof(AlertTimer), nameof(AlertTimer.Prefix)),
                finalizer: new HarmonyMethod(typeof(AlertTimer), nameof(AlertTimer.Finalizer)));
        }

        public static void Prefix(MethodBase __originalMethod, out long __state)
        {
            if (!UnityData.IsInMainThread)
            {
                __state = -2;
                return;
            }
            var slot = byMethod[__originalMethod];
            slot.Calls++;
            __state = slot.Depth++ == 0 ? Stopwatch.GetTimestamp() : -1;
        }

        public static void Finalizer(MethodBase __originalMethod, long __state)
        {
            if (__state == -2)
                return;
            var slot = byMethod[__originalMethod];
            slot.Depth--;
            if (__state >= 0)
                slot.Elapsed += Stopwatch.GetTimestamp() - __state;
        }

        public static void Reset()
        {
            foreach (var s in Slots)
                s.Elapsed = s.Calls = 0;
            Alerts.Clear();
        }

        private static class AlertTimer
        {
            public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            public static void Finalizer(Alert __instance, long __state)
            {
                var key = __instance.GetType().Name;
                if (!Alerts.TryGetValue(key, out var slot))
                    Alerts[key] = slot = new Slot { Label = key };
                slot.Elapsed += Stopwatch.GetTimestamp() - __state;
                slot.Calls++;
            }
        }
    }
}
