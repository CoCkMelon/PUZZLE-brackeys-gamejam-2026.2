using UnityEngine;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.Entities
{
    /// <summary>
    /// Best of old Interactable + IInteractable - now unified.
    /// Works with PlayerCarry (best carry) and PlayerInteraction.
    /// </summary>
    public class Interactable : MonoBehaviour, IInteractable
    {
        [SerializeField] private string displayName;

        [Header("Audio")]
        [Tooltip("Sound played when this object is picked up.")]
        public AudioClip pickupSound;
        [Tooltip("Sound played when this object is dropped.")]
        public AudioClip dropSound;
        [Tooltip("Sound played when this object is thrown.")]
        public AudioClip throwSound;

        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;

        // IInteractable implementation - now unified with PlayerInteraction
        public string InteractionPrompt => IsHeld ? "" : $"Press [E] to Pick Up {DisplayName}";
        public bool CanInteract => !IsHeld;

        private bool IsHeld
        {
            get
            {
                var pc = PlayerCarry.Instance;
                if (pc == null) return false;
                if (pc.HeldInteractable == this) return true;
                // Also check if held as PlaceableItem? No, separate
                return false;
            }
        }

        public void OnInteract()
        {
            var pc = PlayerCarry.Instance;
            if (pc != null)
            {
                // If already carrying something, drop it first? PlayerCarry handles
                if (!pc.IsCarrying)
                    pc.TryPickUp(this);
            }
        }

        private void OnDisable()
        {
            // If disabled while held, notify carry
            var pc = PlayerCarry.Instance;
            if (pc != null && pc.HeldInteractable == this)
                pc.NotifyHeldInteractableLost(this);
        }
    }
}
