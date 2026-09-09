using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Opens a door by rotating its pivot or opens a drawer by sliding it.
    /// The object holding this component needs a collider that can be hit by the player's reticle ray.
    ///
    /// Optional lock: tick <c>Starts Locked</c> and the furniture refuses to open until it is unlocked.
    /// It can be unlocked in three ways:
    ///   1. With a referenced key object: set <c>Required Key</c> to a scene object; the player carries
    ///      that object and presses the interact key while looking at the furniture.
    ///   2. With a key id: set <c>Required Key Id</c>; any carried <see cref="KeyItem"/> with a matching
    ///      id (or a matching id already collected into the <see cref="KeyRing"/>) unlocks it.
    ///   3. From code or a UnityEvent by calling <see cref="Unlock"/> (puzzles, triggers, dialogue, ...).
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class OpenableFurniture : MonoBehaviour
    {
        private enum OpenMode
        {
            Rotate,
            Slide
        }

        [Header("References")]
        [SerializeField] private Transform movingPart;
        [SerializeField] private TMP_Text interactionPrompt;
        [SerializeField] private FPPCameraController playerCameraController;

        [Header("Raycast")]
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionLayers = ~0;

        [Header("Interaction")]
        [Tooltip("Key the player presses while looking at the furniture to open / close / unlock it.")]
        [SerializeField] private Key interactKey = Key.E;

        [Header("Opening")]
        [SerializeField] private OpenMode openMode = OpenMode.Rotate;
        [SerializeField] private Vector3 openRotation = new Vector3(0f, 90f, 0f);
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 0f, 0.4f);
        [SerializeField, Min(0.1f)] private float openSpeed = 8f;

        [Header("Lock (Optional)")]
        [Tooltip("If true, the furniture starts locked and will not open until it is unlocked with a key or Unlock() is called.")]
        [SerializeField] private bool startsLocked;

        [Tooltip("A specific scene object that works as the key. The player has to carry it and press the interact key on this furniture. " +
                 "Leave empty to match keys by Required Key Id only (or to unlock from code/events only).")]
        [SerializeField] private GameObject requiredKey;

        [Tooltip("Any KeyItem whose id equals this value unlocks the furniture (case-insensitive). " +
                 "Leave empty to match by Required Key only (or to unlock from code/events only).")]
        [SerializeField] private string requiredKeyId;

        [Tooltip("Allow the player to unlock this by interacting while owning a matching key. " +
                 "Off = only script / UnityEvent calls to Unlock() open the lock.")]
        [SerializeField] private bool unlockWithKey = true;

        [Tooltip("Where the key may come from: the player's hands, the collected key ring, or either.")]
        [SerializeField] private KeyAccess keyAccess = KeyAccess.HeldOrKeyRing;

        [Tooltip("Take the key out of the player's hands and hide it once it has been used.")]
        [SerializeField] private bool consumeKey = true;

        [Tooltip("Open the furniture right away after it has been unlocked with a key.")]
        [SerializeField] private bool openWhenUnlocked = true;

        [Tooltip("Lock again every time the furniture is closed (the key is needed each time).")]
        [SerializeField] private bool relockOnClose;

        [Tooltip("Played at the furniture when it is unlocked with a key.")]
        [SerializeField] private AudioClip unlockSound;

        [Tooltip("Played at the furniture when the player presses the interact key on it while it is locked without the key.")]
        [SerializeField] private AudioClip lockedSound;

        [Header("Prompts")]
        [Tooltip("{0} = interact key name.")]
        [SerializeField] private string openPromptText = "Press {0} To Open";
        [Tooltip("{0} = interact key name.")]
        [SerializeField] private string closePromptText = "Press {0} To Close";
        [SerializeField] private string lockedPromptText = "Locked [Need Key]";
        [Tooltip("{0} = interact key name, {1} = key display name.")]
        [SerializeField] private string unlockPromptText = "Press {0} To Unlock with {1}";

        [Header("Events")]
        [Tooltip("Fired when the furniture starts opening.")]
        public UnityEvent OnOpened;

        [Tooltip("Fired when the furniture starts closing.")]
        public UnityEvent OnClosed;

        [Tooltip("Fired once when the furniture becomes unlocked (by key or Unlock()).")]
        public UnityEvent OnUnlocked;

        [Tooltip("Fired when the player presses the interact key on the furniture while it is locked and has no matching key.")]
        public UnityEvent OnLockedAttempt;

        private readonly RaycastHit[] _reticleHits = new RaycastHit[32];

        private Quaternion _closedRotation;
        private Quaternion _openRotation;
        private Vector3 _closedPosition;
        private Vector3 _openPosition;
        private bool _isOpen;
        private bool _isLocked;

        /// <summary>Whether the moving part is (or is animating towards) the open pose.</summary>
        public bool IsOpen => _isOpen;

        /// <summary>Whether the furniture currently refuses to open.</summary>
        public bool IsLocked => _isLocked;

        /// <summary>True when a key has been configured (by reference or id) for this lock.</summary>
        public bool RequiresKey => requiredKey != null || !string.IsNullOrWhiteSpace(requiredKeyId);

        /// <summary>The text shown when the player looks at this furniture.</summary>
        public string PromptText => BuildPromptText();

        private void Awake()
        {
            if (movingPart == null)
            {
                movingPart = transform;
            }

            _closedRotation = movingPart.localRotation;
            _openRotation = _closedRotation * Quaternion.Euler(openRotation);
            _closedPosition = movingPart.localPosition;
            _openPosition = _closedPosition + openOffset;
            _isLocked = startsLocked;

            if (playerCameraController == null)
            {
                playerCameraController = FindFirstObjectByType<FPPCameraController>();
            }

            SetPromptVisible(false);
        }

        private void Update()
        {
            AnimateMovingPart();

            var isTargeted = IsTargetedByReticle();
            SetPromptVisible(isTargeted);

            if (!isTargeted)
            {
                return;
            }

            UpdatePromptText();

            if (Keyboard.current != null && Keyboard.current[interactKey].wasPressedThisFrame)
            {
                Interact();
                UpdatePromptText();
            }
        }

        // ─────────────────────────────────────────────
        //  Public API (also usable from UnityEvents)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Same thing the reticle interaction does: unlock with a key if needed,
        /// otherwise toggle open/closed. Safe to call from UnityEvents.
        /// </summary>
        public void Interact()
        {
            if (_isLocked)
            {
                TryUnlockWithKey();
                return;
            }

            SetOpen(!_isOpen);
        }

        /// <summary>Opens the furniture (does nothing while locked).</summary>
        public void Open() => SetOpen(true);

        /// <summary>Closes the furniture.</summary>
        public void Close() => SetOpen(false);

        /// <summary>Unlocks without needing a key (puzzle solved, script, UnityEvent).</summary>
        public void Unlock()
        {
            if (!_isLocked)
            {
                return;
            }

            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();
        }

        /// <summary>Locks the furniture. Closes it first if it is open.</summary>
        public void Lock()
        {
            _isLocked = true;

            if (_isOpen)
            {
                SetOpen(false);
            }
        }

        /// <summary>
        /// Attempts the key unlock explicitly. Returns true when a matching key
        /// was found. Plays the locked sound and fires <see cref="OnLockedAttempt"/>
        /// when the player has no key. Safe to call from UnityEvents.
        /// </summary>
        public bool TryUnlockWithKey()
        {
            if (!_isLocked)
            {
                return true;
            }

            if (unlockWithKey && TryConsumeMatchingKey())
            {
                UnlockNow();
                return true;
            }

            PlaySound(lockedSound);
            OnLockedAttempt?.Invoke();
            return false;
        }

        /// <summary>
        /// Tries to unlock the furniture with <paramref name="candidate"/> (e.g. from another interaction system).
        /// Returns true when the candidate matched and the furniture got unlocked.
        /// </summary>
        public bool TryUnlockWith(GameObject candidate)
        {
            if (!_isLocked || !IsMatchingKey(candidate))
            {
                return false;
            }

            if (consumeKey)
            {
                ConsumeKey(candidate);
            }

            UnlockNow();
            return true;
        }

        /// <summary>Whether <paramref name="candidate"/> (or one of its children/parents) is a key for this furniture.</summary>
        public bool IsMatchingKey(GameObject candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (requiredKey != null &&
                (candidate == requiredKey || candidate.transform.IsChildOf(requiredKey.transform)))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(requiredKeyId))
            {
                return false;
            }

            // Explicit null checks on purpose: '??' bypasses Unity's overloaded '==' operator.
            var furnitureKey = candidate.GetComponentInChildren<FurnitureKey>(true);
            if (furnitureKey == null)
            {
                furnitureKey = candidate.GetComponentInParent<FurnitureKey>();
            }

            if (furnitureKey != null && furnitureKey.Matches(requiredKeyId))
            {
                return true;
            }

            var keyItem = candidate.GetComponentInChildren<KeyItem>(true);
            if (keyItem == null)
            {
                keyItem = candidate.GetComponentInParent<KeyItem>();
            }

            if (keyItem != null && keyItem.Matches(requiredKeyId))
            {
                return true;
            }

            var placeable = candidate.GetComponentInChildren<PlaceableItem>(true);
            if (placeable == null)
            {
                placeable = candidate.GetComponentInParent<PlaceableItem>();
            }

            return placeable != null && FurnitureKey.IdsMatch(placeable.ItemId, requiredKeyId);
        }

        // ─────────────────────────────────────────────
        //  Lock internals
        // ─────────────────────────────────────────────

        /// <summary>
        /// Unlocks with whatever matching key the player has: the referenced key object
        /// in hand, or a matching key id (held or already on the key ring).
        /// </summary>
        private bool TryConsumeMatchingKey()
        {
            // 1. Direct scene reference: the player must hold that exact object.
            var carriedKey = FindCarriedKey();
            if (carriedKey != null)
            {
                UnlockWithReferenceKey(carriedKey);
                return true;
            }

            // 2. Key id via the shared KeyItem / KeyRing system (empty id = no id lock).
            if (!string.IsNullOrWhiteSpace(requiredKeyId) &&
                KeyItem.TryUseKey(requiredKeyId, keyAccess, consumeKey, out _))
            {
                return true;
            }

            return false;
        }

        private void UnlockWithReferenceKey(GameObject key)
        {
            if (consumeKey)
            {
                ConsumeKey(key);
            }
        }

        /// <summary>Shared key-unlock sequence: clear the lock, sound + event, auto-open.</summary>
        private void UnlockNow()
        {
            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();

            if (openWhenUnlocked && !_isOpen)
            {
                SetOpen(true);
            }
        }

        private void SetOpen(bool open)
        {
            if (open && _isLocked)
            {
                return;
            }

            if (_isOpen == open)
            {
                return;
            }

            _isOpen = open;

            if (open)
            {
                OnOpened?.Invoke();
            }
            else
            {
                OnClosed?.Invoke();

                if (relockOnClose)
                {
                    _isLocked = true;
                }
            }
        }

        /// <summary>Returns the object in the player's hands if it unlocks this furniture, otherwise null.</summary>
        private GameObject FindCarriedKey()
        {
            var carried = GetCarriedObject();
            return carried != null && IsMatchingKey(carried) ? carried : null;
        }

        /// <summary>The object the player is currently carrying with either carry system, or null.</summary>
        private GameObject GetCarriedObject()
        {
            // Physics carry (PlayerCarry / PlaceableItem).
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null)
            {
                return carry.HeldItem.gameObject;
            }

            // First-person carry (FPPCameraController / Interactable).
            if (playerCameraController != null && playerCameraController.CarriedInteractable != null)
            {
                return playerCameraController.CarriedInteractable.gameObject;
            }

            return null;
        }

        /// <summary>Takes the key out of the player's hands and hides it.</summary>
        private void ConsumeKey(GameObject key)
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null && carry.HeldItem.gameObject == key)
            {
                carry.TakeHeldItem();
            }

            if (playerCameraController != null &&
                playerCameraController.CarriedInteractable != null &&
                playerCameraController.CarriedInteractable.gameObject == key)
            {
                playerCameraController.ReleaseCarriedObject();
            }

            // Deactivate rather than destroy so puzzle resets (and mirror ghosts) keep working.
            key.SetActive(false);
        }

        // ─────────────────────────────────────────────
        //  Targeting / animation / prompt
        // ─────────────────────────────────────────────

        private bool IsTargetedByReticle()
        {
            if (playerCameraController == null)
            {
                playerCameraController = FindFirstObjectByType<FPPCameraController>();
            }

            if (playerCameraController == null)
            {
                return false;
            }

            var hitCount = Physics.RaycastNonAlloc(playerCameraController.ReticleRay, _reticleHits, interactionDistance,
                interactionLayers, QueryTriggerInteraction.Ignore);
            if (hitCount == 0)
            {
                return false;
            }

            // The object in the player's hands (e.g. the key held up to the lock) must never block
            // the reticle, so the closest hit that is not part of it decides what is targeted.
            var carried = GetCarriedObject();
            var nearest = -1;
            for (var i = 0; i < hitCount; i++)
            {
                var hitCollider = _reticleHits[i].collider;
                if (hitCollider == null ||
                    (carried != null && hitCollider.transform.IsChildOf(carried.transform)))
                {
                    continue;
                }

                if (nearest < 0 || _reticleHits[i].distance < _reticleHits[nearest].distance)
                {
                    nearest = i;
                }
            }

            return nearest >= 0 && _reticleHits[nearest].collider.GetComponentInParent<OpenableFurniture>() == this;
        }

        private void AnimateMovingPart()
        {
            var lerpFactor = 1f - Mathf.Exp(-openSpeed * Time.deltaTime);

            if (openMode == OpenMode.Rotate)
            {
                movingPart.localRotation = Quaternion.Slerp(
                    movingPart.localRotation,
                    _isOpen ? _openRotation : _closedRotation,
                    lerpFactor);
            }
            else
            {
                movingPart.localPosition = Vector3.Lerp(
                    movingPart.localPosition,
                    _isOpen ? _openPosition : _closedPosition,
                    lerpFactor);
            }
        }

        private void UpdatePromptText()
        {
            if (interactionPrompt != null)
            {
                interactionPrompt.text = BuildPromptText();
            }
        }

        private string BuildPromptText()
        {
            var keyName = interactKey.ToString().ToUpperInvariant();

            if (_isLocked)
            {
                if (unlockWithKey && PlayerHasMatchingKey(out var keyDisplayName))
                {
                    return string.Format(unlockPromptText, keyName, keyDisplayName);
                }

                return RequiresKey ? lockedPromptText : "Locked";
            }

            return string.Format(_isOpen ? closePromptText : openPromptText, keyName);
        }

        /// <summary>True when the player holds (or owns) a key that fits this lock right now.</summary>
        private bool PlayerHasMatchingKey(out string keyDisplayName)
        {
            // Referenced key object in hand.
            var carriedKey = FindCarriedKey();
            if (carriedKey != null)
            {
                var interactable = carriedKey.GetComponentInChildren<Interactable>(true);
                if (interactable == null)
                {
                    interactable = carriedKey.GetComponentInParent<Interactable>();
                }

                keyDisplayName = interactable != null ? interactable.DisplayName : carriedKey.name;
                return true;
            }

            // Key id via the shared KeyItem / KeyRing system.
            if (!string.IsNullOrWhiteSpace(requiredKeyId) &&
                KeyItem.PlayerHasKey(requiredKeyId, keyAccess))
            {
                keyDisplayName = KeyItem.DescribeKey(requiredKeyId, keyAccess);
                return true;
            }

            keyDisplayName = null;
            return false;
        }

        private void SetPromptVisible(bool visible)
        {
            if (interactionPrompt != null)
            {
                interactionPrompt.gameObject.SetActive(visible);
            }
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null)
            {
                AudioSource.PlayClipAtPoint(clip, movingPart != null ? movingPart.position : transform.position);
            }
        }
    }
}
