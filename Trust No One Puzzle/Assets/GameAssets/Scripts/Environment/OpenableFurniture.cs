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
    /// Opens door/drawer using Rigidbody joints to prevent penetration.
    /// When locked: Rigidbody disabled (isKinematic true, joint locked at 0).
    /// When unlocked: Rigidbody enabled (isKinematic false), joint allows movement.
    /// Player can push or use PlayerCarry (force-limited) to move it - same carry system moves furniture.
    /// Supports HingeJoint for Rotate and ConfigurableJoint for Slide.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class OpenableFurniture : MonoBehaviour
    {
        private enum OpenMode { Rotate, Slide }

        [Header("References")]
        [SerializeField] private Transform movingPart;
        [SerializeField] private TMP_Text interactionPrompt;
        [SerializeField] private FPPCameraController playerCameraController;

        [Header("Raycast")]
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionLayers = ~0;

        [Header("Interaction")]
        [SerializeField] private Key interactKey = Key.E;

        [Header("Opening")]
        [SerializeField] private OpenMode openMode = OpenMode.Rotate;
        [SerializeField] private Vector3 openRotation = new Vector3(0f, 90f, 0f);
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 0f, 0.4f);
        [SerializeField, Min(0.1f)] private float openSpeed = 8f;

        [Header("Physics - Joint Based (no penetration)")]
        [Tooltip("Use joint + Rigidbody instead of direct transform. Prevents penetration.")]
        [SerializeField] private bool useJoint = true;
        [Tooltip("Rigidbody of moving part. Auto-found if null.")]
        [SerializeField] private Rigidbody movingRigidbody;
        [Tooltip("When locked, Rigidbody is disabled (isKinematic true). When unlocked, enabled (isKinematic false) so PlayerCarry can move it.")]
        [SerializeField] private bool disableRigidbodyWhenLocked = true;
        [Tooltip("Max force for auto open/close motor.")]
        [SerializeField] private float jointMotorForce = 500f;
        [Tooltip("Spring strength for joint.")]
        [SerializeField] private float jointSpring = 500f;
        [SerializeField] private float jointDamper = 50f;
        [Tooltip("If true, player must physically push door (no auto motor). If false, motor drives to open/closed.")]
        [SerializeField] private bool requirePlayerPush = false;

        [Header("Lock")]
        [SerializeField] private bool startsLocked;
        [SerializeField] private GameObject requiredKey;
        [SerializeField] private string requiredKeyId;
        [SerializeField] private bool unlockWithKey = true;
        [SerializeField] private KeyAccess keyAccess = KeyAccess.HeldOrKeyRing;
        [SerializeField] private bool consumeKey = true;
        [SerializeField] private bool openWhenUnlocked = true;
        [SerializeField] private bool relockOnClose;
        [SerializeField] private AudioClip unlockSound;
        [SerializeField] private AudioClip lockedSound;

        [Header("Prompts")]
        [SerializeField] private string openPromptText = "Press {0} To Open";
        [SerializeField] private string closePromptText = "Press {0} To Close";
        [SerializeField] private string lockedPromptText = "Locked [Need Key]";
        [SerializeField] private string unlockPromptText = "Press {0} To Unlock with {1}";

        [Header("Events")]
        public UnityEvent OnOpened;
        public UnityEvent OnClosed;
        public UnityEvent OnUnlocked;
        public UnityEvent OnLockedAttempt;

        private readonly RaycastHit[] _reticleHits = new RaycastHit[32];
        private Quaternion _closedRotation;
        private Quaternion _openRotation;
        private Vector3 _closedPosition;
        private Vector3 _openPosition;
        private bool _isOpen;
        private bool _isLocked;

        // Joint refs
        private HingeJoint _hinge;
        private ConfigurableJoint _configurable;
        private Rigidbody _parentRigidbody;
        private float _openAngle;
        private Vector3 _slideAxis;
        private float _slideDistance;

        public bool IsOpen => _isOpen;
        public bool IsLocked => _isLocked;
        public bool RequiresKey => requiredKey != null || !string.IsNullOrWhiteSpace(requiredKeyId);
        public string PromptText => BuildPromptText();

        private void Awake()
        {
            if (movingPart == null) movingPart = transform;

            _closedRotation = movingPart.localRotation;
            _openRotation = _closedRotation * Quaternion.Euler(openRotation);
            _closedPosition = movingPart.localPosition;
            _openPosition = _closedPosition + openOffset;
            _isLocked = startsLocked;

            if (playerCameraController == null)
                playerCameraController = FindFirstObjectByType<FPPCameraController>();

            if (movingRigidbody == null)
                movingRigidbody = movingPart.GetComponent<Rigidbody>();

            if (movingRigidbody == null && useJoint)
            {
                Debug.LogWarning($"[OpenableFurniture] {name}: useJoint enabled but no Rigidbody on {movingPart.name}. Add Rigidbody (mass 5-20, drag 1, angular drag 5, Continuous).", this);
            }

            // Find parent Rigidbody for joint connection (if any)
            if (movingPart.parent != null)
                _parentRigidbody = movingPart.parent.GetComponentInParent<Rigidbody>();

            // Precompute open angle and slide axis
            _openAngle = openRotation.magnitude;
            // For hinge, axis is normalized openRotation vector
            _slideAxis = openOffset.normalized;
            _slideDistance = openOffset.magnitude;

            if (useJoint && movingRigidbody != null)
            {
                SetupJoint();
                ApplyLockState(); // sets isKinematic and joint limits based on locked
            }

            SetPromptVisible(false);
        }

        private void SetupJoint()
        {
            if (movingRigidbody == null) return;

            // Ensure Rigidbody settings for no penetration
            movingRigidbody.useGravity = false;
            movingRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            movingRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            movingRigidbody.linearDamping = 1f;
            movingRigidbody.angularDamping = 5f;

            if (openMode == OpenMode.Rotate)
            {
                // Use HingeJoint for doors
                _hinge = movingPart.GetComponent<HingeJoint>();
                if (_hinge == null) _hinge = movingPart.gameObject.AddComponent<HingeJoint>();

                _hinge.connectedBody = _parentRigidbody;
                _hinge.anchor = Vector3.zero; // pivot at movingPart origin - designer should set movingPart pivot at hinge
                _hinge.axis = GetHingeAxis();
                _hinge.useLimits = true;
                _hinge.useMotor = !requirePlayerPush;
                _hinge.useSpring = !requirePlayerPush;

                // Configure limits - will be updated in ApplyLockState
                var limits = _hinge.limits;
                limits.min = 0f;
                limits.max = _isLocked ? 0f : _openAngle;
                _hinge.limits = limits;

                if (!requirePlayerPush)
                {
                    var spring = _hinge.spring;
                    spring.spring = jointSpring;
                    spring.damper = jointDamper;
                    spring.targetPosition = _isOpen ? _openAngle : 0f;
                    _hinge.spring = spring;

                    var motor = _hinge.motor;
                    motor.force = jointMotorForce;
                    motor.targetVelocity = 0f;
                    motor.freeSpin = false;
                    _hinge.motor = motor;
                }
            }
            else // Slide
            {
                // Use ConfigurableJoint for drawers
                _configurable = movingPart.GetComponent<ConfigurableJoint>();
                if (_configurable == null) _configurable = movingPart.gameObject.AddComponent<ConfigurableJoint>();

                _configurable.connectedBody = _parentRigidbody;
                _configurable.anchor = Vector3.zero;
                _configurable.axis = _slideAxis;
                _configurable.secondaryAxis = Vector3.up; // arbitrary perpendicular

                // Lock angular motion
                _configurable.angularXMotion = ConfigurableJointMotion.Locked;
                _configurable.angularYMotion = ConfigurableJointMotion.Locked;
                _configurable.angularZMotion = ConfigurableJointMotion.Locked;

                // Linear motion: lock 2 axes, free 1 along slide axis
                // We need to determine which axis is slide - use XMotion for primary axis
                // For simplicity, we set XMotion limited, Y/Z locked, and set axis to slide direction
                _configurable.xMotion = ConfigurableJointMotion.Limited;
                _configurable.yMotion = ConfigurableJointMotion.Locked;
                _configurable.zMotion = ConfigurableJointMotion.Locked;

                // If slide axis is not X, we need to rotate joint frame - easier: keep XMotion as slide and set axis accordingly
                // ConfigurableJoint axis defines XMotion direction, so we already set axis = slideAxis

                var limit = _configurable.linearLimit;
                limit.limit = _isLocked ? 0f : _slideDistance;
                _configurable.linearLimit = limit;

                // Spring to drive to target
                if (!requirePlayerPush)
                {
                    var xDrive = _configurable.xDrive;
                    xDrive.positionSpring = jointSpring;
                    xDrive.positionDamper = jointDamper;
                    xDrive.maximumForce = jointMotorForce;
                    _configurable.xDrive = xDrive;
                    _configurable.targetPosition = _isOpen ? new Vector3(_slideDistance, 0, 0) : Vector3.zero;
                }
            }
        }

        private Vector3 GetHingeAxis()
        {
            // Determine hinge axis from openRotation - dominant component
            Vector3 abs = new Vector3(Mathf.Abs(openRotation.x), Mathf.Abs(openRotation.y), Mathf.Abs(openRotation.z));
            if (abs.x >= abs.y && abs.x >= abs.z) return new Vector3(Mathf.Sign(openRotation.x), 0, 0);
            if (abs.y >= abs.x && abs.y >= abs.z) return new Vector3(0, Mathf.Sign(openRotation.y), 0);
            return new Vector3(0, 0, Mathf.Sign(openRotation.z));
        }

        private void ApplyLockState()
        {
            if (movingRigidbody == null) return;

            if (_isLocked && disableRigidbodyWhenLocked)
            {
                // When locked: Rigidbody disabled (isKinematic true) so it can't be moved, even by PlayerCarry
                movingRigidbody.isKinematic = true;
                movingRigidbody.linearVelocity = Vector3.zero;
                movingRigidbody.angularVelocity = Vector3.zero;

                // Lock joint at 0
                if (_hinge != null)
                {
                    var limits = _hinge.limits;
                    limits.min = 0f;
                    limits.max = 0f;
                    _hinge.limits = limits;
                    if (!requirePlayerPush)
                    {
                        var spring = _hinge.spring;
                        spring.targetPosition = 0f;
                        _hinge.spring = spring;
                    }
                }
                if (_configurable != null)
                {
                    var limit = _configurable.linearLimit;
                    limit.limit = 0f;
                    _configurable.linearLimit = limit;
                    if (!requirePlayerPush)
                        _configurable.targetPosition = Vector3.zero;
                }
            }
            else
            {
                // When unlocked: Rigidbody enabled (isKinematic false) so physics and PlayerCarry can move it
                movingRigidbody.isKinematic = false;
                movingRigidbody.WakeUp();

                // Allow movement up to open range
                if (_hinge != null)
                {
                    var limits = _hinge.limits;
                    limits.min = 0f;
                    limits.max = _openAngle;
                    _hinge.limits = limits;
                }
                if (_configurable != null)
                {
                    var limit = _configurable.linearLimit;
                    limit.limit = _slideDistance;
                    _configurable.linearLimit = limit;
                }

                // Update motor target to current open state
                UpdateJointTarget();
            }
        }

        private void UpdateJointTarget()
        {
            if (movingRigidbody == null || _isLocked) return;
            if (requirePlayerPush) return; // player must push, no motor

            if (_hinge != null)
            {
                var spring = _hinge.spring;
                spring.targetPosition = _isOpen ? _openAngle : 0f;
                _hinge.spring = spring;
            }
            if (_configurable != null)
            {
                _configurable.targetPosition = _isOpen ? new Vector3(_isOpen ? _slideDistance : 0f, 0, 0) : Vector3.zero;
            }
        }

        private void Update()
        {
            // If not using joint or no Rigidbody, fallback to kinematic lerp (old, can penetrate but kept for compatibility)
            if (!useJoint || movingRigidbody == null)
                AnimateMovingPartKinematic();

            var isTargeted = IsTargetedByReticle();
            SetPromptVisible(isTargeted);
            if (!isTargeted) return;

            UpdatePromptText();

            if (Keyboard.current != null && Keyboard.current[interactKey].wasPressedThisFrame)
            {
                Interact();
                UpdatePromptText();
            }
        }

        // ─────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────

        public void Interact()
        {
            if (_isLocked)
            {
                TryUnlockWithKey();
                return;
            }

            // If requirePlayerPush, Interact just toggles intent - player must physically push
            // If not, motor will drive to open/closed
            SetOpen(!_isOpen);
        }

        public void Open() => SetOpen(true);
        public void Close() => SetOpen(false);

        public void Unlock()
        {
            if (!_isLocked) return;
            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();
            ApplyLockState();
        }

        public void Lock()
        {
            _isLocked = true;
            if (_isOpen) SetOpen(false);
            ApplyLockState();
        }

        public bool TryUnlockWithKey()
        {
            if (!_isLocked) return true;
            if (unlockWithKey && TryConsumeMatchingKey())
            {
                UnlockNow();
                return true;
            }
            PlaySound(lockedSound);
            OnLockedAttempt?.Invoke();
            return false;
        }

        public bool TryUnlockWith(GameObject candidate)
        {
            if (!_isLocked || !IsMatchingKey(candidate)) return false;
            if (consumeKey) ConsumeKey(candidate);
            UnlockNow();
            return true;
        }

        public bool IsMatchingKey(GameObject candidate)
        {
            if (candidate == null) return false;
            if (requiredKey != null && (candidate == requiredKey || candidate.transform.IsChildOf(requiredKey.transform))) return true;
            if (string.IsNullOrWhiteSpace(requiredKeyId)) return false;

            var furnitureKey = candidate.GetComponentInChildren<FurnitureKey>(true);
            if (furnitureKey == null) furnitureKey = candidate.GetComponentInParent<FurnitureKey>();
            if (furnitureKey != null && furnitureKey.Matches(requiredKeyId)) return true;

            var keyItem = candidate.GetComponentInChildren<KeyItem>(true);
            if (keyItem == null) keyItem = candidate.GetComponentInParent<KeyItem>();
            if (keyItem != null && keyItem.Matches(requiredKeyId)) return true;

            var placeable = candidate.GetComponentInChildren<PlaceableItem>(true);
            if (placeable == null) placeable = candidate.GetComponentInParent<PlaceableItem>();
            return placeable != null && FurnitureKey.IdsMatch(placeable.ItemId, requiredKeyId);
        }

        private bool TryConsumeMatchingKey()
        {
            var carriedKey = FindCarriedKey();
            if (carriedKey != null) { UnlockWithReferenceKey(carriedKey); return true; }
            if (!string.IsNullOrWhiteSpace(requiredKeyId) && KeyItem.TryUseKey(requiredKeyId, keyAccess, consumeKey, out _)) return true;
            return false;
        }

        private void UnlockWithReferenceKey(GameObject key) { if (consumeKey) ConsumeKey(key); }

        private void UnlockNow()
        {
            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();
            ApplyLockState();
            if (openWhenUnlocked && !_isOpen) SetOpen(true);
        }

        private void SetOpen(bool open)
        {
            if (open && _isLocked) return;
            if (_isOpen == open) return;
            _isOpen = open;
            if (open) OnOpened?.Invoke(); else { OnClosed?.Invoke(); if (relockOnClose) { _isLocked = true; ApplyLockState(); } }
            UpdateJointTarget();
        }

        private GameObject FindCarriedKey()
        {
            var carried = GetCarriedObject();
            return carried != null && IsMatchingKey(carried) ? carried : null;
        }

        private GameObject GetCarriedObject()
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null) return carry.HeldItem.gameObject;
            if (playerCameraController != null && playerCameraController.CarriedInteractable != null) return playerCameraController.CarriedInteractable.gameObject;
            return null;
        }

        private void ConsumeKey(GameObject key)
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null && carry.HeldItem.gameObject == key) carry.TakeHeldItem();
            if (playerCameraController != null && playerCameraController.CarriedInteractable != null && playerCameraController.CarriedInteractable.gameObject == key) playerCameraController.ReleaseCarriedObject();
            key.SetActive(false);
        }

        // ─────────────────────────────────────────────
        // Targeting / prompt / fallback animation
        // ─────────────────────────────────────────────

        private bool IsTargetedByReticle()
        {
            if (playerCameraController == null) playerCameraController = FindFirstObjectByType<FPPCameraController>();
            if (playerCameraController == null) return false;
            var hitCount = Physics.RaycastNonAlloc(playerCameraController.ReticleRay, _reticleHits, interactionDistance, interactionLayers, QueryTriggerInteraction.Ignore);
            if (hitCount == 0) return false;
            var carried = GetCarriedObject();
            var nearest = -1;
            for (var i = 0; i < hitCount; i++)
            {
                var hitCollider = _reticleHits[i].collider;
                if (hitCollider == null || (carried != null && hitCollider.transform.IsChildOf(carried.transform))) continue;
                if (nearest < 0 || _reticleHits[i].distance < _reticleHits[nearest].distance) nearest = i;
            }
            return nearest >= 0 && _reticleHits[nearest].collider.GetComponentInParent<OpenableFurniture>() == this;
        }

        private void AnimateMovingPartKinematic()
        {
            var lerpFactor = 1f - Mathf.Exp(-openSpeed * Time.deltaTime);
            if (openMode == OpenMode.Rotate)
                movingPart.localRotation = Quaternion.Slerp(movingPart.localRotation, _isOpen ? _openRotation : _closedRotation, lerpFactor);
            else
                movingPart.localPosition = Vector3.Lerp(movingPart.localPosition, _isOpen ? _openPosition : _closedPosition, lerpFactor);
        }

        private void UpdatePromptText() { if (interactionPrompt != null) interactionPrompt.text = BuildPromptText(); }

        private string BuildPromptText()
        {
            var keyName = interactKey.ToString().ToUpperInvariant();
            if (_isLocked)
            {
                if (unlockWithKey && PlayerHasMatchingKey(out var keyDisplayName)) return string.Format(unlockPromptText, keyName, keyDisplayName);
                return RequiresKey ? lockedPromptText : "Locked";
            }
            return string.Format(_isOpen ? closePromptText : openPromptText, keyName);
        }

        private bool PlayerHasMatchingKey(out string keyDisplayName)
        {
            var carriedKey = FindCarriedKey();
            if (carriedKey != null)
            {
                var interactable = carriedKey.GetComponentInChildren<Interactable>(true);
                if (interactable == null) interactable = carriedKey.GetComponentInParent<Interactable>();
                keyDisplayName = interactable != null ? interactable.DisplayName : carriedKey.name;
                return true;
            }
            if (!string.IsNullOrWhiteSpace(requiredKeyId) && KeyItem.PlayerHasKey(requiredKeyId, keyAccess))
            {
                keyDisplayName = KeyItem.DescribeKey(requiredKeyId, keyAccess);
                return true;
            }
            keyDisplayName = null;
            return false;
        }

        private void SetPromptVisible(bool visible) { if (interactionPrompt != null) interactionPrompt.gameObject.SetActive(visible); }

        private void PlaySound(AudioClip clip) { if (clip != null) AudioSource.PlayClipAtPoint(clip, movingPart != null ? movingPart.position : transform.position); }
    }
}
