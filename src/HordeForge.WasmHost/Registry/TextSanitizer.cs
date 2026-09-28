using System;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Strips C0 control characters, DEL, the C1 control range, the Unicode
    /// line and paragraph separators, and the
    /// invisible bidi controls from guest- or client-supplied text before it
    /// reaches the server log, the console, or global chat: raw newlines
    /// would let a mod forge log lines attributed to other subsystems, and
    /// escape sequences (ESC and the 8-bit C1 controls such as U+009B CSI)
    /// can drive terminals that decode UTF-8 input even when the 7-bit ESC
    /// byte is gone. The bidi embedding, override, and isolate controls
    /// (U+202A..U+202E, U+2066..U+2069) and the zero-width no-break space
    /// (U+FEFF) render as nothing while reordering or splitting the text
    /// around them, so chat and log lines built from guest text could read
    /// as some other name or as text the guest never wrote. Invisible
    /// formatting that carries meaning in real typography (zero-width
    /// space and joiner, word joiner, variation selectors) is left alone so
    /// emoji sequences and non-Latin scripts still render.
    /// </summary>
    public static class TextSanitizer
    {
        /// <summary>Returns <paramref name="text"/> with every stripped character replaced by '?'.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            int firstControl = FirstControlIndex(text);
            if (firstControl < 0)
            {
                return text;
            }
            var sb = new StringBuilder(text.Length);
            sb.Append(text, 0, firstControl);
            for (int i = firstControl; i < text.Length; i++)
            {
                sb.Append(IsStripped(text[i]) ? '?' : text[i]);
            }
            return sb.ToString();
        }

        private static bool IsControl(char c)
        {
            return c < ' ' || c == '\x7f' || (c >= '\u0080' && c <= '\u009f');
        }

        /// <summary>
        /// U+2028 and U+2029. They are not C0 controls, so the control
        /// filter above leaves them, but every consumer that renders a log
        /// line, a console line, or chat in a text layout treats them as line
        /// breaks (a browser's JavaScript string view, a terminal in
        /// line-wrapping mode, most log viewers). A guest that puts one in a
        /// log or chat message forges exactly the split the C0 filter
        /// exists to prevent.
        /// </summary>
        private static bool IsUnicodeLineBreak(char c)
        {
            return c == '\u2028' || c == '\u2029';
        }

        /// <summary>Zero-width characters that reorder or hide the text around them.</summary>
        private static bool IsInvisibleFormat(char c)
        {
            return (c >= '\u202a' && c <= '\u202e') ||
                   (c >= '\u2066' && c <= '\u2069') ||
                   c == '\ufeff';
        }

        private static bool IsStripped(char c)
        {
            return IsControl(c) || IsUnicodeLineBreak(c) || IsInvisibleFormat(c);
        }

        private static int FirstControlIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (IsStripped(text[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// The exception's type, message, and stack as a single line: the
        /// line breaks a stack trace carries become spaces, and the stripped
        /// characters <see cref="Clean"/> would render as '?' become spaces
        /// too, so a logged failure stays one log entry. Without this a host
        /// fault reported with its exception fills a dozen lines of the
        /// server logfile that a line-oriented reader sees as a dozen
        /// unrelated entries, none of them carrying the module id or tick
        /// number the surrounding line names. Inner exceptions are included
        /// by the same flattening. Empty for a null exception.
        /// </summary>
        public static string Describe(Exception? exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }
            return Flatten(exception.ToString());
        }

        private static string Flatten(string text)
        {
            if (text.Length == 0)
            {
                return text;
            }
            var sb = new StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (IsStripped(c) || char.IsWhiteSpace(c))
                {
                    // One space for the whole run, so a stack trace reads as
                    // "at Frame at Frame" rather than as padded gaps.
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
