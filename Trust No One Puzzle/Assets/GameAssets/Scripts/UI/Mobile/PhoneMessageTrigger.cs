using UnityEngine;
using UnityEngine.Events;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.UI.Mobile
{
    /// <summary>
    /// Sends messages to the phone messenger (MobilePhoneController via PuzzleEvents.HintRequested)
    /// based on triggers. This completes the missing wiring for UI Toolkit phone.
    /// 
    /// Can be triggered by:
    /// - OnTriggerEnter (player enters collider)
    /// - OnStart / OnEnable
    /// - Manual call via UnityEvent or public Send()
    /// - Via WrongHintSystem id lookup
    /// 
    /// Wire it in inspector: drop this on a trigger volume, set message, and it will
    /// automatically send to phone when player enters. Also works with Interaction/IInteractable.
    /// </summary>
    public class PhoneMessageTrigger : MonoBehaviour
    {
        [Header("Message")]
        [Tooltip("If set, will look up hint from WrongHintSystem by id (supports misleading flag from pool).")]
        [SerializeField] private string hintId;
        [Tooltip("If hintId empty, this custom text is sent.")]
        [TextArea] [SerializeField] private string customText;
        [SerializeField] private bool isMisleading;
        [SerializeField] private string sourceId = "trigger";

        [Header("Trigger Conditions")]
        [SerializeField] private bool sendOnTriggerEnter = true;
        [SerializeField] private bool sendOnStart;
        [SerializeField] private bool sendOnEnable;
        [SerializeField] private bool sendOnce = true;
        [SerializeField] private float delaySeconds;
        [SerializeField] private string playerTag = "Player";
        [Tooltip("Also accept Player FPP/TPP without tag - checks for CharacterController or PlayerCarry in parent.")]
        [SerializeField] private bool acceptAnyPlayerCollider = true;

        [Header("Events")]
        public UnityEvent OnMessageSent;

        private bool _sent;

        private void Start()
        {
            if (sendOnStart)
                TrySend();
        }

        private void OnEnable()
        {
            if (sendOnEnable)
                TrySend();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!sendOnTriggerEnter) return;
            if (!IsPlayer(other)) return;
            TrySend();
        }

        // Also support 2D for top-down tests
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!sendOnTriggerEnter) return;
            if (!IsPlayer2D(other)) return;
            TrySend();
        }

        public void TrySend()
        {
            if (sendOnce && _sent) return;
            if (delaySeconds > 0f)
                Invoke(nameof(Send), delaySeconds);
            else
                Send();
        }

        public void Send()
        {
            if (sendOnce && _sent) return;

            if (!string.IsNullOrWhiteSpace(hintId))
            {
                if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendHintById(hintId);
                else
                {
                    // Fallback: raise directly if WrongHintSystem not in scene
                    PuzzleEvents.RaiseHint(new HintMessage { text = $"[Missing WrongHintSystem] id:{hintId}", isMisleading = isMisleading, sourceId = sourceId });
                    Debug.LogWarning($"[PhoneMessageTrigger] WrongHintSystem.Instance not found, cannot send id '{hintId}'");
                }
            }
            else if (!string.IsNullOrWhiteSpace(customText))
            {
                if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendCustom(customText, isMisleading, sourceId);
                else
                    PuzzleEvents.RaiseHint(new HintMessage { text = customText, isMisleading = isMisleading, sourceId = sourceId });
            }
            else
            {
                Debug.LogWarning("[PhoneMessageTrigger] No hintId or customText set.");
                return;
            }

            _sent = true;
            OnMessageSent?.Invoke();
        }

        public void ResetTrigger() => _sent = false;

        // Public helpers for UnityEvent wiring
        public void SendCustomMessage(string text) { customText = text; hintId = ""; Send(); }
        public void SendHintId(string id) { hintId = id; Send(); }

        private bool IsPlayer(Collider other)
        {
            if (other.CompareTag(playerTag)) return true;
            if (acceptAnyPlayerCollider)
            {
                // Check for player components
                if (other.GetComponentInParent<CharacterController>() != null) return true;
                if (other.GetComponentInParent<PlayerCarry>() != null) return true;
                if (other.transform.root.CompareTag(playerTag)) return true;
            }
            return false;
        }

        private bool IsPlayer2D(Collider2D other)
        {
            if (other.CompareTag(playerTag)) return true;
            if (acceptAnyPlayerCollider && other.GetComponentInParent<CharacterController>() != null) return true;
            return false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                sourceId = "trigger";
        }
#endif
    }
}
