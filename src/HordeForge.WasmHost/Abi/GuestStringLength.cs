namespace HordeForge.WasmHost.Abi
{
    /// <summary>
    /// Byte cap on a string the host reads out of a guest's linear memory.
    ///
    /// The guest string imports carry a host-supplied length, so the length
    /// is guest-chosen: a guest can name any value up to its whole linear
    /// memory (32 MiB under the default cap, 4 GiB when the operator raised
    /// it) and repeat the call inside one fuel budget. Reading that many
    /// bytes is host work the fuel budget never sees, so without a cap a
    /// guest spends the game loop's memory on a string the host then
    /// discards: a chat message is refused past 256 code points and a log
    /// line is dropped by the rate cap, but the bytes were already read,
    /// decoded, and allocated in the game process.
    ///
    /// Nothing a guest legitimately hands the host is anywhere near this
    /// bound: a log line, a chat message, a SimCommand, a setting key, a
    /// query request. 64 KiB leaves four orders of magnitude of headroom
    /// and bounds one import at a fraction of the smallest memory ceiling
    /// the host accepts.
    /// </summary>
    public static class GuestStringLength
    {
        /// <summary>Largest string, in bytes, the host reads from guest memory.</summary>
        public const int MaxBytes = 64 * 1024;

        /// <summary>
        /// The number of bytes to read for a guest-declared
        /// <paramref name="declared"/> length: zero for a non-positive
        /// length, otherwise the length capped at <see cref="MaxBytes"/>.
        /// A guest that overstates its length has its text cut short
        /// rather than its call refused, so the import keeps working and
        /// the value it carries is still a prefix of what the guest wrote.
        /// </summary>
        public static int Clamp(int declared)
        {
            if (declared <= 0)
            {
                return 0;
            }
            return declared > MaxBytes ? MaxBytes : declared;
        }
    }
}
