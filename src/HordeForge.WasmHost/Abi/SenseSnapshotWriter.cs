using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HordeForge.WasmHost.Abi
{
    /// <summary>
    /// Builds the binary world snapshot the zdtd `sense` import fills into a
    /// guest buffer. The format is the sibling zdtd-server contract
    /// (BOTS_SPEC sense layout, kept byte-identical so plugins written
    /// against it, like the unmodified fps_bot, parse it unchanged):
    ///
    ///   header 24 bytes:  magic u32 'ZBS3' @0, count u32 @4, tick u32 @8,
    ///                      self_net_id i32 @12, world_time u32 @16,
    ///                      blood_moon u32 @20
    ///   records 32 bytes each: net_id i32 @0, kind u8 @4, is_self u8 @5,
    ///                      alive u8 @6, pad @7, x f32 @8, y f32 @12,
    ///                      z f32 @16, hp f32 @20, yaw f32 @24,
    ///                      target_id i32 @28
    ///   events 16 bytes each: kind u8 @0 (3 damage, 4 bot-info). Damage:
    ///                      attacker i32 @4, victim i32 @8, amount f32 @12.
    ///                      Bot info: weapon id u8 @1, bot net id i32 @4,
    ///                      remaining bytes zero.
    ///
    /// Kinds: 0 player, 1 zombie, 2 bot, 3 damage event, 4 bot-info event.
    /// All integers little-endian, floats IEEE-754 binary32.
    /// </summary>
    public static class SenseSnapshotWriter
    {
        /// <summary>Snapshot header size in bytes (24).</summary>
        public const int HeaderSize = 24;

        /// <summary>Per-entity record size in bytes (32).</summary>
        public const int RecordSize = 32;

        /// <summary>Per-event record size in bytes (16).</summary>
        public const int EventSize = 16;

        /// <summary>Snapshot magic 'ZBS3'.</summary>
        public const uint Magic = 0x3353425a; // 'ZBS3'

        /// <summary>Entity kind: human player.</summary>
        public const byte KindPlayer = 0;

        /// <summary>Entity kind: zombie.</summary>
        public const byte KindZombie = 1;

        /// <summary>Entity kind: bot.</summary>
        public const byte KindBot = 2;

        /// <summary>Event kind: damage (attacker, victim, amount).</summary>
        public const byte KindEventDamage = 3;

        /// <summary>Event kind: bot loadout info (net id, weapon id).</summary>
        public const byte KindEventBotInfo = 4;

        /// <summary>One entity in the sense snapshot (record layout above).</summary>
        public sealed class EntityRecord
        {
            /// <summary>Network id used to key the entity.</summary>
            public int NetId;
            /// <summary>Entity kind (player, zombie, bot).</summary>
            public byte Kind;
            /// <summary>True for the calling bot itself.</summary>
            public bool IsSelf;
            /// <summary>True while the entity is alive.</summary>
            public bool Alive;
            /// <summary>World x position in blocks.</summary>
            public float X;
            /// <summary>World y position in blocks.</summary>
            public float Y;
            /// <summary>World z position in blocks.</summary>
            public float Z;
            /// <summary>Hit points.</summary>
            public float Hp;
            /// <summary>Facing in radians (yaw zero faces +X).</summary>
            public float Yaw;
            /// <summary>Current target net id, or 0.</summary>
            public int TargetId;
        }

        /// <summary>Damage event trailer record.</summary>
        public sealed class DamageEvent
        {
            /// <summary>Net id of the attacker.</summary>
            public int Attacker;
            /// <summary>Net id of the victim.</summary>
            public int Victim;
            /// <summary>Damage amount.</summary>
            public float Amount;
        }

        /// <summary>Bot loadout info event record.</summary>
        public sealed class BotInfoEvent
        {
            /// <summary>Network id used to key the entity.</summary>
            public int NetId;
            /// <summary>Host loadout pool index (pistol 0 ... sniper 3, etc).</summary>
            public int WeaponId;
        }

        /// <summary>World snapshot to serialize (header fields plus records and events).</summary>
        public sealed class Snapshot
        {
            /// <summary>Game tick of the snapshot.</summary>
            public long Tick;
            /// <summary>Net id of the calling bot.</summary>
            public int SelfNetId;
            /// <summary>World time in game minutes (low 32 bits on the wire).</summary>
            public long WorldTime;
            /// <summary>True during a blood moon night.</summary>
            public bool BloodMoon;
            /// <summary>Entity records (players, zombies, bots).</summary>
            public System.Collections.Generic.List<EntityRecord> Records = new System.Collections.Generic.List<EntityRecord>();
            /// <summary>Damage events for the tick.</summary>
            public System.Collections.Generic.List<DamageEvent> Damage = new System.Collections.Generic.List<DamageEvent>();
            /// <summary>Bot loadout info events for the tick.</summary>
            public System.Collections.Generic.List<BotInfoEvent> BotInfo = new System.Collections.Generic.List<BotInfoEvent>();

            /// <summary>Drops all records and events so the snapshot can be reused for the next call.</summary>
            public void Clear()
            {
                Records.Clear();
                Damage.Clear();
                BotInfo.Clear();
            }
        }

        /// <summary>
        /// Serializes a snapshot into the buffer. Returns the byte count, or
        /// 0 when the snapshot does not fit. The caller passes a buffer sized
        /// to the guest's out_cap.
        /// </summary>
        public static int Write(Snapshot snapshot, Span<byte> buffer)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            int total = HeaderSize + snapshot.Records.Count * RecordSize +
                        (snapshot.Damage.Count + snapshot.BotInfo.Count) * EventSize;
            if (total > buffer.Length)
            {
                return 0;
            }

            int pos = 0;
            WriteU32(buffer, ref pos, Magic);
            WriteU32(buffer, ref pos, (uint)snapshot.Records.Count);
            WriteU32(buffer, ref pos, (uint)snapshot.Tick);
            WriteI32(buffer, ref pos, snapshot.SelfNetId);
            WriteU32(buffer, ref pos, (uint)snapshot.WorldTime);
            WriteU32(buffer, ref pos, snapshot.BloodMoon ? 1u : 0u);

            foreach (var r in snapshot.Records)
            {
                WriteI32(buffer, ref pos, r.NetId);
                buffer[pos++] = r.Kind;
                buffer[pos++] = r.IsSelf ? (byte)1 : (byte)0;
                buffer[pos++] = r.Alive ? (byte)1 : (byte)0;
                buffer[pos++] = 0; // pad
                WriteF32(buffer, ref pos, r.X);
                WriteF32(buffer, ref pos, r.Y);
                WriteF32(buffer, ref pos, r.Z);
                WriteF32(buffer, ref pos, r.Hp);
                WriteF32(buffer, ref pos, r.Yaw);
                WriteI32(buffer, ref pos, r.TargetId);
            }

            foreach (var e in snapshot.Damage)
            {
                buffer[pos++] = KindEventDamage;
                buffer[pos++] = 0;
                buffer[pos++] = 0;
                buffer[pos++] = 0;
                WriteI32(buffer, ref pos, e.Attacker);
                WriteI32(buffer, ref pos, e.Victim);
                WriteF32(buffer, ref pos, e.Amount);
            }

            foreach (var e in snapshot.BotInfo)
            {
                buffer[pos++] = KindEventBotInfo;
                buffer[pos++] = (byte)e.WeaponId;
                buffer[pos++] = 0;
                buffer[pos++] = 0;
                WriteI32(buffer, ref pos, e.NetId);
                WriteI32(buffer, ref pos, 0); // unused
                WriteF32(buffer, ref pos, 0f); // unused
            }

            return pos;
        }

        private static void WriteU32(Span<byte> b, ref int p, uint v)
        {
            b[p++] = (byte)(v & 0xff);
            b[p++] = (byte)((v >> 8) & 0xff);
            b[p++] = (byte)((v >> 16) & 0xff);
            b[p++] = (byte)((v >> 24) & 0xff);
        }

        private static void WriteI32(Span<byte> b, ref int p, int v)
        {
            WriteU32(b, ref p, unchecked((uint)v));
        }

        private static void WriteF32(Span<byte> b, ref int p, float v)
        {
            FloatBits bits = default;
            bits.Float = v;
            WriteU32(b, ref p, bits.UInt32);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public uint UInt32;
        }
    }
}
