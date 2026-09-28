using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Minimal, dependency-free TOML parser used for wasm-mod.toml and
    /// wasm.toml, keeping the sandbox trust boundary free of a TOML library
    /// dll (ADR 0007; ADR 0005 recorded the same decision for the retired
    /// JSON manifest).
    ///
    /// Supported subset (documented in docs/CONFIG.md):
    ///   comments (#), top-level key = value, [table] and [table.sub]
    ///   headers, basic "..." strings with escapes, literal '...' strings,
    ///   integers, floats, booleans, and arrays of scalars.
    /// Multi-line strings and dotted keys are not supported.
    /// </summary>
    internal static class MiniToml
    {
        /// <summary>
        /// Maximum array nesting accepted by the parser:
        /// a hostile manifest must fail with a FormatException, never with a
        /// stack overflow that kills the server process.
        /// </summary>
        private const int MaxDepth = 128;

        public static TomlValue Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            var root = new TomlTable();
            var current = root;
            // Tables named by a [header] so far; TOML forbids defining the
            // same table twice while allowing [a] after [a.b].
            var definedTables = new HashSet<string>(StringComparer.Ordinal);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComment(lines[i]).Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                if (line[0] == '[')
                {
                    string[] parts = ParseTableHeader(line, i + 1);
                    string path = string.Join(".", parts);
                    if (!definedTables.Add(path))
                    {
                        throw new FormatException("line " + (i + 1) + ": table [" + path + "] is defined more than once");
                    }
                    current = root;
                    foreach (string part in parts)
                    {
                        if (current.TryGet(part, out TomlValue child))
                        {
                            if (!(child is TomlTable childTable))
                            {
                                throw new FormatException("line " + (i + 1) + ": table [" + path + "] redefines the value '" + part + "' as a table");
                            }
                            current = childTable;
                        }
                        else
                        {
                            var created = new TomlTable();
                            current.Add(part, created);
                            current = created;
                        }
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    throw new FormatException("line " + (i + 1) + ": expected key = value");
                }
                string key = line.Substring(0, eq).Trim();
                string valueText = line.Substring(eq + 1).Trim();
                if (!IsValidKey(key))
                {
                    throw new FormatException("line " + (i + 1) + ": invalid key '" + key + "'");
                }
                if (current.HasKey(key))
                {
                    throw new FormatException("line " + (i + 1) + ": duplicate key '" + key + "' in this table");
                }
                current.Add(key, ParseValue(valueText, key, i + 1));
            }
            return root;
        }

        private static string StripComment(string line)
        {
            int hash = IndexOfOutsideStrings(line, '#', 0);
            return hash < 0 ? line : line.Substring(0, hash);
        }

        /// <summary>
        /// First index of <paramref name="target"/> at or after
        /// <paramref name="start"/> that sits outside quoted strings, or -1
        /// when there is none. A backslash escapes the next character inside
        /// a basic "..." string (so \" does not close it); literal '...'
        /// strings have no escapes.
        /// </summary>
        private static int IndexOfOutsideStrings(string text, char target, int start)
        {
            bool inBasic = false;
            bool inLiteral = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inBasic)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inBasic = false;
                    }
                }
                else if (inLiteral)
                {
                    if (c == '\'')
                    {
                        inLiteral = false;
                    }
                }
                else if (c == '"')
                {
                    inBasic = true;
                }
                else if (c == '\'')
                {
                    inLiteral = true;
                }
                else if (c == target)
                {
                    return i;
                }
            }
            return -1;
        }

        private static string[] ParseTableHeader(string line, int lineNumber)
        {
            if (line.Length < 2 || line[line.Length - 1] != ']')
            {
                throw new FormatException("line " + lineNumber + ": unterminated table header");
            }
            string inner = line.Substring(1, line.Length - 2).Trim();
            if (inner.Length == 0)
            {
                throw new FormatException("line " + lineNumber + ": empty table header");
            }
            var parts = new List<string>();
            foreach (string part in inner.Split('.'))
            {
                string name = part.Trim();
                if (name.Length == 0)
                {
                    throw new FormatException("line " + lineNumber + ": empty table header part");
                }
                // Held to the key rules as well, so a table cannot be named
                // with a name the equivalent key would be rejected for.
                if (!IsValidKey(name))
                {
                    throw new FormatException("line " + lineNumber + ": invalid table name '" + name + "'");
                }
                parts.Add(name);
            }
            return parts.ToArray();
        }

        private static bool IsValidKey(string key)
        {
            if (key.Length == 0)
            {
                return false;
            }
            if (key[0] == '"' || key[0] == '\'')
            {
                return key[key.Length - 1] == key[0] && key.Length >= 2;
            }
            foreach (char c in key)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Parses the right-hand side of <paramref name="key"/>. The key is
        /// named in a parse failure so the operator does not have to count
        /// lines to find the setting they mistyped; an array item carries
        /// the key of the array it sits in.
        /// </summary>
        private static TomlValue ParseValue(string text, string key, int lineNumber, int depth = 0)
        {
            if (text.Length == 0)
            {
                throw new FormatException("line " + lineNumber + ": empty value" + KeyContext(key));
            }
            if (depth > MaxDepth)
            {
                throw new FormatException("line " + lineNumber + ": array nesting deeper than " + MaxDepth);
            }
            char first = text[0];
            if (first == '"' || first == '\'')
            {
                return new TomlString(ParseString(text, lineNumber));
            }
            if (first == '[')
            {
                return ParseArray(text, lineNumber, depth, key);
            }
            if (text == "true")
            {
                return TomlBool.True;
            }
            if (text == "false")
            {
                return TomlBool.False;
            }
            return ParseNumber(text, lineNumber, key);
        }

        /// <summary>
        /// The " for key 'x'" suffix a value parse failure ends with, so
        /// every such message names the setting as the [limits] messages do.
        /// </summary>
        private static string KeyContext(string key)
        {
            return " for key '" + key + "'";
        }

        private static string ParseString(string text, int lineNumber)
        {
            char quote = text[0];
            if (text.Length < 2 || text[text.Length - 1] != quote)
            {
                throw new FormatException("line " + lineNumber + ": unterminated string");
            }
            string body = text.Substring(1, text.Length - 2);
            if (quote == '\'')
            {
                // Literal string: no escapes, but the same Unicode rule
                // applies to its raw content.
                var literal = new StringBuilder(body.Length);
                for (int i = 0; i < body.Length; i++)
                {
                    i += AppendLiteral(literal, body, i, lineNumber) - 1;
                }
                return literal.ToString();
            }
            var sb = new StringBuilder();
            // Tracks an escaped high surrogate waiting for its low half:
            // TOML strings must be valid Unicode, and a lone surrogate has
            // no UTF-8 form, so it could never round-trip the guest string
            // ABI without silent corruption.
            bool pendingHigh = false;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c != '\\')
                {
                    UnicodeEscapes.EndPendingHighOrThrow(ref pendingHigh);
                    i += AppendLiteral(sb, body, i, lineNumber) - 1;
                    continue;
                }
                if (++i >= body.Length)
                {
                    throw new FormatException("line " + lineNumber + ": dangling escape");
                }
                switch (body[i])
                {
                    case '"': UnicodeEscapes.AppendPlainUnit(sb, '"', ref pendingHigh); break;
                    case '\\': UnicodeEscapes.AppendPlainUnit(sb, '\\', ref pendingHigh); break;
                    case 'n': UnicodeEscapes.AppendPlainUnit(sb, '\n', ref pendingHigh); break;
                    case 'r': UnicodeEscapes.AppendPlainUnit(sb, '\r', ref pendingHigh); break;
                    case 't': UnicodeEscapes.AppendPlainUnit(sb, '\t', ref pendingHigh); break;
                    case 'u':
                        if (i + 4 >= body.Length)
                        {
                            throw new FormatException("line " + lineNumber + ": bad unicode escape");
                        }
                        string hex = body.Substring(i + 1, 4);
                        // AllowHexSpecifier only: rejects signs and whitespace
                        // that a plain HexNumber parse would accept, so the
                        // cast below can never overflow.
                        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                        {
                            throw new FormatException("line " + lineNumber + ": bad unicode escape \\u" + hex);
                        }
                        UnicodeEscapes.AppendEscapedCodeUnit(sb, (char)code, hex, ref pendingHigh);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("line " + lineNumber + ": unknown escape \\" + body[i]);
                }
            }
            UnicodeEscapes.EndPendingHighOrThrow(ref pendingHigh);
            return sb.ToString();
        }

        /// <summary>
        /// Appends one unescaped character of string content, plus the low
        /// surrogate a high one must be followed by, and returns how many
        /// characters it consumed. A lone surrogate has no UTF-8 form, so
        /// accepting one would hand the guest a string that cannot
        /// round-trip the ABI; raw content and <c>\uXXXX</c> escapes are
        /// held to the same rule.
        ///
        /// TOML admits no raw control character in a quoted string other
        /// than the tab (U+0000..U+0008, U+000A..U+001F, U+007F must be
        /// escaped), and this parser has no multi-line string form, so a
        /// raw one is always a stray byte rather than a line break that
        /// belongs to the value. The C1 range is rejected with them: the
        /// host already treats it as a control range (TextSanitizer,
        /// ModId), and a NEL or a CSI is a line break or an escape
        /// sequence to whatever renders the text. Either would travel the
        /// guest string ABI inside a setting value, and from there into a
        /// log line or a chat message, forging the split the log
        /// sanitizer exists to prevent.
        /// </summary>
        private static int AppendLiteral(StringBuilder sb, string body, int index, int lineNumber)
        {
            char c = body[index];
            if (c != '\t' && (c < ' ' || (c >= '\x7f' && c <= '\x9f')))
            {
                throw new FormatException("line " + lineNumber + ": raw control character U+" +
                    ((int)c).ToString("X4", CultureInfo.InvariantCulture) + " in a string (only the tab may appear unescaped)");
            }
            if (char.IsHighSurrogate(c))
            {
                if (index + 1 >= body.Length || !char.IsLowSurrogate(body[index + 1]))
                {
                    throw new FormatException("line " + lineNumber + ": high surrogate in a string is not followed by a low surrogate");
                }
                sb.Append(c).Append(body[index + 1]);
                return 2;
            }
            if (char.IsLowSurrogate(c))
            {
                throw new FormatException("line " + lineNumber + ": low surrogate in a string without a preceding high surrogate");
            }
            sb.Append(c);
            return 1;
        }

        private static TomlArray ParseArray(string text, int lineNumber, int depth, string key)
        {
            if (text[text.Length - 1] != ']')
            {
                throw new FormatException("line " + lineNumber + ": unterminated array");
            }
            string inner = text.Substring(1, text.Length - 2).Trim();
            if (inner.Length > 0)
            {
                // Items are parsed so a malformed one still fails the load,
                // then dropped: no manifest field reads array elements, and
                // a value used as a scalar is rejected by AsString below.
                foreach (string item in SplitArrayItems(inner))
                {
                    ParseValue(item.Trim(), key, lineNumber, depth + 1);
                }
            }
            return new TomlArray();
        }

        /// <summary>
        /// Splits an array body on commas that are outside quoted strings, so
        /// scalars containing commas (for example ["a, b", "c"]) parse.
        /// </summary>
        private static IEnumerable<string> SplitArrayItems(string inner)
        {
            var items = new List<string>();
            int start = 0;
            while (true)
            {
                int comma = IndexOfOutsideStrings(inner, ',', start);
                if (comma < 0)
                {
                    break;
                }
                items.Add(inner.Substring(start, comma - start));
                start = comma + 1;
            }
            items.Add(inner.Substring(start));
            return items;
        }

        private static TomlValue ParseNumber(string text, int lineNumber, string key)
        {
            if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    return new TomlDouble(d);
                }
                throw new FormatException("line " + lineNumber + ": invalid float '" + text + "'" + KeyContext(key));
            }
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                return new TomlLong(value);
            }
            throw new FormatException("line " + lineNumber + ": invalid value '" + text + "'" + KeyContext(key));
        }
    }

    /// <summary>
    /// A parsed value. Each accessor rejects with a "must be a ..." message
    /// unless the concrete value supports that type, so a type overrides
    /// only the accessors it can answer and the rest reject here.
    /// </summary>
    internal abstract class TomlValue
    {
        public virtual string AsString(string context)
        {
            throw new FormatException(context + " must be a string");
        }

        public virtual long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
        }

        public virtual TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
        }
    }

    internal sealed class TomlTable : TomlValue
    {
        private readonly Dictionary<string, TomlValue> _values = new Dictionary<string, TomlValue>(StringComparer.Ordinal);

        public bool TryGet(string key, out TomlValue value)
        {
            bool found = _values.TryGetValue(key, out TomlValue? v);
            value = v!;
            return found;
        }

        public bool HasKey(string key)
        {
            return _values.ContainsKey(key);
        }

        public void Add(string key, TomlValue value)
        {
            _values[key] = value;
        }

        /// <summary>Every key in the table.</summary>
        public IEnumerable<string> Keys
        {
            get { return _values.Keys; }
        }

        /// <summary>
        /// Every key with its parsed value, so a caller walking the table
        /// does not look each key up a second time.
        /// </summary>
        public IEnumerable<KeyValuePair<string, TomlValue>> Entries
        {
            get { return _values; }
        }

        public override TomlTable AsTable(string context)
        {
            return this;
        }
    }

    internal sealed class TomlString : TomlValue
    {
        public TomlString(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public override string AsString(string context)
        {
            return Value;
        }
    }

    internal sealed class TomlLong : TomlValue
    {
        public TomlLong(long value)
        {
            Value = value;
        }

        public long Value { get; }

        public override string AsString(string context)
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }

        public override long AsInteger(string context)
        {
            return Value;
        }
    }

    internal sealed class TomlDouble : TomlValue
    {
        public TomlDouble(double value)
        {
            Value = value;
        }

        public double Value { get; }

        public override string AsString(string context)
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class TomlBool : TomlValue
    {
        public static readonly TomlBool True = new TomlBool(true);
        public static readonly TomlBool False = new TomlBool(false);

        private TomlBool(bool value)
        {
            Value = value;
        }

        public bool Value { get; }

        public override string AsString(string context)
        {
            return Value ? "true" : "false";
        }
    }

    /// <summary>
    /// An array value. The parser validates its items but keeps none: no
    /// manifest field reads array elements, and using one as a scalar is
    /// rejected by the inherited As* methods.
    /// </summary>
    internal sealed class TomlArray : TomlValue
    {
    }
}
