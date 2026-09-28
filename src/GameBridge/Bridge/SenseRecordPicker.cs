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
        /// Alive-entity count above which the bounded selection beats
        /// sorting the whole list. Below it the sort wins: it is a
        /// straight-line introsort over contiguous ints, while the
        /// selection pays a branch and a possible sift per element
        /// (measured on this machine: equal at ~1500 ids, and the
        /// selection is 6x faster at 6000 and 12x at 20000). A server
        /// with a few hundred entities never reaches it and keeps the
        /// simpler, faster path.
        /// </summary>
        private const int SelectionThreshold = 2000;

        /// <summary>
        /// Leaves <paramref name="netIds"/> holding its <paramref name="max"/>
        /// lowest ids, ascending, and drops the rest. The result is the same
        /// list a full sort plus a truncate would give. The input is the
        /// live world's whole alive entity set and the call runs at tick
        /// rate, so a world large enough for the sort to dominate keeps a
        /// bounded max-heap of the ids seen so far instead of sorting all
        /// of them: every id that does not beat the largest retained one is
        /// rejected by a single compare, which is the common case once the
        /// heap is full. Either path works in the caller's own list, so the
        /// hot sense path allocates nothing beyond the list's first growth.
        /// </summary>
        public static void SelectLowest(List<int> netIds, int max)
        {
            if (max < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(max));
            }
            if (netIds.Count <= max || netIds.Count < SelectionThreshold)
            {
                // Everything fits, or the sort is the cheaper path: the
                // order is the only thing left to establish.
                netIds.Sort();
                if (netIds.Count > max)
                {
                    netIds.RemoveRange(max, netIds.Count - max);
                }
                return;
            }
            // The heap lives in the caller's list, in the prefix that is
            // kept anyway, and the tail it reads from is untouched while it
            // is scanned: no copy, no second buffer.
            for (int i = 1; i < max; i++)
            {
                SiftUp(netIds, i);
            }
            for (int i = max; i < netIds.Count; i++)
            {
                int candidate = netIds[i];
                if (candidate >= netIds[0])
                {
                    continue;
                }
                netIds[0] = candidate;
                SiftDown(netIds, max);
            }
            // Heap order is not the ascending order the snapshot contract
            // promises; sorting the retained window is O(max log max) and
            // small by construction, and leaves the list holding exactly
            // what the full sort would have.
            netIds.Sort(0, max, Comparer<int>.Default);
            netIds.RemoveRange(max, netIds.Count - max);
        }

        /// <summary>Restores the max-heap property above <paramref name="index"/>.</summary>
        private static void SiftUp(List<int> heap, int index)
        {
            int value = heap[index];
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (heap[parent] >= value)
                {
                    break;
                }
                heap[index] = heap[parent];
                index = parent;
            }
            heap[index] = value;
        }

        /// <summary>Restores the max-heap property below the root of a heap of <paramref name="count"/> ids.</summary>
        private static void SiftDown(List<int> heap, int count)
        {
            int value = heap[0];
            int index = 0;
            while (true)
            {
                int left = (2 * index) + 1;
                if (left >= count)
                {
                    break;
                }
                int largest = left;
                int right = left + 1;
                if (right < count && heap[right] > heap[left])
                {
                    largest = right;
                }
                if (heap[largest] <= value)
                {
                    break;
                }
                heap[index] = heap[largest];
                index = largest;
            }
            heap[index] = value;
        }
    }
}
