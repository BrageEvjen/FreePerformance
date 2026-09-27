using Verse;

namespace ParallelTick.Bench
{
    /// <summary>Loads the benchmark save as soon as the main menu first appears.</summary>
    public static class BenchLauncher
    {
        private static bool launched;

        public static void MainMenuInit_Postfix()
        {
            if (launched)
                return;
            launched = true;
            // save=@new: a freshly generated colony, the same way the main menu's dev "Quick test" button makes one
            // (the -quicktest command line fails in 1.6: a Game already exists when the Play scene starts, so no world).
            if (BenchConfig.SaveName == "@new")
                LongEventHandler.QueueLongEvent(() =>
                {
                    Root_Play.SetupForQuickTestPlay();
                    RimWorld.PageUtility.InitGameStart();
                }, "GeneratingMap", true, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
            else
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(BenchConfig.SaveName));
        }

        // Autosaving mid-measurement would add a multi-second spike and write into the bench folder.
        public static bool SkipAutosave_Prefix() => false;

        /// <summary>saveas=&lt;name&gt;: saves the running game into the bench data folder, to check what the mod writes into saves.</summary>
        public static void SaveIfAsked()
        {
            if (string.IsNullOrEmpty(BenchConfig.SaveAs) || Current.ProgramState != ProgramState.Playing)
                return;
            try
            {
                GameDataSaveLoader.SaveGame(BenchConfig.SaveAs);
                Log.Message($"[Free Performance] Saved the game as '{BenchConfig.SaveAs}'.");
            }
            catch (System.Exception e)
            {
                Log.Error($"[Free Performance] Could not save as '{BenchConfig.SaveAs}': {e}");
            }
        }
    }
}
