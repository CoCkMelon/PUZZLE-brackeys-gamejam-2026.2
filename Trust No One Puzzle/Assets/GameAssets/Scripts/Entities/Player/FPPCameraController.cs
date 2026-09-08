using UnityEngine;
using UnityEngine.InputSystem;

using GameAssets.Scripts.Entities;
using TMPro;

namespace GameAssets.Scripts.Entities.Player
{
    public class FPPCameraController : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionReference lookAction;

        [Header("Camera settings")] 
        [SerializeField] private float sensitivity;
        [SerializeField] private Vector2 pitchLimits;

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

        [Header("Carry")]
        [SerializeField] private Key interactKey = Key.E;
        [SerializeField] private Key dropKey = Key.G;
        [SerializeField] private Transform carryLocation;
        [SerializeField, Range(0.05f, 1f)] private float carriedScaleMultiplier = 0.65f;
        [SerializeField, Min(0f)] private float carryLerpSpeed = 12f;
        [SerializeField, Min(0f)] private float throwForce = 15f;

        private Vector2 _input;
        private float _pitch, _yaw;
        private Camera _camera;
        private TextMeshProUGUI _targetNameLabel;
        private GameObject _highlightCanvas;
        private Interactable _highlightedInteractable;
        private Renderer[] _highlightedRenderers;
        private Material[][] _originalMaterials;
        private Interactable _carriedInteractable;
        private Interactable _droppingInteractable;
        private Rigidbody _carriedRigidbody;
        private CharacterController _playerCharacterController;
        private Collider[] _carriedColliders;
        private bool[] _carriedColliderStates;
        private bool _carriedRigidbodyWasKinematic;
        private Vector3 _carriedOriginalScale;
        private Vector3 _dropTargetPosition;

