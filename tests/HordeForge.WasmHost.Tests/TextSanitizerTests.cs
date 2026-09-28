using System;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Log/chat sanitizing: control characters that could forge log lines
    /// or drive terminals become '?'; everything else passes through, and
    /// clean input returns the same reference (no per-tick allocation).
    /// </summary>
    public sealed class TextSanitizerTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("hello survivor")]
        [InlineData("caf\u00e9 \u4e2d\u6587 \U0001F600")]
        public void CleanInputPassesThrough(string text)
        {
            Assert.Same(text, TextSanitizer.Clean(text));
        }

        [Fact]
        public void NullBecomesEmpty()
        {
            Assert.Equal(string.Empty, TextSanitizer.Clean(null!));
        }

        [Theory]
        [InlineData("a\nb", "a?b")]
        [InlineData("a\rb\nc", "a?b?c")]
        [InlineData("\x1b[31mred", "?[31mred")]
        [InlineData("csi\x9bX", "csi?X")]
        [InlineData("del\x7f!", "del?!")]
        [InlineData("\u0000\u0007\u001f", "???")]
        public void ControlCharactersBecomeQuestionMarks(string text, string expected)
        {
            Assert.Equal(expected, TextSanitizer.Clean(text));
        }

        [Fact]
        public void MixedTextKeepsCleanPrefix()
        {
            Assert.Equal("deployed their parachute?", TextSanitizer.Clean("deployed their parachute\n"));
        }

        [Theory]
        [InlineData("admin\u202egnijubma", "admin?gnijubma")]
        [InlineData("a\u202bb", "a?b")]
        [InlineData("ok\u2066evil\u2069", "ok?evil?")]
        [InlineData("ad\ufeffmin", "ad?min")]
        public void InvisibleBidiFormatCharactersBecomeQuestionMarks(string text, string expected)
        {
            Assert.Equal(expected, TextSanitizer.Clean(text));
        }

        [Theory]
        // U+2028 and U+2029 are not C0 controls, but every renderer that
        // breaks a log line or a chat message treats them as one, so a guest
        // using them forges the same split the newline filter prevents.
        [InlineData("deployed\u2028ERR server crashed", "deployed?ERR server crashed")]
        [InlineData("a\u2029b", "a?b")]
        public void UnicodeLineSeparatorsBecomeQuestionMarks(string text, string expected)
        {
            Assert.Equal(expected, TextSanitizer.Clean(text));
        }

        [Theory]
        // Zero-width space and joiner carry meaning in real typography
        // (emoji sequences, line breaking), so they must survive.
        [InlineData("a\u200bb")]
        [InlineData("family \U0001F468\u200D\U0001F469\u200D\U0001F467")]
        [InlineData("no\u2060break")]
        [InlineData("heart \u2764\ufe0f")]
        public void TypographyZeroWidthCharactersPassThrough(string text)
        {
            Assert.Same(text, TextSanitizer.Clean(text));
        }

        [Fact]
        public void DescribedExceptionIsOneLineAndKeepsTheStack()
        {
            Exception thrown;
            try
            {
                throw new InvalidOperationException("engine refused the store");
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            string described = TextSanitizer.Describe(thrown);
            Assert.DoesNotContain('\n', described);
            Assert.DoesNotContain('\r', described);
            Assert.Contains("InvalidOperationException", described);
            Assert.Contains("engine refused the store", described);
            Assert.Contains(nameof(TextSanitizerTests), described);
        }

        [Fact]
        public void DescribedInnerExceptionIsIncluded()
        {
            var thrown = new WasmModLoadException("demo", "load failed", new InvalidOperationException("inner cause"));
            string described = TextSanitizer.Describe(thrown);
            Assert.DoesNotContain('\n', described);
            Assert.Contains("inner cause", described);
        }

        [Fact]
        public void DescribedNullExceptionIsEmpty()
        {
            Assert.Equal(string.Empty, TextSanitizer.Describe(null));
        }

        [Fact]
        public void DescribedExceptionCollapsesRunsOfStrippedCharacters()
        {
            // A stack trace is newlines, spaces, and tabs in runs; the result
            // must not carry the padding, and must not start with a space.
            var thrown = new InvalidOperationException("a\n\n  b\t\tc");
            Assert.Equal("System.InvalidOperationException: a b c", TextSanitizer.Describe(thrown));
        }
    }
}
