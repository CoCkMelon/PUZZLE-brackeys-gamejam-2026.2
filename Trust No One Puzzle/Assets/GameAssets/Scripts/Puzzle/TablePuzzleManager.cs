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

        [Header("Optional extra wiring (sfx, animations...)")]
        public UnityEvent onPuzzleSolved;

        [Header("Detection")]
        [Tooltip("Layers that can count as puzzle items.")]
        [SerializeField] private LayerMask itemLayers = ~0;

        private class Zone
        {
            public string Wanted;
            public Collider Collider;
            public bool Satisfied;
        }

        private readonly List<Zone> _zones = new List<Zone>();
        private readonly Collider[] _hits = new Collider[32];
        private readonly HashSet<string> _wrongHintsSent = new HashSet<string>();
        private bool _solved;

        public bool IsSolved => _solved;

        // ------------------------------------------------------------- setup

        private void Awake()
        {
            foreach (Transform child in transform)
            {
                var col = child.GetComponent<Collider>();
                if (col == null)
                {
                    Debug.LogWarning($"[TablePuzzleManager] '{child.name}' has no Collider - skipped. Add a BoxCollider (Is Trigger ON).");
                    continue;
                }
                _zones.Add(new Zone { Wanted = child.name, Collider = col });
            }

            if (_zones.Count == 0)
                Debug.LogWarning($"[TablePuzzleManager] No zones found. Add child GameObjects with colliders under '{name}'.");
        }

        private void Update()
        {
            if (_solved) return;

            bool all = true;
            foreach (var zone in _zones)
            {
                if (!EvaluateZone(zone))
                    all = false;
            }

            if (all && _zones.Count > 0)
                Solve();
        }

        // ----------------------------------------------------------- checking

        private bool EvaluateZone(Zone zone)
        {
            int count = OverlapZone(zone.Collider);

            bool satisfied = false;
            PlaceableItem wrongItem = null;

            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (IsPlayer(hit)) continue;

                var item = hit.GetComponentInParent<PlaceableItem>();
                if (IsCarried(item)) continue;      // ignore whatever is in the player's hands

                if (Matches(item, hit, zone.Wanted))
                {
                    satisfied = true;
                    break;
                }

                if (item != null && wrongItem == null)
                    wrongItem = item;                // remember one wrong item for the hint
            }

            if (!satisfied && wrongItem != null)
                SendWrongItemHint(zone, wrongItem);

            zone.Satisfied = satisfied;
            return satisfied;
        }

        private int OverlapZone(Collider col)
        {
            if (col is BoxCollider box)
            {
                var t = box.transform;
                var center = t.TransformPoint(box.center);
                var halfExtents = Vector3.Scale(box.size * 0.5f, t.lossyScale);
                return Physics.OverlapBoxNonAlloc(center, halfExtents, _hits, t.rotation, itemLayers, QueryTriggerInteraction.Ignore);
            }

            if (col is SphereCollider sphere)
            {
                var t = sphere.transform;
                var center = t.TransformPoint(sphere.center);
                var radius = sphere.radius * MaxComponent(t.lossyScale);
                return Physics.OverlapSphereNonAlloc(center, radius, _hits, itemLayers, QueryTriggerInteraction.Ignore);
            }

            var b = col.bounds; // fallback for any other collider type
            return Physics.OverlapBoxNonAlloc(b.center, b.extents, _hits, col.transform.rotation, itemLayers, QueryTriggerInteraction.Ignore);
        }

        private static bool Matches(PlaceableItem item, Collider hit, string wanted)
        {
            // Identity = the pickable item if there is one, otherwise the collider itself.
            var go = item != null ? item.gameObject : hit.gameObject;
            var objectName = go.name.Replace("(Clone)", "").Trim();

            if (string.Equals(objectName, wanted, StringComparison.OrdinalIgnoreCase))
                return true;

            // Also accept ItemId, so a zone can be named "item_candle" instead.
            return item != null && string.Equals(item.ItemId, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlayer(Collider hit)
        {
            var carry = PlayerCarry.Instance;
            return carry != null && hit.transform.root == carry.transform.root;
        }

        private static bool IsCarried(PlaceableItem item)
        {
            var carry = PlayerCarry.Instance;
            return item != null && carry != null && carry.IsCarrying && carry.HeldItem == item;
        }

        // ------------------------------------------------------------ outcome

        private void SendWrongItemHint(Zone zone, PlaceableItem item)
        {
            if (!reactToWrongItems) return;

            var key = zone.Wanted + "|" + (item.ItemId ?? item.name);
            if (!_wrongHintsSent.Add(key)) return;   // one hint per wrong item per zone, no spam

            if (WrongHintSystem.Instance != null)
                WrongHintSystem.Instance.SendCustom(wrongItemHint, wrongItemHintIsMisleading, "table-wrong");
        }

        private void Solve()
        {
            _solved = true;
            Debug.Log("[TablePuzzleManager] Puzzle solved.");

            if (!string.IsNullOrWhiteSpace(drawerId))
                PuzzleEvents.RaiseDrawerUnlocked(drawerId);

            if (WrongHintSystem.Instance != null)
                WrongHintSystem.Instance.SendCustom(completionHint, completionHintIsMisleading, "table-puzzle-solved");
            else
                Debug.LogWarning("[TablePuzzleManager] No WrongHintSystem in scene - message not sent.");

            onPuzzleSolved?.Invoke();
        }

        // -------------------------------------------------------------- debug

        private void OnDrawGizmosSelected()
        {
            foreach (Transform child in transform)
            {
                var col = child.GetComponent<Collider>();
                if (col == null) continue;
                Gizmos.color = Color.cyan;
                var b = col.bounds;
                Gizmos.DrawWireCube(b.center, b.extents * 2f);
            }
        }

        private static float MaxComponent(Vector3 v) =>
            Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    }
}
