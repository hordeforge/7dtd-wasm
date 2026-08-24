using System;
using System.Collections.Generic;
using HordeForge.WasmHost.Abi;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// The bot servant: the game side of the zdtd fps_bot contract
    /// (docs/ABI.md zdtd compatibility section). It spawns bot entities,
    /// applies the SimCommands the guest brain queues (bot move / look /
    /// shoot / spawn / remove / count / skill / cfg), and builds the 'ZBS3'
    /// world snapshot the sense import fills. The brain owns targeting and
    /// aim; the servant owns the bodies.
    ///
    /// Stage 2 status: spawn, move, look, shoot, and sense are implemented;
    /// cover/path queries still return no answer and on_admin_command is
    /// not yet wired to the console.
    /// </summary>
    public sealed class BotServant
    {
        private const string BotEntityClass = "zombieSoldier";
        private const int DefaultBotCount = 4;

        // The brain speaks radians; the game speaks degrees.
        private const float RadiansToDegrees = 57.2957795f;

        // Weapon pool damage (index matches the brain's weapon ids:
        // pistol 0, shotgun 1, ak 2, sniper 3, auto 4, smg 5).
        private static readonly int[] WeaponDamage = { 12, 18, 14, 45, 12, 10 };

        private readonly WasmSettingsProvider _settings;
        private readonly HashSet<int> _bots = new HashSet<int>();
        private readonly Dictionary<int, float> _botYaw = new Dictionary<int, float>();
        private int _countFloor = DefaultBotCount;
        private bool _spawned;

        public BotServant(WasmSettingsProvider settings)
        {
            _settings = settings;
        }

        public bool TryQueue(string command)
        {
            if (command == null || !command.StartsWith("bot ", StringComparison.Ordinal))
            {
                return false;
            }
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
                        if (parts.Length > 2 && int.TryParse(parts[2], out int n) && n >= 0 && n <= 16)
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
                        global::Log.Out("[WasmHost] bot " + verb + ": " + command.Substring(3 + verb.Length));
                        return true;
                    default:
                        global::Log.Out("[WasmHost] bot cmd (unknown verb '" + verb + "'): " + command);
                        return true;
                }
            }
            catch (Exception ex)
            {
                global::Log.Warning("[WasmHost] bot " + verb + " failed: " + ex.Message);
                return false;
            }
        }

        public int WriteSense(Span<byte> buffer)
        {
            EnsureSpawned();
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return 0;
            }
            var snapshot = new SenseSnapshotWriter.Snapshot
            {
                Tick = 0,
                SelfNetId = 0,
                WorldTime = (long)game.World.GetWorldTime(),
                BloodMoon = false,
            };
            try
            {
                var entities = game.World.Entities;
                if (entities == null || entities.list == null)
                {
                    return 0;
                }
                foreach (Entity e in entities.list)
                {
                    if (!(e is EntityAlive alive) || alive.IsDead())
                    {
                        continue;
                    }
                    if (snapshot.Records.Count >= 60)
                    {
                        break; // 60 records fit under the guest's 2048-byte sense cap
                    }
                    snapshot.Records.Add(new SenseSnapshotWriter.EntityRecord
                    {
                        NetId = e.entityId,
                        Kind = Classify(e),
                        IsSelf = _bots.Contains(e.entityId),
                        Alive = true,
                        X = e.position.x,
                        Y = e.position.y,
                        Z = e.position.z,
                        Hp = alive.Health,
                        Yaw = YawFor(e.entityId),
                        TargetId = 0,
                    });
                }
            }
            catch (Exception ex)
            {
                global::Log.Warning("[WasmHost] sense failed: " + ex.Message);
                return 0;
            }
            return SenseSnapshotWriter.Write(snapshot, buffer);
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
            if (_spawned)
            {
                return;
            }
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return; // world not loaded yet; retry on the next call
            }
            // Spawn defensively and retry: the world is not ready to host
            // entities during world creation (the game's own EAIManager can
            // NRE), so _spawned latches only when every spawn succeeded.
            int ok = 0;
            for (int i = 0; i < _countFloor; i++)
            {
                if (SpawnOne())
                {
                    ok++;
                }
            }
            if (ok == _countFloor || _countFloor == 0)
            {
                _spawned = true;
            }
        }

        private bool SpawnOne()
        {
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
                    global::Log.Warning("[WasmHost] entity class '" + BotEntityClass + "' not found");
                    return false;
                }
                Entity e = EntityFactory.CreateEntity(classId, pos, UnityEngine.Vector3.zero);
                if (e == null)
                {
                    global::Log.Warning("[WasmHost] bot entity creation failed");
                    return false;
                }
                game.World.SpawnEntityInWorld(e);
                _bots.Add(e.entityId);
                global::Log.Out("[WasmHost] bot spawned entity " + e.entityId + " at " + pos.x + "," + pos.y + "," + pos.z);
                return true;
            }
            catch (Exception ex)
            {
                global::Log.Warning("[WasmHost] bot spawn failed (world not ready?): " + ex.Message);
                return false;
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
                foreach (int id in new List<int>(_bots))
                {
                    Despawn(id);
                }
                return;
            }
            if (parts.Length > 2 && int.TryParse(parts[2], out int removeId))
            {
                Despawn(removeId);
            }
        }

        private void Despawn(int entityId)
        {
            var game = GameManager.Instance;
            if (game != null && game.World != null)
            {
                Entity e = game.World.GetEntity(entityId);
                if (e is EntityAlive alive)
                {
                    alive.SetDead();
                }
            }
            _bots.Remove(entityId);
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

        private float YawFor(int entityId)
        {
            return _botYaw.TryGetValue(entityId, out float yaw) ? yaw : 0f;
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
            bool head = parts.Length > 4 && parts[4] == "head";
            var game = GameManager.Instance;
            if (game == null || game.World == null)
            {
                return;
            }
            Entity target = game.World.GetEntity(targetId);
            Entity bot = game.World.GetEntity(botId);
            if (!(target is EntityAlive targetAlive) || targetAlive.IsDead())
            {
                return;
            }
            int weapon = WeaponIdFor(bot);
            int dmg = WeaponDamage[weapon];
            if (head)
            {
                dmg *= 2;
            }
            var source = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, botId);
            targetAlive.DamageEntity(source, dmg, head, 1f);
            global::Log.Out("[WasmHost] bot " + botId + " shot " + targetId + " dmg=" + dmg + (head ? " head" : ""));
        }

        private int WeaponIdFor(Entity bot)
        {
            // Stage 2: all bots carry the pistol (weapon 0) until loadout
            // records are wired; the brain's default matches.
            return 0;
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
