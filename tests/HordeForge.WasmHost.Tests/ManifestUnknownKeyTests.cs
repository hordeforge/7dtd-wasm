using System;
using System.Linq;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Top-level keys the manifest parser does not read are tolerated (a
    /// manifest written for a newer host still loads) but must be reported,
    /// so a limit key written above [limits] or a section header the host
    /// does not know cannot leave the operator believing a cap is in force
    /// while the engine runs on the code default.
    /// </summary>
    public sealed class ManifestUnknownKeyTests
    {
        [Fact]
        public void ALimitKeyAboveItsSectionIsReportedAndNotApplied()
        {
            ModManifest manifest = ModManifest.ParseToml(
                "name = \"boss\"\nfuel_per_call = 500\n", "boss");

            Assert.Null(manifest.FuelPerCall);
            Assert.Equal(new[] { "fuel_per_call" }, manifest.IgnoredKeys);
            Assert.True(ModManifest.IsLimitKey("fuel_per_call"));
        }

        [Fact]
        public void AMisspelledSectionHeaderIsReported()
        {
            // [limit] parses as a table the host never reads, so the fuel
            // budget inside it is the code default, not the written one.
            ModManifest manifest = ModManifest.ParseToml("[limit]\nfuel_per_call = 500\n", "boss");

            Assert.Null(manifest.FuelPerCall);
            Assert.Equal(new[] { "limit" }, manifest.IgnoredKeys);
        }

        [Fact]
        public void InformationalAndKnownSectionsAreNotReported()
        {
            ModManifest manifest = ModManifest.ParseToml(
                "name = \"hello\"\ndescription = \"demo\"\nversion = \"0.1.0\"\n"
                    + "[limits]\nfuel_per_call = 1000\nmax_memory_bytes = 33554432\n"
                    + "[settings]\ngreeting = \"hi\"\n",
                "hello");

            Assert.Empty(manifest.IgnoredKeys);
            Assert.Equal(1000UL, manifest.FuelPerCall);
        }

        [Fact]
        public void UnknownKeysAreReportedInOrdinalOrder()
        {
            ModManifest manifest = ModManifest.ParseToml("zebra = 1\nalpha = 2\nmiddle = 3\n", "test");

            Assert.Equal(new[] { "alpha", "middle", "zebra" }, manifest.IgnoredKeys.ToArray());
        }

        [Fact]
        public void AKeyThatIsNotALimitKeyIsNotALimitKey()
        {
            Assert.False(ModManifest.IsLimitKey("fuel_percall"));
            Assert.False(ModManifest.IsLimitKey("greeting"));
            Assert.False(ModManifest.IsLimitKey(null!));
        }
    }
}
