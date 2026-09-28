using System.Collections.Generic;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The deny side of the bot surface: guests share one world, so every id
    /// a guest names is checked against the module that asked for it.
    /// </summary>
    public sealed class BotOwnershipRegistryTests
    {
        private readonly BotOwnershipRegistry _registry = new BotOwnershipRegistry();

        [Fact]
        public void OwnsOnlyTheModuleThatTookTheId()
        {
            Assert.True(_registry.Add("brain", 101));

            Assert.True(_registry.Owns("brain", 101));
            Assert.False(_registry.Owns("parachute", 101));
            Assert.False(_registry.Owns("brain", 102));
        }

        [Fact]
        public void RemoveIsRefusedForAnotherModuleAndLeavesTheOwnerIntact()
        {
            _registry.Add("brain", 101);

            Assert.False(_registry.Remove("parachute", 101));
            Assert.True(_registry.Owns("brain", 101));
            Assert.Equal(1, _registry.Count);

            Assert.True(_registry.Remove("brain", 101));
            Assert.Equal(0, _registry.Count);
        }

        [Fact]
        public void AnIdAlreadyTrackedIsNotStolenByALaterModule()
        {
            Assert.True(_registry.Add("brain", 101));
            Assert.False(_registry.Add("parachute", 101));

            Assert.True(_registry.Owns("brain", 101));
            Assert.False(_registry.Owns("parachute", 101));
            Assert.Equal(1, _registry.Count);
        }

        [Fact]
        public void CountsArePerModule()
        {
            _registry.Add("brain", 101);
            _registry.Add("brain", 102);
            _registry.Add("parachute", 201);

            Assert.Equal(3, _registry.Count);
            Assert.Equal(2, _registry.CountOf("brain"));
            Assert.Equal(1, _registry.CountOf("parachute"));
            Assert.Equal(0, _registry.CountOf("nobody"));

            _registry.Remove("brain", 101);
            Assert.Equal(1, _registry.CountOf("brain"));
            Assert.Equal(2, _registry.Count);
        }

        [Fact]
        public void ReleaseDropsOnlyTheDepartedModulesIds()
        {
            _registry.Add("brain", 102);
            _registry.Add("brain", 101);
            _registry.Add("parachute", 201);

            IReadOnlyList<int> released = _registry.Release("brain");

            Assert.Equal(new[] { 101, 102 }, released);
            Assert.Equal(0, _registry.CountOf("brain"));
            Assert.True(_registry.Owns("parachute", 201));
            Assert.Equal(1, _registry.Count);
        }

        [Fact]
        public void ReleaseOfAnUnknownOwnerChangesNothing()
        {
            _registry.Add("brain", 101);

            Assert.Empty(_registry.Release("parachute"));
            Assert.True(_registry.Owns("brain", 101));
        }

        [Fact]
        public void EntityIdsAreAscendingAndOwnerOfIsNullForUntracked()
        {
            _registry.Add("brain", 102);
            _registry.Add("brain", 101);
            _registry.Add("parachute", 201);

            Assert.Equal(new[] { 101, 102, 201 }, _registry.EntityIds());
            Assert.Equal("brain", _registry.OwnerOf(102));
            Assert.Null(_registry.OwnerOf(999));
        }
    }
}
