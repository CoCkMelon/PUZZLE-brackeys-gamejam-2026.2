using UnityEngine;
using UnityEngine.InputSystem;
using GameAssets.Scripts.Entities;
using GameAssets.Scripts.Puzzle;
using TMPro;

namespace GameAssets.Scripts.Entities.Player
{
    /// <summary>
    /// FPP camera only - carry logic removed, kept as best is PlayerCarry.
    /// Handles mouse look, reticle, and highlight. Disables look when PlayerCarry is in spatial shove mode.
    /// </summary>
    public class FPPCameraController : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionReference lookAction;

        [Header("Camera settings")] 
        [SerializeField] private float sensitivity = 2f;
        [SerializeField] private Vector2 pitchLimits = new Vector2(-89f, 89f);

        [Header("Reticle")]
        [SerializeField] private bool showReticle = true;
        [SerializeField, Min(1f)] private float reticleSize = 14f;
        [SerializeField, Min(1f)] private float reticleThickness = 2f;
        [SerializeField] private Color reticleColor = Color.white;

        [Header("Target Highlight")]
        [SerializeField] private Material targetHighlightMaterial;
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionLayers = ~0;
        [Tooltip("All layers that can block the reticle ray, such as walls, doors, and furniture.")]
        [SerializeField] private LayerMask obstructionLayers = ~0;

        [Header("Target Name Label")]
        [SerializeField] private TMP_FontAsset nameLabelFont;
        [SerializeField, Min(0f)] private float nameLabelOffset = 24f;

        private Vector2 _input;
        private float _pitch, _yaw;
        private Camera _camera;
        private TextMeshProUGUI _targetNameLabel;
        private GameObject _highlightCanvas;
        private Interactable _highlightedInteractable;
        private Renderer[] _highlightedRenderers;
        private Material[][] _originalMaterials;
        private CharacterController _playerCharacterController;

