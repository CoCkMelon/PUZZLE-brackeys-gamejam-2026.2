using GameAssets.Scripts.Interaction;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Puzzle
{
    /// <summary>
    /// Validates whether a carried object belongs here.
    /// Correct placement can unlock drawers via <see cref="DrawerUnlockTrigger"/>.
    /// Wrong placement fires events the hint system can use for deceptive messages.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PlacementSlot : MonoBehaviour
    {
        void OnStart()
        {
            tpm.RegisterTrigger();
        }
        public TablePuzzleManager tpm;
        void OnTriggerEnter(Collider other)
        {
            tpm.ItemInPlace();
        }
    }
}
