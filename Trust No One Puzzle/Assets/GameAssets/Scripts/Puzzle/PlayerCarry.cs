using UnityEngine;
using UnityEngine.InputSystem;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Physics-based carrying.
    ///
    /// Carried items are never made kinematic and never parented to the player:
    /// while held they stay fully simulated rigidbodies with their colliders on,
    /// steered toward the hold pose each physics step with a *bounded* force and
    /// torque (<see cref="maxCarryForce"/>, <see cref="maxCarryTorque"/>).
    /// Because the push is limited, the item can never be driven into a wall or
    /// crush another prop into one: whatever it is pressed against receives at
    /// most that force, the physics engine resolves the contact normally and the
    /// item simply stops, slides along the obstacle or is dropped when it stays
    /// stuck. Velocities are never written directly while held (that would be an
    /// unbounded force) and no physics joints are used: they stretch, oscillate
    /// and can outright explode in tight spaces.
    ///
    /// Heavier items (Rigidbody mass) accelerate slower under the same force, so
    /// they lag behind a sprinting player and feel heavy; light items track the
    /// hand tightly thanks to the acceleration caps.
    ///
    /// Handheld items closely track the hold point pose.
    /// WorldStable / Spatial items keep world rotation (do not spin with look).
    /// Spatial: mouse wheel moves along the camera ray (down = toward cam, up = away);
    /// hold MMB or a mobile HUD button to slide in player space
    /// (screen X → player up / local Y, screen Y → player forward / local Z).
    /// Object yaw stays locked to the player body while shoved.
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

        [Header("Carry force limits")]
        [Tooltip("Strongest push (N) the carry can apply to the held item. This is also the most it can press " +
                 "an item against a wall or another object, so keep it low enough that nothing gets shoved through geometry.")]
        [SerializeField, Min(1f)] private float maxCarryForce = 300f;
        [Tooltip("Strongest twist (N·m) used to turn the held item toward the hold rotation.")]
        [SerializeField, Min(0.1f)] private float maxCarryTorque = 30f;
        [Tooltip("Acceleration cap (m/s²) so very light items don't snap around; heavy items are limited by Max Carry Force instead.")]
        [SerializeField, Min(1f)] private float maxCarryAcceleration = 60f;
        [Tooltip("Angular acceleration cap (rad/s²) for light items; heavy items are limited by Max Carry Torque instead.")]
        [SerializeField, Min(1f)] private float maxCarryAngularAcceleration = 120f;
        [Tooltip("Light extra damping on the held item while carried, a safety net against wobble when the servo is saturated. " +
                 "Keep it low: a heavy item's top carry speed is roughly Max Carry Force / (mass × damping).")]
        [SerializeField, Min(0f)] private float carryDamping = 1f;
        [Tooltip("Speed the item is allowed to keep when it is released. Stops a stuck item from flying off with stored servo speed.")]
        [SerializeField, Min(0f)] private float maxReleaseSpeed = 3f;

        [Header("Auto-drop")]
        [Tooltip("Drop the item when it stays at least this far from its target hold pose.")]
        [SerializeField] private float stuckDropGap = 0.5f;
        [Tooltip("Drop the item after it has been stuck (no progress toward the target) for this long.")]
        [SerializeField] private float stuckDropDelay = 0.7f;
        [Tooltip("Instant drop when the item is this far from its target pose (wedged behind geometry).")]
        [SerializeField] private float carryBreakDistance = 4f;

        [Header("Spatial move")]
        [SerializeField] private float scrollMetersPerNotch = 0.35f;
        [SerializeField] private float planeDragSensitivity = 0.008f;

        public PlaceableItem HeldItem { get; private set; }
        public bool IsCarrying => HeldItem != null;
        public bool SpatialModeActive { get; private set; }

        private float _holdDistance;
        private Vector3 _planarOffset;
        private Quaternion _yawOffsetFromPlayer;
        private bool _hudSpatialHeld;

        private float _stuckTimer;
        private float _bestGap;
        private Collider[] _playerColliders;

        // Hold-pose motion, estimated from successive physics steps so the
        // servo can feed-forward the player's own movement.
        private Vector3 _previousTargetPos;
        private Vector3 _targetVelocity;
        private bool _hasPreviousTarget;

        // Rigidbody settings overwritten while carrying, restored on release.
        private float _savedLinearDamping;
        private float _savedAngularDamping;
        private Rigidbody _tunedBody;

        private void Awake()
        {
            Instance = this;
            if (playerBody == null)
                playerBody = transform;
            if (viewCamera == null)
                viewCamera = Camera.main;
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
        }

        private void OnDisable()
        {
            Unbind(dropAction, OnDrop, true);
            Unbind(spatialModeAction, OnSpatialStarted, true);
            Unbind(spatialModeAction, OnSpatialCanceled, false);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!IsCarrying || !HeldItem.UsesSpatialCarry)
                return;

            UpdateSpatialInput();
        }

        private void FixedUpdate()
        {
            if (!IsCarrying)
                return;

            DriveHeldBody();
        }

        public bool TryPickUp(PlaceableItem item)
        {
            if (item == null || IsCarrying)
                return false;

            HeldItem = item;
            item.SetHeld(true, PlayerColliders);

            var cam = Cam;
            var from = cam != null ? cam.transform.position : holdPoint.position;
            _holdDistance = Mathf.Clamp(
                Vector3.Distance(from, item.transform.position),
                item.MinHoldDistance,
                item.MaxHoldDistance);
            if (_holdDistance < 0.05f)
                _holdDistance = item.DefaultHoldDistance;

            _planarOffset = Vector3.zero;
            _stuckTimer = 0f;
            _bestGap = float.MaxValue;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;
            var playerYaw = YawRotation(playerBody.rotation);
            _yawOffsetFromPlayer = Quaternion.Inverse(playerYaw) * item.transform.rotation;
            TuneBodyForCarry(item.Body);
            return true;
        }

        public PlaceableItem TakeHeldItem()
        {
            var item = HeldItem;
            HeldItem = null;
            SpatialModeActive = false;
            RestoreBodyAfterCarry(true);
            if (item != null)
                item.SetHeld(false);
            return item;
        }

        public void DropInWorld()
        {
            if (!IsCarrying)
                return;

            var item = HeldItem;
            HeldItem = null;
            SpatialModeActive = false;

            // Give back the body's damping and clamp the speed it leaves with:
            // an item that was straining against an obstacle must not shoot
            // off with the servo speed it had built up.
            RestoreBodyAfterCarry(true);

            // Restores gravity, the body's original physics settings and
            // collisions with the player. The item keeps its (clamped) momentum
            // and simply falls where it is.
            item.SetHeld(false);
        }

        /// <summary>Called by a held item when it is destroyed or deactivated.</summary>
        public void NotifyHeldItemLost(PlaceableItem item)
        {
            if (HeldItem == item)
            {
                HeldItem = null;
                SpatialModeActive = false;
                RestoreBodyAfterCarry(false);
            }
        }

        /// <summary>Mobile HUD: pointer down / up on a hold-to-move button.</summary>
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
            if (mouse == null)
                return;

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                // Wheel up = away from camera, wheel down = toward camera.
                var notches = scroll > 0f ? 1f : -1f;
                if (Mathf.Abs(scroll) > 120f)
                    notches = scroll / 120f;
                _holdDistance = Mathf.Clamp(
                    _holdDistance + notches * scrollMetersPerNotch,
                    HeldItem.MinHoldDistance,
                    HeldItem.MaxHoldDistance);
            }

            RefreshSpatialMode();

            if (SpatialModeActive)
            {
                var delta = mouse.delta.ReadValue();
                // Screen X → player local Y (up). Screen Y → player local Z (forward).
                _planarOffset += playerBody.up * (delta.x * planeDragSensitivity);
                _planarOffset += playerBody.forward * (delta.y * planeDragSensitivity);
            }
        }

        /// <summary>
        /// Steers the held body toward the target hold pose with a bounded
        /// force and torque. The body stays dynamic with its colliders on, so
        /// the physics engine resolves every contact — and because the push is
        /// capped, an obstacle (or another prop between the item and a wall)
        /// is never loaded with more than <see cref="maxCarryForce"/>: the item
        /// stops against it instead of tunnelling or crushing it through.
        /// </summary>
        private void DriveHeldBody()
        {
            var item = HeldItem;
            if (item == null)
                return;

            var body = item.Body;
            if (body == null || body.isKinematic)
                return;

            var dt = Time.fixedDeltaTime;
            var targetPos = ComputeTargetPosition(item);
            var targetRot = ComputeTargetRotation(item);
            UpdateTargetVelocity(targetPos, dt);

            // Drop the item when it cannot reach its target pose — pressed
            // into a wall, snagged on a corner or wedged behind geometry.
            var toTarget = targetPos - body.position;
            var gap = toTarget.magnitude;
            var closingSpeed = gap > 1e-4f ? Vector3.Dot(body.linearVelocity, toTarget / gap) : 0f;
            if (gap > carryBreakDistance || IsStuckForTooLong(gap, closingSpeed))
            {
                DropInWorld();
                return;
            }

            ApplyCarryForce(item, body, toTarget, gap, dt);
            ApplyCarryTorque(item, body, targetRot, dt);
        }

        /// <summary>
        /// Linear servo: aim for a velocity that follows the hold point and
        /// closes the remaining gap on a braking profile the force cap can
        /// actually stop, then apply the force needed to reach that velocity
        /// this step — clamped to the carry's force and acceleration limits.
        /// </summary>
        private void ApplyCarryForce(PlaceableItem item, Rigidbody body, Vector3 toTarget, float gap, float dt)
        {
            var mass = Mathf.Max(0.01f, body.mass);
            var forceCap = Mathf.Min(maxCarryForce, mass * maxCarryAcceleration);
            var accelCap = forceCap / mass;
            var maxSpeed = Mathf.Max(0.1f, item.MaxCarrySpeed);

            // Feed-forward the target's own motion (player walking / turning),
            // then add the gap-closing approach speed. The approach speed is
            // the largest speed from which accelCap can still stop within the
            // gap, so the item never overshoots its hold point and oscillates.
            var desiredVelocity = Vector3.ClampMagnitude(_targetVelocity, maxSpeed);
            if (gap > 1e-4f)
            {
                var approachSpeed = Mathf.Min(BrakingSpeed(gap, accelCap, dt), maxSpeed);
                desiredVelocity += toTarget * (approachSpeed / gap);
            }

            // Force that would reach the desired velocity within this step,
            // clamped to the cap. Gravity is disabled while held (PlaceableItem),
            // so nothing else needs compensating. When an obstacle holds the
            // item back the clamp is what the obstacle feels — never more.
            var neededForce = (desiredVelocity - body.linearVelocity) * (mass / dt);
            var force = Vector3.ClampMagnitude(neededForce, forceCap);
            body.AddForce(force, ForceMode.Force);
        }

        /// <summary>
        /// Angular servo toward the hold rotation using a bounded torque.
        /// The desired angular acceleration is converted through the body's
        /// inertia tensor, so oblong items turn evenly about every axis.
        /// </summary>
        private void ApplyCarryTorque(PlaceableItem item, Rigidbody body, Quaternion targetRot, float dt)
        {
            var deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f)
                angleDeg -= 360f;

            var maxAngularSpeed = Mathf.Max(0.1f, item.MaxCarryAngularSpeed) * Mathf.Deg2Rad;
            var desiredAngularVelocity = Vector3.zero;

            if (Mathf.Abs(angleDeg) > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                var angleRad = angleDeg * Mathf.Deg2Rad;
                var speed = Mathf.Min(BrakingSpeed(Mathf.Abs(angleRad), maxCarryAngularAcceleration, dt), maxAngularSpeed);
                desiredAngularVelocity = axis * (Mathf.Sign(angleRad) * speed);
            }

            // Angular acceleration (rad/s²) needed to reach the desired angular
            // velocity within this step, capped, then converted to a torque
            // through the body's inertia tensor so mass distribution matters.
            var angularAccel = (desiredAngularVelocity - body.angularVelocity) / dt;
            angularAccel = Vector3.ClampMagnitude(angularAccel, maxCarryAngularAcceleration);

            var torque = InertiaTensorTimes(body, angularAccel);
            torque = Vector3.ClampMagnitude(torque, maxCarryTorque);
            body.AddTorque(torque, ForceMode.Force);
        }

        /// <summary>
        /// Largest speed that can still be brought to rest within
        /// <paramref name="distance"/> under <paramref name="decel"/>, allowing
        /// for one discrete step of travel before braking begins.
        /// </summary>
        private static float BrakingSpeed(float distance, float decel, float dt)
        {
            var step = decel * dt;
            return Mathf.Sqrt(step * step + 2f * decel * distance) - step;
        }

        /// <summary>World-space torque that produces <paramref name="angularAccel"/> on this body.</summary>
        private static Vector3 InertiaTensorTimes(Rigidbody body, Vector3 angularAccel)
        {
            var tensorRot = body.rotation * body.inertiaTensorRotation;
            var local = Quaternion.Inverse(tensorRot) * angularAccel;
            var tensor = body.inertiaTensor;
            local = new Vector3(local.x * tensor.x, local.y * tensor.y, local.z * tensor.z);
            return tensorRot * local;
        }

        /// <summary>
        /// Estimates how fast the hold pose is moving (player walking, turning,
        /// scrolling the item) so the servo can follow it without lagging.
        /// Lightly filtered because the player's CharacterController moves in
        /// Update while this runs in FixedUpdate.
        /// </summary>
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

        /// <summary>
        /// Adds damping for the duration of the carry. Without it a capped
        /// force leaves the item free to wobble around the hold point or keep
        /// sliding along a wall after a push; with it the carry feels held.
        /// </summary>
        private void TuneBodyForCarry(Rigidbody body)
        {
            if (body == null)
                return;

            _tunedBody = body;
            _savedLinearDamping = body.linearDamping;
            _savedAngularDamping = body.angularDamping;

            body.linearDamping = Mathf.Max(body.linearDamping, carryDamping);
            body.angularDamping = Mathf.Max(body.angularDamping, carryDamping);
        }

        /// <summary>Restores the body's own damping and, optionally, clamps the speed it is released with.</summary>
        private void RestoreBodyAfterCarry(bool clampReleaseSpeed)
        {
            var body = _tunedBody;
            _tunedBody = null;
            _hasPreviousTarget = false;
            _targetVelocity = Vector3.zero;

            if (body == null)
                return;

            body.linearDamping = _savedLinearDamping;
            body.angularDamping = _savedAngularDamping;

            if (clampReleaseSpeed && !body.isKinematic)
            {
                body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, maxReleaseSpeed);
                body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, maxReleaseSpeed);
            }
        }

        /// <summary>
        /// True when the item has been far from its target without making any
        /// progress for <see cref="stuckDropDelay"/> seconds. Items flying to
        /// the player after pickup, or heavy items trailing a sprinting player,
        /// are still moving toward the hand and never count as stuck; only an
        /// item that is held back by something (its closing speed killed by a
        /// contact) runs the timer down.
        /// </summary>
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
                // Still closing in on (or chasing) the target — not stuck.
                _bestGap = Mathf.Min(_bestGap, gap);
                _stuckTimer = 0f;
                return false;
            }

            _stuckTimer += Time.fixedDeltaTime;
            return _stuckTimer >= stuckDropDelay;
        }

        private Vector3 ComputeTargetPosition(PlaceableItem item)
        {
            if (item.RotateWithHolder)
                return holdPoint.position;

            var cam = Cam;
            var origin = cam != null ? cam.transform.position : holdPoint.position;
            var alongCam = cam != null ? cam.transform.forward : playerBody.forward;
            return origin + alongCam * _holdDistance + _planarOffset;
        }

        private Quaternion ComputeTargetRotation(PlaceableItem item)
        {
            return item.RotateWithHolder
                ? holdPoint.rotation
                : YawRotation(playerBody.rotation) * _yawOffsetFromPlayer;
        }

        private void RefreshSpatialMode()
        {
            var mmb = Mouse.current != null && Mouse.current.middleButton.isPressed;
            SpatialModeActive = HeldItem != null && HeldItem.UsesSpatialCarry && (mmb || _hudSpatialHeld);
        }

        private Collider[] PlayerColliders
        {
            get
            {
                if (_playerColliders == null || _playerColliders.Length == 0)
                {
                    if (playerBody != null)
                        _playerColliders = playerBody.GetComponentsInChildren<Collider>();

                    // Fallback for setups where this component sits on a child
                    // (e.g. the camera rig) rather than the player root.
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
            if (HeldItem != null && HeldItem.UsesSpatialCarry)
                SpatialModeActive = true;
        }

        private void OnSpatialCanceled(InputAction.CallbackContext ctx)
        {
            if (!_hudSpatialHeld)
                SpatialModeActive = false;
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
