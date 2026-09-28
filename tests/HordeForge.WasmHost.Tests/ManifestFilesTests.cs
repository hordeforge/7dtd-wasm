using System;
using System.IO;
using System.Text;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Manifest file reads: the size bound, the missing-file and invalid
    /// UTF-8 failures, and the throwing required-read path.
    /// </summary>
    public sealed class ManifestFilesTests : IDisposable
    {
        private readonly string _base;

        public ManifestFilesTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "manifestfiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        [Fact]
        public void ReadsSmallUtf8File()
        {
            string path = Write("ok.toml", "greeting = \"hi\"");
            Assert.True(ManifestFiles.TryRead(path, out string content, out string reason));
            Assert.Equal("greeting = \"hi\"", content);
            Assert.Equal(string.Empty, reason);
        }

        [Fact]
        public void MissingFileReportsReason()
        {
            Assert.False(ManifestFiles.TryRead(Path.Combine(_base, "nope.toml"), out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.Equal("the file does not exist", reason);
        }

        [Fact]
        public void InvalidUtf8FailsInsteadOfCorrupting()
        {
            string path = Path.Combine(_base, "latin1.toml");
            File.WriteAllBytes(path, new byte[] { 0x67, 0x3d, 0xe9 });
            Assert.False(ManifestFiles.TryRead(path, out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.NotEqual(string.Empty, reason);
        }

        [Fact]
        public void Utf16FileFailsInsteadOfSwitchingEncoding()
        {
            // A UTF-16 BOM must not make the host read the file in another
            // encoding: TOML mandates UTF-8, so the load fails with a
            // reason the operator can act on.
            string path = Path.Combine(_base, "utf16.toml");
            File.WriteAllText(path, "greeting = \"hi\"", Encoding.Unicode);
            Assert.False(ManifestFiles.TryRead(path, out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.NotEqual(string.Empty, reason);
        }

        [Fact]
        public void Utf8BomIsStripped()
        {
            string path = Path.Combine(_base, "bom.toml");
            File.WriteAllText(path, "greeting = \"hi\"", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.True(ManifestFiles.TryRead(path, out string content, out string reason));
            Assert.Equal("greeting = \"hi\"", content);
            Assert.Equal(string.Empty, reason);
        }

        [Fact]
        public void NonAsciiTextSurvives()
        {
            string path = Write("unicode.toml", "greeting = \"héllo 中文 \U0001F600\"");
            Assert.True(ManifestFiles.TryRead(path, out string content, out _));
            Assert.Equal("greeting = \"héllo 中文 \U0001F600\"", content);
        }

        [Fact]
        public void ReadRequiredThrowsWithReason()
        {
            string missing = Path.Combine(_base, "nope.toml");
            InvalidOperationException ex = Assert.ThrowsAny<InvalidOperationException>(
                () => ManifestFiles.ReadRequired(missing));
            Assert.Contains("the file does not exist", ex.Message);
        }

        [Fact]
        public void ReadRequiredFailureCarriesPathAndReason()
        {
            // A consumer classifies the failure from the properties, not from
            // the message text; InvalidOperationException stays the base so
            // existing catch sites keep working.
            string missing = Path.Combine(_base, "gone.toml");
            var ex = Assert.Throws<ManifestReadException>(() => ManifestFiles.ReadRequired(missing));
            Assert.Equal(missing, ex.Path);
            Assert.Equal("the file does not exist", ex.Reason);
        }

        [Fact]
        public void ReadRequiredReturnsContent()
        {
            string path = Write("ok.toml", "a = 1");
            Assert.Equal("a = 1", ManifestFiles.ReadRequired(path));
        }

        [Fact]
        public void OversizeFileIsRejected()
        {
            string path = Path.Combine(_base, "big.toml");
            using (var stream = File.Create(path))
            {
                stream.SetLength(ManifestFiles.MaxBytes + 1);
            }
            Assert.False(ManifestFiles.TryRead(path, out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.Contains("larger than", reason);
        }

        private string Write(string name, string text)
        {
            string path = Path.Combine(_base, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
    }
}
