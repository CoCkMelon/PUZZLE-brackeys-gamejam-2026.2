using UnityEngine;
using UnityEngine.Events;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.UI.Mobile;

namespace GameAssets.Scripts.Hints
{
    /// <summary>
    /// Generic player trigger - complete version.
    /// Supports UnityEvents and direct messenger wiring to phone (UI Toolkit).
    /// Replaces old simple Trigger that had no way to wire to messenger.
    /// </summary>
    public class Trigger : MonoBehaviour
    {
        [Header("Player Check")]
        [SerializeField] private string playerTag = "Player";
        [SerializeField] private bool acceptAnyPlayerCollider = true;
        [SerializeField] private bool triggerOnce = true;
        [SerializeField] private float delay;

        [Header("Generic Events")]
        [SerializeField] private UnityEvent OnEnter;
        [SerializeField] private UnityEvent OnExit;
        [SerializeField] private UnityEvent OnStay;

        [Header("Messenger Wiring (UI Toolkit Phone)")]
        [Tooltip("If set, sends this hint id via WrongHintSystem when triggered.")]
        [SerializeField] private string hintId;
        [Tooltip("If hintId empty, sends this custom text to phone messenger.")]
        [TextArea] [SerializeField] private string customMessage;
        [SerializeField] private bool isMisleading;
        [SerializeField] private string sourceId = "trigger";

        private bool _triggered;

        private void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other)) return;
            if (triggerOnce && _triggered) return;

            if (delay > 0f)
                Invoke(nameof(InvokeEnter), delay);
            else
                InvokeEnter();
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other)) return;
            OnExit?.Invoke();
        }

        private void OnTriggerStay(Collider other)
        {
            if (!IsPlayer(other)) return;
            OnStay?.Invoke();
        }

        private void InvokeEnter()
        {
            _triggered = true;
            OnEnter?.Invoke();
            TrySendMessage();
        }

        private void TrySendMessage()
        {
            if (!string.IsNullOrWhiteSpace(hintId))
            {
                if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendHintById(hintId);
                else
                    PuzzleEvents.RaiseHint(new HintMessage { text = customMessage, isMisleading = isMisleading, sourceId = sourceId });
            }
            else if (!string.IsNullOrWhiteSpace(customMessage))
            {
                if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendCustom(customMessage, isMisleading, sourceId);
                else
                    PuzzleEvents.RaiseHint(new HintMessage { text = customMessage, isMisleading = isMisleading, sourceId = sourceId });
            }
        }

        public void ResetTrigger() => _triggered = false;

        // Public API for UnityEvent wiring
        public void SendMessage() => TrySendMessage();

        private bool IsPlayer(Collider other)
        {
            if (other.CompareTag(playerTag)) return true;
            if (acceptAnyPlayerCollider)
            {
                if (other.GetComponentInParent<CharacterController>() != null) return true;
                if (other.GetComponentInParent<PlayerCarry>() != null) return true;
                if (other.transform.root.CompareTag(playerTag)) return true;
            }
            return false;
        }
    }
}
