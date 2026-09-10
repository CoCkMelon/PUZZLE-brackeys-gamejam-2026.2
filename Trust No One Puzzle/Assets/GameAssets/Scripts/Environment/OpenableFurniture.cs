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
    /// NOW USES RIGIDBODY FORCE to prevent penetration (was direct transform lerp).
    /// The object holding this component needs a collider that can be hit by the player's reticle ray.
    ///
    /// Optional lock: tick Starts Locked and the furniture refuses to open until it is unlocked.
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

        [Header("Physics - Prevent Penetration")]
        [Tooltip("If true, uses Rigidbody force/torque to move, respecting collisions. If false, falls back to direct lerp (old, can penetrate).")]
        [SerializeField] private bool usePhysics = true;
        [Tooltip("Rigidbody of the moving part. If null, will try GetComponent on movingPart.")]
        [SerializeField] private Rigidbody movingRigidbody;
        [Tooltip("Max force applied to slide (N). Caps penetration.")]
        [SerializeField, Min(1f)] private float maxForce = 400f;
        [Tooltip("Max torque applied to rotate (Nm).")]
        [SerializeField, Min(1f)] private float maxTorque = 150f;
        [Tooltip("Max linear acceleration (m/s2) - further caps force by mass.")]
        [SerializeField, Min(1f)] private float maxAcceleration = 40f;
        [Tooltip("Max angular acceleration (deg/s2).")]
        [SerializeField, Min(10f)] private float maxAngularAcceleration = 360f;
        [Tooltip("Linear damping while moving (helps stop).")]
        [SerializeField, Min(0f)] private float linearDamping = 2f;
        [Tooltip("Angular damping while moving.")]
        [SerializeField, Min(0f)] private float angularDamping = 5f;
        [Tooltip("Consider open/closed reached within this distance (m).")]
        [SerializeField, Min(0.001f)] private float positionThreshold = 0.005f;
        [Tooltip("Consider rotation reached within this angle (deg).")]
        [SerializeField, Min(0.1f)] private float rotationThreshold = 0.5f;
        [Tooltip("Max linear speed for physics move (m/s).")]
        [SerializeField, Min(0.1f)] private float maxLinearSpeed = 2f;
        [Tooltip("Max angular speed (deg/s).")]
        [SerializeField, Min(10f)] private float maxAngularSpeed = 120f;

        [Header("Lock (Optional)")]
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

        // Physics state
        private float _savedLinearDamping;
        private float _savedAngularDamping;
        private bool _wasKinematic;

        public bool IsOpen => _isOpen;
        public bool IsLocked => _isLocked;
        public bool RequiresKey => requiredKey != null || !string.IsNullOrWhiteSpace(requiredKeyId);
        public string PromptText => BuildPromptText();

        private void Awake()
        {
            if (movingPart == null)
                movingPart = transform;

            _closedRotation = movingPart.localRotation;
            _openRotation = _closedRotation * Quaternion.Euler(openRotation);
            _closedPosition = movingPart.localPosition;
            _openPosition = _closedPosition + openOffset;
            _isLocked = startsLocked;

            if (playerCameraController == null)
                playerCameraController = FindFirstObjectByType<FPPCameraController>();

            // Try to get Rigidbody
            if (movingRigidbody == null)
                movingRigidbody = movingPart.GetComponent<Rigidbody>();

            // If no Rigidbody and usePhysics true, create one (non-kinematic, with constraints)
            if (usePhysics && movingRigidbody == null)
            {
                // For physics movement we need a Rigidbody. If not present, we will fallback to kinematic lerp
                // but log warning so designer adds one with proper collider
                Debug.LogWarning($"[OpenableFurniture] {name}: usePhysics enabled but no Rigidbody on movingPart {movingPart.name}. Add Rigidbody (mass 5-20, drag 1, angular drag 5) and BoxCollider. Falling back to lerp which can penetrate.", this);
            }
            else if (movingRigidbody != null)
            {
                _savedLinearDamping = movingRigidbody.linearDamping;
                _savedAngularDamping = movingRigidbody.angularDamping;
                _wasKinematic = movingRigidbody.isKinematic;
                // Ensure Rigidbody can be moved by forces but still respects collision
                movingRigidbody.isKinematic = false;
                movingRigidbody.useGravity = false;
                // Keep original constraints? For door rotate, we want to allow rotation, but prevent unwanted axes?
                // We don't enforce constraints here - designer can set them (e.g. freeze position for rotate door)
            }

            SetPromptVisible(false);
        }

        private void OnDestroy()
        {
            if (movingRigidbody != null)
            {
                movingRigidbody.linearDamping = _savedLinearDamping;
                movingRigidbody.angularDamping = _savedAngularDamping;
                // Don't restore isKinematic to avoid breaking if object is being destroyed
            }
        }

        private void Update()
        {
            // Non-physics animation in Update (old path)
            if (!usePhysics || movingRigidbody == null || movingRigidbody.isKinematic)
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

        private void FixedUpdate()
        {
            if (usePhysics && movingRigidbody != null && !movingRigidbody.isKinematic)
                AnimateMovingPartPhysics();
        }

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        public void Interact()
        {
            if (_isLocked)
            {
                TryUnlockWithKey();
                return;
            }
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
        }

        public void Lock()
        {
            _isLocked = true;
            if (_isOpen) SetOpen(false);
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
            if (requiredKey != null && (candidate == requiredKey || candidate.transform.IsChildOf(requiredKey.transform)))
                return true;
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

        // ─────────────────────────────────────────────
        //  Lock internals
        // ─────────────────────────────────────────────

        private bool TryConsumeMatchingKey()
        {
            var carriedKey = FindCarriedKey();
            if (carriedKey != null)
            {
                UnlockWithReferenceKey(carriedKey);
                return true;
            }
            if (!string.IsNullOrWhiteSpace(requiredKeyId) && KeyItem.TryUseKey(requiredKeyId, keyAccess, consumeKey, out _))
                return true;
            return false;
        }

        private void UnlockWithReferenceKey(GameObject key)
        {
            if (consumeKey) ConsumeKey(key);
        }

        private void UnlockNow()
        {
            _isLocked = false;
            PlaySound(unlockSound);
            OnUnlocked?.Invoke();
            if (openWhenUnlocked && !_isOpen) SetOpen(true);
        }

        private void SetOpen(bool open)
        {
            if (open && _isLocked) return;
            if (_isOpen == open) return;
            _isOpen = open;
            if (open) OnOpened?.Invoke();
            else
            {
                OnClosed?.Invoke();
                if (relockOnClose) _isLocked = true;
            }
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
            if (playerCameraController != null && playerCameraController.CarriedInteractable != null)
                return playerCameraController.CarriedInteractable.gameObject;
            return null;
        }

        private void ConsumeKey(GameObject key)
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null && carry.HeldItem.gameObject == key)
                carry.TakeHeldItem();
            if (playerCameraController != null && playerCameraController.CarriedInteractable != null && playerCameraController.CarriedInteractable.gameObject == key)
                playerCameraController.ReleaseCarriedObject();
            key.SetActive(false);
        }

        // ─────────────────────────────────────────────
        //  Targeting / animation / prompt
        // ─────────────────────────────────────────────

        private bool IsTargetedByReticle()
        {
            if (playerCameraController == null)
                playerCameraController = FindFirstObjectByType<FPPCameraController>();
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
            {
                movingPart.localRotation = Quaternion.Slerp(movingPart.localRotation, _isOpen ? _openRotation : _closedRotation, lerpFactor);
            }
            else
            {
                movingPart.localPosition = Vector3.Lerp(movingPart.localPosition, _isOpen ? _openPosition : _closedPosition, lerpFactor);
            }
        }

        private void AnimateMovingPartPhysics()
        {
            if (movingRigidbody == null) return;

            // Increase damping while moving to help stop
            movingRigidbody.linearDamping = Mathf.Max(_savedLinearDamping, linearDamping);
            movingRigidbody.angularDamping = Mathf.Max(_savedAngularDamping, angularDamping);

            if (openMode == OpenMode.Slide)
            {
                // Target world position
                Vector3 targetLocal = _isOpen ? _openPosition : _closedPosition;
                Vector3 targetWorld;
                if (movingPart.parent != null)
                    targetWorld = movingPart.parent.TransformPoint(targetLocal);
                else
                    targetWorld = targetLocal;

                Vector3 toTarget = targetWorld - movingRigidbody.position;
                float dist = toTarget.magnitude;

                if (dist < positionThreshold)
                {
                    // Close enough - stop
                    movingRigidbody.linearVelocity = Vector3.Lerp(movingRigidbody.linearVelocity, Vector3.zero, 0.5f);
                    return;
                }

                // Braking speed to avoid overshoot (similar to PlayerCarry)
                float dt = Time.fixedDeltaTime;
                float accelCap = maxAcceleration;
                float maxSpeed = maxLinearSpeed;
                float approachSpeed = Mathf.Min(BrakingSpeed(dist, accelCap, dt), maxSpeed);
                Vector3 desiredVel = toTarget.normalized * approachSpeed;
                Vector3 velError = desiredVel - movingRigidbody.linearVelocity;

                // Force = mass * acceleration, capped
                float mass = Mathf.Max(0.1f, movingRigidbody.mass);
                Vector3 force = velError * (mass / dt);
                float forceCap = Mathf.Min(maxForce, mass * maxAcceleration);
                force = Vector3.ClampMagnitude(force, forceCap);

                movingRigidbody.AddForce(force, ForceMode.Force);
            }
            else // Rotate
            {
                Quaternion targetLocal = _isOpen ? _openRotation : _closedRotation;
                Quaternion targetWorld;
                if (movingPart.parent != null)
                    targetWorld = movingPart.parent.rotation * targetLocal;
                else
                    targetWorld = targetLocal;

                Quaternion currentWorld = movingRigidbody.rotation;
                Quaternion delta = targetWorld * Quaternion.Inverse(currentWorld);
                delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (angleDeg > 180f) angleDeg -= 360f;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                axis.Normalize();

                if (Mathf.Abs(angleDeg) < rotationThreshold)
                {
                    movingRigidbody.angularVelocity = Vector3.Lerp(movingRigidbody.angularVelocity, Vector3.zero, 0.5f);
                    return;
                }

                float dt = Time.fixedDeltaTime;
                float maxAngSpeedRad = maxAngularSpeed * Mathf.Deg2Rad;
                float approachAngSpeed = Mathf.Min(BrakingSpeed(Mathf.Abs(angleRad), maxAngularAcceleration * Mathf.Deg2Rad, dt), maxAngSpeedRad);
                Vector3 desiredAngVel = axis * (approachAngSpeed * Mathf.Sign(angleRad));
                Vector3 angVelError = desiredAngVel - movingRigidbody.angularVelocity;

                float mass = Mathf.Max(0.1f, movingRigidbody.mass);
                // Approximate inertia - use mass * some factor, but we have inertiaTensor
                Vector3 torque = Vector3.Scale(movingRigidbody.inertiaTensor, angVelError) / dt;
                // Clamp torque
                torque = Vector3.ClampMagnitude(torque, maxTorque);

                movingRigidbody.AddTorque(torque, ForceMode.Force);
            }
        }

        private static float BrakingSpeed(float distance, float decel, float dt)
        {
            // v = sqrt(2*a*d) - with small dt compensation to avoid jitter
            if (distance <= 0f || decel <= 0f) return 0f;
            float v = Mathf.Sqrt(2f * decel * distance);
            // Don't go faster than can be stopped in next frame
            return Mathf.Max(0f, v - decel * dt * 0.5f);
        }

        private void UpdatePromptText()
        {
            if (interactionPrompt != null) interactionPrompt.text = BuildPromptText();
        }

        private string BuildPromptText()
        {
            var keyName = interactKey.ToString().ToUpperInvariant();
            if (_isLocked)
            {
                if (unlockWithKey && PlayerHasMatchingKey(out var keyDisplayName))
                    return string.Format(unlockPromptText, keyName, keyDisplayName);
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

        private void SetPromptVisible(bool visible)
        {
            if (interactionPrompt != null) interactionPrompt.gameObject.SetActive(visible);
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null) AudioSource.PlayClipAtPoint(clip, movingPart != null ? movingPart.position : transform.position);
        }
    }
}
