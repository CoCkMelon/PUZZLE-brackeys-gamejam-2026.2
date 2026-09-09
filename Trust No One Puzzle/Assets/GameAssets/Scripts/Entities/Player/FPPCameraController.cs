using UnityEngine;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities;
using TMPro;

namespace GameAssets.Scripts.Entities.Player
{
    public class FPPCameraController : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionReference lookAction;

        [Header("Camera settings")] 
        [SerializeField] private float sensitivity;
        [SerializeField] private Vector2 pitchLimits;

        [Header("Reticle")]
        [SerializeField] private bool showReticle = true;
        [SerializeField, Min(1f)] private float reticleSize = 14f;
        [SerializeField, Min(1f)] private float reticleThickness = 2f;
        [SerializeField] private Color reticleColor = Color.white;

        [Header("Target Highlight")]
        [SerializeField] private Material targetHighlightMaterial;
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionLayers = ~0;
        [Tooltip("All layers that can block the reticle ray, such as walls, doors, and furniture.")]
        [SerializeField] private LayerMask obstructionLayers = ~0;

        [Header("Target Name Label")]
        [SerializeField] private TMP_FontAsset nameLabelFont;
        [SerializeField, Min(0f)] private float nameLabelOffset = 24f;

        [Header("Carry")]
        [SerializeField] private Key interactKey = Key.E;
        [SerializeField] private Key dropKey = Key.G;
        [SerializeField] private Transform carryLocation;
        [SerializeField, Range(0.05f, 1f)] private float carriedScaleMultiplier = 0.65f;
        [SerializeField, Min(0f)] private float carryLerpSpeed = 12f;
        [SerializeField, Min(0f)] private float throwForce = 15f;

        [Header("Carry Physics - Penetration Guard")]
        [Tooltip("Time for the item to catch up with hold pose. Higher = softer, easier for solver to keep out of walls.")]
        [SerializeField, Min(0.01f)] private float followTime = 0.06f;
        [SerializeField, Min(0.01f)] private float rotationFollowTime = 0.05f;
        [SerializeField] private bool sweepAgainstGeometry = true;
        [SerializeField, Min(0.001f)] private float contactSkin = 0.02f;
        [SerializeField] private bool resolveOverlaps = true;
        [SerializeField, Min(0.1f)] private float depenetrationSpeed = 1.5f;
        [SerializeField, Min(1)] private int carrySolverIterations = 16;
        [SerializeField, Min(1)] private int carrySolverVelocityIterations = 8;
        [SerializeField, Min(0.1f)] private float carryMaxDepenetration = 1.5f;
        [SerializeField] private float maxCarrySpeed = 10f;
        [SerializeField] private float maxCarryAngularSpeed = 720f;

        private Vector2 _input;
        private float _pitch, _yaw;
        private Camera _camera;
        private TextMeshProUGUI _targetNameLabel;
        private GameObject _highlightCanvas;
        private Interactable _highlightedInteractable;
        private Renderer[] _highlightedRenderers;
        private Material[][] _originalMaterials;
        private Interactable _carriedInteractable;
        private Interactable _droppingInteractable;
        private Rigidbody _carriedRigidbody;
        private CharacterController _playerCharacterController;
        private Collider[] _carriedColliders;
        private Vector3 _carriedOriginalScale;
        private Vector3 _dropTargetPosition;

        // Saved physics state for restoration
        private RigidbodyInterpolation _savedInterpolation;
        private CollisionDetectionMode _savedCollisionDetection;
        private float _savedMaxDepenetrationVelocity = -1f;
        private float _savedMaxAngularVelocity = -1f;
        private int _savedSolverIterations = -1;
        private int _savedSolverVelocityIterations = -1;
        private bool _savedWasKinematic;
        private bool _savedUseGravity;

        private float _smallestThickness = -1f;
        private Collider[] _playerColliders;
        private readonly Collider[] _overlapBuffer = new Collider[32];
        private readonly System.Collections.Generic.List<Collider> _ignoredCarrierColliders = new System.Collections.Generic.List<Collider>();

