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
        public bool UsesSpatialCarry => carryStyle == CarryStyle.Spatial;
        public bool RotateWithHolder => carryStyle == CarryStyle.Handheld;

        public bool IsHeld { get; private set; }
        public PlacementSlot OccupyingSlot { get; private set; }

        /// <summary>Carrier colliders whose collisions we currently ignore while held.</summary>
        private readonly List<Collider> _ignoredCarrierColliders = new List<Collider>();
        private RigidbodyInterpolation _savedInterpolation = RigidbodyInterpolation.None;
        private CollisionDetectionMode _savedCollisionDetection = CollisionDetectionMode.Discrete;

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
