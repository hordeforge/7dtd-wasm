using System;
using System.Collections.Generic;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Chooses which entities a sense snapshot reports when the live world
    /// holds more alive entities than one snapshot can carry. The choice is
    /// made on the net id alone: the world entity list is in whatever order
    /// the game built it, so taking the first N of it would give two runs
    /// over the same entity set different snapshots, and a guest driving
    /// bots from that snapshot would drive different targets each time. The
    /// lowest N net ids are reported, in ascending order, so the snapshot
    /// bytes are a function of the entity set alone.
    /// </summary>
    public static class SenseRecordPicker
    {
        /// <summary>
        /// Sorts <paramref name="netIds"/> ascending and drops everything
        /// past <paramref name="max"/>, leaving the list holding the ids to
        /// report. Sorts the caller's own list in place, so the hot sense
        /// path allocates nothing beyond the list's first growth.
        /// </summary>
        public static void SelectLowest(List<int> netIds, int max)
        {
            if (max < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(max));
            }
            netIds.Sort();
            if (netIds.Count > max)
            {
                netIds.RemoveRange(max, netIds.Count - max);
            }
        }
    }
}
