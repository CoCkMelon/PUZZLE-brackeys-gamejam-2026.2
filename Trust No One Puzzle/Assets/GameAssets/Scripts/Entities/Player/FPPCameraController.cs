using UnityEngine;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities;
using TMPro;

namespace GameAssets.Scripts.Entities.Player
{
    /// <summary>
    /// FPP camera + physics-based carry that never disables colliders.
    /// Uses bounded force/torque so carried item can never be pushed through geometry.
    /// LMB releases without throw (as requested), G also releases, RMB throws.
    /// Supports scroll wheel distance and MMB plane shove (like PlayerCarry spatial).
    /// </summary>
    public class FPPCameraController : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionReference lookAction;

        [Header("Camera settings")] 
        [SerializeField] private float sensitivity = 2f;
        [SerializeField] private Vector2 pitchLimits = new Vector2(-89f, 89f);

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

        [Header("Carry - Basic")]
        [SerializeField] private Key interactKey = Key.E;
        [SerializeField] private Key dropKey = Key.G;
        [SerializeField] private Transform carryLocation; // legacy hold point, kept for compatibility
        [SerializeField] private Transform playerBody; // for yaw lock and planar shove
        [SerializeField] private Camera viewCamera;
        [SerializeField, Range(0.05f, 1f)] private float carriedScaleMultiplier = 0.65f;
        [SerializeField, Min(0f)] private float carryLerpSpeed = 12f;
        [SerializeField, Min(0f)] private float throwForce = 15f;

        [Header("Carry - Force limits (prevents penetration)")]
        [Tooltip("Strongest push (N) the carry can apply. This is also max force item can press against wall, so keep low enough that nothing gets shoved through geometry.")]
        [SerializeField, Min(1f)] private float maxCarryForce = 300f;
        [SerializeField, Min(0.1f)] private float maxCarryTorque = 30f;
        [SerializeField, Min(1f)] private float maxCarryAcceleration = 60f;
        [SerializeField, Min(1f)] private float maxCarryAngularAcceleration = 120f;
        [SerializeField, Min(0f)] private float carryDamping = 1f;
        [SerializeField, Min(0f)] private float maxReleaseSpeed = 3f;
        [SerializeField] private float maxCarrySpeed = 10f;
        [SerializeField] private float maxCarryAngularSpeed = 720f;

        [Header("Carry - Spatial (lost code from SETUP.md)")]
        [Tooltip("Scroll wheel changes hold distance (like PlayerCarry spatial)")]
        [SerializeField] private float scrollMetersPerNotch = 0.35f;
        [SerializeField] private float minHoldDistance = 0.6f;
        [SerializeField] private float maxHoldDistance = 4f;
        [SerializeField] private float defaultHoldDistance = 1.6f;
        [SerializeField] private float planeDragSensitivity = 0.008f;
        [SerializeField] private bool spatialModeRequiresMMB = true;

        // Runtime
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

        // Physics save
        private RigidbodyInterpolation _savedInterpolation;
        private CollisionDetectionMode _savedCollisionDetection;
        private float _savedMaxDepenetrationVelocity = -1f;
        private float _savedMaxAngularVelocity = -1f;
        private int _savedSolverIterations = -1;
        private int _savedSolverVelocityIterations = -1;
        private bool _savedWasKinematic;
        private bool _savedUseGravity;
        private float _savedLinearDamping;
        private float _savedAngularDamping;
        private Rigidbody _tunedBody;

        // Spatial state
        private float _holdDistance;
        private Vector3 _planarOffset;
        private Quaternion _yawOffsetFromPlayer;
        private Vector3 _previousTargetPos;
        private Vector3 _targetVelocity;
        private bool _hasPreviousTarget;
        private bool _spatialModeActive;
        private bool _hudSpatialHeld;

        private Collider[] _playerColliders;
        private readonly System.Collections.Generic.List<Collider> _ignoredCarrierColliders = new System.Collections.Generic.List<Collider>();

        public Ray ReticleRay => _camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        public Interactable CarriedInteractable => _carriedInteractable;
        public bool IsCarrying => _carriedInteractable != null;
        public bool SpatialModeActive => _spatialModeActive;

        public void ReleaseCarriedObject()
        {
            if (_carriedInteractable == null && _droppingInteractable == null) return;
            ReleaseCarryImmediately();
        }

        public void SetSpatialModeFromHud(bool held)
        {
            _hudSpatialHeld = held;
            RefreshSpatialMode();
        }

