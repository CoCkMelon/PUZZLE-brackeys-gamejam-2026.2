using System;
using UnityEngine;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Marks a pickup as a key for <see cref="OpenableFurniture"/> locks.
    /// Put it on the key object (next to its Interactable / PlaceableItem component) and give it
    /// the same id as the furniture's <c>Required Key Id</c>. One key id can open any number of locks.
    /// </summary>
    public class FurnitureKey : MonoBehaviour
    {
        [Tooltip("Id compared (case-insensitive) with the Required Key Id of an OpenableFurniture.")]
        [SerializeField] private string keyId = "key";

        public string KeyId => keyId;

        /// <summary>Whether this key opens a lock that requires <paramref name="requiredKeyId"/>.</summary>
        public bool Matches(string requiredKeyId) => IdsMatch(keyId, requiredKeyId);

        /// <summary>Case-insensitive id comparison; empty ids never match anything.</summary>
        public static bool IdsMatch(string keyId, string requiredKeyId)
        {
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(requiredKeyId))
            {
                return false;
            }

            return string.Equals(keyId.Trim(), requiredKeyId.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
