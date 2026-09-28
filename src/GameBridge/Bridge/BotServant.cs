using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Damage of the pistol every bot carries (the brain's weapon id 0).
        // Loadout records are not wired yet, so the other weapon ids of the
        // zdtd pool (shotgun 1, ak 2, sniper 3, auto 4, smg 5) have no
        // servant-side damage to read.
        private const int PistolDamage = 12;

        // Glider item tag (matches the parachute mod's items.xml patch and
        // preset.toml [rules.glide] item_tag). A worn item whose ItemClass
        // carries this tag sets the sense v4 wearing_glider bit. The parsed
        // tag set depends on nothing but the constant, so it is built once
        // here instead of once per inspected item on every sense record.
        private const string GliderItemTag = "parachute";
        private static readonly FastTags<TagGroup.Global> GliderTags = FastTags<TagGroup.Global>.Parse(GliderItemTag);

        // Buff applied to a player while the parachute mod's glide flag is
        // armed. Defined by the playtest parachute-items modlet (buffs.xml);
        // the client slow-fall patch keys on it: while held, the fall is
        // clamped to the sink rate and the vp fall impact is skipped, the
        // safe landing on the stock server.
        private const string GlideBuffName = "buffParachuteGlide";

        // Entity records per sense snapshot. With the v4 40-byte records a
        // 2048-byte guest sense cap holds 41 records after reserving the
        // 384-byte event trailer (24 + 41 * 40 + 24 * 16 = 2048), the same
        // sizing zdtd uses for that cap.
        private const int MaxSenseRecords = 41;

        private readonly Func<long> _tickProvider;
        // Every log line below is written on a guest-driven path: a brain
        // that keeps issuing a failing or repetitive SimCommand would
        // otherwise write server log lines at its command rate, which
        // buries everything else. Sources are per verb or per entity, so
        // one noisy mod cannot mute another's diagnostics. Dropped totals
        // surface in "wasm status" and every 100th drop is logged.
        private readonly GuestRateLimiter _commandLogLimiter = new GuestRateLimiter();
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

        /// <summary>Per-source cap on this servant's log lines; exposed for "wasm status".</summary>
        public GuestRateLimiter CommandLogLimiter => _commandLogLimiter;

        /// <summary>
        /// Writes one guest-command log line under the per-source cap, so a
        /// brain repeating a command cannot flood the server log. Suppressed
        /// lines are counted and reported in "wasm status".
        /// </summary>
        private void WriteCapped(string sourceKey, string message)
        {
            if (_commandLogLimiter.TryWrite(sourceKey, out long dropped))
            {
                Log.Out("[WasmHost] " + message);
            }
            else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
            {
                Log.Out("[WasmHost] suppressed " + dropped + " " + sourceKey + " log line(s)");
            }
        }

        /// <summary>Warning-level counterpart of <see cref="WriteCapped"/>.</summary>
        private void WarnCapped(string sourceKey, string message)
        {
            if (_commandLogLimiter.TryWrite(sourceKey, out long dropped))
            {
                Log.Warning("[WasmHost] " + message);
            }
            else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
            {
                Log.Warning("[WasmHost] suppressed " + dropped + " " + sourceKey + " failure log(s)");
            }
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
                WriteCapped("glide/parse", "glide (malformed): " + command);
                return true;
            }
            if (!TryParseId(parts[1], out int netId))
            {
                WriteCapped("glide/parse", "glide (bad id): " + command);
                return true;
            }
            if (!IsPlayer(netId))
            {
                // The net id comes from an untrusted guest, and an armed flag
                // both applies a buff and pins the entity's descent (see
                // ClampGlideDescent), so without this gate a guest could
                // steer any world entity, other players included. Gliding is
                // a player feature: only a live player may be armed.
                WriteCapped("glide/parse", "glide (not a player): " + command);
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
                WriteCapped("glide/parse", "glide (bad flag): " + command);
                return true;
            }
            ApplyGlideBuff(netId, _glide[netId]);
            WriteCapped("glide/" + netId, "glide " + netId + " " + (_glide[netId] ? "armed" : "cleared"));
            return true;
        }

        /// <summary>
        /// Applies or removes the glide buff on the player while the parachute
        /// mod's glide flag is armed. The client slow-fall patch keys on this
        /// buff (clamped sink rate, skipped fall impact), which is the
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
                    // netSync true so the client sees the buff the
                    // slow-fall patch keys on.
                    alive.Buffs.AddBuff(GlideBuffName, 0, true, false, -1f);
                    WriteCapped("glidebuff/" + netId, "glide buff applied " + GlideBuffName + " to " + netId +
                            " has=" + alive.Buffs.HasBuff(GlideBuffName));
                }
                else
                {
                    alive.Buffs.RemoveBuff(GlideBuffName, 0, true);
                    WriteCapped("glidebuff/" + netId, "glide buff removed " + GlideBuffName + " from " + netId);
                }
            }
            catch (Exception ex)
            {
                WarnCapped("glidebuff/" + netId, "glide buff " + netId + " failed: " + ex.Message);
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
                        WriteCapped("bot/cfg", "bot " + verb + ": " + policy);
                        return true;
                    default:
                        WriteCapped("bot/unknown", "bot cmd (unknown verb '" + verb + "'): " + command);
                        return true;
                }
            }
            catch (Exception ex)
            {
                WarnCapped("bot/" + verb, "bot " + verb + " failed: " + ex);
                return false;
            }
        }

        /// <summary>Armed glide flags by net id (ADR 0037); exposed for "wasm status".</summary>
        public IReadOnlyDictionary<int, bool> Glide => _glide;

        /// <summary>
        /// Serializes the current world snapshot into the calling guest's
        /// buffer and returns the byte count, or 0 when there is no world
        /// data or it does not fit. The snapshot, its records, and the
        /// scratch id sets are pooled and refilled per call, so a sense
        /// request at tick rate does not allocate.
        /// </summary>
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
                // One clock read for the whole snapshot: the header tick and
                // every record's velocity must come from the same instant, or
                // a tick boundary mid-scan would mix a new header with old
                // per-entity deltas.
                long tick = _tickProvider();
                snapshot.Tick = tick;
                snapshot.SelfNetId = 0;
                snapshot.WorldTime = (long)game.World.GetWorldTime();
                snapshot.BloodMoon = false;
                var records = _senseRecords;
                var seen = _seenIds;
                seen.Clear();
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
                    record.Vy = VerticalVelocity(e.entityId, e.position, tick, out UnityEngine.Vector3 prevPos, out int elapsedTicks);
                    record.Wearing = WearsGlider(alive);
                    record.TargetId = 0;
                    snapshot.Records.Add(record);
                    seen.Add(e.entityId);
                    ClampGlideDescent(alive, record.Vy, e.position, prevPos, elapsedTicks);
                }
                PrunePositionHistory(seen);
            }
            catch (Exception ex)
            {
                WarnCapped("sense", "sense failed: " + ex.Message);
                return 0;
            }
            return SenseSnapshotWriter.Write(snapshot, buffer);
        }

        // Per-entity position history backing the sense v4 `vy` field, plus
        // the id sets and lists the sense and spawn paths collect into. All
        // pooled: every one of them runs at tick rate, so none may allocate
        // per call (single main-loop thread by contract). The three id lists
        // are separate buffers because a collection is walked while another
        // could be refilled underneath it.
        private readonly Dictionary<int, (long Tick, UnityEngine.Vector3 Pos)> _lastPos =
            new Dictionary<int, (long, UnityEngine.Vector3)>();
        private readonly HashSet<int> _seenIds = new HashSet<int>();
        private readonly List<int> _staleIds = new List<int>();
        private readonly List<int> _deadIds = new List<int>();
        private readonly List<int> _despawnIds = new List<int>();

        /// <summary>
        /// Drops position history for entities no longer in the world
        /// (disconnected players, removed bots), so the history stays
        /// proportional to the live entity list instead of every id ever
        /// seen. Runs inside the sense scan, which already visits them all.
        /// Membership decides, not a size comparison: in a tick where
        /// entities leave and others join the counts can match while ids
        /// differ, and a history entry left behind is served to whatever
        /// entity the game later reuses that net id for, as a vy derived
        /// from the previous occupant's position. The walk allocates
        /// nothing, so running it unconditionally costs only the iteration.
        /// </summary>
        private void PrunePositionHistory(HashSet<int> seen)
        {
            var stale = _staleIds;
            stale.Clear();
            foreach (int id in _lastPos.Keys)
            {
                if (!seen.Contains(id))
                {
                    stale.Add(id);
                }
            }
            foreach (int id in stale)
            {
                _lastPos.Remove(id);
            }
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
        /// <paramref name="elapsedTicks"/> is how many ticks the delta spans,
        /// and 0 when it is unusable (first sighting, a gap wider than the
        /// cap, or a repeat read inside one tick).
        /// </summary>
        private float VerticalVelocity(int netId, UnityEngine.Vector3 position, long tick, out UnityEngine.Vector3 prevPos, out int elapsedTicks)
        {
            bool known = _lastPos.TryGetValue(netId, out (long Tick, UnityEngine.Vector3 Pos) last);
            prevPos = known ? last.Pos : position;
            elapsedTicks = 0;
            float vy = 0f;
            if (known)
            {
                long dtTicks = tick - last.Tick;
                if (dtTicks > 0 && dtTicks <= 10)
                {
                    // 20 TPS bridge tick; blocks per second.
                    elapsedTicks = (int)dtTicks;
                    vy = (position.y - last.Pos.y) / (dtTicks * 0.05f);
                }
            }
            if (!known || last.Tick != tick)
            {
                _lastPos[netId] = (tick, position);
            }
            return vy;
        }

        /// <summary>
        /// The glide fall sink (blocks/s, negative = down): while a player's
        /// glide flag is armed the descent is capped at this rate. Matches
        /// the parachute preset's sink_vy_mps and the client slow-fall
        /// patch, which enforces the same rate on the client-owned physics.
        /// </summary>
        private const float SinkVyMps = 2.5f;

        /// <summary>
        /// Caps a gliding player's descent at the sink rate by nudging the
        /// server entity up when it dropped too far since the previous tick.
        /// Belt and suspenders beside the client slow-fall patch (which owns
        /// the visible glide on client-owned physics); the sense record keeps
        /// the real vy (the parachute mod arms on it). Best effort: only
        /// while the glide flag is armed, never for anyone else.
        ///
        /// The floor is anchored to the last observed position, so it may
        /// only be applied when that observation is the previous tick. A
        /// wider gap (a guest that stopped polling, a rate-capped import, a
        /// first sighting) means the player may legitimately have fallen
        /// further in the meantime; clamping to a one-tick floor there would
        /// snap them back up by the whole gap.
        /// </summary>
        private void ClampGlideDescent(EntityAlive alive, float vy, UnityEngine.Vector3 position, UnityEngine.Vector3 prevPos, int elapsedTicks)
        {
            if (!_glide.TryGetValue(alive.entityId, out bool armed) || !armed)
            {
                return;
            }
            if (vy >= -SinkVyMps)
            {
                return;
            }
            if (elapsedTicks != 1)
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
                    if (itemClass != null && itemClass.HasAnyTags(GliderTags))
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
                    WarnCapped("bot/spawn", "entity class '" + BotEntityClass + "' not found");
                    return false;
                }
                Entity e = EntityFactory.CreateEntity(classId, pos, UnityEngine.Vector3.zero);
                if (e == null)
                {
                    WarnCapped("bot/spawn", "bot entity creation failed");
                    return false;
                }
                game.World.SpawnEntityInWorld(e);
                _bots.Add(e.entityId);
                Log.Out("[WasmHost] bot spawned entity " + e.entityId + " at " + pos.x + "," + pos.y + "," + pos.z);
                return true;
            }
            catch (Exception ex)
            {
                WarnCapped("bot/spawn", "bot spawn failed (world not ready?): " + ex.Message);
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
            // Removal during enumeration invalidates the enumerator, so
            // dead ids are collected first and removed after the loop; the
            // scratch buffer is pooled because this runs from the sense path
            // at tick rate.
            var dead = _deadIds;
            dead.Clear();
            foreach (int id in _bots)
            {
                Entity e = game.World.GetEntity(id);
                if (e == null || !(e is EntityAlive alive) || alive.IsDead())
                {
                    dead.Add(id);
                }
            }
            foreach (int id in dead)
            {
                _bots.Remove(id);
                _botYaw.Remove(id);
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
                // Despawn mutates _bots, so the ids are collected first and
                // removed after the loop (see PruneDeadBots).
                var ids = _despawnIds;
                ids.Clear();
                ids.AddRange(_bots);
                foreach (int id in ids)
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
                    try
                    {
                        alive.SetDead();
                    }
                    catch (Exception ex)
                    {
                        // The body is still alive in the world, so it must go
                        // back under the servant's tracking: a despawn that
                        // silently half-happened would leave a live zombie
                        // nobody can prune, move, or despawn again.
                        _bots.Add(entityId);
                        Log.Warning("[WasmHost] bot despawn of " + entityId + " failed: " + ex.Message +
                                    "; bot stays in the world");
                        return;
                    }
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
            int dmg = head ? PistolDamage * 2 : PistolDamage;
            var source = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, botId);
            targetAlive.DamageEntity(source, dmg, head, 1f);
            WriteCapped("bot/shot", "bot " + botId + " shot " + targetId + " dmg=" + dmg + (head ? " head" : ""));
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

        /// <summary>
        /// True when the net id names a live player in the loaded world.
        /// Ownership gate for the glide verb; see TryQueueGlide.
        /// </summary>
        private static bool IsPlayer(int netId)
        {
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return false;
            }
            return game.World.GetEntity(netId) is EntityPlayer;
        }

        // SimCommands arrive from untrusted guests through the queue import.
        // Every number is parsed invariantly; floats must additionally be
        // finite ("nan" and "Infinity" parse cleanly otherwise) so a hostile
        // command cannot corrupt entity position/rotation or persist a NaN
        // yaw into every later sense snapshot.
        private static bool TryParseId(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
