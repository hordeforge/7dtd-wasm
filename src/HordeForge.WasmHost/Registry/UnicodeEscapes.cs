using System;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Shared \uXXXX escape decoder for the manifest parsers (MiniToml,
    /// MiniJson). Both grammars require strings to be valid Unicode: a lone
    /// surrogate has no UTF-8 form, so it could never round-trip the guest
    /// string ABI without silent corruption. The decoder tracks an escaped
    /// high surrogate waiting for its low half and rejects anything else.
    /// One copy so the pair-validity rule cannot drift between the two.
    /// </summary>
    internal static class UnicodeEscapes
    {
        /// <summary>
        /// Appends one escaped \uXXXX code unit, enforcing surrogate-pair
        /// validity through <paramref name="pendingHigh"/>.
        /// </summary>
        public static void AppendEscapedCodeUnit(StringBuilder sb, char unit, string hex, ref bool pendingHigh)
        {
            if (pendingHigh)
            {
                if (!char.IsLowSurrogate(unit))
                {
                    throw new FormatException("high surrogate escape not followed by a low surrogate escape (got \\u" + hex + ")");
                }
                pendingHigh = false;
            }
            else if (char.IsLowSurrogate(unit))
            {
                throw new FormatException("low surrogate escape \\u" + hex + " without a preceding high surrogate escape");
            }
            else
            {
                pendingHigh = char.IsHighSurrogate(unit);
            }
            sb.Append(unit);
        }

        /// <summary>Appends a non-escape code unit; one may not interrupt a pending surrogate pair.</summary>
        public static void AppendPlainUnit(StringBuilder sb, char unit, ref bool pendingHigh)
        {
            EndPendingHighOrThrow(ref pendingHigh);
            sb.Append(unit);
        }

        /// <summary>Rejects a string ending with an unmatched high surrogate escape.</summary>
        public static void EndPendingHighOrThrow(ref bool pendingHigh)
        {
            if (pendingHigh)
            {
                throw new FormatException("high surrogate escape not followed by a low surrogate escape");
            }
            pendingHigh = false;
        }
    }
}
