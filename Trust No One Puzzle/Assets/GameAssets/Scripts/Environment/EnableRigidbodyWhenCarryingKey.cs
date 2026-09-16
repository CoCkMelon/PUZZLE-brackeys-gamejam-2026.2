using UnityEngine;
using UnityEngine.Events;
using GameAssets.Scripts.Entities;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Controls a Rigidbody's kinematic state based on whether the player is carrying a required item/tool.
    /// Ideal for heavy barricades, levers, or objects that should only be physically movable while carrying a specific item.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class RequireCarriedItemRigidbody : MonoBehaviour
    {
        public enum EvaluationMode
        {
            [Tooltip("Rigidbody is non-kinematic only while actively holding the item.")]
            ActiveWhileCarrying,
            [Tooltip("Once the required item is brought/carried near the object, it permanently enables physics.")]
            PermanentUnlockOnCarry
        }

        [Header("Target Physics")]
        [Tooltip("Rigidbody to enable/disable. Automatically assigned to this GameObject if null.")]
        [SerializeField] private Rigidbody targetRigidbody;
        [Tooltip("Evaluation mode: dynamic while held, or permanent once detected.")]
        [SerializeField] private EvaluationMode mode = EvaluationMode.ActiveWhileCarrying;
        [Tooltip("Wake up Rigidbody immediately when enabled.")]
        [SerializeField] private bool wakeUpOnEnable = true;

        [Header("Item Requirements")]
        [Tooltip("Specific GameObject or prefab reference required.")]
        [SerializeField] private GameObject requiredObject;
        [Tooltip("Specific ID match for KeyItem, FurnitureKey, or PlaceableItem.")]
        [SerializeField] private string requiredItemId;
        [SerializeField] private KeyAccess keyAccess = KeyAccess.HeldOrKeyRing;

        [Header("Proximity Check")]
        [Tooltip("If true, player must be within activation distance for physics to unlock.")]
        [SerializeField] private bool requireProximity = true;
        [SerializeField, Min(0.1f)] private float activationDistance = 3.5f;
        [SerializeField] private Transform playerTransform;

        [Header("Events")]
        public UnityEvent onPhysicsEnabled;
        public UnityEvent onPhysicsDisabled;

        private FPPCameraController _cameraController;
        private bool _isCurrentlyEnabled;
        private bool _isPermanentlyUnlocked;

        public bool IsPhysicsEnabled => _isCurrentlyEnabled;

        private void Awake()
        {
            if (targetRigidbody == null)
                targetRigidbody = GetComponent<Rigidbody>();

            _cameraController = FindFirstObjectByType<FPPCameraController>();

            if (playerTransform == null && _cameraController != null)
                playerTransform = _cameraController.transform;

            // Initialize to locked state
            SetRigidbodyState(false, force: true);
        }

        private void Update()
        {
            if (_isPermanentlyUnlocked) return;

            bool isConditionMet = CheckCarriedCondition();

            if (isConditionMet != _isCurrentlyEnabled)
            {
                SetRigidbodyState(isConditionMet);

                if (isConditionMet && mode == EvaluationMode.PermanentUnlockOnCarry)
                {
                    _isPermanentlyUnlocked = true;
                }
            }
        }

        private bool CheckCarriedCondition()
        {
            // 1. Check distance if proximity is enforced
            if (requireProximity)
            {
                if (playerTransform == null)
                {
                    if (_cameraController != null) playerTransform = _cameraController.transform;
                    else return false;
                }

                float sqrDist = (transform.position - playerTransform.position).sqrMagnitude;
                if (sqrDist > activationDistance * activationDistance)
                    return false;
            }

            // 2. Check held object from PlayerCarry / CameraController
            var carriedObject = GetCarriedObject();
            if (carriedObject != null && IsMatchingObject(carriedObject))
            {
                return true;
            }

            // 3. Fallback check for KeyRing / Inventory (KeyItem system)
            if (!string.IsNullOrWhiteSpace(requiredItemId) && KeyItem.PlayerHasKey(requiredItemId, keyAccess))
            {
                return true;
            }

            return false;
        }

        private void SetRigidbodyState(bool enablePhysics, bool force = false)
        {
            if (_isCurrentlyEnabled == enablePhysics && !force) return;

            _isCurrentlyEnabled = enablePhysics;

            if (targetRigidbody != null)
            {
                // In Unity physics: isKinematic = false enables dynamic physics movement
                targetRigidbody.isKinematic = !enablePhysics;

                if (enablePhysics)
                {
                    if (wakeUpOnEnable)
                        targetRigidbody.WakeUp();
                        targetRigidbody.gameObject.GetComponent<Interactable>().enabled = true;
                        targetRigidbody.gameObject.layer = LayerMask.NameToLayer("Interactable");

                    onPhysicsEnabled?.Invoke();
                }
                else
                {
                    targetRigidbody.linearVelocity = Vector3.zero;
                    targetRigidbody.angularVelocity = Vector3.zero;
                    targetRigidbody.Sleep();

                    onPhysicsDisabled?.Invoke();
                }
            }
        }

        private GameObject GetCarriedObject()
        {
            var carry = PlayerCarry.Instance;
            if (carry != null && carry.HeldItem != null)
                return carry.HeldItem.gameObject;

            if (_cameraController != null && _cameraController.CarriedInteractable != null)
                return _cameraController.CarriedInteractable.gameObject;

            return null;
        }

        private bool IsMatchingObject(GameObject candidate)
        {
            if (candidate == null) return false;

            // Direct object/prefab reference
            if (requiredObject != null && (candidate == requiredObject || candidate.transform.IsChildOf(requiredObject.transform)))
                return true;

            if (string.IsNullOrWhiteSpace(requiredItemId))
                return false;

            // FurnitureKey check
            var furnitureKey = candidate.GetComponentInChildren<FurnitureKey>(true) ?? candidate.GetComponentInParent<FurnitureKey>();
            if (furnitureKey != null && furnitureKey.Matches(requiredItemId))
                return true;

            // KeyItem check
            var keyItem = candidate.GetComponentInChildren<KeyItem>(true) ?? candidate.GetComponentInParent<KeyItem>();
            if (keyItem != null && keyItem.Matches(requiredItemId))
                return true;

            // PlaceableItem check
            var placeable = candidate.GetComponentInChildren<PlaceableItem>(true) ?? candidate.GetComponentInParent<PlaceableItem>();
            if (placeable != null && FurnitureKey.IdsMatch(placeable.ItemId, requiredItemId))
                return true;

            return false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (requireProximity)
            {
                Gizmos.color = _isCurrentlyEnabled ? Color.green : Color.yellow;
                Gizmos.DrawWireSphere(transform.position, activationDistance);
            }
        }
#endif
    }
}
