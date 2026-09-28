using System;
using System.Collections.Generic;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Maps each armed glide net id to the module that armed it. Guests are
    /// independent principals sharing one world, so an armed flag is one
    /// module's authority over a player: without the map any guest could
    /// clear another's flag and its descent clamp, and the player would fall
    /// at full speed from a height the mod had already earned the right to
    /// soften. The map lives in the host (not in the game-side servant) so
    /// the rule is testable without a game.
    /// </summary>
    public sealed class GlideOwnershipRegistry
    {
        private readonly Dictionary<int, string> _ownerByNetId = new Dictionary<int, string>();

        /// <summary>True when <paramref name="owner"/> may change the glide flag
        /// on <paramref name="netId"/>, claiming it when it is unclaimed.</summary>
        public bool Claim(string owner, int netId)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            // Re-arming is the same module's own business: the claim is
            // idempotent for the owner, and only an id another module holds
            // is refused.
            if (_ownerByNetId.TryGetValue(netId, out string? held))
            {
                return string.Equals(held, owner, StringComparison.Ordinal);
            }
            _ownerByNetId[netId] = owner;
            return true;
        }

        /// <summary>True when <paramref name="owner"/> armed <paramref name="netId"/>.</summary>
        public bool Owns(string owner, int netId)
        {
            return owner != null
                && _ownerByNetId.TryGetValue(netId, out string? held)
                && string.Equals(held, owner, StringComparison.Ordinal);
        }

        /// <summary>
        /// Owner of a claimed net id, or null when the id is unclaimed. A
        /// caller enumerating ids at tick rate uses this instead of Owns so
        /// the lookup happens once per entity.
        /// </summary>
        public string? OwnerOf(int netId)
        {
            return _ownerByNetId.TryGetValue(netId, out string? owner) ? owner : null;
        }

        /// <summary>
        /// Drops <paramref name="netId"/> whoever armed it. Called when the
        /// player leaves the world: an id nobody may name again must not
        /// keep a claim.
        /// </summary>
        public void Forget(int netId)
        {
            _ownerByNetId.Remove(netId);
        }

        /// <summary>
        /// Drops every claim held by <paramref name="owner"/> and returns the
        /// released net ids, ascending. Called when a module unloads: the
        /// authority goes with the module, and a reloaded instance starts from
        /// an empty set.
        /// </summary>
        public IReadOnlyList<int> Release(string owner)
        {
            var released = new List<int>();
            if (owner == null)
            {
                return released;
            }
            foreach (KeyValuePair<int, string> pair in _ownerByNetId)
            {
                if (string.Equals(pair.Value, owner, StringComparison.Ordinal))
                {
                    released.Add(pair.Key);
                }
            }
            released.Sort();
            foreach (int id in released)
            {
                _ownerByNetId.Remove(id);
            }
            return released;
        }
    }
}
