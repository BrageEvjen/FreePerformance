using System.Collections.Generic;
using ParallelTick.Optimizations;
using UnityEngine;
using Verse;

namespace ParallelTick
{
    public class ParallelTickSettings : ModSettings
    {
        // Only the checkboxes the player changed are stored, so each optimization keeps its own default otherwise.
        public Dictionary<string, bool> Overrides = new Dictionary<string, bool>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Overrides, "overrides", LookMode.Value, LookMode.Value);
            if (Overrides == null)
                Overrides = new Dictionary<string, bool>();
        }

        public bool IsOn(Optimization opt) => Overrides.TryGetValue(opt.Key, out var on) ? on : opt.DefaultOn;

        /// <summary>Pushes the settings to the optimizations; they switch at runtime, no restart needed.</summary>
        public void ApplyRuntime()
        {
            foreach (var opt in OptimizationRegistry.All)
                opt.Enabled = IsOn(opt);
        }
    }

    public class ParallelTickModEntry : Mod
    {
        public static ParallelTickSettings Settings;

        public ParallelTickModEntry(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ParallelTickSettings>();
        }

        public override string SettingsCategory() => "Free Performance";

        private Vector2 scroll;
        private float contentHeight = 1000f;
        private float copiedUntil;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Many checkboxes: scroll.
            var view = new Rect(0f, 0f, inRect.width - 20f, contentHeight);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var list = new Listing_Standard();
            list.Begin(view);
            list.Label("Each optimization gives the same result as vanilla: it is checked against vanilla and timed in-game " +
                       "before it is added here. All are on by default.");
            list.Gap(6f);
            var buttonRect = list.GetRect(30f);
            if (Widgets.ButtonText(buttonRect, Time.realtimeSinceStartup < copiedUntil ? "Copied" : "Copy compatibility report to clipboard"))
            {
                GUIUtility.systemCopyBuffer = CompatReport.Build();
                copiedUntil = Time.realtimeSinceStartup + 3f;
            }
            TooltipHandler.TipRegion(buttonRect, "Which optimizations are on or off here and why, and your mod list, as text to " +
                                                  "paste into a bug report. Nothing is sent anywhere.");
            list.GapLine();
            foreach (var opt in OptimizationRegistry.All)
            {
                var on = Settings.IsOn(opt);
                list.CheckboxLabeled(opt.Label, ref on, opt.Description);
                // Switched off because another mod changes the same code (known once a game has used it).
                if (opt.Blocked || opt.BlockedReason != null)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.gray;
                    list.Label("      Off in this game: another mod changes the same code (" + (opt.BlockedReason ?? "see the log") + ").");
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                else if (opt.PartlyVanilla.Count > 0)
                {
                    // On, with the parts another mod changes running as vanilla.
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.gray;
                    list.Label("      On; another mod changes part of it, and that part runs as vanilla: " + string.Join("; ", opt.PartlyVanilla) + ".");
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                if (on == opt.DefaultOn)
                    Settings.Overrides.Remove(opt.Key);
                else
                    Settings.Overrides[opt.Key] = on;
            }
            contentHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();
            Settings.ApplyRuntime();
        }
    }
}
