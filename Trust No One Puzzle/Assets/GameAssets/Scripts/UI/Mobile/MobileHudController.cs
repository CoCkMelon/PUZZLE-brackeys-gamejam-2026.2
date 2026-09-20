using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameAssets.Scripts.UI.Mobile
{
    [RequireComponent(typeof(UIDocument))]
    public class MobileHudController : MonoBehaviour
    {
        [Header("Prompt")]
        [Tooltip("If true, will show interaction prompt from PlayerInteraction / FPPCameraController.")]
        [SerializeField] private bool showInteractionPrompt = true;

        private UIDocument _doc;
        private Button _shove;
        private Button _drop;
        private Button _phone;
        private Label _prompt;
        private Label _distance;
        private string _lastPromptText;

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            Bind();
        }

        private void Update()
        {
            if (_shove == null) { Bind(); return; }

            var carry = PlayerCarry.Instance;
            var holding = carry != null && carry.IsCarrying;
            // PlaceableItem removed: every carried Interactable supports spatial shove.
            var spatial = holding;
            var shoving = holding && carry.SpatialModeActive;

            _shove.EnableInClassList("hidden", !spatial);
            _drop.EnableInClassList("hidden", !holding);
            _shove.EnableInClassList("pressed", shoving);

            if (_distance != null)
            {
                _distance.EnableInClassList("visible", spatial);
                if (spatial)
                    _distance.text = shoving ? "SHOVING - drag to move" : "hold SHOVE - scroll depth";
            }

            if (!showInteractionPrompt && _prompt != null)
                _prompt.EnableInClassList("visible", false);
        }

        public void SetPrompt(string text)
        {
            _lastPromptText = text;
            if (_prompt == null) return;
            if (!showInteractionPrompt) { _prompt.EnableInClassList("visible", false); return; }
            var show = !string.IsNullOrEmpty(text);
            _prompt.text = text ?? "";
            _prompt.EnableInClassList("visible", show);
        }

        private void Bind()
        {
            var ve = _doc != null ? _doc.rootVisualElement : null;
            if (ve == null) return;

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

            if (!string.IsNullOrEmpty(_lastPromptText)) SetPrompt(_lastPromptText);
        }

        private void OnShoveDown(PointerDownEvent evt) { PlayerCarry.Instance?.SetSpatialModeFromHud(true); evt.StopPropagation(); }
        private void OnShoveUp(PointerUpEvent evt) { PlayerCarry.Instance?.SetSpatialModeFromHud(false); evt.StopPropagation(); }
    }
}
