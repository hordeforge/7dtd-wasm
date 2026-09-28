using System;
using System.Collections.Generic;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Maps each tracked bot entity id to the module that asked for it.
    /// Guests are independent principals sharing one world, so every id a
    /// guest names is checked against its owner before the servant acts on
    /// it: without the map one module can move, aim, shoot through, and
    /// despawn another module's bots. The map lives in the host (not in the
    /// game-side servant) so the rule is testable without a game.
    /// </summary>
    public sealed class BotOwnershipRegistry
    {
        private readonly Dictionary<int, string> _ownerByEntity = new Dictionary<int, string>();
        private readonly Dictionary<string, int> _countByOwner = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Number of tracked bots across every owner.</summary>
        public int Count => _ownerByEntity.Count;

        /// <summary>Number of tracked bots owned by <paramref name="owner"/>.</summary>
        public int CountOf(string owner)
        {
            return owner != null && _countByOwner.TryGetValue(owner, out int count) ? count : 0;
        }

        /// <summary>
        /// Records <paramref name="entityId"/> as owned by
        /// <paramref name="owner"/>. Returns false when the id is already
        /// tracked: the world hands out entity ids, and an id that arrives
        /// twice belongs to the module that got it first.
        /// </summary>
        public bool Add(string owner, int entityId)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (_ownerByEntity.ContainsKey(entityId))
            {
                return false;
            }
            _ownerByEntity[entityId] = owner;
            _countByOwner.TryGetValue(owner, out int held);
            _countByOwner[owner] = held + 1;
            return true;
        }

        /// <summary>True when <paramref name="owner"/> owns <paramref name="entityId"/>.</summary>
        public bool Owns(string owner, int entityId)
        {
            return owner != null
                && _ownerByEntity.TryGetValue(entityId, out string? held)
                && string.Equals(held, owner, StringComparison.Ordinal);
        }

        /// <summary>
        /// Owner of a tracked id, or null when the id is untracked. A caller
        /// enumerating ids at tick rate uses this instead of Owns so the
        /// lookup happens once per entity.
        /// </summary>
        public string? OwnerOf(int entityId)
        {
            return _ownerByEntity.TryGetValue(entityId, out string? owner) ? owner : null;
        }

        /// <summary>
        /// Drops <paramref name="entityId"/>. Returns false unless
        /// <paramref name="owner"/> owns it, so a rejected id stays tracked
        /// for the module that really holds it.
        /// </summary>
        public bool Remove(string owner, int entityId)
        {
            if (!Owns(owner, entityId))
            {
                return false;
            }
            _ownerByEntity.Remove(entityId);
            _countByOwner[owner] = _countByOwner[owner] - 1;
            return true;
        }

        /// <summary>Every tracked id, ascending. A copy, not a live view.</summary>
        public IReadOnlyList<int> EntityIds()
        {
            var ids = new List<int>(_ownerByEntity.Keys);
            ids.Sort();
            return ids;
        }

        /// <summary>
        /// Drops every id owned by <paramref name="owner"/> and returns the
        /// released ids, ascending. Called when a module unloads: the ids
        /// leave the world with it, and an owner nobody can name again must
        /// not keep a share of the live bot budget.
        /// </summary>
        public IReadOnlyList<int> Release(string owner)
        {
            var released = new List<int>();
            if (owner == null)
            {
                return released;
            }
            foreach (KeyValuePair<int, string> pair in _ownerByEntity)
            {
                if (string.Equals(pair.Value, owner, StringComparison.Ordinal))
                {
                    released.Add(pair.Key);
                }
            }
            released.Sort();
            foreach (int id in released)
            {
                _ownerByEntity.Remove(id);
            }
            _countByOwner.Remove(owner);
            return released;
        }
    }
}
