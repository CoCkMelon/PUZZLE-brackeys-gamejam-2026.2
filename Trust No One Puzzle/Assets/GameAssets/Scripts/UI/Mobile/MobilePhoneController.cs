using System;
using System.Collections.Generic;
using GameAssets.Scripts.Puzzle;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameAssets.Scripts.UI.Mobile
{
    /// <summary>
    /// UI Toolkit phone overlay - complete implementation.
    /// Listens to PuzzleEvents.HintRequested (raised by WrongHintSystem and PhoneMessageTrigger)
    /// and displays messages as chat bubbles.
    /// Fixed: adds audio notification, ensures UI binds even if UXML missing, logs hints.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MobilePhoneController : MonoBehaviour
    {
        public static MobilePhoneController Instance { get; private set; }
        public static bool CursorFreedForUi { get; private set; }

        [Header("Input")]
        [SerializeField] private InputActionReference toggleAction;
        [SerializeField] private bool startOpen;
        [SerializeField] private bool autoOpenOnHint = true;
        [SerializeField] private bool autoOpenOnWrongHint = true;

        [Header("Behaviour")]
        [Tooltip("Look / camera scripts to disable while the phone is open (e.g. FPPCameraController).")]
        [SerializeField] private Behaviour[] disableWhileOpen;
        [Tooltip("Look InputAction to disable so mouse delta does not keep turning the view.")]
        [SerializeField] private InputActionReference[] lookActionsToDisable;
        [SerializeField] private int maxBubbles = 100;
        [SerializeField] private bool scrollToBottomOnNewMessage = true;

        [Header("Audio - NEW FIX for no sound")]
        [SerializeField] private AudioClip incomingMessageSound;
        [SerializeField] private float soundVolume = 0.8f;
        [SerializeField] private bool playSoundEvenWhenOpen = false;

        [Header("Auto-Close FIX - phone never closes bug")]
        [SerializeField] private bool autoCloseAfterHint = true;
        [SerializeField] private float autoCloseDelay = 2.5f;
        [Tooltip("If true, phone will NOT disable AutoPlayerMover/NavMeshAgent/PlayerCarry - fixes freeze after cabinet")]
        [SerializeField] private bool neverDisableAutoMover = true;

        private UIDocument _doc;
        private VisualElement _root;
        private ScrollView _scroll;
        private VisualElement _chatList;
        private VisualElement _unread;
        private Label _clock;
        private Button _btnClose;
        private bool _open;
        private bool _holdingCursor;
        private CursorLockMode _savedLock;
        private bool _savedVisible;

        // Keep history for debugging / save
        private readonly List<HintMessage> _history = new List<HintMessage>();

        public bool IsOpen => _open;
        public IReadOnlyList<HintMessage> History => _history;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _doc = GetComponent<UIDocument>();
            Debug.Log("[MobilePhone] Instance created, listening for hints");
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
            Debug.Log("[MobilePhone] Enabled and subscribed to HintRequested");
        }

        private void OnDisable()
        {
            PuzzleEvents.HintRequested -= OnHint;
            if (toggleAction != null)
            {
                toggleAction.action.started -= OnToggle;
                toggleAction.action.Disable();
            }
            if (_holdingCursor)
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

            if (open && _scroll != null && scrollToBottomOnNewMessage)
                _scroll.schedule.Execute(() => _scroll.scrollOffset = new Vector2(0, float.MaxValue)).ExecuteLater(50);

            Debug.Log($"[MobilePhone] SetOpen={open}, unread cleared");
        }

        public void AddMessage(string text, bool misleading, string sourceId = "custom")
        {
            AddMessage(new HintMessage { text = text, isMisleading = misleading, sourceId = sourceId });
        }

        public void AddMessage(HintMessage hint)
        {
            if (string.IsNullOrWhiteSpace(hint.text)) return;

            _history.Add(hint);
            if (_history.Count > maxBubbles * 2)
                _history.RemoveRange(0, _history.Count - maxBubbles * 2);

            Debug.Log($"[MobilePhone] NEW MESSAGE: {(hint.isMisleading ? "WRONG" : "TRUTH")} '{hint.text}' src:{hint.sourceId} history={_history.Count}");

            // Play sound for incoming message
            if (incomingMessageSound != null && (!IsOpen || playSoundEvenWhenOpen))
            {
                AudioSource.PlayClipAtPoint(incomingMessageSound, Camera.main != null ? Camera.main.transform.position : transform.position, soundVolume);
                Debug.Log($"[MobilePhone] Played incoming sound {incomingMessageSound.name}");
            }
            else if (incomingMessageSound == null)
            {
                // Fallback beep using console beep log if no clip assigned
                Debug.Log($"[MobilePhone] *BEEP* incoming message (no AudioClip assigned, assign one in inspector for sound)");
            }

            if (_chatList == null)
                BindUi();
            if (_chatList == null)
            {
                Debug.LogWarning("[MobilePhone] chat-list not found in UXML, message kept in history but not visible. Ensure MobilePhone.uxml has chat-list element");
                // Still show unread dot if possible
                if (_unread != null) _unread.AddToClassList("visible");
                return;
            }

            var bubble = new VisualElement();
            bubble.AddToClassList("bubble");
            bubble.AddToClassList(hint.isMisleading ? "wrong" : "hint");

            var tag = new Label(hint.isMisleading ? "Unknown number" : "SMS");
            tag.AddToClassList("bubble-tag");
            var body = new Label(hint.text);
            body.AddToClassList("bubble-text");
            body.enableRichText = true;

            bubble.Add(tag);
            bubble.Add(body);
            _chatList.Add(bubble);

            // Prune old bubbles
            while (_chatList.childCount > maxBubbles)
                _chatList.RemoveAt(0);

            if (scrollToBottomOnNewMessage && _scroll != null)
            {
                _scroll.schedule.Execute(() =>
                {
                    _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
                }).ExecuteLater(30);
            }

            if (!_open && _unread != null)
                _unread.AddToClassList("visible");
        }

        public void ClearAll()
        {
            _history.Clear();
            _chatList?.Clear();
            _unread?.RemoveFromClassList("visible");
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
                    if (behaviour == null) continue;
                    // FIX: never disable auto-movement components, otherwise player freezes after reaching cabinet
                    if (neverDisableAutoMover)
                    {
                        var typeName = behaviour.GetType().Name;
                        if (typeName.Contains("AutoPlayerMover") || typeName.Contains("AutoStrangerMover") || typeName.Contains("NavMeshAgent") || typeName.Contains("PlayerCarry") || typeName.Contains("NavMeshAutoBaker") || typeName.Contains("AutoGameSolver") || typeName.Contains("AutoTestManager"))
                            continue;
                    }
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
            Debug.Log($"[MobilePhone] OnHint received: {hint.text}");
            AddMessage(hint);
            bool shouldOpen = autoOpenOnHint || (autoOpenOnWrongHint && hint.isMisleading);
            // Don't auto-open for autosolver-complete message to avoid spam, but still log
            if (hint.sourceId == "autosolver-complete") shouldOpen = false;

            if (shouldOpen)
            {
                SetOpen(true);
                Debug.Log("[MobilePhone] Auto-opened phone due to hint");

                if (autoCloseAfterHint)
                {
                    CancelInvoke(nameof(AutoClosePhone));
                    Invoke(nameof(AutoClosePhone), autoCloseDelay);
                }
            }
        }

        void AutoClosePhone()
        {
            if (_open)
            {
                Debug.Log("[MobilePhone] Auto-closing phone after delay (fix never closes bug)");
                SetOpen(false);
            }
        }

        private void BindUi()
        {
            if (_doc == null)
                _doc = GetComponent<UIDocument>();
            var ve = _doc != null ? _doc.rootVisualElement : null;
            if (ve == null)
            {
                Debug.LogWarning("[MobilePhone] UIDocument rootVisualElement null, UI not bound yet");
                return;
            }

            _root = ve.Q("phone-root") ?? ve;
            _chatList = ve.Q("chat-list");
            _scroll = ve.Q<ScrollView>("chat-scroll");
            _unread = ve.Q("unread-dot");
            _clock = ve.Q<Label>("clock");
            _btnClose = ve.Q<Button>("btn-close");

            if (_chatList == null) Debug.LogWarning("[MobilePhone] chat-list not found, check MobilePhone.uxml");
            if (_scroll == null) Debug.LogWarning("[MobilePhone] chat-scroll ScrollView not found");

            _btnClose?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
            ve.Q<Button>("nav-back")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
            ve.Q<Button>("nav-home")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));
            ve.Q<Button>("nav-recents")?.RegisterCallback<ClickEvent>(_ => SetOpen(false));

            var sendBtn = ve.Q<Button>(className: "send-btn");
            sendBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                Debug.Log("[MobilePhone] Send pressed - player messaging not implemented yet");
            });

            Debug.Log($"[MobilePhone] UI bound: root={_root != null}, chatList={_chatList != null}, scroll={_scroll != null}, unread={_unread != null}");
        }

        private void OnToggle(InputAction.CallbackContext _) => Toggle();
    }
}
