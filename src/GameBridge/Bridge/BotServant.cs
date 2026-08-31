using System;
using System.Collections.Generic;
using HordeForge.WasmHost.Abi;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// The bot servant: the game side of the zdtd fps_bot contract
    /// (docs/ABI.md zdtd compatibility section). It spawns bot entities,
    /// applies the SimCommands the guest brain queues (bot move / look /
    /// shoot / spawn / remove / count / skill / cfg, plus glide for the
    /// parachute mod), and builds the 'ZBS4' world snapshot the sense import
    /// fills. The brain owns targeting and aim; the servant owns the bodies.
    ///
    /// Stage 2 status: spawn, move, look, shoot, and sense are implemented;
    /// cover/path queries still return no answer and on_admin_command is
    /// not yet wired to the console.
    /// </summary>
    public sealed class BotServant
    {
        private const string BotEntityClass = "zombieSoldier";
        private const int DefaultBotCount = 4;

        // Hard ceiling on live servant bots, matching the zdtd-server host
        // cap (max_bots 16) and the "bot count" clamp. Spawn requests beyond
        // the cap are refused: entity creation is game-side work a hostile
        // guest must not be able to multiply without bound.
        private const int MaxBotCount = 16;

        // The brain speaks radians; the game speaks degrees.
        private const float RadiansToDegrees = 57.2957795f;

        // Weapon pool damage (index matches the brain's weapon ids:
        // pistol 0, shotgun 1, ak 2, sniper 3, auto 4, smg 5).
        private static readonly int[] WeaponDamage = { 12, 18, 14, 45, 12, 10 };

        // Glider item tag (matches the parachute mod's items.xml patch and
        // preset.toml [rules.glide] item_tag). A worn item whose ItemClass
        // carries this tag sets the sense v4 wearing_glider bit.
        private const string GliderItemTag = "parachute";

        // Buff applied to a player while the parachute mod's glide flag is
        // armed. Defined by the playtest parachute-items modlet (buffs.xml);
        // its effect is skipping fall damage in buffPlayerFallingDamage, the
        // safe landing on the stock server.
        private const string GlideBuffName = "buffParachuteGlide";

        // Entity records per sense snapshot. With the v4 40-byte records a
        // 2048-byte guest sense cap holds 41 records after reserving the
        // 384-byte event trailer (24 + 41 * 40 + 24 * 16 = 2048), the same
        // sizing zdtd uses for that cap.
        private const int MaxSenseRecords = 41;

        private readonly Func<long> _tickProvider;
        private readonly HashSet<int> _bots = new HashSet<int>();
        private readonly Dictionary<int, float> _botYaw = new Dictionary<int, float>();
        // Sense runs once per
        // tick per calling brain; the snapshot and its entity records are
        // pooled and refilled per call instead of being reallocated every
        // time (single main-loop thread by contract).
        private readonly SenseSnapshotWriter.Snapshot _sense = new SenseSnapshotWriter.Snapshot();
        private readonly SenseSnapshotWriter.EntityRecord[] _senseRecords = CreateSenseRecords();
        // Armed gliders (ADR 0037 `glide <net_id> <0|1>`): net id -> armed.
        // The real game has no C2S movement envelope to exempt, so this is
        // tracked as the mod's authority state and surfaced in "wasm status";
        // the parachute deploy/land state machine still runs correctly.
        private readonly Dictionary<int, bool> _glide = new Dictionary<int, bool>();
        private int _countFloor = DefaultBotCount;

        // Minimum interval between floor top-up passes. EnsureSpawned runs
        // from every sense request; without the throttle a world where
        // spawning persistently fails (entity cap reached, shutdown in
        // progress) would retry and warn at sense rate.
        private const int TopUpIntervalMs = 1000;
        private int _lastTopUpMs = int.MinValue;

        /// <summary>
        /// Creates the servant. <paramref name="tickProvider"/> supplies the
        /// bridge's monotonic tick counter for sense snapshots; injecting it
        /// keeps the servant free of a reference back into BridgeHost.
        /// </summary>
        public BotServant(Func<long> tickProvider)
        {
            _tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
        }

        private static SenseSnapshotWriter.EntityRecord[] CreateSenseRecords()
        {
            var records = new SenseSnapshotWriter.EntityRecord[MaxSenseRecords];
            for (int i = 0; i < records.Length; i++)
            {
                records[i] = new SenseSnapshotWriter.EntityRecord();
            }
            return records;
        }

        /// <summary>
        /// Handles one queued SimCommand. Returns true when the command was
        /// accepted. <paramref name="handled"/> reports whether the command
        /// belonged to the servant surface (bot or glide verbs) at all, so
        /// the caller can tell a rejected servant command from text that was
        /// never ours (chat announce).
        /// </summary>
        public bool TryQueue(string command, out bool handled)
        {
            handled = false;
            if (command == null)
            {
                return false;
            }
            if (command.StartsWith("bot ", StringComparison.Ordinal))
            {
                handled = true;
                return TryQueueBot(command);
            }
            if (command.StartsWith("glide ", StringComparison.Ordinal))
            {
                handled = true;
                return TryQueueGlide(command);
            }
            return false;
        }

        /// <summary>
        /// Handles `glide &lt;net_id&gt; &lt;0|1|on|true|off|false&gt;` (ADR 0037,
        /// the parachute mod's queue verb): tracks the player's glide flag.
        /// The parse mirrors zdtd exactly (arm values "1"/"on"/"true", clear
        /// values "0"/"off"/"false", anything else is malformed and dropped).
        /// </summary>
        private bool TryQueueGlide(string command)
        {
            string[] parts = command.Split(' ');
            if (parts.Length != 3)
            {
                Log.Out("[WasmHost] glide (malformed): " + command);
                return true;
            }
            if (!TryParseId(parts[1], out int netId))
            {
                Log.Out("[WasmHost] glide (bad id): " + command);
                return true;
            }
            string on = parts[2];
            if (on == "1" || on == "on" || on == "true")
            {
                _glide[netId] = true;
            }
            else if (on == "0" || on == "off" || on == "false")
            {
                _glide[netId] = false;
            }
            else
            {
                Log.Out("[WasmHost] glide (bad flag): " + command);
                return true;
            }
            ApplyGlideBuff(netId, _glide[netId]);
            Log.Out("[WasmHost] glide " + netId + " " + (_glide[netId] ? "armed" : "cleared"));
            return true;
        }

        /// <summary>
        /// Applies or removes the glide buff on the player while the parachute
        /// mod's glide flag is armed. The buff (with the playtest buffs.xml
        /// patch) makes the stock client skip fall damage, which is the
        /// parachute's safe landing on the real server (the mod itself only
        /// arms/clears the flag). Best effort: a player that left the world
        /// is skipped, never an error.
        /// </summary>
        private void ApplyGlideBuff(int netId, bool armed)
        {
            try
            {
                var game = GameManager.Instance;
                if (game == null || game.World == null)
                {
                    return;
                }
                if (!(game.World.GetEntity(netId) is EntityAlive alive))
                {
                    return;
                }
                if (alive.Buffs == null)
                {
                    return;
                }
                if (armed)
                {
                    // netSync true so the client applies the fall-damage gate.
                    alive.Buffs.AddBuff(GlideBuffName, 0, true, false, -1f);
                    Log.Out("[WasmHost] glide buff applied " + GlideBuffName + " to " + netId +
                            " has=" + alive.Buffs.HasBuff(GlideBuffName));
                }
                else
                {
                    alive.Buffs.RemoveBuff(GlideBuffName, 0, true);
                    Log.Out("[WasmHost] glide buff removed " + GlideBuffName + " from " + netId);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[WasmHost] glide buff " + netId + " failed: " + ex.Message);
            }
        }

        private bool TryQueueBot(string command)
        {
            string[] parts = command.Split(' ');
            string verb = parts.Length > 1 ? parts[1] : string.Empty;
            try
            {
                switch (verb)
                {
                    case "spawn":
                        SpawnOne();
                        return true;
                    case "remove":
                        RemoveBots(parts);
                        return true;
                    case "count":
                        if (parts.Length > 2 && TryParseId(parts[2], out int n) && n >= 0 && n <= MaxBotCount)
                        {
                            _countFloor = n;
                            EnsureSpawned();
                        }
                        return true;
                    case "move":
                        MoveBot(parts);
                        return true;
                    case "look":
                        LookBot(parts);
                        return true;
                    case "shoot":
                        ShootBot(parts);
                        return true;
                    case "skill":
                    case "cfg":
                        // The guest keeps its own per-slot skill and personality
                        // state; the servant acknowledges and logs the policy.
                        string policy = parts.Length > 2
                            ? string.Join(" ", parts, 2, parts.Length - 2)
                            : string.Empty;
                        Log.Out("[WasmHost] bot " + verb + ": " + policy);
                        return true;
                    default:
                        Log.Out("[WasmHost] bot cmd (unknown verb '" + verb + "'): " + command);
                        return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[WasmHost] bot " + verb + " failed: " + ex);
                return false;
            }
        }

        /// <summary>Armed glide flags by net id (ADR 0037); exposed for "wasm status".</summary>
        public IReadOnlyDictionary<int, bool> Glide => _glide;

        /// <summary>
        /// Clears every armed glide flag. Called when a module that armed
        /// them is reloaded or disabled (zdtd: withdrawn modules have their
        /// applied glide cleared; fail closed).
        /// </summary>
        public void ClearGlide()
        {
            _glide.Clear();
        }

        public int WriteSense(Span<byte> buffer)
        {
            EnsureSpawned();
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return 0;
            }
            SenseSnapshotWriter.Snapshot snapshot = _sense;
            try
            {
                var entities = game.World.Entities;
                if (entities == null || entities.list == null)
                {
                    return 0;
                }
                snapshot.Clear();
                snapshot.Tick = _tickProvider();
                snapshot.SelfNetId = 0;
                snapshot.WorldTime = (long)game.World.GetWorldTime();
                snapshot.BloodMoon = false;
                var records = _senseRecords;
                foreach (Entity e in entities.list)
                {
                    if (!(e is EntityAlive alive) || alive.IsDead())
                    {
                        continue;
                    }
                    if (snapshot.Records.Count >= MaxSenseRecords)
                    {
                        break;
                    }
                    SenseSnapshotWriter.EntityRecord record = records[snapshot.Records.Count];
                    record.NetId = e.entityId;
                    record.Kind = Classify(e);
                    record.IsSelf = _bots.Contains(e.entityId);
                    record.Alive = true;
                    record.X = e.position.x;
                    record.Y = e.position.y;
                    record.Z = e.position.z;
                    record.Hp = alive.Health;
                    record.Yaw = _botYaw.TryGetValue(e.entityId, out float yaw) ? yaw : 0f;
                    record.Vy = VerticalVelocity(e.entityId, e.position, _tickProvider(), out UnityEngine.Vector3 prevPos);
                    record.Wearing = WearsGlider(alive);
                    record.TargetId = 0;
                    snapshot.Records.Add(record);
                    ClampGlideDescent(alive, record.Vy, e.position, prevPos);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[WasmHost] sense failed: " + ex.Message);
                return 0;
            }
            return SenseSnapshotWriter.Write(snapshot, buffer);
        }

        /// <summary>
        /// Current vertical velocity in blocks/s (negative = falling), derived
        /// from the server-side position history. The stock dedicated server
        /// does not populate `Entity.motion` for remote players (the client
        /// owns its own local physics, ADR 0037), so the sense v4 `vy` field
        /// is computed here from the per-tick position delta - the same
        /// approach zdtd uses. The stored position only advances when the
        /// game tick changes, so every module reading sense within one tick
        /// sees the same vy. A teleport-scale jump is reported once (bounded
        /// by the 10-tick delta cap) and never reads as a sustained fall.
        /// </summary>
        private readonly Dictionary<int, (long Tick, UnityEngine.Vector3 Pos)> _lastPos =
            new Dictionary<int, (long, UnityEngine.Vector3)>();

        private float VerticalVelocity(int netId, UnityEngine.Vector3 position, long tick, out UnityEngine.Vector3 prevPos)
        {
            prevPos = position;
            float vy = 0f;
            if (_lastPos.TryGetValue(netId, out (long Tick, UnityEngine.Vector3 Pos) last))
            {
                prevPos = last.Pos;
                long dtTicks = tick - last.Tick;
                if (dtTicks > 0 && dtTicks <= 10)
                {
                    // 20 TPS bridge tick; blocks per second.
                    vy = (position.y - last.Pos.y) / (dtTicks * 0.05f);
                }
            }
            if (!_lastPos.TryGetValue(netId, out (long Tick, UnityEngine.Vector3 Pos) current) || current.Tick != tick)
            {
                _lastPos[netId] = (tick, position);
            }
            return vy;
        }

        /// <summary>
        /// The glide fall sink (blocks/s, negative = down): while a player's
        /// glide flag is armed the descent is capped at this rate, the
        /// real-server equivalent of the zdtd [rules.glide] sink_vy_mps clamp
        /// (the stock server has no C2S movement envelope to exempt, so the
        /// bridge clamps the server entity position and the client follows
        /// the corrections). Matches the parachute preset's sink_vy_mps.
        /// </summary>
        private const float SinkVyMps = 2.5f;

        /// <summary>
        /// Caps a gliding player's descent at the sink rate by nudging the
        /// entity up when it dropped too far since the previous tick. The
        /// sense record keeps the real vy (the parachute mod arms on it);
        /// the correction applies for the next tick, so the glide falls
        /// slowly and lands safely. Best effort: only while the glide flag
        /// is armed, never for anyone else.
        /// </summary>
        private void ClampGlideDescent(EntityAlive alive, float vy, UnityEngine.Vector3 position, UnityEngine.Vector3 prevPos)
        {
            if (!_glide.TryGetValue(alive.entityId, out bool armed) || !armed)
            {
                return;
            }
            if (vy >= -SinkVyMps)
            {
                return;
            }
            float maxDrop = SinkVyMps * 0.05f; // blocks per 20 TPS tick
            float floorY = prevPos.y - maxDrop;
            if (position.y < floorY)
            {
                alive.SetPosition(new UnityEngine.Vector3(position.x, floorY, position.z), true);
            }
        }

        /// <summary>
        /// True when the entity wears an item whose ItemClass carries the
        /// glider tag (sense v4 wearing_glider, ADR 0037). Mirrors zdtd's
        /// armor-slot tag scan; the tag name matches the parachute mod's
        /// items.xml patch. Defensive: an equipment read failure reports 0
        /// rather than killing the snapshot.
        /// </summary>
        private static byte WearsGlider(EntityAlive alive)
        {
            if (!(alive is EntityPlayer player))
            {
                return 0;
            }
            try
            {
                if (player.equipment == null)
                {
                    return 0;
                }
                ItemValue[] items = player.equipment.GetItems();
                if (items == null)
                {
                    return 0;
                }
                foreach (ItemValue item in items)
                {
                    if (item == null || item.IsEmpty())
                    {
                        continue;
                    }
                    ItemClass itemClass = item.ItemClass;
                    if (itemClass != null && itemClass.HasAnyTags(FastTags<TagGroup.Global>.Parse(GliderItemTag)))
                    {
                        return 1;
                    }
                }
            }
            catch
            {
                // Worn-state reads are best effort; never break the snapshot.
            }
            return 0;
        }

        private byte Classify(Entity e)
        {
            // Our own bots are zombie-bodied entities; they must be reported
            // as bots, not zombies, or the brain never drives them.
            if (_bots.Contains(e.entityId))
            {
                return SenseSnapshotWriter.KindBot;
            }
            if (e is EntityZombie)
            {
                return SenseSnapshotWriter.KindZombie;
            }
            if (e is EntityPlayer)
            {
                return SenseSnapshotWriter.KindPlayer;
            }
            return SenseSnapshotWriter.KindBot;
        }

        private void EnsureSpawned()
        {
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return; // world not loaded yet; retry on the next call
            }
            // Top up to the configured floor on every pass, not only once:
            // bots die in the world and a raised "bot count N" must take
            // effect without an explicit spawn command. Throttled (see
            // TopUpIntervalMs) and idempotent, so calling it per sense
            // request costs nothing in steady state. Unchecked int
            // subtraction stays correct across TickCount wraparound (same
            // reasoning as GuestRateLimiter).
            int nowMs = Environment.TickCount;
            if (_lastTopUpMs != int.MinValue && nowMs - _lastTopUpMs < TopUpIntervalMs)
            {
                return;
            }
            _lastTopUpMs = nowMs;
            // Spawn defensively: the world is not ready to host entities
            // during world creation (the game's own EAIManager can NRE), so
            // every attempt is guarded inside SpawnOne and a partially failed
            // round is repaired by the next pass instead of stacking another
            // batch on top of the bots that already spawned.
            int target = Math.Min(_countFloor, MaxBotCount);
            PruneDeadBots();
            while (_bots.Count < target && SpawnOne())
            {
            }
        }

        private bool SpawnOne()
        {
            // Free cap slots held by bots that died in the world so the
            // ceiling bounds live bodies, not history.
            PruneDeadBots();
            if (_bots.Count >= MaxBotCount)
            {
                return false;
            }
            try
            {
                var game = GameManager.Instance;
                if (game == null || game.World == null)
                {
                    return false;
                }
                UnityEngine.Vector3 pos = SpawnPosition(game.World);
                int classId = EntityClass.FromString(BotEntityClass);
                if (classId < 0)
                {
                    Log.Warning("[WasmHost] entity class '" + BotEntityClass + "' not found");
                    return false;
                }
                Entity e = EntityFactory.CreateEntity(classId, pos, UnityEngine.Vector3.zero);
                if (e == null)
                {
                    Log.Warning("[WasmHost] bot entity creation failed");
                    return false;
                }
                game.World.SpawnEntityInWorld(e);
                _bots.Add(e.entityId);
                Log.Out("[WasmHost] bot spawned entity " + e.entityId + " at " + pos.x + "," + pos.y + "," + pos.z);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[WasmHost] bot spawn failed (world not ready?): " + ex.Message);
                return false;
            }
        }

        private void PruneDeadBots()
        {
            if (_bots.Count == 0)
            {
                return;
            }
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return;
            }
            // Removal during enumeration is safe for HashSet<T>: the
            // enumerator visits the untouched slots, so no defensive copy
            // is needed (this runs on spawn attempts and warm-up sense
            // requests, so steady-state allocation-free matters).
            foreach (int id in _bots)
            {
                Entity e = game.World.GetEntity(id);
                if (e == null || !(e is EntityAlive alive) || alive.IsDead())
                {
                    _bots.Remove(id);
                    _botYaw.Remove(id);
                }
            }
        }

        private static UnityEngine.Vector3 SpawnPosition(World world)
        {
            if (world.Players != null && world.Players.list != null && world.Players.list.Count > 0)
            {
                var p = world.Players.list[0];
                if (p != null)
                {
                    return new UnityEngine.Vector3(p.position.x + 3, p.position.y, p.position.z + 3);
                }
            }
            return new UnityEngine.Vector3(0, 60, 0);
        }

        private void RemoveBots(string[] parts)
        {
            if (parts.Length > 2 && parts[2] == "all")
            {
                // Despawn removes from _bots; HashSet tolerates removal
                // during enumeration (see PruneDeadBots).
                foreach (int id in _bots)
                {
                    Despawn(id);
                }
                return;
            }
            if (parts.Length > 2 && TryParseId(parts[2], out int removeId))
            {
                Despawn(removeId);
            }
        }

        private void Despawn(int entityId)
        {
            // Only our own bots may be despawned. The id comes from a guest
            // command; without this gate "bot remove <player entity id>"
            // would kill any world entity, players included.
            if (!_bots.Remove(entityId))
            {
                return;
            }
            var game = GameManager.Instance;
            if (game != null && game.World != null)
            {
                Entity e = game.World.GetEntity(entityId);
                if (e is EntityAlive alive)
                {
                    alive.SetDead();
                }
            }
            _botYaw.Remove(entityId);
        }

        private void MoveBot(string[] parts)
        {
            if (parts.Length < 6)
            {
                return;
            }
            if (!TryParseId(parts[2], out int id) ||
                !TryParseFloat(parts[3], out float x) ||
                !TryParseFloat(parts[4], out float y) ||
                !TryParseFloat(parts[5], out float z))
            {
                return;
            }
            Entity? e = FindBot(id);
            if (e != null)
            {
                e.SetPosition(new UnityEngine.Vector3(x, y, z), true);
            }
        }

        private void LookBot(string[] parts)
        {
            if (parts.Length < 4)
            {
                return;
            }
            if (!TryParseId(parts[2], out int id) || !TryParseFloat(parts[3], out float yaw))
            {
                return;
            }
            Entity? e = FindBot(id);
            if (e != null)
            {
                // The brain emits radians; the game uses degrees.
                e.SetRotation(new UnityEngine.Vector3(0, yaw * RadiansToDegrees, 0));
                _botYaw[e.entityId] = yaw;
            }
        }

        private void ShootBot(string[] parts)
        {
            if (parts.Length < 4)
            {
                return;
            }
            if (!TryParseId(parts[2], out int botId) || !TryParseId(parts[3], out int targetId))
            {
                return;
            }
            // Only a live servant bot may fire (zdtd BotManager.shoot parity:
            // find(shooter) orelse return). Without this gate any guest id
            // would deal game-side damage attributed to an entity that is
            // not ours, players included.
            Entity? shooter = FindBot(botId);
            if (shooter == null)
            {
                return;
            }
            bool head = parts.Length > 4 && parts[4] == "head";
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return;
            }
            Entity target = game.World.GetEntity(targetId);
            if (!(target is EntityAlive targetAlive) || targetAlive.IsDead())
            {
                return;
            }
            int dmg = WeaponDamage[0];
            // Stage 2: all bots carry the pistol (weapon 0) until loadout
            // records are wired; the brain's default matches.
            if (head)
            {
                dmg *= 2;
            }
            var source = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, botId);
            targetAlive.DamageEntity(source, dmg, head, 1f);
            Log.Out("[WasmHost] bot " + botId + " shot " + targetId + " dmg=" + dmg + (head ? " head" : ""));
        }

        private Entity? FindBot(int entityId)
        {
            if (!_bots.Contains(entityId))
            {
                return null;
            }
            var game = GameManager.Instance;
            return game != null && game.World != null ? game.World.GetEntity(entityId) : null;
        }

        // SimCommands arrive from untrusted guests through the queue import.
        // Every number is parsed invariantly; floats must additionally be
        // finite ("nan" and "Infinity" parse cleanly otherwise) so a hostile
        // command cannot corrupt entity position/rotation or persist a NaN
        // yaw into every later sense snapshot.
        private static bool TryParseId(string text, out int value)
        {
            return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
