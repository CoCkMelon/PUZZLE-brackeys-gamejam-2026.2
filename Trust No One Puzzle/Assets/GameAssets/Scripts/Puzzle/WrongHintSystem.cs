using System.Collections.Generic;
using GameAssets.Scripts.UI.Mobile;
using UnityEngine;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Phone / narrator hints. Some are true, some are deliberately wrong ("Trust No One").
    /// Pushed directly into MobilePhoneController - no PuzzleEvents bus anymore.
    /// Drive it from UnityEvents: SendHintById / SendCustomFromTrigger / SendMisleadingFromTrigger.
    /// </summary>
    [System.Serializable]
    public class HintMessage
    {
        public string text;
        public bool isMisleading;
        public string sourceId;
    }

    public class WrongHintSystem : MonoBehaviour
    {
        public static WrongHintSystem Instance { get; private set; }

        [System.Serializable]
        public class HintEntry
        {
            public string id;
            [TextArea] public string text;
            public bool isMisleading;
        }

        [Header("Pool")]
        [SerializeField] private List<HintEntry> hints = new List<HintEntry>();

        [Header("Behaviour")]
        [SerializeField] private bool neverRepeat = true;
        [SerializeField] private bool logToConsole = true;

        [Header("Output")]
        [Tooltip("Leave empty to auto-find MobilePhoneController at runtime.")]
        [SerializeField] private MobilePhoneController phone;

        private readonly HashSet<string> _sent = new HashSet<string>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private MobilePhoneController Phone
        {
            get
            {
                if (phone != null) return phone;
                if (MobilePhoneController.Instance != null) phone = MobilePhoneController.Instance;
                else phone = FindFirstObjectByType<MobilePhoneController>();
                return phone;
            }
        }

        // ---- Public API (UnityEvent friendly) ----

        public void SendHintById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var entry = hints.Find(h => h.id == id);
            if (entry != null) Push(entry);
            else Debug.LogWarning($"[WrongHintSystem] Hint id '{id}' not found.", this);
        }

        public void SendCustom(string text, bool misleading, string sourceId = "custom")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Push(new HintMessage { text = text, isMisleading = misleading, sourceId = sourceId });
        }

        public void SendCustomFromTrigger(string text) => SendCustom(text, false, "trigger");
        public void SendMisleadingFromTrigger(string text) => SendCustom(text, true, "trigger-misleading");

        public void ClearHistory() => _sent.Clear();

        // ---- Internals ----

        private void Push(HintEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.text)) return;
            if (neverRepeat && !string.IsNullOrEmpty(entry.id) && !_sent.Add(entry.id)) return;

            Push(new HintMessage
            {
                text = entry.text,
                isMisleading = entry.isMisleading,
                sourceId = entry.id
            });
        }

        private void Push(HintMessage message)
        {
            var target = Phone;
            if (target != null) target.AddMessage(message);
            else Debug.LogWarning("[WrongHintSystem] No MobilePhoneController in scene - hint dropped.", this);

            if (logToConsole)
                Debug.Log($"[Hint{(message.isMisleading ? " WRONG" : "")}] {message.text} (src:{message.sourceId})");
        }
    }
}