        public Ray ReticleRay => _camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        public Interactable CarriedInteractable => _carriedInteractable;

        public void ReleaseCarriedObject()
        {
            if (_carriedInteractable == null && _droppingInteractable == null) return;
            ReleaseCarryImmediately();
        }
    
        private void OnEnable()
        {
            _camera = GetComponentInChildren<Camera>();
            _playerCharacterController = GetComponentInParent<CharacterController>();
            CreateTargetHighlight();
            if (lookAction != null)
            {
                lookAction.action.Enable();
                lookAction.action.performed += ActionOnLook;
                lookAction.action.canceled += ActionOnLook;
            }
            LockCursor();
        }

        private void OnDisable()
        {
            if (lookAction != null)
            {
                lookAction.action.performed -= ActionOnLook;
                lookAction.action.canceled -= ActionOnLook;
                lookAction.action.Disable();
            }
            ReleaseCarryImmediately();
            if (_highlightCanvas != null)
            {
                Destroy(_highlightCanvas);
                _highlightCanvas = null;
                _targetNameLabel = null;
            }
            ClearHologramHighlight();
        }

        private void LockCursor(bool value = true)
        {
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !value;
        }

        private void Update()
        {
            RotationHandler();
            HandleInteraction();
            UpdateTargetHighlight(); // highlight + scale lerp + dropping lerp (non-physics part)
        }

        private void FixedUpdate()
        {
            if (_carriedInteractable != null)
                DriveCarriedBody();
            else if (_droppingInteractable != null)
                DriveDroppedBody();
        }

        private void RotationHandler()
        {
            _yaw += _input.x * sensitivity;
            _pitch -= _input.y * sensitivity;
            _pitch = Mathf.Clamp(_pitch, pitchLimits.x, pitchLimits.y);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
        }

