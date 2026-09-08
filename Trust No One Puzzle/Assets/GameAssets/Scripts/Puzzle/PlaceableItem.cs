using System.Collections.Generic;
using GameAssets.Scripts.Interaction;
using UnityEngine;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Pickup that can be carried, shoved in tight spaces, and dropped into a <see cref="PlacementSlot"/>.
    ///
    /// Physics carry: while held the item is NEVER made kinematic, NEVER parented
    /// to the player and its transform is never written directly. It stays a fully
    /// simulated rigidbody that <see cref="PlayerCarry"/> steers purely via
    /// velocity, so every contact with walls and props is resolved by the physics
    /// engine — a carried item cannot be dragged, teleported or shoved through
    /// geometry, it presses against it and slides along it instead.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PlaceableItem : MonoBehaviour, IInteractable
    {
        public enum CarryStyle
        {
            /// <summary>Small items: closely track the hold point pose (position + rotation).</summary>
            Handheld,
            /// <summary>Keep world rotation while following the hold position (crates, furniture).</summary>
            WorldStable,
            /// <summary>World-stable plus scroll distance and MMB / HUD spatial shoving.</summary>
            Spatial
        }

        [SerializeField] private string itemId = "item";
        [SerializeField] private string displayName = "Object";
        [SerializeField] private CarryStyle carryStyle = CarryStyle.Handheld;
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider[] colliders;

        [Header("Carry physics")]
        [Tooltip("Top speed (m/s) at which this item is steered while carried. Lower = heavier feel.")]
        [SerializeField] private float maxCarrySpeed = 10f;
        [Tooltip("Top turn rate (deg/s) at which this item is rotated while carried.")]
        [SerializeField] private float maxCarryAngularSpeed = 720f;

        [Tooltip("Solver iteration count used while carried. Higher = contacts against walls " +
                 "are resolved harder, so the item does not sink into geometry.")]
        [SerializeField, Min(1)] private int carrySolverIterations = 16;

        [Tooltip("Velocity iteration count used while carried.")]
        [SerializeField, Min(1)] private int carrySolverVelocityIterations = 8;

        [Tooltip("How fast PhysX may push the item out of something it overlaps (m/s). " +
                 "Low values stop wedged items from being launched across the room.")]
        [SerializeField, Min(0.1f)] private float carryMaxDepenetrationSpeed = 1.5f;

        [Header("Spatial (boxes)")]
        [SerializeField] private float defaultHoldDistance = 1.6f;
        [SerializeField] private float minHoldDistance = 0.6f;
        [SerializeField] private float maxHoldDistance = 4f;

        public string ItemId => itemId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;
        public CarryStyle Style => carryStyle;
        public Rigidbody Body => body;
        public float DefaultHoldDistance => defaultHoldDistance;
        public float MinHoldDistance => minHoldDistance;
        public float MaxHoldDistance => maxHoldDistance;
        public float MaxCarrySpeed => maxCarrySpeed;
        public float MaxCarryAngularSpeed => maxCarryAngularSpeed;

        /// <summary>Colliders that make up this item (used for sweeps and overlap tests).</summary>
        public Collider[] Colliders => colliders ?? System.Array.Empty<Collider>();

        /// <summary>
        /// Thickness of the item's thinnest axis, in metres. The carry code caps
        /// the per-step movement with it so a thin object can never step past a
        /// collider between two physics ticks.
        /// </summary>
        public float SmallestColliderThickness
        {
            get
            {
                if (_smallestThickness <= 0f)
                    _smallestThickness = ComputeSmallestThickness();
                return _smallestThickness;
            }
        }
        public bool UsesSpatialCarry => carryStyle == CarryStyle.Spatial;
        public bool RotateWithHolder => carryStyle == CarryStyle.Handheld;

        public bool IsHeld { get; private set; }
        public PlacementSlot OccupyingSlot { get; private set; }

        /// <summary>Carrier colliders whose collisions we currently ignore while held.</summary>
        private readonly List<Collider> _ignoredCarrierColliders = new List<Collider>();
        private RigidbodyInterpolation _savedInterpolation = RigidbodyInterpolation.None;
        private CollisionDetectionMode _savedCollisionDetection = CollisionDetectionMode.Discrete;
        private float _savedMaxDepenetrationVelocity = -1f;
        private float _savedMaxAngularVelocity = -1f;
        private int _savedSolverIterations = -1;
        private int _savedSolverVelocityIterations = -1;
        private float _smallestThickness = -1f;

        public string InteractionPrompt => IsHeld ? "" : $"Press [E] to Pick Up {DisplayName}";
        public bool CanInteract => !IsHeld && OccupyingSlot == null;

        private void Awake()
        {
            if (body == null)
                body = GetComponent<Rigidbody>();
            if (colliders == null || colliders.Length == 0)
                colliders = GetComponentsInChildren<Collider>();
        }

        public void OnInteract()
        {
            var carrier = PlayerCarry.Instance;
            if (carrier != null)
                carrier.TryPickUp(this);
        }

        /// <param name="carrierColliders">
        /// Colliders of whoever picks the item up. While held, collisions between
        /// the item and the carrier are ignored so the player capsule cannot fight
        /// the carried body; they are restored on release.
        /// </param>
        public void SetHeld(bool held, Collider[] carrierColliders = null)
        {
            IsHeld = held;
            OccupyingSlot = null;

            if (colliders == null || colliders.Length == 0)
                colliders = GetComponentsInChildren<Collider>();

            if (held && body == null)
                EnsureBody();

            if (body != null)
            {
                if (held)
                {
                    _savedInterpolation = body.interpolation;
                    _savedCollisionDetection = body.collisionDetectionMode;
                    _savedMaxDepenetrationVelocity = body.maxDepenetrationVelocity;
                    _savedMaxAngularVelocity = body.maxAngularVelocity;
                    _savedSolverIterations = body.solverIterations;
                    _savedSolverVelocityIterations = body.solverVelocityIterations;

                    // The heart of the physics carry: the body stays fully
                    // simulated. PlayerCarry only writes its velocity, so all
                    // contacts (walls, floors, props) are resolved by PhysX and
                    // the item can never tunnel or be pushed through geometry.
                    body.isKinematic = false;
                    body.useGravity = false;
                    body.interpolation = RigidbodyInterpolation.Interpolate;
                    // Continuous collision keeps even fast carries from
                    // tunnelling through thin walls.
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                    // Contacts win over the carry servo: more solver iterations
                    // keep the item on the surface of a wall instead of letting
                    // the driven velocity push it inside, and the capped
                    // depenetration speed stops wedged items from exploding out.
                    body.solverIterations = Mathf.Max(_savedSolverIterations, carrySolverIterations);
                    body.solverVelocityIterations =
                        Mathf.Max(_savedSolverVelocityIterations, carrySolverVelocityIterations);
                    body.maxDepenetrationVelocity = carryMaxDepenetrationSpeed;

                    // Unity clamps angular velocity to 7 rad/s by default, which
                    // silently fights the carry rotation servo.
                    body.maxAngularVelocity = Mathf.Max(
                        _savedMaxAngularVelocity,
                        maxCarryAngularSpeed * Mathf.Deg2Rad);

                    body.WakeUp();
                }
                else
                {
                    RestoreBodySimulation();
                }
            }

            SetCarrierCollisionIgnored(held, carrierColliders);

            // Never parent to the player while held — a parented rigidbody is
            // dragged through geometry by the transform hierarchy.
            transform.SetParent(null);
        }

        public void SnapToSlot(PlacementSlot slot)
        {
            OccupyingSlot = slot;
            IsHeld = false;

            foreach (var c in colliders)
            {
                if (c != null)
                    c.enabled = false;
            }

            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            SetCarrierCollisionIgnored(false, null);

            transform.SetParent(slot.SnapPoint);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        public void ReleaseFromSlot()
        {
            OccupyingSlot = null;
            foreach (var c in colliders)
            {
                if (c != null)
                    c.enabled = true;
            }

            if (body != null)
            {
                body.isKinematic = false;
                body.useGravity = true;
                body.interpolation = _savedInterpolation;
                body.collisionDetectionMode = _savedCollisionDetection;
            }

            transform.SetParent(null);
        }

        private void OnDisable()
        {
            // If the item disappears while held (destroyed / deactivated),
            // make sure the carrier lets go and no ignored collision pair leaks.
            if (!IsHeld)
                return;

            IsHeld = false;
            SetCarrierCollisionIgnored(false, null);
            RestoreBodySimulation();

            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem == this)
                carry.NotifyHeldItemLost(this);
        }

        /// <summary>Creates a rigidbody at pickup time so every pickable item can be carried with physics.</summary>
        private void EnsureBody()
        {
            // Dynamic rigidbodies only work with convex colliders.
            foreach (var c in colliders)
            {
                if (c is MeshCollider mesh && !mesh.convex)
                {
                    mesh.convex = true;
                    Debug.LogWarning(
                        $"[PlaceableItem] {gameObject.name}: non-convex MeshCollider was made convex so the item can be carried with physics.",
                        this);
                }
            }

            body = gameObject.AddComponent<Rigidbody>();
        }

        private void RestoreBodySimulation()
        {
            if (body == null)
                return;

            body.isKinematic = false;
            body.useGravity = true;
            body.interpolation = _savedInterpolation;
            body.collisionDetectionMode = _savedCollisionDetection;

            if (_savedMaxDepenetrationVelocity >= 0f)
                body.maxDepenetrationVelocity = _savedMaxDepenetrationVelocity;
            if (_savedMaxAngularVelocity >= 0f)
                body.maxAngularVelocity = _savedMaxAngularVelocity;
            if (_savedSolverIterations > 0)
                body.solverIterations = _savedSolverIterations;
            if (_savedSolverVelocityIterations > 0)
                body.solverVelocityIterations = _savedSolverVelocityIterations;
        }

        /// <summary>Smallest world-space dimension of the item's combined bounds.</summary>
        private float ComputeSmallestThickness()
        {
            var found = false;
            var bounds = new Bounds(transform.position, Vector3.zero);

            foreach (var c in Colliders)
            {
                if (c == null)
                    continue;

                if (!found)
                {
                    bounds = c.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(c.bounds);
                }
            }

            if (!found)
                return 0.1f;

            var size = bounds.size;
            return Mathf.Max(0.02f, Mathf.Min(size.x, Mathf.Min(size.y, size.z)));
        }

        private void SetCarrierCollisionIgnored(bool ignore, Collider[] carrierColliders)
        {
            // Restore previously ignored pairs first so state can never leak
            // between pickups.
            for (var i = 0; i < _ignoredCarrierColliders.Count; i++)
            {
                for (var c = 0; c < colliders.Length; c++)
                {
                    if (colliders[c] != null && _ignoredCarrierColliders[i] != null)
                        Physics.IgnoreCollision(colliders[c], _ignoredCarrierColliders[i], false);
                }
            }
            _ignoredCarrierColliders.Clear();

            if (!ignore || carrierColliders == null)
                return;

            foreach (var carrierCol in carrierColliders)
            {
                if (carrierCol == null)
                    continue;

                _ignoredCarrierColliders.Add(carrierCol);
                foreach (var itemCol in colliders)
                {
                    if (itemCol != null)
                        Physics.IgnoreCollision(itemCol, carrierCol, true);
                }
            }
        }
    }
}
