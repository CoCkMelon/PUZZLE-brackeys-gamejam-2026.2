using UnityEngine;
using UnityEngine.AI;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Entities;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.UI.Mobile;

/// <summary>
/// Fully automatic player for AutoTest scenes.
/// Disables manual FPP controller, CharacterController, PlayerInteraction
/// and drives player via NavMeshAgent, auto-picking, placing, opening, breaking.
/// Fixed: handles missing NavMesh, unreadable meshes, fallback direct movement, safe tag handling.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AutoPlayerMover : MonoBehaviour
{
    public enum TestMode { ManualWaypoints, AutoPuzzleRoom1, AutoPuzzleRoom2, FullGameAuto }

    [Header("Mode")]
    [SerializeField] private TestMode mode = TestMode.FullGameAuto;
    [SerializeField] private bool autoStart = true;
    [SerializeField] private float waypointReachDistance = 0.6f;
    [SerializeField] private float waitAtWaypoint = 0.6f;
    [SerializeField] private float interactRange = 3f;

    [Header("Waypoints (optional)")]
    [SerializeField] private Transform[] waypoints;
    [SerializeField] private Transform mirrorKeyLocation;
    [SerializeField] private Transform cabinetLocation;
    [SerializeField] private Transform placementTableLocation;
    [SerializeField] private Transform toolboxDrawerLocation;
    [SerializeField] private Transform toolboxLocation;
    [SerializeField] private Transform sofaHammerLocation;
    [SerializeField] private Transform windowLocation;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float runSpeed = 5f;
    [SerializeField] private bool useRun = false;
    [SerializeField] private bool fallbackDirectMove = true;

    [Header("Auto-Solve")]
    [SerializeField] private bool autoPickup = true;
    [SerializeField] private bool autoPlace = true;
    [SerializeField] private bool autoOpen = true;
    [SerializeField] private bool autoBreakWindow = true;
    [SerializeField] private float actionCooldown = 0.4f;

    private NavMeshAgent _agent;
    private int _currentIndex;
    private float _waitTimer;
    private float _actionTimer;
    private bool _isRunning;
    private Vector3 _fallbackTarget;
    private bool _hasFallbackTarget;

    // Manual controllers to disable
    private CharacterController _charController;
    private PlayerController _playerController;
    private FPPCameraController _fppCamera;
    private PlayerInteraction _playerInteraction;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _charController = GetComponent<CharacterController>();
        _playerController = GetComponent<PlayerController>();
        _fppCamera = GetComponentInChildren<FPPCameraController>();
        if (_fppCamera == null) _fppCamera = GetComponent<FPPCameraController>();
        _playerInteraction = GetComponent<PlayerInteraction>();

        DisableManualControllers();

        if (_agent != null)
        {
            _agent.speed = useRun ? runSpeed : moveSpeed;
            _agent.angularSpeed = 360f;
            _agent.acceleration = 12f;
            _agent.stoppingDistance = waypointReachDistance;
            _agent.autoBraking = true;
            _agent.updateRotation = true;
        }
    }

    private void OnEnable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Start()
    {
        if (_agent != null && NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
        {
            _agent.Warp(hit.position);
        }
        if (autoStart) StartAuto();
    }

    private void DisableManualControllers()
    {
        if (_charController != null) _charController.enabled = false;
        if (_playerController != null) _playerController.enabled = false;
        if (_fppCamera != null) _fppCamera.enabled = false;
        if (_playerInteraction != null) _playerInteraction.enabled = false;

        var pcs = GetComponentsInChildren<PlayerController>();
        foreach (var pc in pcs) pc.enabled = false;
        var fpcs = GetComponentsInChildren<FPPCameraController>();
        foreach (var f in fpcs) f.enabled = false;
        var pis = GetComponentsInChildren<PlayerInteraction>();
        foreach (var pi in pis) pi.enabled = false;
    }

    public void StartAuto()
    {
        DisableManualControllers();
        _isRunning = true;
        _currentIndex = 0;
        _waitTimer = 0f;
        _actionTimer = 0f;

        var baker = FindFirstObjectByType<NavMeshAutoBaker>();
        baker?.TryBake();

        Debug.Log($"[AutoPlayerMover] Starting auto in mode {mode}, NavMesh vertices={NavMesh.CalculateTriangulation().vertices.Length}");

        if (_agent != null && !_agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(transform.position, out var hit2, 5f, NavMesh.AllAreas))
            {
                _agent.Warp(hit2.position);
                Debug.Log($"[AutoPlayerMover] Warped agent to NavMesh at {hit2.position}");
            }
            else if (fallbackDirectMove)
            {
                Debug.LogWarning("[AutoPlayerMover] Agent not on NavMesh, will use direct movement fallback");
            }
        }

        if (mode == TestMode.ManualWaypoints && waypoints != null && waypoints.Length > 0 && waypoints[0] != null)
            SetDestination(waypoints[0].position);
        else
            SetNextPuzzleDestination();

        Debug.Log($"[AutoPlayerMover] Auto started in mode {mode}");
    }

    public void StopAuto()
    {
        _isRunning = false;
        if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
        _hasFallbackTarget = false;
    }

    private void Update()
    {
        if (!_isRunning) return;
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }
        if (_actionTimer > 0) { _actionTimer -= Time.deltaTime; }

        if (_agent != null && !_agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, 10f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
            else if (fallbackDirectMove && _hasFallbackTarget)
            {
                UpdateFallbackMove();
                return;
            }
            else return;
        }

        // Fallback direct movement if NavMeshAgent fails
        if (fallbackDirectMove && _hasFallbackTarget && _agent != null && (!_agent.isOnNavMesh || !_agent.hasPath))
        {
            UpdateFallbackMove();
        }

        bool reached = false;
        if (_agent != null && _agent.isOnNavMesh)
        {
            reached = !_agent.pathPending && _agent.remainingDistance <= waypointReachDistance + 0.3f;
        }
        else if (_hasFallbackTarget)
        {
            reached = Vector3.Distance(transform.position, _fallbackTarget) <= waypointReachDistance + 0.5f;
        }

        if (reached)
        {
            if (_actionTimer <= 0f)
            {
                bool didAction = TryAutoInteract();
                if (didAction) _actionTimer = actionCooldown;
            }
            _waitTimer = waitAtWaypoint;
            SetNextPuzzleDestination();
        }
    }

    void UpdateFallbackMove()
    {
        if (!_hasFallbackTarget) return;
        Vector3 dir = _fallbackTarget - transform.position;
        dir.y = 0;
        float dist = dir.magnitude;
        if (dist <= waypointReachDistance) return;
        dir.Normalize();
        float speed = useRun ? runSpeed : moveSpeed;
        Vector3 move = dir * speed * Time.deltaTime;

        // CharacterController is disabled, so use transform
        transform.position += move;

        if (dir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 5f);
        }
    }

    private void SetNextPuzzleDestination()
    {
        Transform next = FindNextPuzzleTarget();
        if (next != null) SetDestination(next.position);
        else if (waypoints != null && waypoints.Length > 0)
        {
            _currentIndex = (_currentIndex + 1) % waypoints.Length;
            if (waypoints[_currentIndex] != null) SetDestination(waypoints[_currentIndex].position);
        }
        else
        {
            TryFindDynamicTarget();
        }
    }

    private void SetDestination(Vector3 pos)
    {
        _fallbackTarget = pos;
        _hasFallbackTarget = true;

        if (_agent != null && _agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(pos, out var hit, 5f, NavMesh.AllAreas))
            {
                pos = hit.position;
                _agent.SetDestination(pos);
                return;
            }
            else
            {
                Debug.LogWarning($"[AutoPlayerMover] No NavMesh near target {pos}, using direct move");
            }
        }
    }

    void TryFindDynamicTarget()
    {
        string[] searchOrder = { "Table", "Cabin 8", "Cabin 1", "Drawer 1", "Drawer 2", "tool Box", "Tool Box", "Hammer", "chair 2" };
        foreach (var name in searchOrder)
        {
            var go = GameObject.Find(name);
            if (go != null)
            {
                SetDestination(go.transform.position);
                Debug.Log($"[AutoPlayerMover] Dynamic target found: {name}");
                return;
            }
        }
    }

    private Transform FindNextPuzzleTarget()
    {
        var carry = PlayerCarry.Instance;
        bool isCarrying = carry != null && carry.IsCarrying;

        if (isCarrying && carry.HeldItem != null)
        {
            var heldId = carry.HeldItem.ItemId;
            var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
            PlacementSlot bestSlot = null;
            float bestDist = float.MaxValue;
            foreach (var slot in slots)
            {
                if (slot.IsOccupied) continue;
                if (slot.RequiredItemId != heldId) continue;
                float d = Vector3.Distance(transform.position, slot.transform.position);
                if (d < bestDist) { bestDist = d; bestSlot = slot; }
            }
            if (bestSlot != null) return bestSlot.transform;
            foreach (var slot in slots)
            {
                if (slot.IsOccupied) continue;
                float d = Vector3.Distance(transform.position, slot.transform.position);
                if (d < bestDist) { bestDist = d; bestSlot = slot; }
            }
            if (bestSlot != null) return bestSlot.transform;
        }

        if (!isCarrying && autoPickup)
        {
            var items = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
            PlaceableItem closest = null;
            float bestDist = float.MaxValue;
            foreach (var item in items)
            {
                if (item.IsHeld) continue;
                if (item.OccupyingSlot != null && item.OccupyingSlot.IsCorrectlyFilled) continue;
                bool needed = false;
                var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
                foreach (var s in slots) { if (!s.IsOccupied && s.RequiredItemId == item.ItemId) { needed = true; break; } }
                if (!needed && mode == TestMode.FullGameAuto) needed = true;
                if (!needed) continue;
                float d = Vector3.Distance(transform.position, item.transform.position);
                if (d < bestDist && d < 20f) { bestDist = d; closest = item; }
            }
            if (closest != null) return closest.transform;
        }

        if (autoOpen)
        {
            var furnitures = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
            OpenableFurniture closestF = null;
            float bestDist = float.MaxValue;
            foreach (var f in furnitures)
            {
                if (f.IsOpen) continue;
                float d = Vector3.Distance(transform.position, f.transform.position);
                if (d < bestDist && d < 15f) { bestDist = d; closestF = f; }
            }
            if (closestF != null) return closestF.transform;

            var toolboxes = FindObjectsByType<ToolBoxInteractable>(FindObjectsSortMode.None);
            foreach (var tb in toolboxes)
            {
                if (tb.IsOpen) continue;
                float d = Vector3.Distance(transform.position, tb.transform.position);
                if (d < bestDist && d < 15f) { bestDist = d; return tb.transform; }
            }
        }

        if (autoBreakWindow)
        {
            bool hasHammer = false;
            if (carry != null && carry.IsCarrying)
            {
                if (carry.HeldItem != null && carry.HeldItem.ItemId.ToLower().Contains("hammer")) hasHammer = true;
                if (carry.HeldInteractable != null && carry.HeldInteractable.name.ToLower().Contains("hammer")) hasHammer = true;
            }
            if (!hasHammer)
            {
                var hammerItems = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
                foreach (var h in hammerItems)
                {
                    if (h.ItemId.ToLower().Contains("hammer") || h.name.ToLower().Contains("hammer"))
                    {
                        float d = Vector3.Distance(transform.position, h.transform.position);
                        if (d < 20f) return h.transform;
                    }
                }
                var hammerInt = FindObjectsByType<Interactable>(FindObjectsSortMode.None);
                foreach (var hi in hammerInt)
                {
                    if (hi.name.ToLower().Contains("hammer"))
                    {
                        float d = Vector3.Distance(transform.position, hi.transform.position);
                        if (d < 20f) return hi.transform;
                    }
                }
            }
            else
            {
                var glasses = FindObjectsByType<Glass>(FindObjectsSortMode.None);
                Glass closestG = null;
                float bestDist = float.MaxValue;
                foreach (var g in glasses)
                {
                    float d = Vector3.Distance(transform.position, g.transform.position);
                    if (d < bestDist) { bestDist = d; closestG = g; }
                }
                if (closestG != null) return closestG.transform;
                if (windowLocation != null) return windowLocation;
            }
        }

        if (mode == TestMode.FullGameAuto || mode == TestMode.AutoPuzzleRoom1)
        {
            if (_currentIndex == 0 && mirrorKeyLocation != null) { _currentIndex++; return mirrorKeyLocation; }
            if (_currentIndex == 1 && cabinetLocation != null) { _currentIndex++; return cabinetLocation; }
            if (_currentIndex == 2 && placementTableLocation != null) { _currentIndex++; return placementTableLocation; }
            if (_currentIndex == 3 && toolboxDrawerLocation != null) { _currentIndex++; return toolboxDrawerLocation; }
            if (_currentIndex == 4 && toolboxLocation != null) { _currentIndex++; return toolboxLocation; }
            if (_currentIndex == 5 && sofaHammerLocation != null) { _currentIndex++; return sofaHammerLocation; }
            if (_currentIndex == 6 && windowLocation != null) { _currentIndex++; return windowLocation; }
        }
        else if (mode == TestMode.AutoPuzzleRoom2)
        {
            if (_currentIndex == 0 && toolboxDrawerLocation != null) { _currentIndex++; return toolboxDrawerLocation; }
            if (_currentIndex == 1 && toolboxLocation != null) { _currentIndex++; return toolboxLocation; }
            if (_currentIndex == 2 && sofaHammerLocation != null) { _currentIndex++; return sofaHammerLocation; }
            if (_currentIndex == 3 && windowLocation != null) { _currentIndex++; return windowLocation; }
        }

        var triggers = FindObjectsByType<PhoneMessageTrigger>(FindObjectsSortMode.None);
        if (triggers.Length > 0)
        {
            var closest = triggers[Random.Range(0, triggers.Length)];
            float bestDist = float.MaxValue;
            foreach (var t in triggers)
            {
                float d = Vector3.Distance(transform.position, t.transform.position);
                if (d < bestDist && d > 2f) { bestDist = d; closest = t; }
            }
            return closest.transform;
        }

        return null;
    }

    private bool TryAutoInteract()
    {
        bool did = false;
        if (TryPickupClosest()) did = true;
        if (TryPlaceInSlot()) did = true;
        if (TryOpenClosest()) did = true;
        if (TryBreakGlass()) did = true;
        if (TryInteractClosestGeneric()) did = true;
        return did;
    }

    private bool TryPickupClosest()
    {
        if (!autoPickup) return false;
        var carry = PlayerCarry.Instance;
        if (carry != null && carry.IsCarrying) return false;

        var items = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
        PlaceableItem closest = null;
        float bestDist = float.MaxValue;
        foreach (var item in items)
        {
            if (item.IsHeld) continue;
            if (item.OccupyingSlot != null) continue;
            float d = Vector3.Distance(transform.position, item.transform.position);
            if (d < interactRange && d < bestDist) { bestDist = d; closest = item; }
        }
        if (closest != null)
        {
            Debug.Log($"[AutoPlayerMover] Auto pickup {closest.DisplayName}");
            closest.OnInteract();
            return true;
        }

        var inters = FindObjectsByType<Interactable>(FindObjectsSortMode.None);
        Interactable closestI = null;
        bestDist = float.MaxValue;
        foreach (var inter in inters)
        {
            if (inter == null) continue;
            if (inter.GetComponent<OpenableFurniture>() != null) continue;
            if (inter.GetComponent<ToolBoxInteractable>() != null) continue;
            float d = Vector3.Distance(transform.position, inter.transform.position);
            if (d < interactRange && d < bestDist) { bestDist = d; closestI = inter; }
        }
        if (closestI != null)
        {
            Debug.Log($"[AutoPlayerMover] Auto pickup Interactable {closestI.name}");
            var pc = PlayerCarry.Instance;
            if (pc != null) pc.TryPickUp(closestI);
            else closestI.GetComponent<IInteractable>()?.OnInteract();
            return true;
        }

        return false;
    }

    private bool TryPlaceInSlot()
    {
        if (!autoPlace) return false;
        var carry = PlayerCarry.Instance;
        if (carry == null || !carry.IsCarrying) return false;

        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        PlacementSlot closest = null;
        float bestDist = float.MaxValue;
        foreach (var slot in slots)
        {
            if (slot.IsOccupied) continue;
            float d = Vector3.Distance(transform.position, slot.transform.position);
            if (d < interactRange && d < bestDist) { bestDist = d; closest = slot; }
        }
        if (closest != null)
        {
            Debug.Log($"[AutoPlayerMover] Auto place into {closest.SlotId}");
            closest.OnInteract();
            return true;
        }
        return false;
    }

    private bool TryOpenClosest()
    {
        if (!autoOpen) return false;

        var furnitures = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
        foreach (var f in furnitures)
        {
            float d = Vector3.Distance(transform.position, f.transform.position);
            if (d < interactRange)
            {
                Debug.Log($"[AutoPlayerMover] Auto interact furniture {f.name} locked={f.IsLocked} open={f.IsOpen}");
                f.Interact();
                return true;
            }
        }

        var toolboxes = FindObjectsByType<ToolBoxInteractable>(FindObjectsSortMode.None);
        foreach (var tb in toolboxes)
        {
            float d = Vector3.Distance(transform.position, tb.transform.position);
            if (d < interactRange)
            {
                Debug.Log($"[AutoPlayerMover] Auto interact toolbox {tb.name}");
                tb.OnInteract();
                return true;
            }
        }

        return false;
    }

    private bool TryBreakGlass()
    {
        if (!autoBreakWindow) return false;
        var glasses = FindObjectsByType<Glass>(FindObjectsSortMode.None);
        foreach (var g in glasses)
        {
            float d = Vector3.Distance(transform.position, g.transform.position);
            if (d < interactRange + 1f)
            {
                Debug.Log($"[AutoPlayerMover] Auto break glass {g.name}");
                g.BreakFromHammer();
                return true;
            }
        }
        return false;
    }

    private bool TryInteractClosestGeneric()
    {
        var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        IInteractable closest = null;
        float bestDist = float.MaxValue;
        Vector3 pos = transform.position;
        foreach (var mb in all)
        {
            if (mb is IInteractable inter)
            {
                if (!inter.CanInteract) continue;
                if (mb is PlacementSlot) continue;
                if (mb is PlaceableItem) continue;
                if (mb is OpenableFurniture) continue;
                if (mb is ToolBoxInteractable) continue;
                float d = Vector3.Distance(pos, mb.transform.position);
                if (d < interactRange && d < bestDist) { bestDist = d; closest = inter; }
            }
        }
        if (closest != null)
        {
            Debug.Log($"[AutoPlayerMover] Auto generic interact {closest}");
            closest.OnInteract();
            return true;
        }
        return false;
    }
}