        private void OnGUI()
        {
            if (!showReticle || _camera == null || !_camera.enabled) return;
            var previousColor = GUI.color;
            GUI.color = reticleColor;
            var centreX = (Screen.width - reticleThickness) * 0.5f;
            var centreY = (Screen.height - reticleThickness) * 0.5f;
            var halfSize = reticleSize * 0.5f;
            GUI.DrawTexture(new Rect(centreX - halfSize, centreY, reticleSize, reticleThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centreX, centreY - halfSize, reticleThickness, reticleSize), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private void CreateTargetHighlight()
        {
            _highlightCanvas = new GameObject("Target Highlight Canvas", typeof(Canvas));
            var canvas = _highlightCanvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;
            if (nameLabelFont == null)
            {
                Debug.LogWarning("Assign a TextMesh Pro font to Name Label Font on FPPCameraController.", this);
                return;
            }
            var labelObject = new GameObject("Target Name", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(_highlightCanvas.transform, false);
            _targetNameLabel = labelObject.GetComponent<TextMeshProUGUI>();
            _targetNameLabel.font = nameLabelFont;
            _targetNameLabel.fontSize = 24f;
            _targetNameLabel.alignment = TextAlignmentOptions.Center;
            _targetNameLabel.textWrappingMode = TextWrappingModes.NoWrap;
            _targetNameLabel.raycastTarget = false;
            _targetNameLabel.enabled = false;
        }

        private void UpdateTargetHighlight()
        {
            // Scale lerp is visual, keep in Update
            if (_carriedInteractable != null)
            {
                // Scale towards carried scale
                var t = _carriedInteractable.transform;
                var lerpFactor = 1f - Mathf.Exp(-carryLerpSpeed * Time.deltaTime);
                t.localScale = Vector3.Lerp(t.localScale, _carriedOriginalScale * carriedScaleMultiplier, lerpFactor);
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            if (_droppingInteractable != null)
            {
                var t = _droppingInteractable.transform;
                var lerpFactor = 1f - Mathf.Exp(-carryLerpSpeed * Time.deltaTime);
                t.localScale = Vector3.Lerp(t.localScale, _carriedOriginalScale, lerpFactor);
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            if (!TryGetTargetedInteractable(out _, out var interactable) ||
                !TryGetScreenBounds(interactable, out var screenBounds))
            {
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            ApplyHologramHighlight(interactable);
            if (_targetNameLabel != null)
            {
                var labelTransform = _targetNameLabel.rectTransform;
                labelTransform.anchorMin = labelTransform.anchorMax = new Vector2(0.5f, 0.5f);
                labelTransform.anchoredPosition = new Vector2(screenBounds.center.x, screenBounds.yMax + nameLabelOffset) -
                                                new Vector2(Screen.width, Screen.height) * 0.5f;
                labelTransform.sizeDelta = new Vector2(Mathf.Max(200f, screenBounds.width), 40f);
                _targetNameLabel.text = interactable.DisplayName;
            }
            SetHighlightVisible(true);
        }

        private void HandleInteraction()
        {
            if (Keyboard.current == null || _droppingInteractable != null) return;

            if (_carriedInteractable != null)
            {
                if (Keyboard.current[dropKey].wasPressedThisFrame)
                    BeginDrop();
                else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                    ThrowCarriedObject();
                return;
            }

            if (!Keyboard.current[interactKey].wasPressedThisFrame) return;

            if (TryGetTargetedInteractable(out _, out var interactable))
                PickUpObject(interactable);
        }

        private bool TryGetTargetedInteractable(out RaycastHit hit, out Interactable interactable)
        {
            interactable = null;
            if (!TryGetFirstNonPlayerRaycast(out hit)) return false;
            interactable = hit.collider.GetComponentInParent<Interactable>();
            return interactable != null && (interactionLayers.value & (1 << hit.collider.gameObject.layer)) != 0;
        }

        private void PickUpObject(Interactable interactable)
        {
            if (interactable.pickupSound != null)
                AudioSource.PlayClipAtPoint(interactable.pickupSound, interactable.transform.position);

            _carriedInteractable = interactable;
            _carriedRigidbody = interactable.GetComponentInChildren<Rigidbody>();
            _carriedOriginalScale = interactable.transform.localScale;
            _carriedColliders = interactable.GetComponentsInChildren<Collider>(true);
            _smallestThickness = -1f; // recompute

            // Ensure we have a rigidbody for physics carry
            if (_carriedRigidbody == null)
            {
                // Create one like PlaceableItem does - make mesh colliders convex
                foreach (var c in _carriedColliders)
                {
                    if (c is MeshCollider mesh && !mesh.convex)
                    {
                        mesh.convex = true;
                        Debug.LogWarning($"[FPPCameraController] {interactable.name}: non-convex MeshCollider made convex for physics carry.", interactable);
                    }
                }
                _carriedRigidbody = interactable.gameObject.AddComponent<Rigidbody>();
            }

            // Save original physics settings
            _savedInterpolation = _carriedRigidbody.interpolation;
            _savedCollisionDetection = _carriedRigidbody.collisionDetectionMode;
            _savedMaxDepenetrationVelocity = _carriedRigidbody.maxDepenetrationVelocity;
            _savedMaxAngularVelocity = _carriedRigidbody.maxAngularVelocity;
            _savedSolverIterations = _carriedRigidbody.solverIterations;
            _savedSolverVelocityIterations = _carriedRigidbody.solverVelocityIterations;
            _savedWasKinematic = _carriedRigidbody.isKinematic;
            _savedUseGravity = _carriedRigidbody.useGravity;

            // Configure for physics carry - NEVER kinematic, NEVER disable colliders
            _carriedRigidbody.isKinematic = false;
            _carriedRigidbody.useGravity = false;
            _carriedRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _carriedRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _carriedRigidbody.solverIterations = Mathf.Max(_savedSolverIterations, carrySolverIterations);
            _carriedRigidbody.solverVelocityIterations = Mathf.Max(_savedSolverVelocityIterations, carrySolverVelocityIterations);
            _carriedRigidbody.maxDepenetrationVelocity = carryMaxDepenetration;
            _carriedRigidbody.maxAngularVelocity = Mathf.Max(_savedMaxAngularVelocity, maxCarryAngularSpeed * Mathf.Deg2Rad);
            _carriedRigidbody.linearVelocity = Vector3.zero;
            _carriedRigidbody.angularVelocity = Vector3.zero;
            _carriedRigidbody.WakeUp();

            // Ignore collisions with player so carried item doesn't fight the capsule
            SetCarriedObjectPlayerCollisionIgnored(true);

            ClearHologramHighlight();
            SetHighlightVisible(false);
        }

        // Physics-based drive for carried object
        private void DriveCarriedBody()
        {
            if (_carriedInteractable == null || _carriedRigidbody == null) return;

            var body = _carriedRigidbody;
            if (body.isKinematic) return;

            var dt = Time.fixedDeltaTime;
            var targetPos = carryLocation != null ? carryLocation.position : ReticleRay.GetPoint(interactionDistance);
            var targetRot = carryLocation != null ? carryLocation.rotation : _camera.transform.rotation;

            // Damped follow
            var toTarget = targetPos - body.position;
            var velocity = toTarget / Mathf.Max(followTime, dt);
            velocity = Vector3.ClampMagnitude(velocity, MaxSafeSpeed(dt));

            // Resolve overlaps
            var separation = resolveOverlaps ? ComputeSeparationVelocity(dt) : Vector3.zero;
            var overlapping = separation.sqrMagnitude > 1e-8f;
            if (overlapping) velocity += separation;

            // Sweep against geometry
            if (sweepAgainstGeometry)
                velocity = LimitBySweep(body, velocity, dt);

            body.linearVelocity = velocity;

            // Angular
            var deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (Mathf.Abs(angleDeg) > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                var stepAngular = axis * (angleDeg * Mathf.Deg2Rad / Mathf.Max(rotationFollowTime, dt));
                var maxAngular = Mathf.Max(1f, maxCarryAngularSpeed) * Mathf.Deg2Rad;
                if (overlapping) maxAngular *= 0.25f;
                body.angularVelocity = Vector3.ClampMagnitude(stepAngular, maxAngular);
            }
            else
            {
                body.angularVelocity = Vector3.zero;
            }
        }

        private void BeginDrop()
        {
            if (_carriedInteractable == null) return;
            if (_carriedInteractable.dropSound != null)
                AudioSource.PlayClipAtPoint(_carriedInteractable.dropSound, _carriedInteractable.transform.position);

            _droppingInteractable = _carriedInteractable;
            _dropTargetPosition = GetDropTargetPosition();
            _carriedInteractable = null;
            // Keep physics settings, keep ignoring player until drop finishes, but colliders stay enabled
        }

        private void DriveDroppedBody()
        {
            if (_droppingInteractable == null || _carriedRigidbody == null) return;
            var body = _carriedRigidbody;
            var dt = Time.fixedDeltaTime;
            var toTarget = _dropTargetPosition - body.position;
            var dist = toTarget.magnitude;

            // If close enough, finish drop
            if (dist < 0.02f)
            {
                // Snap and restore
                body.position = _dropTargetPosition;
                body.transform.localScale = _carriedOriginalScale;
                RestoreCarriedPhysics();
                _droppingInteractable = null;
                _carriedRigidbody = null;
                _carriedColliders = null;
                return;
            }

            var velocity = toTarget / Mathf.Max(followTime, dt);
            velocity = Vector3.ClampMagnitude(velocity, MaxSafeSpeed(dt));
            if (sweepAgainstGeometry)
                velocity = LimitBySweep(body, velocity, dt);
            body.linearVelocity = velocity;
            body.angularVelocity = Vector3.zero;

            // Scale lerp handled in Update
        }

        private void ThrowCarriedObject()
        {
            if (_carriedInteractable == null) return;
            if (_carriedInteractable.throwSound != null)
                AudioSource.PlayClipAtPoint(_carriedInteractable.throwSound, _camera.transform.position);

            var thrownTransform = _carriedInteractable.transform;
            thrownTransform.localScale = _carriedOriginalScale;

            if (_carriedRigidbody != null)
            {
                RestoreCarriedPhysics(saveVelocity: false);
                _carriedRigidbody.AddForce(_camera.transform.forward * throwForce, ForceMode.Impulse);
            }
            else
            {
                SetCarriedObjectPlayerCollisionIgnored(false);
            }

            _carriedInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
        }

        private Vector3 GetDropTargetPosition()
        {
            var hits = Physics.RaycastAll(ReticleRay, interactionDistance, obstructionLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            var target = ReticleRay.GetPoint(interactionDistance);
            foreach (var hit in hits)
            {
                if (!IsPlayerCollider(hit.collider) && !IsCarriedCollider(hit.collider) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    target = hit.point;
                }
            }
            return target;
        }

        private bool TryGetFirstNonPlayerRaycast(out RaycastHit closestHit)
        {
            var hits = Physics.RaycastAll(ReticleRay, interactionDistance, obstructionLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            closestHit = default;
            foreach (var hit in hits)
            {
                if (!IsPlayerCollider(hit.collider) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    closestHit = hit;
                }
            }
            return nearest < float.MaxValue;
        }

        private void ReleaseCarryImmediately()
        {
            var heldTransform = _carriedInteractable != null ? _carriedInteractable.transform : _droppingInteractable != null ? _droppingInteractable.transform : null;
            if (heldTransform != null) heldTransform.localScale = _carriedOriginalScale;

            if (_carriedRigidbody != null)
                RestoreCarriedPhysics(saveVelocity: false);

            _carriedInteractable = null;
            _droppingInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
        }

        private void RestoreCarriedPhysics(bool saveVelocity = true)
        {
            SetCarriedObjectPlayerCollisionIgnored(false);
            if (_carriedRigidbody == null) return;

            if (saveVelocity)
            {
                // keep current velocity
            }

            _carriedRigidbody.isKinematic = _savedWasKinematic;
            _carriedRigidbody.useGravity = _savedUseGravity;
            _carriedRigidbody.interpolation = _savedInterpolation;
            _carriedRigidbody.collisionDetectionMode = _savedCollisionDetection;
            if (_savedMaxDepenetrationVelocity >= 0f) _carriedRigidbody.maxDepenetrationVelocity = _savedMaxDepenetrationVelocity;
            if (_savedMaxAngularVelocity >= 0f) _carriedRigidbody.maxAngularVelocity = _savedMaxAngularVelocity;
            if (_savedSolverIterations > 0) _carriedRigidbody.solverIterations = _savedSolverIterations;
            if (_savedSolverVelocityIterations > 0) _carriedRigidbody.solverVelocityIterations = _savedSolverVelocityIterations;
        }

        // --- Collision helpers ---

        private float MaxSafeSpeed(float dt)
        {
            var speed = Mathf.Max(0.1f, maxCarrySpeed);
            if (sweepAgainstGeometry) return speed;
            var thickness = SmallestThickness;
            if (thickness > 0f) speed = Mathf.Min(speed, thickness / Mathf.Max(dt, 1e-4f));
            return speed;
        }

        private float SmallestThickness
        {
            get
            {
                if (_smallestThickness > 0f) return _smallestThickness;
                _smallestThickness = ComputeSmallestThickness();
                return _smallestThickness;
            }
        }

        private float ComputeSmallestThickness()
        {
            if (_carriedColliders == null || _carriedColliders.Length == 0) return 0.1f;
            var found = false;
            var bounds = new Bounds();
            foreach (var c in _carriedColliders)
            {
                if (c == null) continue;
                if (!found) { bounds = c.bounds; found = true; }
                else bounds.Encapsulate(c.bounds);
            }
            if (!found) return 0.1f;
            var size = bounds.size;
            return Mathf.Max(0.02f, Mathf.Min(size.x, Mathf.Min(size.y, size.z)));
        }

        private Vector3 LimitBySweep(Rigidbody body, Vector3 velocity, float dt)
        {
            var speed = velocity.magnitude;
            if (speed < 1e-4f) return velocity;
            var dir = velocity / speed;
            var distance = speed * dt + contactSkin;
            var hits = body.SweepTestAll(dir, distance, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            var normal = Vector3.zero;
            foreach (var hit in hits)
            {
                var other = hit.collider;
                if (other == null || IsSelfOrCarrier(other)) continue;
                if ((obstructionLayers.value & (1 << other.gameObject.layer)) == 0) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                normal = hit.normal;
            }
            if (nearest >= float.MaxValue || normal.sqrMagnitude < 1e-6f) return velocity;

            var allowed = Mathf.Max(0f, nearest - contactSkin);
            var maxApproach = allowed / Mathf.Max(dt, 1e-4f);
            var approach = Vector3.Dot(velocity, -normal);
            if (approach > maxApproach)
                velocity += normal * (approach - maxApproach);
            return velocity;
        }

        private Vector3 ComputeSeparationVelocity(float dt)
        {
            if (_carriedColliders == null) return Vector3.zero;
            var separation = Vector3.zero;
            foreach (var itemCol in _carriedColliders)
            {
                if (itemCol == null || !itemCol.enabled || !itemCol.gameObject.activeInHierarchy) continue;
                var bounds = itemCol.bounds;
                var count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * contactSkin, _overlapBuffer, Quaternion.identity, obstructionLayers, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var other = _overlapBuffer[i];
                    if (other == null || IsSelfOrCarrier(other)) continue;
                    var otherBody = other.attachedRigidbody;
                    if (otherBody != null && !otherBody.isKinematic) continue;
                    if (Physics.ComputePenetration(itemCol, itemCol.transform.position, itemCol.transform.rotation, other, other.transform.position, other.transform.rotation, out var dir, out var dist))
                        separation += dir * dist;
                }
            }
            if (separation.sqrMagnitude < 1e-8f) return Vector3.zero;
            var speed = Mathf.Min(separation.magnitude / Mathf.Max(dt, 1e-4f), depenetrationSpeed);
            return separation.normalized * speed;
        }

        private bool IsSelfOrCarrier(Collider candidate)
        {
            if (candidate == null) return false;
            if (_carriedColliders != null)
            {
                foreach (var c in _carriedColliders)
                    if (c == candidate) return true;
                if (_carriedRigidbody != null && candidate.attachedRigidbody == _carriedRigidbody) return true;
                // child of carried
                if (_carriedInteractable != null && candidate.transform.IsChildOf(_carriedInteractable.transform)) return true;
                if (_droppingInteractable != null && candidate.transform.IsChildOf(_droppingInteractable.transform)) return true;
            }
            foreach (var pc in PlayerColliders)
                if (pc == candidate) return true;
            return false;
        }

        private bool IsCarriedCollider(Collider collider)
        {
            if (collider == null) return false;
            if (_carriedRigidbody != null && collider.attachedRigidbody == _carriedRigidbody) return true;
            if (_carriedColliders != null)
                foreach (var c in _carriedColliders)
                    if (collider == c) return true;
            return collider.GetComponentInParent<Interactable>() == _droppingInteractable;
        }

        private bool IsPlayerCollider(Collider collider)
        {
            if (collider == null || _playerCharacterController == null) return false;
            var playerTransform = _playerCharacterController.transform;
            return collider == _playerCharacterController ||
                   collider.transform.IsChildOf(playerTransform) ||
                   playerTransform.IsChildOf(collider.transform);
        }

        private void SetCarriedObjectPlayerCollisionIgnored(bool ignored)
        {
            // Restore first
            for (int i = 0; i < _ignoredCarrierColliders.Count; i++)
            {
                var carrierCol = _ignoredCarrierColliders[i];
                if (carrierCol == null) continue;
                if (_carriedColliders != null)
                {
                    foreach (var itemCol in _carriedColliders)
                        if (itemCol != null)
                            Physics.IgnoreCollision(itemCol, carrierCol, false);
                }
            }
            _ignoredCarrierColliders.Clear();

            if (!ignored) return;

            var pcs = PlayerColliders;
            if (pcs == null) return;
            foreach (var pc in pcs)
            {
                if (pc == null) continue;
                _ignoredCarrierColliders.Add(pc);
                if (_carriedColliders != null)
                {
                    foreach (var itemCol in _carriedColliders)
                        if (itemCol != null)
                            Physics.IgnoreCollision(itemCol, pc, true);
                }
            }
        }

        private Collider[] PlayerColliders
        {
            get
            {
                if (_playerColliders == null || _playerColliders.Length == 0)
                {
                    if (_playerCharacterController != null)
                        _playerColliders = _playerCharacterController.GetComponentsInChildren<Collider>();
                    if ((_playerColliders == null || _playerColliders.Length == 0) && _playerCharacterController != null)
                        _playerColliders = _playerCharacterController.transform.root.GetComponentsInChildren<Collider>();
                }
                return _playerColliders ?? System.Array.Empty<Collider>();
            }
        }

        private bool TryGetScreenBounds(Interactable interactable, out Rect screenBounds)
        {
            var renderers = interactable.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) { screenBounds = default; return false; }
            var worldBounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) worldBounds.Encapsulate(renderers[i].bounds);
            var centre = worldBounds.center;
            var extents = worldBounds.extents;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var point = centre + Vector3.Scale(extents, new Vector3(x, y, z));
                var screenPoint = _camera.WorldToScreenPoint(point);
                if (screenPoint.z <= 0f) { screenBounds = default; return false; }
                min = Vector2.Min(min, screenPoint);
                max = Vector2.Max(max, screenPoint);
            }
            screenBounds = new Rect(min, max - min);
            return true;
        }

        private void ApplyHologramHighlight(Interactable interactable)
        {
            if (targetHighlightMaterial == null) { ClearHologramHighlight(); return; }
            if (_highlightedInteractable == interactable) return;
            ClearHologramHighlight();
            _highlightedInteractable = interactable;
            _highlightedRenderers = interactable.GetComponentsInChildren<Renderer>(true);
            _originalMaterials = new Material[_highlightedRenderers.Length][];
            for (var i = 0; i < _highlightedRenderers.Length; i++)
            {
                var renderer = _highlightedRenderers[i];
                _originalMaterials[i] = renderer.sharedMaterials;
                var hologramMaterials = new Material[_originalMaterials[i].Length];
                for (var mi = 0; mi < hologramMaterials.Length; mi++) hologramMaterials[mi] = targetHighlightMaterial;
                renderer.sharedMaterials = hologramMaterials;
            }
        }

        private void ClearHologramHighlight()
        {
            if (_highlightedRenderers == null || _originalMaterials == null) return;
            for (var i = 0; i < _highlightedRenderers.Length; i++)
                if (_highlightedRenderers[i] != null)
                    _highlightedRenderers[i].sharedMaterials = _originalMaterials[i];
            _highlightedInteractable = null;
            _highlightedRenderers = null;
            _originalMaterials = null;
        }

        private void SetHighlightVisible(bool visible)
        {
            if (_targetNameLabel != null) _targetNameLabel.enabled = visible;
        }

        private void ActionOnLook(InputAction.CallbackContext ctx) => _input = ctx.ReadValue<Vector2>();
    }
}
