using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Minimal, dependency-free JSON parser used for wasm-mod.json manifests.
    /// Supports the JSON value grammar except fractional and exponent
    /// numbers (manifest values are integers; see <see cref="Parser.ParseNumber"/>):
    /// objects, arrays, strings with escapes, integers, booleans, null. The
    /// host keeps its dependency surface deliberately small: the sandbox
    /// trust boundary should not grow with JSON library dlls.
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>
        /// Maximum container nesting accepted by the parser. Manifests are flat
        /// config files; anything deeper is rejected with a FormatException so a
        /// hostile document cannot overflow the stack (StackOverflowException
        /// would take the whole server process down, defeating the per-mod
        /// fail-soft guarantee).
        /// </summary>
        private const int MaxDepth = 128;

        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            var parser = new Parser(text);
            JsonValue value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw new FormatException("unexpected trailing characters");
            }
            return value;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            private int _depth;

            public Parser(string text)
            {
                _text = text;
            }

            public bool AtEnd => _pos >= _text.Length;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                }
            }

            public JsonValue ParseValue()
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    throw new FormatException("unexpected end of input");
                }
                if (++_depth > MaxDepth)
                {
                    throw new FormatException("nesting deeper than " + MaxDepth);
                }
                try
                {
                    return ParseValueAtDepth();
                }
                finally
                {
                    _depth--;
                }
            }

            private JsonValue ParseValueAtDepth()
            {
                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return new JsonString(ParseString());
                    case 't':
                        Expect("true");
                        return JsonBool.True;
                    case 'f':
                        Expect("false");
                        return JsonBool.False;
                    case 'n':
                        Expect("null");
                        return JsonNull.Instance;
                    default:
                        return ParseNumber();
                }
            }

            private JsonObject ParseObject()
            {
                _pos++; // {
                var obj = new JsonObject();
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new FormatException("expected string key in object at " + _pos);
                    }
                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new FormatException("expected ':' after key at " + _pos);
                    }
                    _pos++;
                    obj.Add(key, ParseValue());
                    SkipWhitespace();
                    char next = Peek();
                    if (next == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (next == '}')
                    {
                        _pos++;
                        return obj;
                    }
                    throw new FormatException("expected ',' or '}' at " + _pos);
                }
            }

            private JsonArray ParseArray()
            {
                _pos++; // [
                var array = new JsonArray();
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return array;
                }
                while (true)
                {
                    array.Add(ParseValue());
                    SkipWhitespace();
                    char next = Peek();
                    if (next == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (next == ']')
                    {
                        _pos++;
                        return array;
                    }
                    throw new FormatException("expected ',' or ']' at " + _pos);
                }
            }

            private string ParseString()
            {
                if (Peek() != '"')
                {
                    throw new FormatException("expected string at " + _pos);
                }
                _pos++;
                var sb = new StringBuilder();
                // Tracks an escaped high surrogate waiting for its low half:
                // JSON strings must be valid Unicode, and a lone surrogate
                // has no UTF-8 form, so it could never round-trip the guest
                // string ABI without silent corruption.
                bool pendingHigh = false;
                while (true)
                {
                    if (_pos >= _text.Length)
                    {
                        throw new FormatException("unterminated string");
                    }
                    char c = _text[_pos++];
                    if (c == '"')
                    {
                        EndPendingHighOrThrow(ref pendingHigh);
                        return sb.ToString();
                    }
                    if (c == '\\')
                    {
                        if (_pos >= _text.Length)
                        {
                            throw new FormatException("unterminated escape");
                        }
                        char e = _text[_pos++];
                        switch (e)
                        {
                            case '"': AppendPlainUnit(sb, '"', ref pendingHigh); break;
                            case '\\': AppendPlainUnit(sb, '\\', ref pendingHigh); break;
                            case '/': AppendPlainUnit(sb, '/', ref pendingHigh); break;
                            case 'b': AppendPlainUnit(sb, '\b', ref pendingHigh); break;
                            case 'f': AppendPlainUnit(sb, '\f', ref pendingHigh); break;
                            case 'n': AppendPlainUnit(sb, '\n', ref pendingHigh); break;
                            case 'r': AppendPlainUnit(sb, '\r', ref pendingHigh); break;
                            case 't': AppendPlainUnit(sb, '\t', ref pendingHigh); break;
                            case 'u':
                                if (_pos + 4 > _text.Length)
                                {
                                    throw new FormatException("bad unicode escape");
                                }
                                string hex = _text.Substring(_pos, 4);
                                // AllowHexSpecifier only (see MiniToml): a
                                // signed or malformed escape is a document
                                // error, not an overflow at decode time.
                                if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                                {
                                    throw new FormatException("bad unicode escape \\u" + hex + " at " + (_pos - 2));
                                }
                                _pos += 4;
                                AppendEscapedCodeUnit(sb, (char)code, hex, ref pendingHigh);
                                break;
                            default:
                                throw new FormatException("unknown escape \\" + e);
                        }
                    }
                    else
                    {
                        AppendPlainUnit(sb, c, ref pendingHigh);
                    }
                }
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

            private JsonNumber ParseNumber()
            {
                int start = _pos;
                if (Peek() == '-')
                {
                    _pos++;
                }
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == 'e' || _text[_pos] == 'E' || _text[_pos] == '+' || _text[_pos] == '-'))
                {
                    _pos++;
                }
                string token = _text.Substring(start, _pos - start);
                if (token.Length == 0 || token == "-")
                {
                    throw new FormatException("expected number at " + start);
                }
                if (token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 || token.IndexOf('E') >= 0)
                {
                    throw new FormatException("fractional or exponent numbers are not supported in manifests");
                }
                if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                {
                    throw new FormatException("number out of range: " + token);
                }
                return new JsonNumber(value);
            }

            private char Peek()
            {
                return _pos < _text.Length ? _text[_pos] : '\0';
            }

            private void Expect(string word)
            {
                if (_pos + word.Length > _text.Length || string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                {
                    throw new FormatException("expected " + word + " at " + _pos);
                }
                _pos += word.Length;
            }
        }
    }

    internal abstract class JsonValue
    {
        public abstract JsonObject AsObject();

        public abstract long AsInteger(string context);
    }

    internal sealed class JsonObject : JsonValue
    {
        private readonly Dictionary<string, JsonValue> _values = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        public bool TryGet(string key, out JsonValue value)
        {
            bool found = _values.TryGetValue(key, out JsonValue? v);
            value = v!;
            return found;
        }

        public void Add(string key, JsonValue value)
        {
            _values[key] = value;
        }

        public override JsonObject AsObject()
        {
            return this;
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be an object");
        }
    }

    internal sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> _items = new List<JsonValue>();

        public void Add(JsonValue value)
        {
            _items.Add(value);
        }

        public override JsonObject AsObject()
        {
            throw new FormatException("expected object");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be a number");
        }
    }

    internal sealed class JsonString : JsonValue
    {
        public JsonString(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public override JsonObject AsObject()
        {
            throw new FormatException("expected object");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be a number");
        }
    }

    internal sealed class JsonNumber : JsonValue
    {
        public JsonNumber(long value)
        {
            Value = value;
        }

        public long Value { get; }

        public override JsonObject AsObject()
        {
            throw new FormatException("expected object");
        }

        public override long AsInteger(string context)
        {
            return Value;
        }
    }

    internal sealed class JsonBool : JsonValue
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);

        private JsonBool(bool value)
        {
            Value = value;
        }

        public bool Value { get; }

        public override JsonObject AsObject()
        {
            throw new FormatException("expected object");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be a number");
        }
    }

    internal sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();

        private JsonNull()
        {
        }

        public override JsonObject AsObject()
        {
            throw new FormatException("expected object");
        }

        public override long AsInteger(string context)
        {
            throw new FormatException(context + " must be a number");
        }
    }
}
