using UnityEngine;
using UnityEngine.Events;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.Environment
{
    /// <summary>
    /// Enables a Rigidbody's physics when the required object touches this trigger/collider.
    /// Cabinet door stays kinematic (locked) until the key item is inserted.
    /// If the player is carrying that exact object, it is force-released first.
    /// </summary>
    public class RigidbodyUnlockByCollision : MonoBehaviour
    {
        [Header("Target Rigidbody")]
        [Tooltip("The Rigidbody to unlock (usually the cabinet door with the HingeJoint).")]
        [SerializeField] private Rigidbody cabinetRigidbody;

        [Header("Required Item")]
        [Tooltip("Specific pickup GameObject reference (children count too).")]
        [SerializeField] private GameObject requiredObject;
        [Tooltip("Optional fallback: any object with this tag unlocks. Leave empty to disable.")]
        [SerializeField] private string requiredTag = "";

        [Header("Settings")]
        [SerializeField] private bool consumeRequiredItem = true;
        [SerializeField] private bool onlyOnce = true;
        [SerializeField] private bool disableColliderAfterUse = true;
        [Tooltip("Layer applied to the door after unlocking. Empty = keep current layer.")]
        [SerializeField] private string unlockedLayer = "Interactable";
        [SerializeField] private AudioClip unlockSound;

        [Header("Events")]
        public UnityEvent OnUnlocked;

        private bool _used;

        private void Awake()
        {
            if (cabinetRigidbody == null) cabinetRigidbody = GetComponentInParent<Rigidbody>();
            if (cabinetRigidbody != null) cabinetRigidbody.isKinematic = true; // locked at start
        }

        private void OnTriggerEnter(Collider other) => TryUnlock(other.gameObject);
        private void OnCollisionEnter(Collision collision) => TryUnlock(collision.gameObject);

        private void TryUnlock(GameObject candidate)
        {
            if (_used && onlyOnce) return;

            var matched = ResolveMatch(candidate);
            if (matched == null) return;


            Unlock();

            if (unlockSound != null)
                AudioSource.PlayClipAtPoint(unlockSound, transform.position);

            OnUnlocked?.Invoke();

            if (consumeRequiredItem) Destroy(matched);

            _used = true;
            if (onlyOnce && disableColliderAfterUse)
            {
                foreach (var col in GetComponents<Collider>())
                    col.enabled = false;
            }
        }

        private void Unlock()
        {
            if (cabinetRigidbody == null) return;

            cabinetRigidbody.isKinematic = false;
            cabinetRigidbody.WakeUp();

            if (!string.IsNullOrEmpty(unlockedLayer))
            {
                int layer = LayerMask.NameToLayer(unlockedLayer);
                if (layer >= 0) cabinetRigidbody.gameObject.layer = layer;
                else Debug.LogWarning($"[RigidbodyUnlockByCollision] Layer '{unlockedLayer}' does not exist.", this);
            }
        }

        private GameObject ResolveMatch(GameObject candidate)
        {
            if (candidate == null) return null;

            if (requiredObject != null &&
                (candidate == requiredObject || candidate.transform.IsChildOf(requiredObject.transform)))
                return requiredObject;

            if (!string.IsNullOrEmpty(requiredTag) && candidate.CompareTag(requiredTag))
                return candidate;

            return null;
        }
    }
}
