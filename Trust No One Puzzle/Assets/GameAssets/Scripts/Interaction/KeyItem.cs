using GameAssets.Scripts.Entities;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Interaction
{
    /// <summary>
    /// Where a lock is allowed to look for a matching key.
    /// </summary>
    public enum KeyAccess
    {
        /// <summary>Only a key the player is physically holding right now counts.</summary>
        HeldItemOnly,

        /// <summary>Only a key that was collected into the <see cref="KeyRing"/> counts.</summary>
        KeyRingOnly,

        /// <summary>A held key or a collected key counts (default, most forgiving).</summary>
        HeldOrKeyRing
    }

    /// <summary>
    /// Marks any prop as a key. Put it on the same GameObject as the pickup
    /// (<see cref="PlaceableItem"/> for the physics carry, or <see cref="Interactable"/>
    /// for the FPP camera carry) — it only tags the object, it never fights the
    /// carrying systems for input.
    ///
    /// Locks ask <see cref="KeyItem"/> statics whether the player can open them,
    /// so a key works whether it is in the player's hands or already collected
    /// into the <see cref="KeyRing"/>.
    /// </summary>
    public class KeyItem : MonoBehaviour
    {
        public enum ConsumeBehaviour
        {
            /// <summary>Deactivate the key GameObject when spent.</summary>
            Disable,

            /// <summary>Destroy the key GameObject when spent.</summary>
            Destroy,

            /// <summary>Keep the object in the world (re-usable master key).</summary>
            KeepInWorld
        }

        [Header("Identity")]
        [Tooltip("Id a lock must ask for. Locks with an empty required id accept any key.")]
        [SerializeField] private string keyId = "key";

        [Tooltip("Name shown in prompts, e.g. \"Press E To Unlock with Brass Key\".")]
        [SerializeField] private string displayName = "Key";

        [Header("Behaviour")]
        [Tooltip("Add this key id to the global KeyRing as soon as the player picks the key up. " +
                 "Lets the player unlock things later without still holding the key.")]
        [SerializeField] private bool collectOnPickup = true;

        [Tooltip("Spend the key when it opens something (single-use key).")]
        [SerializeField] private bool consumedOnUse;

        [Tooltip("What happens to the key object when it is spent.")]
        [SerializeField] private ConsumeBehaviour consumeBehaviour = ConsumeBehaviour.Destroy;

        [Header("Events")]
        public UnityEvent OnCollected;
        public UnityEvent OnUsed;

        private bool _collected;
        private static FPPCameraController _cachedFppController;

        public string KeyId => keyId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;
        public bool ConsumedOnUse => consumedOnUse;
        public bool IsCollected => _collected;

        // ─────────────────────────────────────────────
        //  Instance API
        // ─────────────────────────────────────────────

        /// <summary>True when this key satisfies a lock asking for <paramref name="requiredKeyId"/>.</summary>
        public bool Matches(string requiredKeyId)
        {
            if (string.IsNullOrWhiteSpace(requiredKeyId))
                return true;

            return string.Equals(keyId?.Trim(), requiredKeyId.Trim(),
                System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True while the player carries this key (either carry system).</summary>
        public bool IsHeld => IsCarriedByPlayer(transform);

        /// <summary>Adds the key id to the <see cref="KeyRing"/>. Safe to call repeatedly.</summary>
        public void Collect()
        {
            if (_collected)
                return;

            _collected = true;
            KeyRing.Add(keyId);
            OnCollected?.Invoke();
        }

        /// <summary>
        /// Called by a lock that just opened with this key. Spends the key when
        /// it is single-use.
        /// </summary>
        public void Use()
        {
            OnUsed?.Invoke();

            if (!consumedOnUse)
                return;

            KeyRing.Consume(keyId);

            switch (consumeBehaviour)
            {
                case ConsumeBehaviour.Disable:
                    gameObject.SetActive(false);
                    break;
                case ConsumeBehaviour.Destroy:
                    Destroy(gameObject);
                    break;
            }
        }

        private void Update()
        {
            // Cheap poll (a scene has a handful of keys at most): the moment the
            // player picks the key up it goes onto the key ring, so a lock can be
            // opened later even after the key was dropped somewhere else.
            if (collectOnPickup && !_collected && IsHeld)
                Collect();
        }

        // ─────────────────────────────────────────────
        //  Static lookup used by locks
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns the key the player is currently holding that matches
        /// <paramref name="requiredKeyId"/>, or null.
        /// </summary>
        public static KeyItem FindHeldKey(string requiredKeyId)
        {
            // Physics carry (PlayerCarry / PlaceableItem).
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null)
            {
                var key = FindMatching(carry.HeldItem.transform, requiredKeyId);
                if (key != null)
                    return key;
            }

            // FPP camera carry (Interactable).
            var carried = CarriedByFppController();
            if (carried != null)
            {
                var key = FindMatching(carried.transform, requiredKeyId);
                if (key != null)
                    return key;
            }

            return null;
        }

        /// <summary>True when the player can satisfy a lock asking for this key id.</summary>
        public static bool PlayerHasKey(string requiredKeyId, KeyAccess access = KeyAccess.HeldOrKeyRing)
        {
            if (access != KeyAccess.KeyRingOnly && FindHeldKey(requiredKeyId) != null)
                return true;

            return access != KeyAccess.HeldItemOnly && KeyRing.Has(requiredKeyId);
        }

        /// <summary>
        /// Name of the key that would be used, for prompt text ("Brass Key").
        /// Falls back to the required id when the key only lives on the key ring.
        /// </summary>
        public static string DescribeKey(string requiredKeyId, KeyAccess access = KeyAccess.HeldOrKeyRing)
        {
            var held = access != KeyAccess.KeyRingOnly ? FindHeldKey(requiredKeyId) : null;
            if (held != null)
                return held.DisplayName;

            return string.IsNullOrWhiteSpace(requiredKeyId) ? "Key" : requiredKeyId.Trim();
        }

        /// <summary>
        /// Tries to open a lock with a matching key.
        /// </summary>
        /// <param name="requiredKeyId">Key id the lock wants. Empty = any key.</param>
        /// <param name="access">Where the key may come from.</param>
        /// <param name="consume">Spend the key (single-use keys only) when it fits.</param>
        /// <param name="usedKeyName">Display name of the key that opened the lock.</param>
        public static bool TryUseKey(string requiredKeyId, KeyAccess access, bool consume, out string usedKeyName)
        {
            usedKeyName = null;

            var held = access != KeyAccess.KeyRingOnly ? FindHeldKey(requiredKeyId) : null;
            if (held != null)
            {
                usedKeyName = held.DisplayName;
                if (consume)
                    held.Use();
                return true;
            }

            if (access != KeyAccess.HeldItemOnly && KeyRing.Has(requiredKeyId))
            {
                usedKeyName = string.IsNullOrWhiteSpace(requiredKeyId) ? "Key" : requiredKeyId.Trim();
                if (consume)
                    KeyRing.Consume(requiredKeyId);
                return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        private static KeyItem FindMatching(Transform root, string requiredKeyId)
        {
            var keys = root.GetComponentsInChildren<KeyItem>(true);
            foreach (var key in keys)
            {
                if (key != null && key.Matches(requiredKeyId))
                    return key;
            }
            return null;
        }

        private static bool IsCarriedByPlayer(Transform candidate)
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null && candidate.IsChildOf(carry.HeldItem.transform))
                return true;

            var carried = CarriedByFppController();
            return carried != null && candidate.IsChildOf(carried.transform);
        }

        private static Interactable CarriedByFppController()
        {
            if (_cachedFppController == null)
                _cachedFppController = Object.FindFirstObjectByType<FPPCameraController>();

            return _cachedFppController != null ? _cachedFppController.CarriedInteractable : null;
        }
    }
}
