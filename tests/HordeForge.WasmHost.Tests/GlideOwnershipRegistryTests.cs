using System.Collections.Generic;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The deny side of the glide verb: an armed flag is one module's
    /// authority over a player, so no other module may clear it.
    /// </summary>
    public sealed class GlideOwnershipRegistryTests
    {
        private readonly GlideOwnershipRegistry _registry = new GlideOwnershipRegistry();

        [Fact]
        public void OwnsOnlyTheModuleThatArmedTheFlag()
        {
            Assert.True(_registry.Claim("parachute", 101));

            Assert.True(_registry.Owns("parachute", 101));
            Assert.False(_registry.Owns("brain", 101));
            Assert.False(_registry.Owns("parachute", 102));
        }

        [Fact]
        public void AnotherModuleCannotTakeOverAnArmedFlag()
        {
            _registry.Claim("parachute", 101);

            // The refusal a "glide <id> 0" from a second module gets.
            Assert.False(_registry.Claim("brain", 101));

            Assert.Equal("parachute", _registry.OwnerOf(101));
            Assert.False(_registry.Owns("brain", 101));
        }

        [Fact]
        public void TheOwnerReclaimsItsOwnFlagForArmAndClear()
        {
            _registry.Claim("parachute", 101);

            Assert.True(_registry.Claim("parachute", 101));
            Assert.True(_registry.Claim("parachute", 101));
            Assert.Equal("parachute", _registry.OwnerOf(101));
        }

        [Fact]
        public void AnUnclaimedNetIdIsFreeForTheNextModule()
        {
            _registry.Claim("parachute", 101);
            _registry.Forget(101);

            Assert.Null(_registry.OwnerOf(101));
            Assert.True(_registry.Claim("brain", 101));
            Assert.True(_registry.Owns("brain", 101));
        }

        [Fact]
        public void ReleaseDropsOnlyTheDepartedModulesClaims()
        {
            _registry.Claim("parachute", 102);
            _registry.Claim("parachute", 101);
            _registry.Claim("brain", 201);

            IReadOnlyList<int> released = _registry.Release("parachute");

            Assert.Equal(new[] { 101, 102 }, released);
            Assert.True(_registry.Owns("brain", 201));
            Assert.Null(_registry.OwnerOf(101));
        }

        [Fact]
        public void ReleaseOfAnUnknownModuleChangesNothing()
        {
            _registry.Claim("parachute", 101);

            Assert.Empty(_registry.Release("brain"));
            Assert.True(_registry.Owns("parachute", 101));
        }
    }
}
