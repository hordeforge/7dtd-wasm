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
    }
}
