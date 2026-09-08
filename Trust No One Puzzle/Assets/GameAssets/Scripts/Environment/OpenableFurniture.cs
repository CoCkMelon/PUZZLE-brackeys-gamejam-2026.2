using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Interaction;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Opens a door by rotating its pivot or opens a drawer by sliding it.
    /// The object holding this component needs a collider that can be hit by the player's reticle ray.
    ///
    /// Locking (optional): set <c>Starts Locked</c> and the furniture refuses to
    /// open until it is unlocked. When <c>Unlock With Key</c> is on, the player
    /// only has to look at it and press the interact button while owning a
    /// matching <see cref="KeyItem"/> — either physically carried or already
    /// collected into the <see cref="KeyRing"/>. Leave <c>Unlock With Key</c> off
    /// to keep a puzzle-only lock that is opened from script / UnityEvents
    /// through <see cref="Unlock"/>.
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

        [Header("Opening")]
        [SerializeField] private OpenMode openMode = OpenMode.Rotate;
        [SerializeField] private Vector3 openRotation = new Vector3(0f, 90f, 0f);
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 0f, 0.4f);
        [SerializeField, Min(0.1f)] private float openSpeed = 8f;

        [Header("Input")]
        [SerializeField] private Key interactKey = Key.E;

        [Header("Lock (Optional)")]
        [Tooltip("Starts locked: the furniture cannot be opened until it is unlocked.")]
        [SerializeField] private bool startsLocked;

        [Tooltip("Allow the player to unlock this by interacting while owning a matching key. " +
                 "Off = only script / UnityEvent calls to Unlock() open the lock.")]
        [SerializeField] private bool unlockWithKey = true;

        [Tooltip("Key id that fits this lock (must match KeyItem.keyId). Leave empty to accept any key.")]
        [SerializeField] private string requiredKeyId = "";

        [Tooltip("Where the key may come from: the player's hands, the collected key ring, or either.")]
        [SerializeField] private KeyAccess keyAccess = KeyAccess.HeldOrKeyRing;

        [Tooltip("Spend the key when it opens this lock (only affects keys marked Consumed On Use).")]
        [SerializeField] private bool consumeKeyOnUnlock = true;

        [Tooltip("Open immediately in the same interaction that unlocks it.")]
        [SerializeField] private bool openOnUnlock = true;

        [Tooltip("Lock again every time the furniture is closed (the key is needed each time).")]
        [SerializeField] private bool relockOnClose;

        [Header("Prompts")]
        [SerializeField] private string openPromptText = "Press {0} To Open";
        [SerializeField] private string closePromptText = "Press {0} To Close";
        [SerializeField] private string lockedPromptText = "Locked [Need Key]";
        [SerializeField] private string unlockPromptText = "Press {0} To Unlock with {1}";

        [Header("Audio (Optional)")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip openSound;
        [SerializeField] private AudioClip closeSound;
        [SerializeField] private AudioClip unlockSound;
        [SerializeField] private AudioClip lockedSound;

        [Header("Events")]
        public UnityEvent OnOpened;
        public UnityEvent OnClosed;
        public UnityEvent OnUnlocked;
        public UnityEvent OnLockedAttempt;

        private Quaternion _closedRotation;
        private Quaternion _openRotation;
        private Vector3 _closedPosition;
        private Vector3 _openPosition;
        private bool _isOpen;
        private bool _isLocked;

        /// <summary>Whether the furniture is currently in the open state.</summary>
        public bool IsOpen => _isOpen;

        /// <summary>Whether the furniture is currently locked.</summary>
        public bool IsLocked => _isLocked;

        /// <summary>Key id this lock asks for (empty = any key).</summary>
        public string RequiredKeyId => requiredKeyId;

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
        //  Public API
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
        /// when the player has no key.
        /// </summary>
        public bool TryUnlockWithKey()
        {
            if (!_isLocked)
            {
                return true;
            }

            if (unlockWithKey &&
                KeyItem.TryUseKey(requiredKeyId, keyAccess, consumeKeyOnUnlock, out _))
            {
                _isLocked = false;
                PlaySound(unlockSound);
                OnUnlocked?.Invoke();

                if (openOnUnlock && !_isOpen)
                {
                    SetOpen(true);
                }

                return true;
            }

            PlaySound(lockedSound);
            OnLockedAttempt?.Invoke();
            return false;
        }

        /// <summary>True when the player owns a key that fits this lock right now.</summary>
        public bool PlayerHasMatchingKey() =>
            unlockWithKey && KeyItem.PlayerHasKey(requiredKeyId, keyAccess);

        // ─────────────────────────────────────────────
        //  Internals
        // ─────────────────────────────────────────────

        private void SetOpen(bool open)
        {
            if (open && _isLocked)
            {
                PlaySound(lockedSound);
                OnLockedAttempt?.Invoke();
                return;
            }

            if (_isOpen == open)
            {
                return;
            }

            _isOpen = open;
            PlaySound(open ? openSound : closeSound);

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

        private bool IsTargetedByReticle()
        {
            if (playerCameraController == null)
            {
                playerCameraController = FindFirstObjectByType<FPPCameraController>();
            }

            if (playerCameraController == null ||
                !Physics.Raycast(playerCameraController.ReticleRay, out var hit, interactionDistance, interactionLayers,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return hit.collider.GetComponentInParent<OpenableFurniture>() == this;
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
            if (interactionPrompt == null)
            {
                return;
            }

            interactionPrompt.text = BuildPromptText();
        }

        private string BuildPromptText()
        {
            var keyName = interactKey.ToString().ToUpperInvariant();

            if (_isLocked)
            {
                if (unlockWithKey && KeyItem.PlayerHasKey(requiredKeyId, keyAccess))
                {
                    return string.Format(unlockPromptText, keyName,
                        KeyItem.DescribeKey(requiredKeyId, keyAccess));
                }

                return lockedPromptText;
            }

            return string.Format(_isOpen ? closePromptText : openPromptText, keyName);
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
            if (audioSource != null && clip != null)
            {
                audioSource.PlayOneShot(clip);
            }
        }
    }
}
