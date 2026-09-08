using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameAssets.Scripts.Interaction
{
    /// <summary>
    /// Scene-independent set of key ids the player currently owns.
    ///
    /// A key can unlock furniture in two ways:
    ///   • it is physically carried in the player's hands (see <see cref="KeyItem"/>), or
    ///   • it was collected into this key ring (picked up once, then kept).
    ///
    /// Nothing in the lock code needs a reference to the key object itself, so
    /// drawers, doors and toolboxes stay decoupled from where the key came from.
    /// </summary>
    public static class KeyRing
    {
        private static readonly HashSet<string> Keys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when a key id is added to the ring.</summary>
        public static event Action<string> KeyCollected;

        /// <summary>Raised when a key id is spent (consumed by a lock).</summary>
        public static event Action<string> KeyUsed;

        public static IReadOnlyCollection<string> CollectedKeys => Keys;
        public static int Count => Keys.Count;

        /// <summary>Adds a key id. Returns false when it was already owned.</summary>
        public static bool Add(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                return false;

            if (!Keys.Add(keyId.Trim()))
                return false;

            KeyCollected?.Invoke(keyId.Trim());
            return true;
        }

        /// <summary>
        /// True when the ring contains <paramref name="keyId"/>.
        /// An empty / null id means "any key at all" (generic locks).
        /// </summary>
        public static bool Has(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                return Keys.Count > 0;

            return Keys.Contains(keyId.Trim());
        }

        /// <summary>
        /// Spends a key. With an empty id any single owned key is spent.
        /// Returns false when the key was not owned.
        /// </summary>
        public static bool Consume(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                foreach (var owned in Keys)
                {
                    Keys.Remove(owned);
                    KeyUsed?.Invoke(owned);
                    return true;
                }
                return false;
            }

            var id = keyId.Trim();
            if (!Keys.Remove(id))
                return false;

            KeyUsed?.Invoke(id);
            return true;
        }

        public static void Clear() => Keys.Clear();

        /// <summary>
        /// Statics survive scene loads (and domain reloads when they are disabled),
        /// so the ring is wiped whenever play mode starts.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => Keys.Clear();
    }
}
