using System;
using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameAssets.Scripts.UI.Mobile
{
    /// <summary>
    /// UI Toolkit phone overlay. Unlocks the FPS cursor while open so the
    /// panel can receive clicks; restores lock on close.
    /// Assign MobilePhone.uxml and MobileTheme.tss on a UIDocument.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MobilePhoneController : MonoBehaviour
    {
        public static MobilePhoneController Instance { get; private set; }

        /// <summary>True while the phone owns the hardware cursor.</summary>
        public static bool CursorFreedForUi { get; private set; }

        [SerializeField] private InputActionReference toggleAction;
        [SerializeField] private bool startOpen;
        [SerializeField] private bool autoOpenOnHint = true;
        [SerializeField] private bool autoOpenOnWrongHint = true;
        [Tooltip("Look / camera scripts to disable while the phone is open (e.g. FPPCameraController).")]
        [SerializeField] private Behaviour[] disableWhileOpen;
        [Tooltip("Look InputAction to disable so mouse delta does not keep turning the view.")]
        [SerializeField] private InputActionReference[] lookActionsToDisable;

        private UIDocument _doc;
        private VisualElement _root;
        private VisualElement _chatList;
        private VisualElement _unread;
        private Label _clock;
        private bool _open;
        private bool _holdingCursor;
        private CursorLockMode _savedLock;
        private bool _savedVisible;

        public bool IsOpen => _open;

        private void Awake()
        {
            Instance = this;
            _doc = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            BindUi();
            SetOpen(startOpen);
            PuzzleEvents.HintRequested += OnHint;
            if (toggleAction != null)
            {
                toggleAction.action.Enable();
                toggleAction.action.started += OnToggle;
            }
        }

        private void OnDisable()
        {
            PuzzleEvents.HintRequested -= OnHint;
            if (toggleAction != null)
            {
                toggleAction.action.started -= OnToggle;
                toggleAction.action.Disable();
            }
            //if (_holdingCursor)
                ReleaseCursor();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (_clock != null)
                _clock.text = DateTime.Now.ToString("HH:mm");

            if (toggleAction == null && Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
                Toggle();

            // Cameras often re-lock the cursor every frame; keep it free while the phone is up.
            if (_holdingCursor && UnityEngine.Cursor.lockState != CursorLockMode.None)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
        }

        public void Toggle() => SetOpen(!_open);

        public void SetOpen(bool open)
        {
            if (_root == null)
                BindUi();

            _open = open;
            if (_root != null)
                _root.EnableInClassList("hidden", !open);

            if (open && _unread != null)
                _unread.RemoveFromClassList("visible");

            if (open)
                CaptureCursor();
            else
                ReleaseCursor();

            SetLookEnabled(!open);
        }

        public void AddMessage(string text, bool misleading)
        {
            if (_chatList == null)
                BindUi();
            if (_chatList == null)
                return;

            var bubble = new VisualElement();
            bubble.AddToClassList("bubble");
            bubble.AddToClassList(misleading ? "wrong" : "hint");

            var tag = new Label(misleading ? "Unknown number" : "SMS");
            tag.AddToClassList("bubble-tag");
            var body = new Label(text);
            body.AddToClassList("bubble-text");

            bubble.Add(tag);
            bubble.Add(body);
            _chatList.Add(bubble);

            if (!_open && _unread != null)
                _unread.AddToClassList("visible");
        }

        private void CaptureCursor()
        {
            if (_holdingCursor)
                return;

            _savedLock = UnityEngine.Cursor.lockState;
            _savedVisible = UnityEngine.Cursor.visible;
            _holdingCursor = true;
            CursorFreedForUi = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        private void ReleaseCursor()
        {
            if (!_holdingCursor)
                return;

            _holdingCursor = false;
            CursorFreedForUi = false;
            UnityEngine.Cursor.lockState = _savedLock;
            UnityEngine.Cursor.visible = _savedVisible;
        }

        private void SetLookEnabled(bool enabled)
        {
            if (disableWhileOpen != null)
            {
                foreach (var behaviour in disableWhileOpen)
                {
                    if (behaviour != null)
                        behaviour.enabled = enabled;
                }
            }

            if (lookActionsToDisable == null)
                return;

            foreach (var reference in lookActionsToDisable)
            {
                if (reference == null)
                    continue;
                if (enabled)
                    reference.action.Enable();
                else
                    reference.action.Disable();
            }
        }

        private void OnHint(HintMessage hint)
        {
            AddMessage(hint.text, hint.isMisleading);
            if (autoOpenOnHint || (autoOpenOnWrongHint && hint.isMisleading))
                SetOpen(true);
        }

        private void BindUi()
        {
            if (_doc == null)
                _doc = GetComponent<UIDocument>();
            var ve = _doc != null ? _doc.rootVisualElement : null;
            if (ve == null)
                return;

            _root = ve.Q("phone-root") ?? ve;
            _chatList = ve.Q("chat-list");
            _unread = ve.Q("unread-dot");
            _clock = ve.Q<Label>("clock");
            ve.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
            ve.Q<Button>("nav-back")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
            ve.Q<Button>("nav-home")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
        }

        private void OnToggle(InputAction.CallbackContext _) => Toggle();
    }
}
