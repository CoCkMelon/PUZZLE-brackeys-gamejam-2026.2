using UnityEngine;
using UnityEngine.InputSystem;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Physics-based carrying.
    ///
    /// Carried items are never made kinematic and never parented to the player:
    /// while held they stay fully simulated rigidbodies that are steered by
    /// writing their velocity each physics step (a servo toward the hold pose).
    /// All contacts are still resolved by the physics engine, so a carried item
    /// can never be teleported, dragged or shoved through walls — it presses
    /// against them and slides along them. No physics joints are used: they
    /// stretch, oscillate and can outright explode in tight spaces.
    ///
    /// Three layers keep the item out of the geometry it touches:
    ///   1. a damped follow servo (never an instant "close the whole gap" jump),
    ///      so the requested step is always something the solver can handle;
    ///   2. a shape sweep along the requested motion which cancels exactly the
    ///      part of the velocity that would push the item into a surface — the
    ///      tangential part survives, so the item slides instead of stopping dead;
    ///   3. an overlap/ComputePenetration pass that eases the item back out of
    ///      anything it already intersects (spawned inside a shelf, squeezed by a
    ///      moving door) at a limited speed instead of popping it across the room.
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

        [Header("Follow servo")]
        [Tooltip("Time the item needs to catch up with the hold pose. Higher = softer, heavier, " +
                 "and easier for the solver to keep out of walls. 0 = snap (not recommended).")]
        [SerializeField, Min(0f)] private float followTime = 0.06f;

        [Tooltip("Same, for rotation.")]
        [SerializeField, Min(0f)] private float rotationFollowTime = 0.05f;

        [Header("Penetration guard")]
        [Tooltip("Sweep the carried shape along its motion and cancel the part of the velocity " +
                 "that would drive it into a surface. Turn off only for debugging.")]
        [SerializeField] private bool sweepAgainstGeometry = true;

        [Tooltip("Gap kept between the carried item and the surfaces it presses against (m).")]
        [SerializeField, Min(0.001f)] private float contactSkin = 0.02f;

        [Tooltip("Layers the carried item is swept and overlap-tested against. " +
                 "Exclude the player layer and anything the item should ignore.")]
        [SerializeField] private LayerMask obstructionMask = ~0;

        [Tooltip("Push the item out of colliders it already overlaps (ComputePenetration).")]
        [SerializeField] private bool resolveOverlaps = true;

        [Tooltip("Top speed (m/s) used to ease an overlapping item back out.")]
        [SerializeField, Min(0.1f)] private float depenetrationSpeed = 1.5f;

        [Header("Auto-drop")]
        [Tooltip("Let go when the item cannot reach the hold pose. Off = the item just stays " +
                 "pressed against whatever is blocking it until the player drops it.")]
        [SerializeField] private bool dropWhenStuck = true;

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

        /// <summary>True while the carried item is resting against something.</summary>
        public bool HeldItemBlocked { get; private set; }

        private float _holdDistance;
        private Vector3 _planarOffset;
        private Quaternion _yawOffsetFromPlayer;
        private bool _hudSpatialHeld;

        private float _stuckTimer;
        private float _bestGap;
        private Collider[] _playerColliders;

        private readonly Collider[] _overlapBuffer = new Collider[24];

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
            HeldItemBlocked = false;
            var playerYaw = YawRotation(playerBody.rotation);
            _yawOffsetFromPlayer = Quaternion.Inverse(playerYaw) * item.transform.rotation;
            return true;
        }

        public PlaceableItem TakeHeldItem()
        {
            var item = HeldItem;
            HeldItem = null;
            SpatialModeActive = false;
            HeldItemBlocked = false;
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
            HeldItemBlocked = false;

            // Restores gravity, the body's original physics settings and
            // collisions with the player. The item keeps its current momentum
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
                HeldItemBlocked = false;
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
        /// Steers the held body toward the target hold pose by writing its
        /// velocity. Because the body stays dynamic, the physics engine still
        /// resolves every contact: walls stop the item, it pushes other props
        /// realistically, and it can never pass through geometry.
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

            // Drop the item when it cannot reach its target pose — pressed
            // into a wall, snagged on a corner or wedged behind geometry.
            var gap = Vector3.Distance(body.position, targetPos);
            if (dropWhenStuck && (gap > carryBreakDistance || IsStuckForTooLong(gap)))
            {
                DropInWorld();
                return;
            }

            // 1. Damped follow: the velocity that closes the gap over followTime
            //    (never in a single step), clamped to the item's carry speed.
            //    Fast target moves make the item lag slightly (weighty feel)
            //    instead of teleporting; obstacles stop it dead with no
            //    penetration to resolve afterwards.
            var toTarget = targetPos - body.position;
            var velocity = toTarget / Mathf.Max(followTime, dt);
            velocity = Vector3.ClampMagnitude(velocity, MaxSafeSpeed(item, dt));

            // 2. Ease out of anything the item already overlaps.
            var separation = resolveOverlaps ? ComputeSeparationVelocity(item, dt) : Vector3.zero;
            var overlapping = separation.sqrMagnitude > 1e-8f;
            if (overlapping)
                velocity += separation;

            // 3. Cancel the part of the motion that would drive the shape into a
            //    surface this step; the tangential part is kept so the item
            //    slides along walls instead of grinding into them.
            var blocked = false;
            if (sweepAgainstGeometry)
                velocity = LimitBySweep(item, body, velocity, dt, out blocked);

            HeldItemBlocked = blocked || overlapping;

            body.linearVelocity = velocity;

            // Rotation: steer by angular velocity toward the target rotation.
            // While the item is in contact the turn rate is damped — rotating a
            // pressed-in object is the classic way to force it through a wall.
            var deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f)
                angleDeg -= 360f;

            if (Mathf.Abs(angleDeg) > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                var stepAngular = axis * (angleDeg * Mathf.Deg2Rad / Mathf.Max(rotationFollowTime, dt));
                var maxAngular = Mathf.Max(1f, item.MaxCarryAngularSpeed) * Mathf.Deg2Rad;
                if (HeldItemBlocked)
                    maxAngular *= 0.25f;
                body.angularVelocity = Vector3.ClampMagnitude(stepAngular, maxAngular);
            }
            else
            {
                body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Carry speed capped so the item can never travel further than its own
        /// thinnest dimension in one physics step — the case continuous collision
        /// detection is worst at.
        /// </summary>
        private float MaxSafeSpeed(PlaceableItem item, float dt)
        {
            var speed = Mathf.Max(0.1f, item.MaxCarrySpeed);

            // With the sweep enabled the shape is already stopped at the first
            // surface on its path, so it may move at full speed.
            if (sweepAgainstGeometry)
                return speed;

            var thickness = item.SmallestColliderThickness;
            if (thickness > 0f)
                speed = Mathf.Min(speed, thickness / Mathf.Max(dt, 1e-4f));

            return speed;
        }

        /// <summary>
        /// Shape-sweeps the carried body along its requested motion and removes
        /// only the component that points into the first blocking surface.
        /// </summary>
        private Vector3 LimitBySweep(PlaceableItem item, Rigidbody body, Vector3 velocity, float dt, out bool blocked)
        {
            blocked = false;

            var speed = velocity.magnitude;
            if (speed < 1e-4f)
                return velocity;

            var direction = velocity / speed;
            var distance = speed * dt + contactSkin;

            var hits = body.SweepTestAll(direction, distance, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            var normal = Vector3.zero;

            foreach (var hit in hits)
            {
                var other = hit.collider;
                if (other == null || IsSelfOrCarrier(item, other))
                    continue;
                if ((obstructionMask.value & (1 << other.gameObject.layer)) == 0)
                    continue;
                if (hit.distance >= nearest)
                    continue;

                nearest = hit.distance;
                normal = hit.normal;
            }

            if (nearest >= float.MaxValue || normal.sqrMagnitude < 1e-6f)
                return velocity;

            blocked = true;

            // How much the item may still travel before it touches the surface.
            var allowed = Mathf.Max(0f, nearest - contactSkin);
            var maxApproachSpeed = allowed / Mathf.Max(dt, 1e-4f);

            var approachSpeed = Vector3.Dot(velocity, -normal);
            if (approachSpeed > maxApproachSpeed)
                velocity += normal * (approachSpeed - maxApproachSpeed);

            return velocity;
        }

        /// <summary>
        /// Velocity that eases the item out of colliders it currently intersects.
        /// Limited to <see cref="depenetrationSpeed"/> so a wedged crate slides
        /// free instead of being fired across the room.
        /// </summary>
        private Vector3 ComputeSeparationVelocity(PlaceableItem item, float dt)
        {
            var separation = Vector3.zero;

            foreach (var itemCollider in item.Colliders)
            {
                if (itemCollider == null || !itemCollider.enabled || !itemCollider.gameObject.activeInHierarchy)
                    continue;

                var bounds = itemCollider.bounds;
                var count = Physics.OverlapBoxNonAlloc(
                    bounds.center,
                    bounds.extents + Vector3.one * contactSkin,
                    _overlapBuffer,
                    Quaternion.identity,
                    obstructionMask,
                    QueryTriggerInteraction.Ignore);

                for (var i = 0; i < count; i++)
                {
                    var other = _overlapBuffer[i];
                    if (other == null || IsSelfOrCarrier(item, other))
                        continue;

                    // Dynamic bodies are pushed by the solver itself; forcing them
                    // apart here would double up and jitter.
                    var otherBody = other.attachedRigidbody;
                    if (otherBody != null && !otherBody.isKinematic)
                        continue;

                    if (Physics.ComputePenetration(
                            itemCollider, itemCollider.transform.position, itemCollider.transform.rotation,
                            other, other.transform.position, other.transform.rotation,
                            out var direction, out var distance))
                    {
                        separation += direction * distance;
                    }
                }
            }

            if (separation.sqrMagnitude < 1e-8f)
                return Vector3.zero;

            var speed = Mathf.Min(separation.magnitude / Mathf.Max(dt, 1e-4f), depenetrationSpeed);
            return separation.normalized * speed;
        }

        private bool IsSelfOrCarrier(PlaceableItem item, Collider candidate)
        {
            if (candidate.transform.IsChildOf(item.transform))
                return true;

            if (item.Body != null && candidate.attachedRigidbody == item.Body)
                return true;

            foreach (var playerCollider in PlayerColliders)
            {
                if (playerCollider == candidate)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when the item has been far from its target without making any
        /// progress for <see cref="stuckDropDelay"/> seconds. Items flying to
        /// the player after pickup keep making progress and never count as stuck.
        /// </summary>
        private bool IsStuckForTooLong(float gap)
        {
            if (gap <= stuckDropGap)
            {
                _stuckTimer = 0f;
                _bestGap = gap;
                return false;
            }

            if (gap < _bestGap - 0.01f)
            {
                // Still closing in on the target — not stuck.
                _bestGap = gap;
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