        public Ray ReticleRay => _camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));

        // Compatibility shims - carry now handled by PlayerCarry, keep properties for old code (OpenableFurniture)
        public Interactable CarriedInteractable
        {
            get
            {
                // Old code asked FPP for carried object. Now delegate to PlayerCarry if it holds an Interactable
                var pc = PlayerCarry.Instance;
                if (pc != null && pc.HeldInteractable != null)
                    return pc.HeldInteractable;
                return null;
            }
        }

        public void ReleaseCarriedObject()
        {
            // Old API used when key consumed. Delegate to PlayerCarry
            var pc = PlayerCarry.Instance;
            if (pc != null && pc.IsCarrying)
                pc.DropInWorld();
        }

        private void OnEnable()
        {
            _camera = GetComponentInChildren<Camera>();
            _playerCharacterController = GetComponentInParent<CharacterController>();
            CreateTargetHighlight();
            if (lookAction != null)
            {
                lookAction.action.Enable();
                lookAction.action.performed += ActionOnLook;
                lookAction.action.canceled += ActionOnLook;
            }
            LockCursor();
        }

        private void OnDisable()
        {
            if (lookAction != null)
            {
                lookAction.action.performed -= ActionOnLook;
                lookAction.action.canceled -= ActionOnLook;
                lookAction.action.Disable();
            }
            if (_highlightCanvas != null)
            {
                Destroy(_highlightCanvas);
                _highlightCanvas = null;
                _targetNameLabel = null;
            }
            ClearHologramHighlight();
        }

        private void LockCursor(bool value = true)
        {
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !value;
        }

        private void Update()
        {
            RotationHandler();
            UpdateTargetHighlight();
        }

        private void RotationHandler()
        {
            // Do not rotate camera while PlayerCarry is in spatial shove mode (MMB hold)
            var pc = PlayerCarry.Instance;
            if (pc != null && pc.SpatialModeActive)
                return;

            _yaw += _input.x * sensitivity;
            _pitch -= _input.y * sensitivity;
            _pitch = Mathf.Clamp(_pitch, pitchLimits.x, pitchLimits.y);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
        }

        private void OnGUI()
        {
            if (!showReticle || _camera == null || !_camera.enabled) return;
            var previousColor = GUI.color;
            GUI.color = reticleColor;
            var centreX = (Screen.width - reticleThickness) * 0.5f;
            var centreY = (Screen.height - reticleThickness) * 0.5f;
            var halfSize = reticleSize * 0.5f;
            GUI.DrawTexture(new Rect(centreX - halfSize, centreY, reticleSize, reticleThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centreX, centreY - halfSize, reticleThickness, reticleSize), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private void CreateTargetHighlight()
        {
            _highlightCanvas = new GameObject("Target Highlight Canvas", typeof(Canvas));
            var canvas = _highlightCanvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;
            if (nameLabelFont == null) return;
            var labelObject = new GameObject("Target Name", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(_highlightCanvas.transform, false);
            _targetNameLabel = labelObject.GetComponent<TextMeshProUGUI>();
            _targetNameLabel.font = nameLabelFont;
            _targetNameLabel.fontSize = 24f;
            _targetNameLabel.alignment = TextAlignmentOptions.Center;
            _targetNameLabel.textWrappingMode = TextWrappingModes.NoWrap;
            _targetNameLabel.raycastTarget = false;
            _targetNameLabel.enabled = false;
        }

        private void UpdateTargetHighlight()
        {
            // Don't highlight while carrying (PlayerCarry handles its own)
            var pc = PlayerCarry.Instance;
            if (pc != null && pc.IsCarrying)
            {
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            if (!TryGetTargetedInteractable(out _, out var interactable) || !TryGetScreenBounds(interactable, out var screenBounds))
            {
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            ApplyHologramHighlight(interactable);
            if (_targetNameLabel != null)
            {
                var labelTransform = _targetNameLabel.rectTransform;
                labelTransform.anchorMin = labelTransform.anchorMax = new Vector2(0.5f, 0.5f);
                labelTransform.anchoredPosition = new Vector2(screenBounds.center.x, screenBounds.yMax + nameLabelOffset) - new Vector2(Screen.width, Screen.height) * 0.5f;
                labelTransform.sizeDelta = new Vector2(Mathf.Max(200f, screenBounds.width), 40f);
                _targetNameLabel.text = interactable.DisplayName;
            }
            SetHighlightVisible(true);
        }

        private bool TryGetTargetedInteractable(out RaycastHit hit, out Interactable interactable)
        {
            interactable = null;
            if (!TryGetFirstNonPlayerRaycast(out hit)) return false;
            interactable = hit.collider.GetComponentInParent<Interactable>();
            return interactable != null && (interactionLayers.value & (1 << hit.collider.gameObject.layer)) != 0;
        }

        private bool TryGetFirstNonPlayerRaycast(out RaycastHit closestHit)
        {
            var hits = Physics.RaycastAll(ReticleRay, interactionDistance, obstructionLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            closestHit = default;
            foreach (var hit in hits)
            {
                if (!IsPlayerCollider(hit.collider) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    closestHit = hit;
                }
            }
            return nearest < float.MaxValue;
        }

        private bool IsPlayerCollider(Collider collider)
        {
            if (collider == null || _playerCharacterController == null) return false;
            var playerTransform = _playerCharacterController.transform;
            return collider == _playerCharacterController ||
                   collider.transform.IsChildOf(playerTransform) ||
                   playerTransform.IsChildOf(collider.transform);
        }

        private bool TryGetScreenBounds(Interactable interactable, out Rect screenBounds)
        {
            var renderers = interactable.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) { screenBounds = default; return false; }
            var worldBounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) worldBounds.Encapsulate(renderers[i].bounds);
            var centre = worldBounds.center;
            var extents = worldBounds.extents;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var point = centre + Vector3.Scale(extents, new Vector3(x, y, z));
                var screenPoint = _camera.WorldToScreenPoint(point);
                if (screenPoint.z <= 0f) { screenBounds = default; return false; }
                min = Vector2.Min(min, screenPoint);
                max = Vector2.Max(max, screenPoint);
            }
            screenBounds = new Rect(min, max - min);
            return true;
        }

        private void ApplyHologramHighlight(Interactable interactable)
        {
            if (targetHighlightMaterial == null) { ClearHologramHighlight(); return; }
            if (_highlightedInteractable == interactable) return;
            ClearHologramHighlight();
            _highlightedInteractable = interactable;
            _highlightedRenderers = interactable.GetComponentsInChildren<Renderer>(true);
            _originalMaterials = new Material[_highlightedRenderers.Length][];
            for (var i = 0; i < _highlightedRenderers.Length; i++)
            {
                var renderer = _highlightedRenderers[i];
                _originalMaterials[i] = renderer.sharedMaterials;
                var hologramMaterials = new Material[_originalMaterials[i].Length];
                for (var mi = 0; mi < hologramMaterials.Length; mi++) hologramMaterials[mi] = targetHighlightMaterial;
                renderer.sharedMaterials = hologramMaterials;
            }
        }

        private void ClearHologramHighlight()
        {
            if (_highlightedRenderers == null || _originalMaterials == null) return;
            for (var i = 0; i < _highlightedRenderers.Length; i++)
                if (_highlightedRenderers[i] != null)
                    _highlightedRenderers[i].sharedMaterials = _originalMaterials[i];
            _highlightedInteractable = null;
            _highlightedRenderers = null;
            _originalMaterials = null;
        }

        private void SetHighlightVisible(bool visible)
        {
            if (_targetNameLabel != null) _targetNameLabel.enabled = visible;
        }

        private void ActionOnLook(InputAction.CallbackContext ctx) => _input = ctx.ReadValue<Vector2>();
    }
}
