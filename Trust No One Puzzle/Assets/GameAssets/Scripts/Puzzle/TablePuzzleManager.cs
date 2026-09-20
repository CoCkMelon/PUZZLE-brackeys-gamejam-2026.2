using System;
using System.Collections.Generic;
using GameAssets.Scripts.Interaction;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Zero-wiring table puzzle.
    /// - Put this component on a parent object (e.g. "TablePuzzle").
    /// - Every CHILD with a collider becomes a target zone.
    /// - Name each child exactly like the item that belongs in it
    ///   (item's GameObject name or its ItemId, case-insensitive, "(Clone)" ignored).
    /// Zones are checked by polling overlap tests, so items work no matter how
    /// they got there (carried, thrown, placed, pre-existing in the scene).
    /// When every zone holds its item -> drawer unlocks + phone message.
    /// </summary>
    public class TablePuzzleManager : MonoBehaviour
    {
        [Header("Unlock")]
        [Tooltip("Drawer id unlocked on completion. Leave empty to skip the event.")]
        [SerializeField] private string drawerId = "drawer_desk_01";

        [Header("Completion message (goes to the phone)")]
        [SerializeField, TextArea] private string completionHint = "The drawer key is near the TV.";
        [SerializeField] private bool completionHintIsMisleading = false;

        [Header("Wrong item feedback (optional)")]
        [SerializeField] private bool reactToWrongItems = true;
        [SerializeField, TextArea] private string wrongItemHint = "That doesn't belong there. Trust me.";
        [SerializeField] private bool wrongItemHintIsMisleading = true;
        int itemCount = 0;

        [Header("Optional extra wiring (sfx, animations...)")]
        public UnityEvent onPuzzleSolved;
        public void RegisterTrigger() {
            itemCount+=1;
        }

        public void ItemInPlace() {
            itemCount-=1;
            if(itemCount<=0) {
            if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendCustom(completionHint, completionHintIsMisleading, "table-puzzle-solved");
                else
                    Debug.LogWarning("[TablePuzzleManager] No WrongHintSystem in scene - message not sent.");

                onPuzzleSolved?.Invoke();
            }
        }
    }
}