        /// <summary>
        /// The first-person camera's ray through the reticle at the centre of the screen.
        /// Use this as the origin for pickup and drop raycasts.
        /// </summary>
        public Ray ReticleRay => _camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));

        /// <summary>The interactable currently held in front of the camera, or null when the hands are empty.</summary>
        public Interactable CarriedInteractable => _carriedInteractable;

        /// <summary>
        /// Lets go of the carried object immediately, without the drop animation or throw impulse.
        /// Used when the object is taken away by the world, e.g. a key consumed by a lock.
        /// </summary>
        public void ReleaseCarriedObject()
        {
            if (_carriedInteractable == null && _droppingInteractable == null)
            {
                return;
            }

            ReleaseCarryImmediately();
        }
    
        private void OnEnable()
        {
            _camera = GetComponentInChildren<Camera>();
            _playerCharacterController = GetComponentInParent<CharacterController>();
            CreateTargetHighlight();
            lookAction.action.Enable();
        
            lookAction.action.performed += ActionOnLook;
            lookAction.action.canceled += ActionOnLook;
        
            LockCursor();
        }

        private void OnDisable()
        {
            lookAction.action.performed -= ActionOnLook;
            lookAction.action.canceled -= ActionOnLook;
        
            lookAction.action.Disable();
            ReleaseCarryImmediately();

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
            HandleInteraction();
            UpdateTargetHighlight();
        }

        private void RotationHandler()
        {
            _yaw += _input.x * sensitivity;
            _pitch -= _input.y * sensitivity;
            _pitch = Mathf.Clamp(_pitch, pitchLimits.x, pitchLimits.y);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
        }

        private void OnGUI()
        {
            if (!showReticle || _camera == null || !_camera.enabled)
            {
                return;
            }

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

            if (nameLabelFont == null)
            {
                Debug.LogWarning("Assign a TextMesh Pro font to Name Label Font on FPPCameraController.", this);
                return;
            }

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
            if (_carriedInteractable != null)
            {
                MoveCarriedObject();
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            if (_droppingInteractable != null)
            {
                MoveDroppedObject();
                ClearHologramHighlight();
                SetHighlightVisible(false);
                return;
            }

            if (!TryGetTargetedInteractable(out _, out var interactable) ||
                !TryGetScreenBounds(interactable, out var screenBounds))
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
                labelTransform.anchoredPosition = new Vector2(screenBounds.center.x, screenBounds.yMax + nameLabelOffset) -
                                                new Vector2(Screen.width, Screen.height) * 0.5f;
                labelTransform.sizeDelta = new Vector2(Mathf.Max(200f, screenBounds.width), 40f);
                _targetNameLabel.text = interactable.DisplayName;
            }
            SetHighlightVisible(true);
        }

        private void HandleInteraction()
        {
            if (Keyboard.current == null || _droppingInteractable != null)
            {
                return;
            }

            if (_carriedInteractable != null)
            {
                if (Keyboard.current[dropKey].wasPressedThisFrame)
                {
                    BeginDrop();
                }
                else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    ThrowCarriedObject();
                }
                return;
            }

            if (!Keyboard.current[interactKey].wasPressedThisFrame)
            {
                return;
            }

            if (TryGetTargetedInteractable(out _, out var interactable))
            {
                PickUpObject(interactable);
            }
        }

        private bool TryGetTargetedInteractable(out RaycastHit hit, out Interactable interactable)
        {
            interactable = null;

            // The first non-player collider hit is the closest real obstacle in the reticle's path.
            // An interactable behind any wall, door, or prop therefore cannot be selected.
            if (!TryGetFirstNonPlayerRaycast(out hit))
            {
                return false;
            }

            interactable = hit.collider.GetComponentInParent<Interactable>();
            return interactable != null && (interactionLayers.value & (1 << hit.collider.gameObject.layer)) != 0;
        }

        private void PickUpObject(Interactable interactable)
        {
            if (interactable.pickupSound != null)
            {
                AudioSource.PlayClipAtPoint(interactable.pickupSound, interactable.transform.position);
            }

            _carriedInteractable = interactable;
            _carriedRigidbody = interactable.GetComponentInChildren<Rigidbody>();
            _carriedOriginalScale = interactable.transform.localScale;
            _carriedColliders = interactable.GetComponentsInChildren<Collider>(true);
            DisableCarriedColliders();
            SetCarriedObjectPlayerCollisionIgnored(true);

            if (_carriedRigidbody != null)
            {
                _carriedRigidbodyWasKinematic = _carriedRigidbody.isKinematic;
                _carriedRigidbody.isKinematic = true;
            }

            ClearHologramHighlight();
            SetHighlightVisible(false);
        }

        private void MoveCarriedObject()
        {
            var carriedTransform = _carriedInteractable.transform;
            var targetPosition = carryLocation != null ? carryLocation.position : ReticleRay.GetPoint(interactionDistance);
            var targetRotation = carryLocation != null ? carryLocation.rotation : _camera.transform.rotation;
            var lerpFactor = 1f - Mathf.Exp(-carryLerpSpeed * Time.deltaTime);

            carriedTransform.position = Vector3.Lerp(carriedTransform.position, targetPosition, lerpFactor);
            carriedTransform.rotation = Quaternion.Slerp(carriedTransform.rotation, targetRotation, lerpFactor);
            carriedTransform.localScale = Vector3.Lerp(
                carriedTransform.localScale,
                _carriedOriginalScale * carriedScaleMultiplier,
                lerpFactor);
        }

        private void BeginDrop()
        {
            if (_carriedInteractable.dropSound != null)
            {
                AudioSource.PlayClipAtPoint(_carriedInteractable.dropSound, _carriedInteractable.transform.position);
            }

            _droppingInteractable = _carriedInteractable;
            _dropTargetPosition = GetDropTargetPosition();
            _carriedInteractable = null;
        }

        private void ThrowCarriedObject()
        {
            if (_carriedInteractable.throwSound != null)
            {
                AudioSource.PlayClipAtPoint(_carriedInteractable.throwSound, _camera.transform.position);
            }

            var thrownTransform = _carriedInteractable.transform;
            thrownTransform.localScale = _carriedOriginalScale;

            if (_carriedRigidbody != null)
            {
                _carriedRigidbody.isKinematic = _carriedRigidbodyWasKinematic;
                _carriedRigidbody.AddForce(_camera.transform.forward * throwForce, ForceMode.Impulse);
            }

            RestoreCarriedColliders();
            _carriedInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
            _carriedColliderStates = null;
        }

        private Vector3 GetDropTargetPosition()
        {
            var raycastHits = Physics.RaycastAll(ReticleRay, interactionDistance, obstructionLayers,
                QueryTriggerInteraction.Ignore);
            var nearestDistance = float.MaxValue;
            var targetPosition = ReticleRay.GetPoint(interactionDistance);

            foreach (var hit in raycastHits)
            {
                if (!IsPlayerCollider(hit.collider) && !IsCarriedCollider(hit.collider) && hit.distance < nearestDistance)
                {
                    nearestDistance = hit.distance;
                    targetPosition = hit.point;
                }
            }

            return targetPosition;
        }

        private bool TryGetFirstNonPlayerRaycast(out RaycastHit closestHit)
        {
            var raycastHits = Physics.RaycastAll(ReticleRay, interactionDistance, obstructionLayers,
                QueryTriggerInteraction.Ignore);
            var nearestDistance = float.MaxValue;
            closestHit = default;

            foreach (var hit in raycastHits)
            {
                if (!IsPlayerCollider(hit.collider) && hit.distance < nearestDistance)
                {
                    nearestDistance = hit.distance;
                    closestHit = hit;
                }
            }

            return nearestDistance < float.MaxValue;
        }

        private void MoveDroppedObject()
        {
            var droppedTransform = _droppingInteractable.transform;
            var lerpFactor = 1f - Mathf.Exp(-carryLerpSpeed * Time.deltaTime);

            droppedTransform.position = Vector3.Lerp(droppedTransform.position, _dropTargetPosition, lerpFactor);
            droppedTransform.localScale = Vector3.Lerp(droppedTransform.localScale, _carriedOriginalScale, lerpFactor);

            if (Vector3.SqrMagnitude(droppedTransform.position - _dropTargetPosition) > 0.0001f ||
                Vector3.SqrMagnitude(droppedTransform.localScale - _carriedOriginalScale) > 0.0001f)
            {
                return;
            }

            droppedTransform.position = _dropTargetPosition;
            droppedTransform.localScale = _carriedOriginalScale;

            if (_carriedRigidbody != null)
            {
                _carriedRigidbody.isKinematic = _carriedRigidbodyWasKinematic;
            }

            RestoreCarriedColliders();
            _droppingInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
            _carriedColliderStates = null;
        }

        private void ReleaseCarryImmediately()
        {
            var heldTransform = _carriedInteractable != null
                ? _carriedInteractable.transform
                : _droppingInteractable != null ? _droppingInteractable.transform : null;

            if (heldTransform != null)
            {
                heldTransform.localScale = _carriedOriginalScale;
            }

            if (_carriedRigidbody != null)
            {
                _carriedRigidbody.isKinematic = _carriedRigidbodyWasKinematic;
            }

            RestoreCarriedColliders();
            _carriedInteractable = null;
            _droppingInteractable = null;
            _carriedRigidbody = null;
            _carriedColliders = null;
            _carriedColliderStates = null;
        }

        private void DisableCarriedColliders()
        {
            if (_carriedColliders == null)
            {
                return;
            }

            _carriedColliderStates = new bool[_carriedColliders.Length];
            for (var i = 0; i < _carriedColliders.Length; i++)
            {
                if (_carriedColliders[i] != null)
                {
                    _carriedColliderStates[i] = _carriedColliders[i].enabled;
                    _carriedColliders[i].enabled = false;
                }
            }
        }

        private void RestoreCarriedColliders()
        {
            SetCarriedObjectPlayerCollisionIgnored(false);

            if (_carriedColliders == null || _carriedColliderStates == null)
            {
                return;
            }

            for (var i = 0; i < _carriedColliders.Length; i++)
            {
                if (_carriedColliders[i] != null)
                {
                    _carriedColliders[i].enabled = _carriedColliderStates[i];
                }
            }
        }

        private bool IsCarriedCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            if (_carriedRigidbody != null && collider.attachedRigidbody == _carriedRigidbody)
            {
                return true;
            }

            if (_carriedColliders != null)
            {
                foreach (var carriedCollider in _carriedColliders)
                {
                    if (collider == carriedCollider)
                    {
                        return true;
                    }
                }
            }

            return collider.GetComponentInParent<Interactable>() == _droppingInteractable;
        }

        private bool IsPlayerCollider(Collider collider)
        {
            if (collider == null || _playerCharacterController == null)
            {
                return false;
            }

            var playerTransform = _playerCharacterController.transform;
            return collider == _playerCharacterController ||
                   collider.transform.IsChildOf(playerTransform) ||
                   playerTransform.IsChildOf(collider.transform);
        }

        private void SetCarriedObjectPlayerCollisionIgnored(bool ignored)
        {
            if (_playerCharacterController == null || _carriedColliders == null)
            {
                return;
            }

            foreach (var carriedCollider in _carriedColliders)
            {
                if (carriedCollider != null)
                {
                    Physics.IgnoreCollision(_playerCharacterController, carriedCollider, ignored);
                }
            }
        }

        private bool TryGetScreenBounds(Interactable interactable, out Rect screenBounds)
        {
            var renderers = interactable.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                screenBounds = default;
                return false;
            }

            var worldBounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                worldBounds.Encapsulate(renderers[i].bounds);
            }

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
                if (screenPoint.z <= 0f)
                {
                    screenBounds = default;
                    return false;
                }

                min = Vector2.Min(min, screenPoint);
                max = Vector2.Max(max, screenPoint);
            }

            screenBounds = new Rect(min, max - min);
            return true;
        }

        private void ApplyHologramHighlight(Interactable interactable)
        {
            if (targetHighlightMaterial == null)
            {
                ClearHologramHighlight();
                return;
            }

            if (_highlightedInteractable == interactable)
            {
                return;
            }

            ClearHologramHighlight();

            _highlightedInteractable = interactable;
            _highlightedRenderers = interactable.GetComponentsInChildren<Renderer>(true);
            _originalMaterials = new Material[_highlightedRenderers.Length][];

            for (var i = 0; i < _highlightedRenderers.Length; i++)
            {
                var renderer = _highlightedRenderers[i];
                _originalMaterials[i] = renderer.sharedMaterials;

                var hologramMaterials = new Material[_originalMaterials[i].Length];
                for (var materialIndex = 0; materialIndex < hologramMaterials.Length; materialIndex++)
                {
                    hologramMaterials[materialIndex] = targetHighlightMaterial;
                }

                renderer.sharedMaterials = hologramMaterials;
            }
        }

        private void ClearHologramHighlight()
        {
            if (_highlightedRenderers == null || _originalMaterials == null)
            {
                return;
            }

            for (var i = 0; i < _highlightedRenderers.Length; i++)
            {
                if (_highlightedRenderers[i] != null)
                {
                    _highlightedRenderers[i].sharedMaterials = _originalMaterials[i];
                }
            }

            _highlightedInteractable = null;
            _highlightedRenderers = null;
            _originalMaterials = null;
        }

        private void SetHighlightVisible(bool visible)
        {
            if (_targetNameLabel != null)
            {
                _targetNameLabel.enabled = visible;
            }
        }
        
        private void ActionOnLook(InputAction.CallbackContext ctx) =>
            _input = ctx.ReadValue<Vector2>();
    }
}
