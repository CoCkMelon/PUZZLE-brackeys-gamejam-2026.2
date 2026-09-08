using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Opens a door by rotating its pivot or opens a drawer by sliding it.
    /// The object holding this component needs a collider that can be hit by the player's reticle ray.
    ///
    /// Optional lock: tick <c>Starts Locked</c> and the furniture refuses to open until it is unlocked.
    /// It can be unlocked in two ways:
    ///   1. With a key: the player carries the key object and presses E while looking at the furniture.
    ///      The key is matched either by a direct scene reference (<c>Required Key</c>) or by id
    ///      (<c>Required Key Id</c> against a <see cref="FurnitureKey"/> / <see cref="PlaceableItem"/> on the carried object).
    ///   2. From code or a UnityEvent by calling <see cref="Unlock"/> (puzzles, triggers, dialogue, ...).
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class OpenableFurniture : MonoBehaviour
    {
        private enum OpenMode
        {
            Rotate,
            Slide
        }

        private const string PromptOpen = "Press E To Open";
        private const string PromptClose = "Press E To Close";
        private const string PromptUnlock = "Press E To Unlock";
        private const string PromptLockedNeedKey = "Locked [Need Key]";
        private const string PromptLocked = "Locked";

        [Header("References")]
        [SerializeField] private Transform movingPart;
        [SerializeField] private TMP_Text interactionPrompt;
        [SerializeField] private FPPCameraController playerCameraController;

        [Header("Raycast")]
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionLayers = ~0;

        [Header("Opening")]
        [SerializeField] private OpenMode openMode = OpenMode.Rotate;
        [SerializeField] private Vector3 openRotation = new Vector3(0f, 90f, 0f);
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 0f, 0.4f);
        [SerializeField, Min(0.1f)] private float openSpeed = 8f;

        [Header("Lock (Optional)")]
        [Tooltip("If true, the furniture starts locked and will not open until it is unlocked with a key or Unlock() is called.")]
        [SerializeField] private bool startsLocked;

        [Tooltip("A specific scene object that works as the key. The player has to carry it and press E on this furniture. " +
                 "Leave empty to match keys by Required Key Id only (or to unlock from code/events only).")]
        [SerializeField] private GameObject requiredKey;

        [Tooltip("Any carried object with a FurnitureKey (or PlaceableItem) whose id equals this value unlocks the furniture. " +
                 "Case-insensitive. Leave empty to match by Required Key only (or to unlock from code/events only).")]
        [SerializeField] private string requiredKeyId;

        [Tooltip("Take the key out of the player's hands and hide it once it has been used.")]
        [SerializeField] private bool consumeKey = true;

        [Tooltip("Open the furniture right away after it has been unlocked with a key.")]
        [SerializeField] private bool openWhenUnlocked = true;

        [Tooltip("Played at the furniture when it is unlocked with a key.")]
        [SerializeField] private AudioClip unlockSound;

        [Tooltip("Played at the furniture when the player presses E on it while it is locked without the key.")]
        [SerializeField] private AudioClip lockedSound;

        [Header("Lock Events")]
        [Tooltip("Fired once when the furniture becomes unlocked (by key or Unlock()).")]
        public UnityEvent OnUnlocked;

        [Tooltip("Fired when the player presses E on the furniture while it is locked and has no matching key.")]
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
        public string PromptText
        {
            get
            {
                if (_isLocked)
                {
                    if (FindCarriedKey() != null)
                    {
                        return PromptUnlock;
                    }

                    return RequiresKey ? PromptLockedNeedKey : PromptLocked;
                }

                return _isOpen ? PromptClose : PromptOpen;
            }
        }

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

            if (isTargeted)
            {
                UpdatePromptText();

                if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
                {
                    Interact();
                    UpdatePromptText();
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Public API (also usable from UnityEvents)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Same as the player pressing E while looking at the furniture:
        /// toggles it when unlocked, otherwise tries to unlock it with the carried key.
        /// </summary>
        public void Interact()
        {
            if (_isLocked)
            {
                var key = FindCarriedKey();
                if (key != null)
                {
                    UnlockWithKey(key);
                }
                else
                {
                    RejectLockedInteraction();
                }

                return;
            }

            _isOpen = !_isOpen;
        }

        /// <summary>Opens the furniture unless it is locked.</summary>
        public void Open()
        {
            if (!_isLocked)
            {
                _isOpen = true;
            }
        }

        /// <summary>Closes the furniture.</summary>
        public void Close()
        {
            _isOpen = false;
        }

        /// <summary>Unlocks the furniture so it can be opened. Call from other puzzles, triggers or events.</summary>
        public void Unlock()
        {
            if (!_isLocked)
            {
                return;
            }

            _isLocked = false;
            OnUnlocked?.Invoke();
        }

        /// <summary>Locks the furniture again. If it is open, it closes.</summary>
        public void Lock()
        {
            _isLocked = true;
            _isOpen = false;
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

            UnlockWithKey(candidate);
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

        private void UnlockWithKey(GameObject key)
        {
            if (consumeKey)
            {
                ConsumeKey(key);
            }

            PlaySound(unlockSound);
            Unlock();

            if (openWhenUnlocked)
            {
                _isOpen = true;
            }
        }

        private void RejectLockedInteraction()
        {
            PlaySound(lockedSound);
            OnLockedAttempt?.Invoke();
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
                interactionPrompt.text = PromptText;
            }
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
