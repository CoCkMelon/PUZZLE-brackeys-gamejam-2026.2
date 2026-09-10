using System.Collections.Generic;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Interaction;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Unlocks one or more <see cref="OpenableFurniture"/> furniture drawers when
    /// placement conditions are met (all listed slots correct, or any of them).
    /// </summary>
    public class DrawerUnlockTrigger : MonoBehaviour
    {
        public enum Condition
        {
            AllSlotsCorrect,
            AnySlotCorrect,
            SpecificItemInAnySlot
        }

        [SerializeField] private string drawerId = "drawer";
        [SerializeField] private Condition condition = Condition.AllSlotsCorrect;
        [SerializeField] private List<PlacementSlot> requiredSlots = new List<PlacementSlot>();
        [SerializeField] private string requiredItemId;
        [SerializeField] private List<OpenableFurniture> drawers = new List<OpenableFurniture>();
        [SerializeField] private bool lockAgainIfUnsolved;
        [SerializeField] private bool unlockOnce = true;

        public UnityEvent OnUnlocked;
        public UnityEvent OnRelocked;

        private bool _unlocked;

        public string DrawerId => drawerId;
        public bool IsUnlocked => _unlocked;

        private void OnEnable()
        {
            PuzzleEvents.SlotChanged += HandleSlotChanged;
            Evaluate();
        }

        private void OnDisable()
        {
            PuzzleEvents.SlotChanged -= HandleSlotChanged;
        }

        private void HandleSlotChanged(PlacementSlot _) => Evaluate();

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
            switch (condition)
            {
                case Condition.AllSlotsCorrect:
                    if (requiredSlots.Count == 0)
                        return false;
                    foreach (var slot in requiredSlots)
                    {
                        if (slot == null || !slot.IsCorrectlyFilled)
                            return false;
                    }
                    return true;

                case Condition.AnySlotCorrect:
                    foreach (var slot in requiredSlots)
                    {
                        if (slot != null && slot.IsCorrectlyFilled)
                            return true;
                    }
                    return false;

                case Condition.SpecificItemInAnySlot:
                    foreach (var slot in requiredSlots)
                    {
                        if (slot != null && slot.Occupant != null &&
                            slot.Occupant.ItemId == requiredItemId)
                            return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private void UnlockDrawers()
        {
            _unlocked = true;
            foreach (var drawer in drawers)
            {
                if (drawer != null)
                    drawer.Unlock();
            }

            OnUnlocked?.Invoke();
            PuzzleEvents.RaiseDrawerUnlocked(drawerId);
        }

        private void RelockDrawers()
        {
            _unlocked = false;
            foreach (var drawer in drawers)
            {
                if (drawer != null)
                    drawer.Lock();
            }

            OnRelocked?.Invoke();
        }

        /// <summary>Call from UnityEvents (keys, other puzzles) to force-unlock.</summary>
        public void ForceUnlock()
        {
            UnlockDrawers();
        }
    }
}