        private void OnEnable()
        {
            _camera = GetComponentInChildren<Camera>();
            if (viewCamera == null) viewCamera = _camera;
            if (playerBody == null) playerBody = GetComponentInParent<CharacterController>() != null ? GetComponentInParent<CharacterController>().transform : transform;
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
            UpdateTargetHighlight();
            if (IsCarrying)
                UpdateSpatialInput();
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
            if (nameLabelFont == null) return;
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
            if (_carriedInteractable != null)
            {
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
            if (!TryGetTargetedInteractable(out _, out var interactable) || !TryGetScreenBounds(interactable, out var screenBounds))
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
                labelTransform.anchoredPosition = new Vector2(screenBounds.center.x, screenBounds.yMax + nameLabelOffset) - new Vector2(Screen.width, Screen.height) * 0.5f;
                labelTransform.sizeDelta = new Vector2(Mathf.Max(200f, screenBounds.width), 40f);
                _targetNameLabel.text = interactable.DisplayName;
            }
            SetHighlightVisible(true);
        }

        private void HandleInteraction()
        {
            if (Keyboard.current == null) return;
            if (_droppingInteractable != null) return;

            if (_carriedInteractable != null)
            {
                // LMB now RELEASES without throwing (requested)
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    BeginDrop(); // release
                    return;
                }
                // G also releases
                if (Keyboard.current[dropKey].wasPressedThisFrame)
                {
                    BeginDrop();
                    return;
                }
                // RMB throws (optional, keep throw functionality)
                if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
                {
                    ThrowCarriedObject();
                    return;
                }
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

            if (_carriedRigidbody == null)
            {
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

            // Save physics
            _savedInterpolation = _carriedRigidbody.interpolation;
            _savedCollisionDetection = _carriedRigidbody.collisionDetectionMode;
            _savedMaxDepenetrationVelocity = _carriedRigidbody.maxDepenetrationVelocity;
            _savedMaxAngularVelocity = _carriedRigidbody.maxAngularVelocity;
            _savedSolverIterations = _carriedRigidbody.solverIterations;
            _savedSolverVelocityIterations = _carriedRigidbody.solverVelocityIterations;
            _savedWasKinematic = _carriedRigidbody.isKinematic;
            _savedUseGravity = _carriedRigidbody.useGravity;
            _savedLinearDamping = _carriedRigidbody.linearDamping;
            _savedAngularDamping = _carriedRigidbody.angularDamping;
            _tunedBody = _carriedRigidbody;

            // Configure for force-based carry
            _carriedRigidbody.isKinematic = false;
            _carriedRigidbody.useGravity = false;
            _carriedRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _carriedRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _carriedRigidbody.solverIterations = Mathf.Max(_savedSolverIterations, 16);
            _carriedRigidbody.solverVelocityIterations = Mathf.Max(_savedSolverVelocityIterations, 8);
            _carriedRigidbody.maxDepenetrationVelocity = 1.5f;
            _carriedRigidbody.maxAngularVelocity = Mathf.Max(_savedMaxAngularVelocity, maxCarryAngularSpeed * Mathf.Deg2Rad);
            _carriedRigidbody.linearDamping = Mathf.Max(_savedLinearDamping, carryDamping);
            _carriedRigidbody.angularDamping = Mathf.Max(_savedAngularDamping, carryDamping);
            _carriedRigidbody.linearVelocity = Vector3.zero;
            _carriedRigidbody.angularVelocity = Vector3.zero;
            _carriedRigidbody.WakeUp();

            // Init spatial state
            var cam = Cam;
            var from = cam != null ? cam.transform.position : (carryLocation != null ? carryLocation.position : transform.position + transform.forward * defaultHoldDistance);
            var dist = Vector3.Distance(from, interactable.transform.position);
            _holdDistance = Mathf.Clamp(dist, minHoldDistance, maxHoldDistance);
            if (_holdDistance < 0.05f) _holdDistance = defaultHoldDistance;
            _planarOffset = Vector3.zero;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;
            var playerYaw = YawRotation(playerBody != null ? playerBody.rotation : transform.rotation);
            _yawOffsetFromPlayer = Quaternion.Inverse(playerYaw) * interactable.transform.rotation;

            SetCarriedObjectPlayerCollisionIgnored(true);
            ClearHologramHighlight();
            SetHighlightVisible(false);
        }

        // Spatial input: scroll distance, MMB plane shove
        private void UpdateSpatialInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                var notches = scroll > 0f ? 1f : -1f;
                if (Mathf.Abs(scroll) > 120f) notches = scroll / 120f;
                _holdDistance = Mathf.Clamp(_holdDistance + notches * scrollMetersPerNotch, minHoldDistance, maxHoldDistance);
            }

