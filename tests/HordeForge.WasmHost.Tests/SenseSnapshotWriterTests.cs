using System;
using HordeForge.WasmHost.Abi;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Byte-level contract of the zdtd sense snapshot ('ZBS4', BOTS_SPEC v4,
    /// zdtd ADR 0037): the layout is kept identical to the sibling zdtd-server
    /// wire format so unmodified plugins parse it. These tests pin every
    /// documented field offset from the SenseSnapshotWriter docstring; a
    /// drift here is a silent ABI break guests read as garbage.
    /// </summary>
    public sealed class SenseSnapshotWriterTests
    {
        private static uint FloatBits(float value)
        {
            return BitConverter.SingleToUInt32Bits(value);
        }

        private static uint ReadU32(byte[] buffer, int offset)
        {
            return BitConverter.ToUInt32(buffer, offset);
        }

        [Fact]
        public void HeaderAndRecordOffsetsMatchContract()
        {
            var snapshot = new SenseSnapshotWriter.Snapshot
            {
                Tick = 0x01020304,
                SelfNetId = -7,
                WorldTime = 0x1_0000_0005, // low 32 bits on the wire
                BloodMoon = true,
                Records =
                {
                    new SenseSnapshotWriter.EntityRecord
                    {
                        NetId = 42,
                        Kind = SenseSnapshotWriter.KindZombie,
                        IsSelf = false,
                        Alive = true,
                        X = 1.5f,
                        Y = -2.5f,
                        Z = 3.5f,
                        Hp = 75f,
                        Yaw = 0.25f,
                        Vy = -6.5f,
                        TargetId = 900,
                        Wearing = 1,
                    },
                },
            };
            var buffer = new byte[SenseSnapshotWriter.HeaderSize + SenseSnapshotWriter.RecordSize];

            int written = SenseSnapshotWriter.Write(snapshot, buffer);

            Assert.Equal(buffer.Length, written);

            Assert.Equal(SenseSnapshotWriter.Magic, ReadU32(buffer, 0)); // 'ZBS4'
            Assert.Equal(1u, ReadU32(buffer, 4));                        // count
            Assert.Equal(0x01020304u, ReadU32(buffer, 8));               // tick
            Assert.Equal(unchecked((uint)-7), ReadU32(buffer, 12));      // self net id
            Assert.Equal(5u, ReadU32(buffer, 16));                       // world time low bits
            Assert.Equal(1u, ReadU32(buffer, 20));                       // blood moon

            int r = SenseSnapshotWriter.HeaderSize;
            Assert.Equal(42u, ReadU32(buffer, r + 0));                   // net id
            Assert.Equal(SenseSnapshotWriter.KindZombie, buffer[r + 4]);
            Assert.Equal(0, buffer[r + 5]);                              // is_self
            Assert.Equal(1, buffer[r + 6]);                              // alive
            Assert.Equal(0, buffer[r + 7]);                              // pad
            Assert.Equal(FloatBits(1.5f), ReadU32(buffer, r + 8));
            Assert.Equal(FloatBits(-2.5f), ReadU32(buffer, r + 12));
            Assert.Equal(FloatBits(3.5f), ReadU32(buffer, r + 16));
            Assert.Equal(FloatBits(75f), ReadU32(buffer, r + 20));
            Assert.Equal(FloatBits(0.25f), ReadU32(buffer, r + 24));
            Assert.Equal(FloatBits(-6.5f), ReadU32(buffer, r + 28));     // vy (f32 bits, v4)
            Assert.Equal(900u, ReadU32(buffer, r + 32));                 // target id
            Assert.Equal(1, buffer[r + 36]);                             // wearing (v4)
            Assert.Equal(0, buffer[r + 37]);                             // pad
            Assert.Equal(0, buffer[r + 38]);                             // pad
            Assert.Equal(0, buffer[r + 39]);                             // pad
        }

        [Fact]
        public void EmptySnapshotWritesHeaderOnly()
        {
            var buffer = new byte[SenseSnapshotWriter.HeaderSize];
            int written = SenseSnapshotWriter.Write(new SenseSnapshotWriter.Snapshot { Tick = 9 }, buffer);

            Assert.Equal(SenseSnapshotWriter.HeaderSize, written);
            Assert.Equal(0u, ReadU32(buffer, 4));
            Assert.Equal(9u, ReadU32(buffer, 8));
        }

        [Fact]
        public void DamageEventLayoutMatchesContract()
        {
            var snapshot = new SenseSnapshotWriter.Snapshot();
            snapshot.Damage.Add(new SenseSnapshotWriter.DamageEvent { Attacker = 42, Victim = -1, Amount = 12.5f });
            var buffer = new byte[SenseSnapshotWriter.HeaderSize + SenseSnapshotWriter.EventSize];

            int written = SenseSnapshotWriter.Write(snapshot, buffer);

            Assert.Equal(buffer.Length, written);
            int e = SenseSnapshotWriter.HeaderSize;
            Assert.Equal(SenseSnapshotWriter.KindEventDamage, buffer[e + 0]);
            Assert.Equal(0, buffer[e + 1]);
            Assert.Equal(0, buffer[e + 2]);
            Assert.Equal(0, buffer[e + 3]);
            Assert.Equal(42u, ReadU32(buffer, e + 4));
            Assert.Equal(unchecked((uint)-1), ReadU32(buffer, e + 8));
            Assert.Equal(FloatBits(12.5f), ReadU32(buffer, e + 12));
        }

        [Fact]
        public void BotInfoEventLayoutMatchesContract()
        {
            var snapshot = new SenseSnapshotWriter.Snapshot();
            snapshot.BotInfo.Add(new SenseSnapshotWriter.BotInfoEvent { NetId = 77, WeaponId = 2 });
            var buffer = new byte[SenseSnapshotWriter.HeaderSize + SenseSnapshotWriter.EventSize];

            int written = SenseSnapshotWriter.Write(snapshot, buffer);

            Assert.Equal(buffer.Length, written);
            int e = SenseSnapshotWriter.HeaderSize;
            Assert.Equal(SenseSnapshotWriter.KindEventBotInfo, buffer[e + 0]);
            Assert.Equal(2, buffer[e + 1]);                              // weapon id
            Assert.Equal(0, buffer[e + 2]);
            Assert.Equal(0, buffer[e + 3]);
            Assert.Equal(77u, ReadU32(buffer, e + 4));
            Assert.Equal(0u, ReadU32(buffer, e + 8));                    // unused
            Assert.Equal(0u, ReadU32(buffer, e + 12));                   // unused
        }

        [Fact]
        public void ExactlySizedBufferFitsAndShortBufferReportsNoData()
        {
            var snapshot = new SenseSnapshotWriter.Snapshot();
            snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord { NetId = 1 });
            int needed = SenseSnapshotWriter.HeaderSize + SenseSnapshotWriter.RecordSize;

            // The boundary: a buffer of exactly the serialized size fits;
            // one byte less reports "no data" instead of truncating.
            Assert.Equal(needed, SenseSnapshotWriter.Write(snapshot, new byte[needed]));
            Assert.Equal(0, SenseSnapshotWriter.Write(snapshot, new byte[needed - 1]));
        }

        [Fact]
        public void RecordCountTooLargeForIntArithmeticReportsNoData()
        {
            // 60M records is 2.4 GB serialized, past int.MaxValue: a count
            // whose size was computed in int would wrap negative, pass a
            // narrower fits check, and write past the end of the caller's
            // buffer. The size is summed in long, so the snapshot is
            // refused instead. The list holds one shared record, so the cost
            // is the pointer array, not 60M objects.
            var snapshot = new SenseSnapshotWriter.Snapshot();
            var record = new SenseSnapshotWriter.EntityRecord { NetId = 1 };
            for (int i = 0; i < 60_000_000; i++)
            {
                snapshot.Records.Add(record);
            }
            var buffer = new byte[SenseSnapshotWriter.HeaderSize];
            Assert.Equal(0, SenseSnapshotWriter.Write(snapshot, buffer));
            // Refused before any write: the header magic is still untouched.
            Assert.Equal(0u, ReadU32(buffer, 0));
        }

        [Fact]
        public void ExactFitIsComputedOverRecordsAndBothEventKinds()
        {
            // The wire order is records, then damage events, then bot-info
            // events, and the fits check has to add up all three; a size
            // formula that counted only the records would report "no data"
            // for a buffer that holds the whole snapshot, and one that
            // counted only events would write records past the end.
            var snapshot = new SenseSnapshotWriter.Snapshot();
            for (int i = 0; i < 3; i++)
            {
                snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord { NetId = i });
            }
            snapshot.Damage.Add(new SenseSnapshotWriter.DamageEvent { Attacker = 1, Victim = 2, Amount = 3f });
            snapshot.BotInfo.Add(new SenseSnapshotWriter.BotInfoEvent { NetId = 4, WeaponId = 5 });
            int needed = SenseSnapshotWriter.HeaderSize
                         + 3 * SenseSnapshotWriter.RecordSize
                         + 2 * SenseSnapshotWriter.EventSize;

            Assert.Equal(needed, SenseSnapshotWriter.Write(snapshot, new byte[needed]));
            Assert.Equal(0, SenseSnapshotWriter.Write(snapshot, new byte[needed - 1]));

            // The events follow the records, in the documented order, so a
            // plugin reading the stream sees them where its own parser
            // expects them.
            var buffer = new byte[needed];
            SenseSnapshotWriter.Write(snapshot, buffer);
            int firstRecord = SenseSnapshotWriter.HeaderSize;
            int firstEvent = firstRecord + 3 * SenseSnapshotWriter.RecordSize;
            Assert.Equal(3u, ReadU32(buffer, 4));
            Assert.Equal(0u, ReadU32(buffer, firstRecord + 0));
            Assert.Equal(1u, ReadU32(buffer, firstRecord + SenseSnapshotWriter.RecordSize + 0));
            Assert.Equal(2u, ReadU32(buffer, firstRecord + 2 * SenseSnapshotWriter.RecordSize + 0));
            Assert.Equal(SenseSnapshotWriter.KindEventDamage, buffer[firstEvent + 0]);
            Assert.Equal(1u, ReadU32(buffer, firstEvent + 4));
            Assert.Equal(2u, ReadU32(buffer, firstEvent + 8));
            Assert.Equal(FloatBits(3f), ReadU32(buffer, firstEvent + 12));
            Assert.Equal(SenseSnapshotWriter.KindEventBotInfo, buffer[firstEvent + SenseSnapshotWriter.EventSize + 0]);
            Assert.Equal(5, buffer[firstEvent + SenseSnapshotWriter.EventSize + 1]);
            Assert.Equal(4u, ReadU32(buffer, firstEvent + SenseSnapshotWriter.EventSize + 4));
        }

        [Fact]
        public void EmptySnapshotFitsAHeaderOnlyBufferAndNothingLess()
        {
            // The zero-record case is the lower end of the same fits check:
            // it must still refuse a buffer one byte short of the header.
            var snapshot = new SenseSnapshotWriter.Snapshot();
            Assert.Equal(SenseSnapshotWriter.HeaderSize,
                SenseSnapshotWriter.Write(snapshot, new byte[SenseSnapshotWriter.HeaderSize]));
            Assert.Equal(0, SenseSnapshotWriter.Write(snapshot, new byte[SenseSnapshotWriter.HeaderSize - 1]));
        }

        [Fact]
        public void ClearDropsEveryRecordAndEvent()
        {
            // The bridge reuses one snapshot per mod for the life of the
            // server; a Clear that left any list populated would append the
            // next tick's world onto the previous one, and the fits check
            // would then report "no data" forever.
            var snapshot = new SenseSnapshotWriter.Snapshot { Tick = 5 };
            snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord { NetId = 1 });
            snapshot.Damage.Add(new SenseSnapshotWriter.DamageEvent { Attacker = 1, Victim = 2 });
            snapshot.BotInfo.Add(new SenseSnapshotWriter.BotInfoEvent { NetId = 3 });

            snapshot.Clear();

            Assert.Empty(snapshot.Records);
            Assert.Empty(snapshot.Damage);
            Assert.Empty(snapshot.BotInfo);
            // The header fields are the caller's, not the snapshot's.
            Assert.Equal(5, snapshot.Tick);
            int written = SenseSnapshotWriter.Write(snapshot, new byte[SenseSnapshotWriter.HeaderSize]);
            Assert.Equal(SenseSnapshotWriter.HeaderSize, written);
            Assert.Equal(0u, ReadU32(new byte[SenseSnapshotWriter.HeaderSize], 4));
        }

        [Fact]
        public void NullSnapshotIsRejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => SenseSnapshotWriter.Write(null!, new byte[SenseSnapshotWriter.HeaderSize]));
        }
    }
}
