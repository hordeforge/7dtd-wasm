using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Minimal, dependency-free TOML parser used for wasm-mod.toml and
    /// wasm.toml, following the same reasoning as MiniJson (ADR 0005): the
    /// sandbox trust boundary does not grow with a TOML library dll.
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
        /// Maximum array nesting accepted by the parser (see MiniJson.MaxDepth):
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
                // Quoted keys are TOML strings: unwrap them so "boss_name"
                // and boss_name define the same key instead of one carrying
                // its quote characters into every lookup.
                string keyName = key[0] == '"' || key[0] == '\'' ? ParseString(key, i + 1) : key;
                if (keyName.Length == 0)
                {
                    throw new FormatException("line " + (i + 1) + ": invalid key '" + key + "'");
                }
                if (current.HasKey(keyName))
                {
                    throw new FormatException("line " + (i + 1) + ": duplicate key '" + keyName + "' in this table");
                }
                current.Add(keyName, ParseValue(valueText, i + 1));
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
                // Quoted header parts are TOML strings: unwrap them so
                // ["settings"] and [settings] name the same table. A part
                // that starts a quote must close it (dotted keys inside a
                // quoted name stay unsupported, matching this parser's
                // documented subset).
                if (name[0] == '"' || name[0] == '\'')
                {
                    name = ParseString(name, lineNumber);
                    if (name.Length == 0)
                    {
                        throw new FormatException("line " + lineNumber + ": empty table header part");
                    }
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

        private static TomlValue ParseValue(string text, int lineNumber, int depth = 0)
        {
            if (text.Length == 0)
            {
                throw new FormatException("line " + lineNumber + ": empty value");
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
                return ParseArray(text, lineNumber, depth);
            }
            if (text == "true")
            {
                return TomlBool.True;
            }
            if (text == "false")
            {
                return TomlBool.False;
            }
            return ParseNumber(text, lineNumber);
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
                return body; // literal string, no escapes
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
                    EndPendingHighOrThrow(ref pendingHigh);
                    sb.Append(c);
                    continue;
                }
                if (++i >= body.Length)
                {
                    throw new FormatException("line " + lineNumber + ": dangling escape");
                }
                switch (body[i])
                {
                    case '"': AppendPlainUnit(sb, '"', ref pendingHigh); break;
                    case '\\': AppendPlainUnit(sb, '\\', ref pendingHigh); break;
                    case 'n': AppendPlainUnit(sb, '\n', ref pendingHigh); break;
                    case 'r': AppendPlainUnit(sb, '\r', ref pendingHigh); break;
                    case 't': AppendPlainUnit(sb, '\t', ref pendingHigh); break;
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
                        AppendEscapedCodeUnit(sb, (char)code, hex, ref pendingHigh);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("line " + lineNumber + ": unknown escape \\" + body[i]);
                }
            }
            EndPendingHighOrThrow(ref pendingHigh);
            return sb.ToString();
        }

        /// <summary>
        /// Appends one escaped \uXXXX code unit, enforcing surrogate-pair
        /// validity through <paramref name="pendingHigh"/>.
        /// </summary>
        private static void AppendEscapedCodeUnit(StringBuilder sb, char unit, string hex, ref bool pendingHigh)
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
        private static void AppendPlainUnit(StringBuilder sb, char unit, ref bool pendingHigh)
        {
            EndPendingHighOrThrow(ref pendingHigh);
            sb.Append(unit);
        }

        private static void EndPendingHighOrThrow(ref bool pendingHigh)
        {
            if (pendingHigh)
            {
                throw new FormatException("high surrogate escape not followed by a low surrogate escape");
            }
            pendingHigh = false;
        }

        private static TomlArray ParseArray(string text, int lineNumber, int depth)
        {
            if (text[text.Length - 1] != ']')
            {
                throw new FormatException("line " + lineNumber + ": unterminated array");
            }
            string inner = text.Substring(1, text.Length - 2).Trim();
            var array = new TomlArray();
            if (inner.Length == 0)
            {
                return array;
            }
            foreach (string item in SplitArrayItems(inner))
            {
                array.Add(ParseValue(item.Trim(), lineNumber, depth + 1));
            }
            return array;
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

        private static TomlValue ParseNumber(string text, int lineNumber)
        {
            if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    return new TomlDouble(d);
                }
                throw new FormatException("line " + lineNumber + ": invalid float '" + text + "'");
            }
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                return new TomlLong(value);
            }
            throw new FormatException("line " + lineNumber + ": invalid value '" + text + "'");
        }
    }

    internal abstract class TomlValue
    {
        public abstract string AsString(string context);

        public abstract long AsInteger(string context);

        public abstract TomlTable AsTable(string context);
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

        public IEnumerable<string> Keys
        {
            get { return _values.Keys; }
        }

        public override string AsString(string context)
        {
            throw new FormatException(context + " must be a string");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
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

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
        }

        public override TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
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

        public override TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
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

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
        }

        public override TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
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

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
        }

        public override TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
        }
    }

    internal sealed class TomlArray : TomlValue
    {
        private readonly List<TomlValue> _items = new List<TomlValue>();

        public void Add(TomlValue value)
        {
            _items.Add(value);
        }

        public override string AsString(string context)
        {
            throw new FormatException(context + " must be a string");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an integer");
        }

        public override TomlTable AsTable(string context)
        {
            throw new FormatException(context + " must be a table");
        }
    }
}