            RefreshSpatialMode();
            if (_spatialModeActive)
            {
                var delta = mouse.delta.ReadValue();
                if (playerBody != null)
                {
                    _planarOffset += playerBody.up * (delta.x * planeDragSensitivity);
                    _planarOffset += playerBody.forward * (delta.y * planeDragSensitivity);
                }
            }
        }

        private void RefreshSpatialMode()
        {
            var mmb = Mouse.current != null && Mouse.current.middleButton.isPressed;
            _spatialModeActive = IsCarrying && (mmb || _hudSpatialHeld || !spatialModeRequiresMMB);
            // If spatialModeRequiresMMB is true, require MMB; else always allow planar offset? We keep logic:
            if (spatialModeRequiresMMB)
                _spatialModeActive = IsCarrying && (mmb || _hudSpatialHeld);
        }

        private void DriveCarriedBody()
        {
            if (_carriedInteractable == null || _carriedRigidbody == null) return;
            var body = _carriedRigidbody;
            if (body.isKinematic) return;

            var dt = Time.fixedDeltaTime;
            var targetPos = ComputeTargetPosition();
            var targetRot = ComputeTargetRotation();
            UpdateTargetVelocity(targetPos, dt);

            var toTarget = targetPos - body.position;
            var gap = toTarget.magnitude;

            ApplyCarryForce(body, toTarget, gap, dt);
            ApplyCarryTorque(body, targetRot, dt);
        }

        private void ApplyCarryForce(Rigidbody body, Vector3 toTarget, float gap, float dt)
        {
            var mass = Mathf.Max(0.01f, body.mass);
            var forceCap = Mathf.Min(maxCarryForce, mass * maxCarryAcceleration);
            var accelCap = forceCap / mass;
            var maxSpeed = Mathf.Max(0.1f, maxCarrySpeed);

            var desiredVelocity = Vector3.ClampMagnitude(_targetVelocity, maxSpeed);
            if (gap > 1e-4f)
            {
                var approachSpeed = Mathf.Min(BrakingSpeed(gap, accelCap, dt), maxSpeed);
                desiredVelocity += toTarget * (approachSpeed / gap);
            }

            var neededForce = (desiredVelocity - body.linearVelocity) * (mass / dt);
            var force = Vector3.ClampMagnitude(neededForce, forceCap);
            body.AddForce(force, ForceMode.Force);
        }

        private void ApplyCarryTorque(Rigidbody body, Quaternion targetRot, float dt)
        {
            var deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f) angleDeg -= 360f;

            var maxAngularSpeed = Mathf.Max(0.1f, maxCarryAngularSpeed) * Mathf.Deg2Rad;
            var desiredAngularVelocity = Vector3.zero;

            if (Mathf.Abs(angleDeg) > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                var angleRad = angleDeg * Mathf.Deg2Rad;
                var speed = Mathf.Min(BrakingSpeed(Mathf.Abs(angleRad), maxCarryAngularAcceleration, dt), maxAngularSpeed);
                desiredAngularVelocity = axis * (Mathf.Sign(angleRad) * speed);
            }

            var angularAccel = (desiredAngularVelocity - body.angularVelocity) / dt;
            angularAccel = Vector3.ClampMagnitude(angularAccel, maxCarryAngularAcceleration);

            var torque = InertiaTensorTimes(body, angularAccel);
            torque = Vector3.ClampMagnitude(torque, maxCarryTorque);
            body.AddTorque(torque, ForceMode.Force);
        }

        private static float BrakingSpeed(float distance, float decel, float dt)
        {
            var step = decel * dt;
            return Mathf.Sqrt(step * step + 2f * decel * distance) - step;
        }

        private static Vector3 InertiaTensorTimes(Rigidbody body, Vector3 angularAccel)
        {
            var tensorRot = body.rotation * body.inertiaTensorRotation;
            var local = Quaternion.Inverse(tensorRot) * angularAccel;
            var tensor = body.inertiaTensor;
            local = new Vector3(local.x * tensor.x, local.y * tensor.y, local.z * tensor.z);
            return tensorRot * local;
        }

        private void UpdateTargetVelocity(Vector3 targetPos, float dt)
        {
            if (_hasPreviousTarget)
            {
                var measured = (targetPos - _previousTargetPos) / dt;
                _targetVelocity = Vector3.Lerp(_targetVelocity, measured, 0.5f);
            }
            else
            {
                _targetVelocity = Vector3.zero;
                _hasPreviousTarget = true;
            }
            _previousTargetPos = targetPos;
        }

        private Vector3 ComputeTargetPosition()
        {
            var cam = Cam;
            var origin = cam != null ? cam.transform.position : (carryLocation != null ? carryLocation.position : transform.position);
            var alongCam = cam != null ? cam.transform.forward : transform.forward;
            return origin + alongCam * _holdDistance + _planarOffset;
        }

        private Quaternion ComputeTargetRotation()
        {
            if (carryLocation != null && !_spatialModeActive)
                return carryLocation.rotation;
            var pb = playerBody != null ? playerBody.rotation : transform.rotation;
            return YawRotation(pb) * _yawOffsetFromPlayer;
        }

        private void BeginDrop()
        {
            if (_carriedInteractable == null) return;
            if (_carriedInteractable.dropSound != null)
                AudioSource.PlayClipAtPoint(_carriedInteractable.dropSound, _carriedInteractable.transform.position);

            _droppingInteractable = _carriedInteractable;
            _dropTargetPosition = GetDropTargetPosition();
            _carriedInteractable = null;
        }

        private void DriveDroppedBody()
        {
            if (_droppingInteractable == null || _carriedRigidbody == null) return;
            var body = _carriedRigidbody;
            var dt = Time.fixedDeltaTime;
            var toTarget = _dropTargetPosition - body.position;
            var dist = toTarget.magnitude;
            if (dist < 0.05f)
            {
                body.position = _dropTargetPosition;
                body.transform.localScale = _carriedOriginalScale;
                RestoreCarriedPhysics(true);
                _droppingInteractable = null;
                _carriedRigidbody = null;
                _carriedColliders = null;
                return;
            }
            // Use same force approach to drop target
            ApplyCarryForce(body, toTarget, dist, dt);
            body.angularVelocity *= 0.95f;
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
                RestoreCarriedPhysics(false);
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
                RestoreCarriedPhysics(false);
            _carriedInteractable = null;
            _droppingInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
        }

        private void RestoreCarriedPhysics(bool clampReleaseSpeed)
        {
            SetCarriedObjectPlayerCollisionIgnored(false);
            if (_carriedRigidbody == null) return;
            var body = _carriedRigidbody;
            body.isKinematic = _savedWasKinematic;
            body.useGravity = _savedUseGravity;
            body.interpolation = _savedInterpolation;
            body.collisionDetectionMode = _savedCollisionDetection;
            if (_savedMaxDepenetrationVelocity >= 0f) body.maxDepenetrationVelocity = _savedMaxDepenetrationVelocity;
            if (_savedMaxAngularVelocity >= 0f) body.maxAngularVelocity = _savedMaxAngularVelocity;
            if (_savedSolverIterations > 0) body.solverIterations = _savedSolverIterations;
            if (_savedSolverVelocityIterations > 0) body.solverVelocityIterations = _savedSolverVelocityIterations;
            body.linearDamping = _savedLinearDamping;
            body.angularDamping = _savedAngularDamping;
            if (clampReleaseSpeed && !body.isKinematic)
            {
                body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, maxReleaseSpeed);
                body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, maxReleaseSpeed);
            }
            _tunedBody = null;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;
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
            for (int i = 0; i < _ignoredCarrierColliders.Count; i++)
            {
                var carrierCol = _ignoredCarrierColliders[i];
                if (carrierCol == null) continue;
                if (_carriedColliders != null)
                    foreach (var itemCol in _carriedColliders)
                        if (itemCol != null)
                            Physics.IgnoreCollision(itemCol, carrierCol, false);
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
                    foreach (var itemCol in _carriedColliders)
                        if (itemCol != null)
                            Physics.IgnoreCollision(itemCol, pc, true);
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
                    if ((_playerColliders == null || _playerColliders.Length == 0) && playerBody != null)
                        _playerColliders = playerBody.GetComponentsInChildren<Collider>();
                }
                return _playerColliders ?? System.Array.Empty<Collider>();
            }
        }

        private Camera Cam => viewCamera != null ? viewCamera : (_camera != null ? _camera : Camera.main);
        private static Quaternion YawRotation(Quaternion rot)
        {
            var e = rot.eulerAngles;
            return Quaternion.Euler(0f, e.y, 0f);
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
