using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ParallelTick
{
    /// <summary>
    /// Keeps the mod out of save files, so it can be added to or removed from a running save without load errors.
    /// Its game components hold no saved state; they are taken out of Game.components while the list is written and
    /// put back at the same positions afterwards. On load the game recreates them (Game.FillComponents).
    /// </summary>
    public static class SaveCompat
    {
        private static readonly AccessTools.FieldRef<Game, List<GameComponent>> Components =
            AccessTools.FieldRefAccess<Game, List<GameComponent>>("components");

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Game), "ExposeSmallComponents"),
                prefix: new HarmonyMethod(typeof(SaveCompat), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(SaveCompat), nameof(After)));
        }

        private static bool Ours(GameComponent c) => c != null && c.GetType().Assembly == typeof(SaveCompat).Assembly;

        public static void Before(Game __instance, out List<(int index, GameComponent component)> __state)
        {
            __state = null;
            if (Scribe.mode != LoadSaveMode.Saving)
                return;
            var list = Components(__instance);
            if (list == null)
                return;
            for (var i = 0; i < list.Count; i++)
                if (Ours(list[i]))
                    (__state ??= new List<(int, GameComponent)>()).Add((i, list[i]));
            list.RemoveAll(Ours);
        }

        public static void After(Game __instance, List<(int index, GameComponent component)> __state)
        {
            if (__state == null)
                return;
            var list = Components(__instance);
            foreach (var (index, component) in __state)
                list.Insert(index <= list.Count ? index : list.Count, component);
        }
    }
}
