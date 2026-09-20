using System.Collections.Generic;
using GameAssets.Scripts.Environment;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Unlocks one or more <see cref="OpenableFurniture"/> drawers when conditions are reported met.
    /// Wire slots / key triggers / unlock colliders to ReportMet(id) and ReportUnmet(id) via UnityEvents,
    /// or just call ForceUnlock().
    /// </summary>
    public class DrawerUnlockTrigger : MonoBehaviour
    {
        public enum Condition
        {
            AllConditionsMet,
            AnyConditionMet
        }

        [SerializeField] private string drawerId = "drawer";
        [SerializeField] private Condition condition = Condition.AllConditionsMet;

        [Tooltip("Condition ids that must be reported. Use ReportMet/ReportUnmet from UnityEvents.")]
        [SerializeField] private List<string> requiredConditionIds = new List<string>();

        [SerializeField] private List<OpenableFurniture> drawers = new List<OpenableFurniture>();
        [SerializeField] private bool lockAgainIfUnsolved;
        [SerializeField] private bool unlockOnce = true;

        public UnityEvent OnUnlocked;
        public UnityEvent OnRelocked;

        private readonly HashSet<string> _met = new HashSet<string>();
        private bool _unlocked;

        public string DrawerId => drawerId;
        public bool IsUnlocked => _unlocked;

        private void OnEnable() => Evaluate();

        /// <summary>Report a condition id as satisfied (UnityEvent string parameter).</summary>
        public void ReportMet(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (_met.Add(id)) Evaluate();
        }

        /// <summary>Report a condition id as no longer satisfied.</summary>
        public void ReportUnmet(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (_met.Remove(id)) Evaluate();
        }

        public void ResetConditions()
        {
            _met.Clear();
            Evaluate();
        }

        public void Evaluate()
        {
            var solved = IsConditionMet();

            if (solved && !_unlocked)
                UnlockDrawers();
            else if (!solved && _unlocked && lockAgainIfUnsolved && !unlockOnce)
                RelockDrawers();
        }

        private bool IsConditionMet()
        {
            if (requiredConditionIds.Count == 0) return false;

            if (condition == Condition.AnyConditionMet)
            {
                foreach (var id in requiredConditionIds)
                    if (!string.IsNullOrWhiteSpace(id) && _met.Contains(id)) return true;
                return false;
            }

            foreach (var id in requiredConditionIds)
                if (string.IsNullOrWhiteSpace(id) || !_met.Contains(id)) return false;
            return true;
        }

        private void UnlockDrawers()
        {
            _unlocked = true;
            foreach (var drawer in drawers)
                if (drawer != null) drawer.Unlock();

            OnUnlocked?.Invoke();
        }

        private void RelockDrawers()
        {
            _unlocked = false;
            foreach (var drawer in drawers)
                if (drawer != null) drawer.Lock();

            OnRelocked?.Invoke();
        }

        /// <summary>Call from UnityEvents (keys, other puzzles) to force-unlock.</summary>
        public void ForceUnlock() => UnlockDrawers();

        /// <summary>Call from UnityEvents to force-relock.</summary>
        public void ForceRelock() => RelockDrawers();
    }
}
