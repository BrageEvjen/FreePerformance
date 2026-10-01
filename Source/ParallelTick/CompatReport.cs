using System.Linq;
using System.Text;
using ParallelTick.Optimizations;
using RimWorld;
using Verse;

namespace ParallelTick
{
    /// <summary>
    /// A plain-text summary of which optimizations are on in this game, which are off or partly vanilla and because of which
    /// mod, and the active mod list. The player copies it to the clipboard from the mod settings and pastes it into a
    /// bug report; nothing is sent anywhere.
    /// </summary>
    public static class CompatReport
    {
        private const string PackageId = "brage.paralleltick";

        public static string Build()
        {
            var sb = new StringBuilder();
            var self = ModLister.GetActiveModWithIdentifier(PackageId, true);
            sb.AppendLine($"Free Performance {self?.ModVersion ?? "?"} on RimWorld {VersionControl.CurrentVersionStringWithRev}");
            sb.AppendLine(Current.ProgramState == ProgramState.Playing
                ? "Report taken in a running game."
                : "Report taken in the main menu: parts that run as vanilla because of other mods are only known after a game has used them.");
            sb.AppendLine();
            sb.AppendLine("Optimizations:");
            foreach (var opt in OptimizationRegistry.All)
                sb.AppendLine($"  {opt.Key}: {State(opt)}");
            sb.AppendLine();
            var mods = LoadedModManager.RunningModsListForReading;
            sb.AppendLine($"Active mods ({mods.Count}, in load order):");
            foreach (var mod in mods)
                sb.AppendLine($"  {mod.PackageIdPlayerFacing} - {mod.Name}");
            return sb.ToString();
        }

        private static string State(Optimization opt)
        {
            if (!ParallelTickModEntry.Settings.IsOn(opt))
                return "switched off in the settings";
            if (opt.Blocked || opt.BlockedReason != null)
                return "OFF, " + (opt.BlockedReason ?? "another mod changes the same code (see the log)");
            if (opt.PartlyVanilla.Count > 0)
                return "on, but these parts run as vanilla: " + string.Join("; ", opt.PartlyVanilla.Distinct());
            return "on";
        }
    }
}
