namespace ParallelTick.Optimizations
{
    public enum OptMode
    {
        /// <summary>Not patched at all.</summary>
        Off,

        /// <summary>Optimization active.</summary>
        On,

        /// <summary>Vanilla behavior, but the optimized path runs alongside and every disagreement is counted.</summary>
        Verify,
    }
}
