using System;
using System.Globalization;
using HordeForge.WasmHost.Abi;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for the sense snapshot serializer, the one
    /// place where host state is written into a buffer whose size the guest
    /// chose: the zdtd <c>sense</c> import hands the writer a span of
    /// exactly the guest's <c>out_cap</c> in its own linear memory, so a
    /// writer that miscounts an entity, an event, or a header field writes
    /// past the guest's buffer into whatever the next allocation is, in the
    /// game server process, on data the game itself produced.
    ///
    /// The fuzzer drives the writer with random snapshots (hostile count
    /// combinations, integer extremes, every float class from NaN to the
    /// denormals) against a guard-bounded buffer, and asserts the wire
    /// contract rather than only "it did not crash":
    ///
    ///   * the guard region behind the buffer is byte-for-byte unchanged, so
    ///     an overrun past out_cap is a failed assertion rather than silent
    ///     heap damage,
    ///   * a snapshot that fits returns exactly the size the layout
    ///     documents, writes the magic and the entity count, and leaves the
    ///     rest of the buffer untouched,
    ///   * a snapshot that does not fit returns 0 and writes nothing at all,
    ///     so a guest that sized its buffer one byte short reads a "no data"
    ///     answer instead of a half-written world,
    ///   * one snapshot stays inside a wall-clock budget, so a hostile
    ///     entity count cannot wedge the game loop.
    /// </summary>
    public sealed class SenseSnapshotFuzzTests
    {
        /// <summary>
        /// Bytes kept past the buffer handed to the writer. An overrun of a
        /// few bytes is the interesting case and would land here; the layout
        /// arithmetic is a plain count times a constant, so more than one
        /// record past the end would mean a wildly wrong count.
        /// </summary>
        private const int GuardBytes = 64;

        /// <summary>Fill for the guard region, a value no field can produce by accident.</summary>
        private const byte GuardFill = 0xA5;

        /// <summary>Largest entity or event count a single random pass builds.</summary>
        private const int MaxGeneratedCount = 512;

        private static readonly int[] HostileInts =
        {
            0, 1, -1, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1, 65535, -65536, 7, -7,
        };

        private static readonly float[] HostileFloats =
        {
            0f, -0f, 1f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            float.MaxValue, float.MinValue, float.Epsilon, -float.Epsilon, 1e-45f, -1e-45f,
            3.4028235e38f, -3.4028235e38f, 1.5f, -2.5f, 0.25f, -6.5f, 1e30f,
        };

        [Fact]
        public void SnapshotsNeverEscapeTheBufferTheGuestSized()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                SenseSnapshotWriter.Snapshot snapshot = Generate(rng);
                int needed = RequiredSize(snapshot);
                int capacity = PickCapacity(rng, needed);
                var storage = new byte[capacity + GuardBytes];
                for (int g = 0; g < storage.Length; g++)
                {
                    storage[g] = GuardFill;
                }

                int written = 0;
                long elapsed = FuzzDriver.Timed(() => written = SenseSnapshotWriter.Write(snapshot, new Span<byte>(storage, 0, capacity)));

                Assert.True(elapsed <= FuzzDriver.PerInputBudgetMs, FuzzDriver.Report(seed, i, Describe(snapshot, capacity),
                    "serializing one snapshot took " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms"));
                AssertWireContract(seed, i, snapshot, storage, capacity, needed, written);
            }
        }

        /// <summary>
        /// The boundaries a random pass reaches only by luck: a guest buffer
        /// one byte short of the header, one byte short of the last record,
        /// exactly the size of the snapshot, and a buffer of nothing at all.
        /// Each one is pinned so a regression in the fits check cannot hide
        /// behind the generator's count distribution.
        /// </summary>
        [Fact]
        public void CapacityBoundariesBehaveTheSameForEveryShape()
        {
            long seed = FuzzDriver.Seed();
            for (int shape = 0; shape < 4; shape++)
            {
                var snapshot = FixedSnapshot(shape);
                int needed = RequiredSize(snapshot);
                int[] capacities = shape == 0
                    ? new[] { 0, 1, SenseSnapshotWriter.HeaderSize - 1, SenseSnapshotWriter.HeaderSize }
                    : new[] { 0, 1, needed - 1, needed, needed + 1, needed + GuardBytes };
                for (int c = 0; c < capacities.Length; c++)
                {
                    int capacity = capacities[c];
                    if (capacity < 0)
                    {
                        continue;
                    }
                    var storage = new byte[capacity + GuardBytes];
                    for (int g = 0; g < storage.Length; g++)
                    {
                        storage[g] = GuardFill;
                    }
                    int written = SenseSnapshotWriter.Write(snapshot, new Span<byte>(storage, 0, capacity));
                    AssertWireContract(seed, shape * 100 + c, snapshot, storage, capacity, needed, written);
                }
            }
        }

        private static void AssertWireContract(long seed, int iteration, SenseSnapshotWriter.Snapshot snapshot, byte[] storage, int capacity, int needed, int written)
        {
            string because = Describe(snapshot, capacity);
            Assert.True(written >= 0 && written <= storage.Length, FuzzDriver.Report(seed, iteration, because,
                "returned " + written.ToString(CultureInfo.InvariantCulture) + " for a "
                + storage.Length.ToString(CultureInfo.InvariantCulture) + " byte buffer"));
            if (needed > capacity)
            {
                Assert.Equal(0, written);
            }
            else
            {
                Assert.Equal(needed, written);
                Assert.Equal(SenseSnapshotWriter.Magic, BitConverter.ToUInt32(storage, 0));
                Assert.Equal(unchecked((uint)snapshot.Records.Count), BitConverter.ToUInt32(storage, 4));
            }
            // Nothing past the returned length may have moved: the tail of an
            // oversized buffer and the whole guard region behind a full one.
            for (int i = written; i < storage.Length; i++)
            {
                Assert.Equal(GuardFill, storage[i]);
            }
        }

        /// <summary>Bytes the layout docstring says a snapshot of this shape occupies.</summary>
        private static int RequiredSize(SenseSnapshotWriter.Snapshot snapshot)
        {
            return SenseSnapshotWriter.HeaderSize
                + snapshot.Records.Count * SenseSnapshotWriter.RecordSize
                + (snapshot.Damage.Count + snapshot.BotInfo.Count) * SenseSnapshotWriter.EventSize;
        }

        /// <summary>
        /// A capacity that is sometimes too small, sometimes exact, and
        /// sometimes generous. A pure random pick almost never lands on the
        /// two interesting values (needed - 1 and needed), so they are
        /// weighted in rather than left to chance.
        /// </summary>
        private static int PickCapacity(Random rng, int needed)
        {
            switch (rng.Next(6))
            {
                case 0: return Math.Max(0, needed - 1);
                case 1: return needed;
                case 2: return needed + rng.Next(0, 128);
                case 3: return rng.Next(0, Math.Min(needed, 4096) + 1);
                case 4: return 0;
                default: return needed + GuardBytes;
            }
        }

        private static SenseSnapshotWriter.Snapshot Generate(Random rng)
        {
            // Big shapes stay rare: they are the slow ones, and one pass over
            // them per twenty is enough to cover the count arithmetic.
            int scale = rng.Next(8) == 0 ? MaxGeneratedCount : 8;
            int records = rng.Next(scale + 1);
            int damage = rng.Next(scale + 1);
            int botInfo = rng.Next(scale + 1);
            var snapshot = new SenseSnapshotWriter.Snapshot
            {
                Tick = rng.Next(2) == 0 ? rng.Next() : (long)rng.Next() << 32,
                SelfNetId = HostileInts[rng.Next(HostileInts.Length)],
                WorldTime = rng.Next(2) == 0 ? rng.Next() : (long)rng.Next() << 32,
                BloodMoon = rng.Next(2) == 0,
            };
            for (int i = 0; i < records; i++)
            {
                snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord
                {
                    NetId = HostileInts[rng.Next(HostileInts.Length)],
                    Kind = (byte)rng.Next(0, 256),
                    IsSelf = rng.Next(2) == 0,
                    Alive = rng.Next(2) == 0,
                    X = HostileFloats[rng.Next(HostileFloats.Length)],
                    Y = HostileFloats[rng.Next(HostileFloats.Length)],
                    Z = HostileFloats[rng.Next(HostileFloats.Length)],
                    Hp = HostileFloats[rng.Next(HostileFloats.Length)],
                    Yaw = HostileFloats[rng.Next(HostileFloats.Length)],
                    Vy = HostileFloats[rng.Next(HostileFloats.Length)],
                    TargetId = HostileInts[rng.Next(HostileInts.Length)],
                    Wearing = (byte)rng.Next(0, 256),
                });
            }
            for (int i = 0; i < damage; i++)
            {
                snapshot.Damage.Add(new SenseSnapshotWriter.DamageEvent
                {
                    Attacker = HostileInts[rng.Next(HostileInts.Length)],
                    Victim = HostileInts[rng.Next(HostileInts.Length)],
                    Amount = HostileFloats[rng.Next(HostileFloats.Length)],
                });
            }
            for (int i = 0; i < botInfo; i++)
            {
                snapshot.BotInfo.Add(new SenseSnapshotWriter.BotInfoEvent
                {
                    NetId = HostileInts[rng.Next(HostileInts.Length)],
                    // The wire field is a u8, so a value past 255 is a
                    // truncation the writer must perform, not an overflow.
                    WeaponId = rng.Next(2) == 0 ? rng.Next(-256, 512) : rng.Next(0, 256),
                });
            }
            return snapshot;
        }

        /// <summary>One fixed shape per index, so the boundary test is not a coin flip.</summary>
        private static SenseSnapshotWriter.Snapshot FixedSnapshot(int shape)
        {
            var snapshot = new SenseSnapshotWriter.Snapshot
            {
                Tick = 1234,
                SelfNetId = shape,
                WorldTime = 5678,
                BloodMoon = shape % 2 == 0,
            };
            for (int i = 0; i < shape; i++)
            {
                snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord { NetId = i, Kind = SenseSnapshotWriter.KindBot, Alive = true, Vy = -6.5f });
            }
            for (int i = 0; i < shape; i++)
            {
                snapshot.Damage.Add(new SenseSnapshotWriter.DamageEvent { Attacker = i, Victim = i + 1, Amount = float.NaN });
                snapshot.BotInfo.Add(new SenseSnapshotWriter.BotInfoEvent { NetId = i, WeaponId = 255 });
            }
            return snapshot;
        }

        private static string Describe(SenseSnapshotWriter.Snapshot snapshot, int capacity)
        {
            return "records=" + snapshot.Records.Count.ToString(CultureInfo.InvariantCulture)
                + " damage=" + snapshot.Damage.Count.ToString(CultureInfo.InvariantCulture)
                + " botinfo=" + snapshot.BotInfo.Count.ToString(CultureInfo.InvariantCulture)
                + " capacity=" + capacity.ToString(CultureInfo.InvariantCulture);
        }
    }
}
