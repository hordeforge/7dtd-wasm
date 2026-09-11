using System.Collections.Generic;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Settings precedence for the get_setting host import: the calling
    /// mod's own settings win over shared ones; misses return false.
    /// </summary>
    public sealed class SettingsTableTests
    {
        [Fact]
        public void ModSettingsWinOverShared()
        {
            var table = new SettingsTable();
            table.UpdateShared(new Dictionary<string, string> { ["k"] = "shared", ["s"] = "shared" });
            table.UpdateMod("mod", new Dictionary<string, string> { ["k"] = "mod" });
            Assert.True(table.TryGetSetting("mod", "k", out string value));
            Assert.Equal("mod", value);
            Assert.True(table.TryGetSetting("mod", "s", out value));
            Assert.Equal("shared", value);
        }

        [Fact]
        public void UnknownModFallsBackToShared()
        {
            var table = new SettingsTable();
            table.UpdateShared(new Dictionary<string, string> { ["k"] = "shared" });
            Assert.True(table.TryGetSetting("unknown", "k", out string value));
            Assert.Equal("shared", value);
        }

        [Fact]
        public void MissReturnsFalse()
        {
            var table = new SettingsTable();
            Assert.False(table.TryGetSetting("mod", "k", out _));
            table.UpdateMod("mod", new Dictionary<string, string> { ["k"] = "v" });
            Assert.False(table.TryGetSetting("mod", "other", out _));
        }

        [Fact]
        public void RemoveModDropsItsSettings()
        {
            var table = new SettingsTable();
            table.UpdateShared(new Dictionary<string, string> { ["k"] = "shared" });
            table.UpdateMod("mod", new Dictionary<string, string> { ["k"] = "mod" });
            table.RemoveMod("mod");
            Assert.True(table.TryGetSetting("mod", "k", out string value));
            Assert.Equal("shared", value);
        }

        [Fact]
        public void UpdateSharedReplaces()
        {
            var table = new SettingsTable();
            table.UpdateShared(new Dictionary<string, string> { ["k"] = "old" });
            table.UpdateShared(new Dictionary<string, string> { ["j"] = "new" });
            Assert.False(table.TryGetSetting(string.Empty, "k", out _));
            Assert.True(table.TryGetSetting(string.Empty, "j", out string value));
            Assert.Equal("new", value);
        }

        [Fact]
        public void ClearSharedEmpties()
        {
            var table = new SettingsTable();
            table.UpdateShared(new Dictionary<string, string> { ["k"] = "v" });
            table.ClearShared();
            Assert.False(table.TryGetSetting(string.Empty, "k", out _));
        }
    }
}
