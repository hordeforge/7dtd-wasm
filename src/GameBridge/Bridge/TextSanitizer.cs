using System.Text;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Strips C0 control characters and DEL from guest- or client-supplied
    /// text before it reaches the server log, the console, or global chat:
    /// raw newlines would let a mod forge log lines attributed to other
    /// subsystems, and escape sequences (ESC and friends) can drive
    /// terminals that display them.
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
                char c = text[i];
                sb.Append(c < ' ' || c == '\x7f' ? '?' : c);
            }
            return sb.ToString();
        }

        private static int FirstControlIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < ' ' || text[i] == '\x7f')
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
