using UnityEngine;
using UnityEngine.Events;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.Entities.Player;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Enables a Rigidbody's physics when the required carried/pickup object enters the trigger.
    /// Perfect for cabinets with Rigidbody + HingeJoint. Keeps door locked until item is inserted.
    /// Automatically drops the item from PlayerCarry if the player is holding it.
    /// </summary>
    public class RigidbodyUnlockByCollision : MonoBehaviour
    {
        [Header("Target Rigidbody")]
        [Tooltip("The Rigidbody to enable physics on (Usually the Cabinet Door).")]
        [SerializeField] private Rigidbody cabinetRigidbody;

        [Header("Required Item")]
        [Tooltip("Specific pickup GameObject reference.")]
        [SerializeField] private GameObject requiredObject;
        [Tooltip("Match by PlaceableItem, KeyItem or FurnitureKey ID.")]
        [SerializeField] private string requiredItemId;
        [Tooltip("Match by Interactable Display Name (Recommended for your system).")]
        [SerializeField] private string requiredItemName;

        [Header("Settings")]
        [Tooltip("Destroy/Consume the inserted item after unlocking?")]
        [SerializeField] private bool consumeRequiredItem = true;
        [Tooltip("Can only be used once?")]
        [SerializeField] private bool onlyOnce = true;
        [Tooltip("Play unlock sound at this trigger position.")]
        [SerializeField] private AudioClip unlockSound;
        [Tooltip("Disable this trigger after use?")]
        [SerializeField] private bool disableTriggerAfterUse = true;

        [Header("Events")]
        public UnityEvent OnUnlocked;

        private bool _used;

        private void Awake()
        {
            if (cabinetRigidbody == null)
                cabinetRigidbody = GetComponentInParent<Rigidbody>();

            // Keep cabinet locked at start
            if (cabinetRigidbody != null)
            {
                cabinetRigidbody.isKinematic = true;
            }
        }

        private void OnCollisionEnter(Collision other)
        {
            if (_used) return;

            if (IsMatching(other.gameObject))
            {
                // VERY IMPORTANT: If player is currently carrying this object, force drop it
                PlayerCarry carry = PlayerCarry.Instance;
                if (carry != null && carry.HeldItem != null)
                {
                    if (carry.HeldItem.gameObject == other.gameObject)
                    {
                        carry.TakeHeldItem();
                    }
                }

                // Enable Rigidbody Physics (Unlock the door)
                if (cabinetRigidbody != null)
                {
                    cabinetRigidbody.isKinematic = false;
                    cabinetRigidbody.WakeUp();
                    gameObject.layer = LayerMask.NameToLayer("Interactable");
                }

                if (unlockSound != null)
                    AudioSource.PlayClipAtPoint(unlockSound, transform.position);

                OnUnlocked?.Invoke();

                if (consumeRequiredItem)
                {
                    Destroy(other.gameObject);
                }

                // if (onlyOnce)
                // {
                    _used = true;

                    // if (disableTriggerAfterUse)
                        // gameObject.SetActive(false);
                // }
            }
        }
        private void OnTriggerEnter(Collider other)
        {
            if (_used) return;

            if (IsMatching(other.gameObject))
            {
                // VERY IMPORTANT: If player is currently carrying this object, force drop it
                PlayerCarry carry = PlayerCarry.Instance;
                if (carry != null && carry.HeldItem != null)
                {
                    if (carry.HeldItem.gameObject == other.gameObject)
                    {
                        carry.TakeHeldItem();
                    }
                }

                // Enable Rigidbody Physics (Unlock the door)
                if (cabinetRigidbody != null)
                {
                    cabinetRigidbody.isKinematic = false;
                    cabinetRigidbody.WakeUp();
                    gameObject.layer = LayerMask.NameToLayer("Interactable");
                }

                if (unlockSound != null)
                    AudioSource.PlayClipAtPoint(unlockSound, transform.position);

                OnUnlocked?.Invoke();

                if (consumeRequiredItem)
                {
                    Destroy(other.gameObject);
                }

                _used = true;
            }
        }

        private bool IsMatching(GameObject candidate)
        {
            if (candidate == null) return false;

            // 1. Direct GameObject Reference (Highest Priority)
            if (requiredObject != null)
            {
                if (candidate == requiredObject)
                    return true;

                if (candidate.transform.IsChildOf(requiredObject.transform))
                    return true;
            }


            return false;
        }
    }
}
