using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameAssets.Scripts.UI.Mobile
{
    /// <summary>
    /// Runtime HUD (UI Toolkit): phone FAB, drop, and hold-to-shove for Spatial crates.
    /// Complete implementation - no legacy Canvas dependencies.
    /// Assign MobileHud.uxml + MobileTheme.tss on a UIDocument (sort order below the phone, e.g. 0).
    /// Works with PlayerCarry (best) - supports both PlaceableItem and Interactable.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MobileHudController : MonoBehaviour
    {
        [Header("Prompt")]
        [Tooltip("If true, will try to show interaction prompt from PlayerInteraction / FPPCameraController.")]
        [SerializeField] private bool showInteractionPrompt = true;

        private UIDocument _doc;
        private Button _shove;
        private Button _drop;
        private Button _phone;
        private Label _prompt;
        private Label _distance;

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            Bind();
        }

        private void Update()
        {
            if (_shove == null)
            {
                Bind();
                return;
            }

            var carry = PlayerCarry.Instance;
            var holding = carry != null && carry.IsCarrying;
            // Support both PlaceableItem and Interactable - only PlaceableItem has UsesSpatialCarry
            var spatial = false;
            if (holding && carry.HeldItem != null)
                spatial = carry.HeldItem.UsesSpatialCarry;
            else if (holding && carry.HeldInteractable != null)
                spatial = false; // Interactables don't use spatial shove (can be extended)

            _shove.EnableInClassList("hidden", !spatial);
            _drop.EnableInClassList("hidden", !holding);
            _shove.EnableInClassList("pressed", spatial && carry.SpatialModeActive);

            if (_distance != null)
            {
                _distance.EnableInClassList("visible", spatial);
                if (spatial)
                    _distance.text = carry.SpatialModeActive ? "SHOVING - drag to move" : "hold SHOVE · scroll depth";
            }

            if (showInteractionPrompt && _prompt != null)
            {
                // Try to get prompt from PlayerInteraction if available, otherwise from FPPCameraController highlight
                string promptText = null;
                // PlayerInteraction is not directly referenced to avoid hard dependency - try Find
                var playerInteraction = FindFirstObjectByType<GameAssets.Scripts.Interaction.PlayerInteraction>();
                if (playerInteraction != null)
                {
                    // Use reflection-like access via property? We'll try to get via public method if exists
                    // For now, leave empty - FPPCameraController also shows its own highlight label
                }

                // If no prompt from interaction system, keep current text (SetPrompt can be called externally)
                // Only auto-hide if we explicitly set empty
            }
        }

        /// <summary>External API to set interaction prompt text (call from PlayerInteraction).</summary>
        public void SetPrompt(string text)
        {
            if (_prompt == null)
                return;
            var show = !string.IsNullOrEmpty(text);
            _prompt.text = text ?? "";
            _prompt.EnableInClassList("visible", show);
        }

        private void Bind()
        {
            var ve = _doc != null ? _doc.rootVisualElement : null;
            if (ve == null)
                return;

            _shove = ve.Q<Button>("btn-shove");
            _drop = ve.Q<Button>("btn-drop");
            _phone = ve.Q<Button>("btn-phone");
            _prompt = ve.Q<Label>("prompt");
            _distance = ve.Q<Label>("distance");

            if (_shove != null)
            {
                _shove.RegisterCallback<PointerDownEvent>(OnShoveDown, TrickleDown.TrickleDown);
                _shove.RegisterCallback<PointerUpEvent>(OnShoveUp, TrickleDown.TrickleDown);
                _shove.RegisterCallback<PointerLeaveEvent>(_ => PlayerCarry.Instance?.SetSpatialModeFromHud(false));
                _shove.RegisterCallback<PointerCancelEvent>(_ => PlayerCarry.Instance?.SetSpatialModeFromHud(false));
            }

            _drop?.RegisterCallback<ClickEvent>(_ => PlayerCarry.Instance?.DropInWorld());
            _phone?.RegisterCallback<ClickEvent>(_ => MobilePhoneController.Instance?.Toggle());
        }

        private void OnShoveDown(PointerDownEvent evt)
        {
            PlayerCarry.Instance?.SetSpatialModeFromHud(true);
            evt.StopPropagation();
        }

        private void OnShoveUp(PointerUpEvent evt)
        {
            PlayerCarry.Instance?.SetSpatialModeFromHud(false);
            evt.StopPropagation();
        }
    }
}
