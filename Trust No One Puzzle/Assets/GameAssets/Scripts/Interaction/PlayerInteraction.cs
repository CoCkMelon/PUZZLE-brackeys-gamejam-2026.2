using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GameAssets.Scripts.Interaction
{
    /// <summary>
    /// Handles player interaction detection via raycast and input binding.
    /// Attach this to the Player root (same level as PlayerController).
    /// Requires the camera transform reference and the Interact InputActionReference.
    /// </summary>
    public class PlayerInteraction : MonoBehaviour
    {
        [Header("Input")]
        [SerializeField] private InputActionReference interactAction;

        [Header("Raycast Settings")]
        [Tooltip("The camera (or aim origin) from which the interaction ray is cast.")]
        [SerializeField] private Transform rayOrigin;

        [Tooltip("Maximum distance at which the player can interact with objects.")]
        [SerializeField] private float interactRange = 3f;

        [Tooltip("Layers that can be interacted with. Set your interactable objects to one of these layers.")]
        [SerializeField] private LayerMask interactableMask = ~0; // default: everything

        [Header("UI (Optional)")]
        [Tooltip("Assign a UI Text/TextMeshPro element to display the interaction prompt. Can be left empty.")]
        [SerializeField] private TMPro.TextMeshProUGUI promptText;

        // The interactable currently in the crosshair
        private IInteractable _currentTarget;

        // Reusable raycast buffer for DetectInteractable
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        private void OnEnable()
        {
            interactAction.action.Enable();
            // Use 'started' instead of 'performed' because the Interact action
            // has a Hold interaction — 'performed' only fires after holding the
            // button for the hold duration. 'started' fires immediately on press.
            interactAction.action.started += OnInteractPerformed;
        }

        private void OnDisable()
        {
            interactAction.action.started -= OnInteractPerformed;
            interactAction.action.Disable();
        }

        private void Update()
        {
            DetectInteractable();
            UpdatePromptUI();
        }

        /// <summary>
        /// Casts a ray from the camera and checks for IInteractable on hit objects.
        /// The ray ignores the item currently being carried (it keeps its colliders
        /// for physics carrying, so it must not block the crosshair) and behaves
        /// like a single raycast otherwise: the first other hit decides.
        /// </summary>
        private void DetectInteractable()
        {
            var carried = PlayerCarry.Instance != null ? PlayerCarry.Instance.HeldItem : null;
            var count = Physics.RaycastNonAlloc(rayOrigin.position, rayOrigin.forward, _hits, interactRange, interactableMask);

            // Sort the used range by distance (insertion sort, count is tiny).
            for (var i = 1; i < count; i++)
            {
                var hit = _hits[i];
                var j = i - 1;
                while (j >= 0 && _hits[j].distance > hit.distance)
                {
                    _hits[j + 1] = _hits[j];
                    j--;
                }
                _hits[j + 1] = hit;
            }

            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];

                // Never blocked by the item in our own hands.
                if (carried != null && hit.collider != null && hit.collider.transform.IsChildOf(carried.transform))
                    continue;

                // Try the hit object first, then walk up to the parent
                var interactable = hit.collider.GetComponent<IInteractable>()
                                ?? hit.collider.GetComponentInParent<IInteractable>();

                _currentTarget = interactable != null && interactable.CanInteract
                    ? interactable
                    : null;
                return;
            }

            _currentTarget = null;
        }

        /// <summary>
        /// Shows/hides the interaction prompt on-screen.
        /// </summary>
        private void UpdatePromptUI()
        {
            if (promptText == null) return;

            if (_currentTarget != null)
            {
                promptText.gameObject.SetActive(true);
                promptText.text = _currentTarget.InteractionPrompt;
            }
            else
            {
                promptText.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Fires when the player presses the Interact button (E / Gamepad Y).
        /// </summary>
        private void OnInteractPerformed(InputAction.CallbackContext ctx)
        {
            if (_currentTarget != null && _currentTarget.CanInteract)
            {
                _currentTarget.OnInteract();
            }
        }

        #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (rayOrigin == null) return;
            Gizmos.color = _currentTarget != null ? Color.green : Color.yellow;
            Gizmos.DrawRay(rayOrigin.position, rayOrigin.forward * interactRange);
        }
        #endif
    }
}
