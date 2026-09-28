using System;
using System.Text;
using HordeForge.WasmHost.Abi;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The byte cap on a guest buffer must cut UTF-8 at a character
    /// boundary: a copy that ends mid-sequence decodes as U+FFFD in the
    /// guest, silently corrupting the last value it received.
    /// </summary>
    public sealed class Utf8PrefixTests
    {
        [Fact]
        public void WholeBufferFitsUnchanged()
        {
            byte[] utf8 = Encoding.UTF8.GetBytes("a = 1\n");
            Assert.Equal(utf8.Length, Utf8Prefix.Length(utf8, 4096));
        }

        [Fact]
        public void AsciiCutIsExact()
        {
            byte[] utf8 = Encoding.UTF8.GetBytes("deploy_vy=-6");
            Assert.Equal(4, Utf8Prefix.Length(utf8, 4));
        }

        [Theory]
        [InlineData("é")]       // 2-byte sequence
        [InlineData("中")]       // 3-byte sequence
        [InlineData("\U0001F600")] // 4-byte sequence
        public void CutNeverSplitsAMultiByteCharacter(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            for (int capacity = 1; capacity <= utf8.Length; capacity++)
            {
                int length = Utf8Prefix.Length(utf8, capacity);
                // Whole characters only: the copy decodes back to a prefix of
                // the input, never to replacement characters, and it stops on
                // a lead byte (or the end of the buffer).
                string decoded = Encoding.UTF8.GetString(utf8, 0, length);
                Assert.StartsWith(decoded, text, StringComparison.Ordinal);
                Assert.DoesNotContain('�', decoded);
                Assert.True(length <= capacity);
                if (length < utf8.Length && length < capacity)
                {
                    // Backing off only happens when a character straddled the
                    // cap, so the byte the cut lands on starts the next one.
                    Assert.NotEqual(0x80, utf8[length] & 0xC0);
                }
            }
            Assert.Equal(utf8.Length, Utf8Prefix.Length(utf8, utf8.Length));
        }

        [Fact]
        public void TrailingTextIsCutAtTheLastWholeCharacter()
        {
            // "aé中": 1 + 2 + 3 bytes. A capacity of 4 ends inside the
            // 3-byte character, so the copy holds "aé" only.
            byte[] utf8 = Encoding.UTF8.GetBytes("aé中");
            Assert.Equal(3, Utf8Prefix.Length(utf8, 4));
            Assert.Equal("aé", Encoding.UTF8.GetString(utf8, 0, 3));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NoCapacityYieldsNoBytes(int capacity)
        {
            Assert.Equal(0, Utf8Prefix.Length(Encoding.UTF8.GetBytes("é"), capacity));
        }

        [Fact]
        public void EmptyBufferYieldsNoBytes()
        {
            Assert.Equal(0, Utf8Prefix.Length(Array.Empty<byte>(), 16));
        }
    }
}
