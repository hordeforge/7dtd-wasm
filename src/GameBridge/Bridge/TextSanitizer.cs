using System.Text;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Strips C0 control characters, DEL, and the C1 control range from
    /// guest- or client-supplied text before it reaches the server log, the
    /// console, or global chat: raw newlines would let a mod forge log lines
    /// attributed to other subsystems, and escape sequences (ESC and the
    /// 8-bit C1 controls such as U+009B CSI) can drive terminals that decode
    /// UTF-8 input even when the 7-bit ESC byte is gone.
    /// </summary>
    internal static class TextSanitizer
    {
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
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
                sb.Append(IsControl(text[i]) ? '?' : text[i]);
            }
            return sb.ToString();
        }

        private static bool IsControl(char c)
        {
            return c < ' ' || c == '\x7f' || (c >= '\u0080' && c <= '\u009f');
        }

        private static int FirstControlIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (IsControl(text[i]))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
