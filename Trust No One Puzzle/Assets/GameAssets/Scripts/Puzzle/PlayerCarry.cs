using UnityEngine;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Best carry - force/torque limited, supports both PlaceableItem and Interactable.
    /// Configurable controls, scroll distance, plane shove with flipped X/Y (X->forward, Y->up) and no camera rotation during shove.
    /// LMB releases without throw.
    /// </summary>
    public class PlayerCarry : MonoBehaviour
    {
        public static PlayerCarry Instance { get; private set; }

        [Header("Hold")]
        [SerializeField] private Transform holdPoint;
        [SerializeField] private Transform playerBody;
        [SerializeField] private Camera viewCamera;
        [SerializeField] private InputActionReference dropAction;
        [SerializeField] private InputActionReference spatialModeAction;
        [SerializeField] private InputActionReference scrollAction; // optional, if null uses Mouse scroll

        [Header("Carry force limits - prevents penetration")]
        [Tooltip("Strongest push (N) the carry can apply. This is also max force item can press against wall.")]
        [SerializeField, Min(1f)] private float maxCarryForce = 300f;
        [SerializeField, Min(0.1f)] private float maxCarryTorque = 30f;
        [SerializeField, Min(1f)] private float maxCarryAcceleration = 60f;
        [SerializeField, Min(1f)] private float maxCarryAngularAcceleration = 120f;
        [SerializeField, Min(0f)] private float carryDamping = 1f;
        [SerializeField, Min(0f)] private float maxReleaseSpeed = 3f;
        [SerializeField] private float maxCarrySpeed = 10f;
        [SerializeField] private float maxCarryAngularSpeed = 720f;

        [Header("Carry - Interactable (old) support")]
        [SerializeField, Range(0.05f, 1f)] private float interactableScaleMultiplier = 1f; // fixed - was 0.65 causing shrink bug
        [SerializeField] private float interactableFollowTimeScale = 1f; // extra lerp for scale

        [Header("Auto-drop")]
        [SerializeField] private float stuckDropGap = 0.5f;
        [SerializeField] private float stuckDropDelay = 0.7f;
        [SerializeField] private float carryBreakDistance = 4f;

        [Header("Spatial move - configurable")]
        [SerializeField] private float scrollMetersPerNotch = 0.35f;
        [SerializeField] private float planeDragSensitivity = 0.008f;
        [SerializeField] private float minHoldDistance = 0.6f;
        [SerializeField] private float maxHoldDistance = 4f;
        [SerializeField] private float defaultHoldDistance = 1.6f;
        [Tooltip("Flip X/Y: true = X->forward, Y->up (intuitive), false = old X->up, Y->forward")]
        [SerializeField] private bool flipPlanarAxes = true;
        [SerializeField] private bool spatialModeRequiresMMB = true;

        // Public state
        public PlaceableItem HeldItem { get; private set; }
        public Interactable HeldInteractable { get; private set; }
        public bool IsCarrying => HeldItem != null || HeldInteractable != null;
        public bool SpatialModeActive { get; private set; }

        // Common held data
        private Rigidbody _heldBody;
        private Collider[] _heldColliders;
        private Vector3 _heldOriginalScale;
        private float _holdDistance;
        private Vector3 _planarOffset;
        private Quaternion _yawOffsetFromPlayer;
        private bool _hudSpatialHeld;

        private float _stuckTimer;
        private float _bestGap;
        private Collider[] _playerColliders;

        private Vector3 _previousTargetPos;
        private Vector3 _targetVelocity;
        private bool _hasPreviousTarget;

        private float _savedLinearDamping;
        private float _savedAngularDamping;
        private RigidbodyInterpolation _savedInterpolation;
        private CollisionDetectionMode _savedCollisionDetection;
        private float _savedMaxDepenetrationVelocity = -1f;
        private float _savedMaxAngularVelocity = -1f;
        private int _savedSolverIterations = -1;
        private int _savedSolverVelocityIterations = -1;
        private bool _savedWasKinematic;
        private bool _savedUseGravity;
        private Rigidbody _tunedBody;

        private void Awake()
        {
            Instance = this;
            if (playerBody == null) playerBody = transform;
            if (viewCamera == null) viewCamera = Camera.main;
            if (holdPoint == null)
            {
                var go = new GameObject("HoldPoint");
                go.transform.SetParent(transform);
                go.transform.localPosition = new Vector3(0.4f, 1.2f, 0.6f);
                holdPoint = go.transform;
            }
        }

        private void OnEnable()
        {
            Bind(dropAction, OnDrop, true);
            Bind(spatialModeAction, OnSpatialStarted, true);
            Bind(spatialModeAction, OnSpatialCanceled, false);
            if (scrollAction != null) scrollAction.action.Enable();
        }

        private void OnDisable()
        {
            Unbind(dropAction, OnDrop, true);
            Unbind(spatialModeAction, OnSpatialStarted, true);
            Unbind(spatialModeAction, OnSpatialCanceled, false);
            if (scrollAction != null) scrollAction.action.Disable();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (IsCarrying)
            {
                // LMB releases without throwing (requested)
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    DropInWorld();
                    return;
                }
                if (Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame)
                {
                    DropInWorld();
                    return;
                }
            }

            if (!IsCarrying) return;

            // No scale change - keep original scale while carried (was shrinking bug)
            // Previously lerped to _heldOriginalScale * interactableScaleMultiplier (0.65)

            // Spatial input only for items that use spatial or for Interactable if we allow
            if (HeldItem != null && !HeldItem.UsesSpatialCarry) return;
            UpdateSpatialInput();
        }

        private void FixedUpdate()
        {
            if (!IsCarrying) return;
            DriveHeldBody();
        }

        // --- PlaceableItem API ---

        public bool TryPickUp(PlaceableItem item)
        {
            if (item == null || IsCarrying) return false;

            HeldItem = item;
            HeldInteractable = null;
            _heldBody = item.Body;
            _heldColliders = item.Colliders;
            _heldOriginalScale = item.transform.localScale;

            PreparePickup(item.transform);
            item.SetHeld(true, PlayerColliders);
            TuneBodyForCarry(_heldBody);
            return true;
        }

        // --- Interactable API (old system) ---

        public bool TryPickUp(Interactable interactable)
        {
            if (interactable == null || IsCarrying) return false;

            var body = interactable.GetComponentInChildren<Rigidbody>();
            var colliders = interactable.GetComponentsInChildren<Collider>(true);

            if (body == null)
            {
                foreach (var c in colliders)
                {
                    if (c is MeshCollider mesh && !mesh.convex)
                    {
                        mesh.convex = true;
                        Debug.LogWarning($"[PlayerCarry] {interactable.name}: non-convex MeshCollider made convex for physics carry.", interactable);
                    }
                }
                body = interactable.gameObject.AddComponent<Rigidbody>();
            }

            HeldInteractable = interactable;
            HeldItem = null;
            _heldBody = body;
            _heldColliders = colliders;
            _heldOriginalScale = interactable.transform.localScale;

            PreparePickup(interactable.transform);
            // Ignore player collision
            SetPlayerCollisionIgnored(true);
            // Configure body
            SaveBodySettings(body);
            body.isKinematic = false;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.solverIterations = Mathf.Max(_savedSolverIterations, 16);
            body.solverVelocityIterations = Mathf.Max(_savedSolverVelocityIterations, 8);
            body.maxDepenetrationVelocity = 1.5f;
            body.maxAngularVelocity = Mathf.Max(_savedMaxAngularVelocity, maxCarryAngularSpeed * Mathf.Deg2Rad);
            body.linearDamping = Mathf.Max(_savedLinearDamping, carryDamping);
            body.angularDamping = Mathf.Max(_savedAngularDamping, carryDamping);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.WakeUp();
            _tunedBody = body;

            if (interactable.pickupSound != null)
                AudioSource.PlayClipAtPoint(interactable.pickupSound, interactable.transform.position);

            return true;
        }

        private void PreparePickup(Transform itemTransform)
        {
            var cam = Cam;
            var from = cam != null ? cam.transform.position : holdPoint.position;
            var dist = Vector3.Distance(from, itemTransform.position);
            _holdDistance = Mathf.Clamp(dist, minHoldDistance, maxHoldDistance);
            if (_holdDistance < 0.05f) _holdDistance = defaultHoldDistance;

            _planarOffset = Vector3.zero;
            _stuckTimer = 0f;
            _bestGap = float.MaxValue;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;
            var playerYaw = YawRotation(playerBody.rotation);
            _yawOffsetFromPlayer = Quaternion.Inverse(playerYaw) * itemTransform.rotation;
        }

        public PlaceableItem TakeHeldItem()
        {
            var item = HeldItem;
            HeldItem = null;
            SpatialModeActive = false;
            RestoreBodyAfterCarry(true);
            if (item != null) item.SetHeld(false);
            return item;
        }

        public void DropInWorld()
        {
            if (!IsCarrying) return;

            if (HeldItem != null)
            {
                var item = HeldItem;
                HeldItem = null;
                SpatialModeActive = false;
                RestoreBodyAfterCarry(true);
                item.SetHeld(false);
            }
            else if (HeldInteractable != null)
            {
                var interactable = HeldInteractable;
                HeldInteractable = null;
                SpatialModeActive = false;

                if (interactable.dropSound != null)
                    AudioSource.PlayClipAtPoint(interactable.dropSound, interactable.transform.position);

                interactable.transform.localScale = _heldOriginalScale;
                RestoreBodyAfterCarry(true);
                SetPlayerCollisionIgnored(false);
                _heldBody = null;
                _heldColliders = null;
            }
        }

        public void NotifyHeldItemLost(PlaceableItem item)
        {
            if (HeldItem == item)
            {
                HeldItem = null;
                SpatialModeActive = false;
                RestoreBodyAfterCarry(false);
            }
        }

        public void NotifyHeldInteractableLost(Interactable interactable)
        {
            if (HeldInteractable == interactable)
            {
                HeldInteractable = null;
                SpatialModeActive = false;
                RestoreBodyAfterCarry(false);
                SetPlayerCollisionIgnored(false);
            }
        }

        public void SetSpatialModeFromHud(bool held)
        {
            _hudSpatialHeld = held;
            RefreshSpatialMode();
        }

        public void ToggleSpatialModeFromHud()
        {
            _hudSpatialHeld = !_hudSpatialHeld;
            RefreshSpatialMode();
        }

        private void UpdateSpatialInput()
        {
            var mouse = Mouse.current;
            float scroll = 0f;
            if (scrollAction != null)
                scroll = scrollAction.action.ReadValue<Vector2>().y;
            else if (mouse != null)
                scroll = mouse.scroll.ReadValue().y;

            if (Mathf.Abs(scroll) > 0.01f)
            {
                var notches = scroll > 0f ? 1f : -1f;
                if (Mathf.Abs(scroll) > 120f) notches = scroll / 120f;
                _holdDistance = Mathf.Clamp(_holdDistance + notches * scrollMetersPerNotch, minHoldDistance, maxHoldDistance);
            }

            RefreshSpatialMode();

            if (SpatialModeActive && mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                // Flipped X/Y: X -> forward, Y -> up (intuitive, was opposite)
                if (flipPlanarAxes)
                {
                    _planarOffset += playerBody.forward * (delta.x * planeDragSensitivity);
                    _planarOffset += playerBody.up * (delta.y * planeDragSensitivity);
                }
                else
                {
                    _planarOffset += playerBody.up * (delta.x * planeDragSensitivity);
                    _planarOffset += playerBody.forward * (delta.y * planeDragSensitivity);
                }
            }
        }

        private void DriveHeldBody()
        {
            var body = _heldBody;
            if (body == null || body.isKinematic) return;

            var dt = Time.fixedDeltaTime;
            var targetPos = ComputeTargetPosition();
            var targetRot = ComputeTargetRotation();
            UpdateTargetVelocity(targetPos, dt);

            var toTarget = targetPos - body.position;
            var gap = toTarget.magnitude;
            var closingSpeed = gap > 1e-4f ? Vector3.Dot(body.linearVelocity, toTarget / gap) : 0f;
            if (gap > carryBreakDistance || IsStuckForTooLong(gap, closingSpeed))
            {
                DropInWorld();
                return;
            }

            ApplyCarryForce(body, toTarget, gap, dt);
            ApplyCarryTorque(body, targetRot, dt);
        }

        private void ApplyCarryForce(Rigidbody body, Vector3 toTarget, float gap, float dt)
        {
            var mass = Mathf.Max(0.01f, body.mass);
            var forceCap = Mathf.Min(maxCarryForce, mass * maxCarryAcceleration);
            var accelCap = forceCap / mass;
            var maxSpeed = Mathf.Max(0.1f, maxCarrySpeed);
            // For Interactable, use maxCarrySpeed as well
            if (HeldItem != null) maxSpeed = Mathf.Max(0.1f, HeldItem.MaxCarrySpeed);

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

            var maxAngularSpeed = maxCarryAngularSpeed * Mathf.Deg2Rad;
            if (HeldItem != null) maxAngularSpeed = Mathf.Max(0.1f, HeldItem.MaxCarryAngularSpeed) * Mathf.Deg2Rad;

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

        private void SaveBodySettings(Rigidbody body)
        {
            if (body == null) return;
            _savedLinearDamping = body.linearDamping;
            _savedAngularDamping = body.angularDamping;
            _savedInterpolation = body.interpolation;
            _savedCollisionDetection = body.collisionDetectionMode;
            _savedMaxDepenetrationVelocity = body.maxDepenetrationVelocity;
            _savedMaxAngularVelocity = body.maxAngularVelocity;
            _savedSolverIterations = body.solverIterations;
            _savedSolverVelocityIterations = body.solverVelocityIterations;
            _savedWasKinematic = body.isKinematic;
            _savedUseGravity = body.useGravity;
        }

        private void TuneBodyForCarry(Rigidbody body)
        {
            if (body == null) return;
            SaveBodySettings(body);
            _tunedBody = body;
            body.linearDamping = Mathf.Max(body.linearDamping, carryDamping);
            body.angularDamping = Mathf.Max(body.angularDamping, carryDamping);
        }

        private void RestoreBodyAfterCarry(bool clampReleaseSpeed)
        {
            var body = _tunedBody ?? _heldBody;
            _tunedBody = null;
            _heldBody = null;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;

            if (body == null) return;

            body.linearDamping = _savedLinearDamping;
            body.angularDamping = _savedAngularDamping;
            body.interpolation = _savedInterpolation;
            body.collisionDetectionMode = _savedCollisionDetection;
            if (_savedMaxDepenetrationVelocity >= 0f) body.maxDepenetrationVelocity = _savedMaxDepenetrationVelocity;
            if (_savedMaxAngularVelocity >= 0f) body.maxAngularVelocity = _savedMaxAngularVelocity;
            if (_savedSolverIterations > 0) body.solverIterations = _savedSolverIterations;
            if (_savedSolverVelocityIterations > 0) body.solverVelocityIterations = _savedSolverVelocityIterations;

            // For PlaceableItem, SetHeld restores kinematic/gravity, for Interactable we restore here
            if (HeldItem == null && HeldInteractable == null)
            {
                body.isKinematic = _savedWasKinematic;
                body.useGravity = _savedUseGravity;
            }

            if (clampReleaseSpeed && !body.isKinematic)
            {
                body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, maxReleaseSpeed);
                body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, maxReleaseSpeed);
            }
        }

        private void SetPlayerCollisionIgnored(bool ignored)
        {
            if (_heldColliders == null) return;
            foreach (var pc in PlayerColliders)
            {
                if (pc == null) continue;
                foreach (var hc in _heldColliders)
                {
                    if (hc == null) continue;
                    Physics.IgnoreCollision(hc, pc, ignored);
                }
            }
        }

        private bool IsStuckForTooLong(float gap, float closingSpeed)
        {
            if (gap <= stuckDropGap)
            {
                _stuckTimer = 0f;
                _bestGap = gap;
                return false;
            }
            if (gap < _bestGap - 0.01f || closingSpeed > 0.15f)
            {
                _bestGap = Mathf.Min(_bestGap, gap);
                _stuckTimer = 0f;
                return false;
            }
            _stuckTimer += Time.fixedDeltaTime;
            return _stuckTimer >= stuckDropDelay;
        }

        private Vector3 ComputeTargetPosition()
        {
            Transform itemTransform = null;
            bool rotateWithHolder = false;
            if (HeldItem != null)
            {
                itemTransform = HeldItem.transform;
                rotateWithHolder = HeldItem.RotateWithHolder;
            }
            else if (HeldInteractable != null)
            {
                itemTransform = HeldInteractable.transform;
                rotateWithHolder = false;
            }

            if (rotateWithHolder) return holdPoint.position;

            var cam = Cam;
            var origin = cam != null ? cam.transform.position : holdPoint.position;
            var alongCam = cam != null ? cam.transform.forward : playerBody.forward;
            return origin + alongCam * _holdDistance + _planarOffset;
        }

        private Quaternion ComputeTargetRotation()
        {
            if (HeldItem != null)
                return HeldItem.RotateWithHolder ? holdPoint.rotation : YawRotation(playerBody.rotation) * _yawOffsetFromPlayer;
            else
                return YawRotation(playerBody.rotation) * _yawOffsetFromPlayer;
        }

        private void RefreshSpatialMode()
        {
            var mmb = Mouse.current != null && Mouse.current.middleButton.isPressed;
            bool usesSpatial = HeldItem != null ? HeldItem.UsesSpatialCarry : true; // Interactable allows spatial too
            SpatialModeActive = IsCarrying && usesSpatial && (mmb || _hudSpatialHeld || !spatialModeRequiresMMB);
            if (spatialModeRequiresMMB)
                SpatialModeActive = IsCarrying && usesSpatial && (mmb || _hudSpatialHeld);
        }

        private Collider[] PlayerColliders
        {
            get
            {
                if (_playerColliders == null || _playerColliders.Length == 0)
                {
                    if (playerBody != null) _playerColliders = playerBody.GetComponentsInChildren<Collider>();
                    if ((_playerColliders == null || _playerColliders.Length == 0) && playerBody != null)
                        _playerColliders = playerBody.transform.root.GetComponentsInChildren<Collider>();
                }
                return _playerColliders ?? System.Array.Empty<Collider>();
            }
        }

        private Camera Cam => viewCamera != null ? viewCamera : Camera.main;
        private static Quaternion YawRotation(Quaternion rot)
        {
            var e = rot.eulerAngles;
            return Quaternion.Euler(0f, e.y, 0f);
        }

        private void OnDrop(InputAction.CallbackContext ctx) => DropInWorld();
        private void OnSpatialStarted(InputAction.CallbackContext ctx)
        {
            if (IsCarrying) SpatialModeActive = true;
        }
        private void OnSpatialCanceled(InputAction.CallbackContext ctx)
        {
            if (!_hudSpatialHeld) SpatialModeActive = false;
        }

        private static void Bind(InputActionReference reference, System.Action<InputAction.CallbackContext> cb, bool started)
        {
            if (reference == null) return;
            reference.action.Enable();
            if (started) reference.action.started += cb;
            else reference.action.canceled += cb;
        }
        private static void Unbind(InputActionReference reference, System.Action<InputAction.CallbackContext> cb, bool started)
        {
            if (reference == null) return;
            if (started) reference.action.started -= cb;
            else reference.action.canceled -= cb;
        }
    }
}
