using System;
using System.Collections.Generic;
using HordeForge.GameBridge.Bridge;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Which entities a sense snapshot reports when the world holds more
    /// alive entities than one snapshot carries. The pick must be a function
    /// of the entity set alone: the game keeps its entity list in whatever
    /// order it built it, so a first-N-of-the-list pick would hand a guest a
    /// different snapshot on every run over the same world.
    /// </summary>
    public sealed class SenseRecordPickerTests
    {
        [Fact]
        public void KeepsLowestIdsWhenTheWorldIsLargerThanTheSnapshot()
        {
            var netIds = new List<int> { 91, 7, 55, 12, 40, 3 };
            SenseRecordPicker.SelectLowest(netIds, 3);
            Assert.Equal(new[] { 3, 7, 12 }, netIds);
        }

        [Fact]
        public void SelectionIgnoresTheOrderTheWorldListedEntitiesIn()
        {
            var ascending = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 };
            var shuffled = new List<int> { 8, 3, 6, 1, 7, 2, 5, 4 };
            SenseRecordPicker.SelectLowest(ascending, 4);
            SenseRecordPicker.SelectLowest(shuffled, 4);
            Assert.Equal(ascending, shuffled);
            Assert.Equal(new[] { 1, 2, 3, 4 }, shuffled);
        }

        [Fact]
        public void EmptyAndUnderfullWorldsPassThrough()
        {
            var empty = new List<int>();
            SenseRecordPicker.SelectLowest(empty, 4);
            Assert.Empty(empty);

            var one = new List<int> { 17 };
            SenseRecordPicker.SelectLowest(one, 4);
            Assert.Equal(new[] { 17 }, one);
        }

        [Fact]
        public void RejectsANegativeRecordCap()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SenseRecordPicker.SelectLowest(new List<int> { 1 }, -1));
        }
    }
}
