using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Interaction
{
    /// <summary>
    /// Generic openable furniture component that implements IInteractable.
    /// Supports rotating (doors, lids, cabinet doors) and sliding (drawers) open modes,
    /// optional locking, audio feedback, and UnityEvents.
    ///
    /// Attach this to any GameObject with a Collider. The PlayerInteraction system
    /// handles raycast detection and input — this script only manages the open/close behaviour.
    ///
    /// Scene hierarchy example:
    ///   Furniture (this script + Collider)
    ///     └── MovingPart  ← the part that rotates or slides
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class OpenableInteractable : MonoBehaviour, IInteractable
    {
        public enum OpenMode
        {
            /// <summary>Rotates the moving part (doors, lids, cabinet doors).</summary>
            Rotate,
            /// <summary>Slides the moving part along a local offset (drawers).</summary>
            Slide
        }

        // ─────────────────────────────────────────────
        //  Inspector Fields
        // ─────────────────────────────────────────────

        [Header("Moving Part")]
        [Tooltip("The child transform that moves when opened/closed. If left empty, uses this transform.")]
        [SerializeField] private Transform movingPart;

        [Tooltip("Whether the part rotates (door/lid) or slides (drawer).")]
        [SerializeField] private OpenMode openMode = OpenMode.Rotate;

        [Header("Rotate Mode")]
        [Tooltip("Euler angles (local space) the part rotates to when open. e.g. (0, 90, 0) for a door.")]
        [SerializeField] private Vector3 openRotation = new Vector3(0f, 90f, 0f);

        [Header("Slide Mode")]
        [Tooltip("Local-space offset the part slides to when open. e.g. (0, 0, 0.4) for a drawer.")]
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 0f, 0.4f);

        [Header("Animation")]
        [Tooltip("Duration of the open/close animation in seconds.")]
        [SerializeField] private float animationDuration = 0.6f;

        [Tooltip("Easing curve for the animation.")]
        [SerializeField] private AnimationCurve animationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Lock (Optional)")]
        [Tooltip("If true, the object starts locked and requires Unlock() to be called first.")]
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

        [Tooltip("Lock again every time the object is closed (the key is needed each time).")]
        [SerializeField] private bool relockOnClose;

        [Header("Audio (Optional)")]
        [Tooltip("AudioSource for playing interaction sounds. If null, no sound plays.")]
        [SerializeField] private AudioSource audioSource;

        [Tooltip("Sound played when the object opens.")]
        [SerializeField] private AudioClip openSound;

        [Tooltip("Sound played when the object closes.")]
        [SerializeField] private AudioClip closeSound;

        [Tooltip("Sound played when the player tries to interact while locked.")]
        [SerializeField] private AudioClip lockedSound;

        [Tooltip("Sound played when a key unlocks the object.")]
        [SerializeField] private AudioClip unlockSound;

        [Header("Events")]
        [Tooltip("Fired when the object finishes opening.")]
        public UnityEvent OnOpened;

        [Tooltip("Fired when the object finishes closing.")]
        public UnityEvent OnClosed;

        [Tooltip("Fired when the player tries to interact but the object is locked.")]
        public UnityEvent OnLockedAttempt;

        [Tooltip("Fired when the object is unlocked (by key or by script).")]
        public UnityEvent OnUnlocked;

        // ─────────────────────────────────────────────
        //  Runtime State
        // ─────────────────────────────────────────────

        private bool _isOpen;
        private bool _isLocked;
        private bool _isAnimating;
        private Quaternion _closedRotation;
        private Quaternion _openRotationQ;
        private Vector3 _closedPosition;
        private Vector3 _openPosition;
        private Coroutine _animationCoroutine;

        // ─────────────────────────────────────────────
        //  IInteractable
        // ─────────────────────────────────────────────

        /// <inheritdoc/>
        public string InteractionPrompt
        {
            get
            {
                if (_isAnimating) return "";

                if (_isLocked)
                {
                    if (unlockWithKey && KeyItem.PlayerHasKey(requiredKeyId, keyAccess))
                        return $"Press [E] to Unlock with {KeyItem.DescribeKey(requiredKeyId, keyAccess)}";

                    return "Locked [Need Key]";
                }

                return _isOpen ? "Press [E] to Close" : "Press [E] to Open";
            }
        }

        /// <inheritdoc/>
        public bool CanInteract => !_isAnimating;

        /// <inheritdoc/>
        public void OnInteract()
        {
            if (_isAnimating) return;

            if (_isLocked)
            {
                TryUnlockWithKey();
                return;
            }

            if (_isOpen)
                Close();
            else
                Open();
        }

        // ─────────────────────────────────────────────
        //  Unity Lifecycle
        // ─────────────────────────────────────────────

        private void Awake()
        {
            if (movingPart == null)
                movingPart = transform;

            _isLocked = startsLocked;

            // Cache closed state
            _closedRotation = movingPart.localRotation;
            _closedPosition = movingPart.localPosition;

            // Compute open targets
            _openRotationQ = _closedRotation * Quaternion.Euler(openRotation);
            _openPosition = _closedPosition + openOffset;
        }

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>Opens the object with animation.</summary>
        public void Open()
        {
            if (_isOpen || _isAnimating || _isLocked) return;

            PlaySound(openSound);
            _animationCoroutine = StartCoroutine(Animate(true));
        }

        /// <summary>Closes the object with animation.</summary>
        public void Close()
        {
            if (!_isOpen || _isAnimating) return;

            PlaySound(closeSound);
            _animationCoroutine = StartCoroutine(Animate(false));
        }

        /// <summary>Instantly snaps to the open position without animation.</summary>
        public void ForceOpen()
        {
            StopCurrentAnimation();
            _isOpen = true;
            ApplyState(_openRotationQ, _openPosition);
        }

        /// <summary>Instantly snaps to the closed position without animation.</summary>
        public void ForceClose()
        {
            StopCurrentAnimation();
            _isOpen = false;
            ApplyState(_closedRotation, _closedPosition);
        }

        /// <summary>Unlocks the object so the player can interact.</summary>
        public void Unlock()
        {
            if (!_isLocked) return;

            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();
        }

        /// <summary>
        /// Tries to unlock with a key the player owns (held or on the key ring).
        /// Returns true when a matching key was found; otherwise plays the locked
        /// feedback. Safe to call from UnityEvents.
        /// </summary>
        public bool TryUnlockWithKey()
        {
            if (!_isLocked) return true;

            if (unlockWithKey &&
                KeyItem.TryUseKey(requiredKeyId, keyAccess, consumeKeyOnUnlock, out _))
            {
                _isLocked = false;
                PlaySound(unlockSound);
                OnUnlocked?.Invoke();

                if (openOnUnlock && !_isOpen)
                    Open();

                return true;
            }

            PlaySound(lockedSound);
            OnLockedAttempt?.Invoke();
            return false;
        }

        /// <summary>True when the player owns a key that fits this lock right now.</summary>
        public bool PlayerHasMatchingKey() =>
            unlockWithKey && KeyItem.PlayerHasKey(requiredKeyId, keyAccess);

        /// <summary>Locks the object. If currently open, force-closes it.</summary>
        public void Lock()
        {
            _isLocked = true;

            if (_isOpen)
                ForceClose();
        }

        /// <summary>Whether the object is currently in the open state.</summary>
        public bool IsOpen => _isOpen;

        /// <summary>Whether the object is currently locked.</summary>
        public bool IsLocked => _isLocked;

        // ─────────────────────────────────────────────
        //  Animation
        // ─────────────────────────────────────────────

        private IEnumerator Animate(bool opening)
        {
            _isAnimating = true;

            Quaternion fromRot = opening ? _closedRotation : _openRotationQ;
            Quaternion toRot = opening ? _openRotationQ : _closedRotation;
            Vector3 fromPos = opening ? _closedPosition : _openPosition;
            Vector3 toPos = opening ? _openPosition : _closedPosition;

            float elapsed = 0f;

            while (elapsed < animationDuration)
            {
                elapsed += Time.deltaTime;
                float t = animationCurve.Evaluate(Mathf.Clamp01(elapsed / animationDuration));

                if (openMode == OpenMode.Rotate)
                    movingPart.localRotation = Quaternion.Slerp(fromRot, toRot, t);
                else
                    movingPart.localPosition = Vector3.Lerp(fromPos, toPos, t);

                yield return null;
            }

            // Snap to final state
            if (openMode == OpenMode.Rotate)
                movingPart.localRotation = toRot;
            else
                movingPart.localPosition = toPos;

            _isOpen = opening;
            _isAnimating = false;
            _animationCoroutine = null;

            if (opening)
            {
                OnOpened?.Invoke();
            }
            else
            {
                if (relockOnClose)
                    _isLocked = true;

                OnClosed?.Invoke();
            }
        }

        private void StopCurrentAnimation()
        {
            if (_animationCoroutine != null)
            {
                StopCoroutine(_animationCoroutine);
                _animationCoroutine = null;
            }
            _isAnimating = false;
        }

        private void ApplyState(Quaternion rotation, Vector3 position)
        {
            if (movingPart == null) return;

            if (openMode == OpenMode.Rotate)
                movingPart.localRotation = rotation;
            else
                movingPart.localPosition = position;
        }

        // ─────────────────────────────────────────────
        //  Audio
        // ─────────────────────────────────────────────

        private void PlaySound(AudioClip clip)
        {
            if (audioSource != null && clip != null)
                audioSource.PlayOneShot(clip);
        }
    }
}
