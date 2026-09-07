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
            var playerYaw = YawRotation(playerBody.rotation);
            _yawOffsetFromPlayer = Quaternion.Inverse(playerYaw) * item.transform.rotation;
            return true;
        }

        public PlaceableItem TakeHeldItem()
        {
            var item = HeldItem;
            HeldItem = null;
            SpatialModeActive = false;
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

            var targetPos = ComputeTargetPosition(item);
            var targetRot = ComputeTargetRotation(item);

            // Drop the item when it cannot reach its target pose — pressed
            // into a wall, snagged on a corner or wedged behind geometry.
            var gap = Vector3.Distance(body.position, targetPos);
            if (gap > carryBreakDistance || IsStuckForTooLong(gap))
            {
                DropInWorld();
                return;
            }

            // Position: the velocity that would close the whole gap in one
            // physics step, clamped to the item's max carry speed. Fast target
            // moves make the item lag slightly (weighty feel) instead of
            // teleporting; obstacles stop it dead with zero penetration.
            var stepVelocity = (targetPos - body.position) / Time.fixedDeltaTime;
            var maxSpeed = Mathf.Max(0.1f, item.MaxCarrySpeed);
            body.linearVelocity = Vector3.ClampMagnitude(stepVelocity, maxSpeed);

            // Rotation: steer by angular velocity toward the target rotation.
            var deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f)
                angleDeg -= 360f;

            if (angleDeg > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                var stepAngular = axis * (angleDeg * Mathf.Deg2Rad / Time.fixedDeltaTime);
                var maxAngular = Mathf.Max(1f, item.MaxCarryAngularSpeed) * Mathf.Deg2Rad;
                body.angularVelocity = Vector3.ClampMagnitude(stepAngular, maxAngular);
            }
            else
            {
                body.angularVelocity = Vector3.zero;
            }
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
