namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Conversion of the game's world clock onto the i64 the guest ABI
    /// carries (<c>get_world_time</c> returns i64; the sense v4 header field
    /// is written from the same value). <c>World.GetWorldTime()</c> is
    /// unsigned, so a plain (long) cast would reinterpret anything past
    /// long.MaxValue as a time before the world started, and every guest
    /// that reads it (a day/night cycle, a world-age schedule) would
    /// silently compute from a wrapped clock. The value saturates at
    /// long.MaxValue instead: past that point the clock is beyond any
    /// schedule a guest tracks, and a clamped reading is still monotonic.
    /// </summary>
    public static class WorldTime
    {
        /// <summary>Game world time as the i64 the guest ABI carries.</summary>
        public static long ToAbi(ulong worldTime)
        {
            return worldTime > long.MaxValue ? long.MaxValue : (long)worldTime;
        }
    }
}
