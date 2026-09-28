using System;

namespace HordeForge.WasmHost.Abi
{
    /// <summary>
    /// Longest prefix of a well-formed UTF-8 buffer that fits in a byte
    /// capacity, cut at a code point boundary. The guest string imports are
    /// byte-capped, and a plain min(capacity, length) cut can end in the
    /// middle of a sequence: the guest then decodes a truncated character
    /// into U+FFFD, so the last value it received is silently corrupted
    /// instead of the guest seeing a short buffer it can grow and retry.
    /// </summary>
    public static class Utf8Prefix
    {
        /// <summary>
        /// Byte count of the longest prefix of <paramref name="utf8"/> that
        /// fits in <paramref name="capacity"/> without splitting a
        /// multi-byte sequence. Zero when the capacity cannot hold the
        /// first character.
        /// </summary>
        public static int Length(byte[] utf8, int capacity)
        {
            if (utf8 == null)
            {
                throw new ArgumentNullException(nameof(utf8));
            }
            if (capacity <= 0)
            {
                return 0;
            }
            int length = Math.Min(utf8.Length, capacity);
            // A truncated sequence ends with continuation bytes (10xxxxxx);
            // step back over them to the lead byte of the cut character.
            // The whole buffer needs no walk: nothing was cut.
            while (length > 0 && length < utf8.Length && (utf8[length] & 0xC0) == 0x80)
            {
                length--;
            }
            return length;
        }
    }
}
