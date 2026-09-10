using System.Collections.Generic;
using UnityEngine;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Phone / narrator hints. Some are true, some are deliberately wrong ("Trust No One").
    /// Now fully decoupled from old Canvas DialogueManager - uses PuzzleEvents.HintRequested
    /// which MobilePhoneController (UI Toolkit) listens to.
    /// Supports trigger-based messages via SendHintById / SendCustom / PhoneMessageTrigger.
    /// </summary>
    public class WrongHintSystem : MonoBehaviour
    {
        public static WrongHintSystem Instance { get; private set; }

        [System.Serializable]
        public class HintEntry
        {
            public string id;
            [TextArea] public string text;
            public bool isMisleading;
            [Tooltip("If set, only send after this slot is filled incorrectly.")]
            public string triggerOnWrongSlotId;
            [Tooltip("If set, send when this slot is solved correctly.")]
            public string triggerOnCorrectSlotId;
            [Tooltip("Send once when the matching drawer unlocks.")]
            public string triggerOnDrawerId;
        }

        [Header("Pool")]
        [SerializeField] private List<HintEntry> hints = new List<HintEntry>();

        [Header("Behaviour")]
        [SerializeField] private bool sendRandomWrongHintOnWrongPlacement = true;
        [SerializeField] [Range(0f, 1f)] private float wrongHintChance = 0.7f;
        [SerializeField] private bool neverRepeat = true;
        [SerializeField] private bool logToConsole = true;

        [Header("Fallback copy")]
        [SerializeField] [TextArea] private string genericWrongHint =
            "Put it in the other drawer. Trust me.";
        [SerializeField] [TextArea] private string genericCorrectHint =
            "That looks right. Keep going.";

        private readonly HashSet<string> _sent = new HashSet<string>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnEnable()
        {
            PuzzleEvents.WrongPlacement += HandleWrongPlacement;
            PuzzleEvents.CorrectPlacement += HandleCorrectPlacement;
            PuzzleEvents.DrawerUnlocked += HandleDrawerUnlocked;
        }

        private void OnDisable()
        {
            PuzzleEvents.WrongPlacement -= HandleWrongPlacement;
            PuzzleEvents.CorrectPlacement -= HandleCorrectPlacement;
            PuzzleEvents.DrawerUnlocked -= HandleDrawerUnlocked;
        }

        public void SendHintById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var entry = hints.Find(h => h.id == id);
            if (entry != null)
                Push(entry);
            else
                Debug.LogWarning($"[WrongHintSystem] Hint id '{id}' not found.");
        }

        public void SendCustom(string text, bool misleading, string sourceId = "custom")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Push(new HintMessage
            {
                text = text,
                isMisleading = misleading,
                sourceId = sourceId
            });
        }

        /// <summary>Helper for UnityEvent wiring - sends custom text as misleading or not.</summary>
        public void SendCustomFromTrigger(string text) => SendCustom(text, false, "trigger");
        public void SendMisleadingFromTrigger(string text) => SendCustom(text, true, "trigger-misleading");

        public void ClearHistory() => _sent.Clear();

        private void HandleWrongPlacement(PlacementSlot slot, PlaceableItem item)
        {
            if (slot == null) return;
            var specific = hints.Find(h =>
                !string.IsNullOrEmpty(h.triggerOnWrongSlotId) &&
                h.triggerOnWrongSlotId == slot.SlotId);

            if (specific != null)
            {
                Push(specific);
                return;
            }

            if (!sendRandomWrongHintOnWrongPlacement || Random.value > wrongHintChance)
                return;

            var pool = hints.FindAll(h => h.isMisleading && string.IsNullOrEmpty(h.triggerOnWrongSlotId)
                                                          && string.IsNullOrEmpty(h.triggerOnCorrectSlotId)
                                                          && string.IsNullOrEmpty(h.triggerOnDrawerId));
            if (pool.Count > 0)
                Push(pool[Random.Range(0, pool.Count)]);
            else
                SendCustom(genericWrongHint, true, "generic-wrong");
        }

        private void HandleCorrectPlacement(PlacementSlot slot, PlaceableItem item)
        {
            if (slot == null) return;
            var specific = hints.Find(h =>
                !string.IsNullOrEmpty(h.triggerOnCorrectSlotId) &&
                h.triggerOnCorrectSlotId == slot.SlotId);

            if (specific != null)
            {
                Push(specific);
                return;
            }

            // Optional generic correct hint - can be disabled by leaving empty
            if (!string.IsNullOrWhiteSpace(genericCorrectHint))
                SendCustom(genericCorrectHint, false, "generic-correct");
        }

        private void HandleDrawerUnlocked(string drawerId)
        {
            var specific = hints.Find(h =>
                !string.IsNullOrEmpty(h.triggerOnDrawerId) &&
                h.triggerOnDrawerId == drawerId);

            if (specific != null)
                Push(specific);
        }

        private void Push(HintEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.text)) return;
            if (neverRepeat && _sent.Contains(entry.id))
                return;

            if (!string.IsNullOrEmpty(entry.id))
                _sent.Add(entry.id);

            Push(new HintMessage
            {
                text = entry.text,
                isMisleading = entry.isMisleading,
                sourceId = entry.id
            });
        }

        private void Push(HintMessage message)
        {
            PuzzleEvents.RaiseHint(message);
            if (logToConsole)
                Debug.Log($"[Hint{(message.isMisleading ? " WRONG" : "")}] {message.text} (src:{message.sourceId})");
        }
    }
}
