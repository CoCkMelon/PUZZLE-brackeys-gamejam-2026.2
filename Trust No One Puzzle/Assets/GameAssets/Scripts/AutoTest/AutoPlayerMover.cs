using UnityEngine;
using UnityEngine.AI;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.Entities.Player;

/// <summary>
/// Automatically moves player via NavMeshAgent for testing puzzles.
/// Attach to Player FPP or TPP. Requires NavMeshAgent.
/// When enabled, takes control of player movement to speed up testing.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AutoPlayerMover : MonoBehaviour
{
    public enum TestMode { ManualWaypoints, AutoPuzzleRoom1, AutoPuzzleRoom2, FullGameAuto }

    [Header("Mode")]
    [SerializeField] private TestMode mode = TestMode.FullGameAuto;
    [SerializeField] private bool autoStart = true;
    [SerializeField] private float waypointReachDistance = 0.5f;
    [SerializeField] private float waitAtWaypoint = 0.5f;

    [Header("Manual Waypoints")]
    [SerializeField] private Transform[] waypoints;

    [Header("Auto Puzzle - Room 1")]
    [SerializeField] private Transform mirrorKeyLocation;
    [SerializeField] private Transform cabinetLocation;
    [SerializeField] private Transform bookLocation;
    [SerializeField] private Transform candleLocation;
    [SerializeField] private Transform vaseLocation;
    [SerializeField] private Transform placementTableLocation;
    [SerializeField] private Transform drawerKeyLocation;

    [Header("Auto Puzzle - Room 2")]
    [SerializeField] private Transform toolboxDrawerLocation;
    [SerializeField] private Transform toolboxLocation;
    [SerializeField] private Transform sofaHammerLocation;
    [SerializeField] private Transform windowLocation;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float runSpeed = 5f;
    [SerializeField] private bool useRun = false;

    private NavMeshAgent _agent;
    private int _currentIndex;
    private float _waitTimer;
    private bool _isRunning;
    private PlayerCarry _carry;
    private FPPCameraController _fppCamera;
    private enum Room1State { GoToKey, PickupKey, GoToCabinet, OpenCabinet, GoToObjects, PickupObjects, PlaceObjects, GoToNextKey, Done }
    private Room1State _room1State;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _carry = GetComponentInChildren<PlayerCarry>() ?? FindFirstObjectByType<PlayerCarry>();
        _fppCamera = GetComponentInChildren<FPPCameraController>() ?? FindFirstObjectByType<FPPCameraController>();
        _agent.speed = useRun ? runSpeed : moveSpeed;
        _agent.angularSpeed = 360f;
        _agent.acceleration = 8f;
        _agent.stoppingDistance = waypointReachDistance;
    }

    private void Start() { if (autoStart) StartAuto(); }

    public void StartAuto()
    {
        _isRunning = true;
        _currentIndex = 0;
        _waitTimer = 0f;
        _room1State = Room1State.GoToKey;
        if (mode == TestMode.ManualWaypoints && waypoints != null && waypoints.Length > 0)
            _agent.SetDestination(waypoints[0].position);
        else if (mode == TestMode.FullGameAuto)
            StartRoom1Auto();
    }

    public void StopAuto() { _isRunning = false; _agent.ResetPath(); }

    private void Update()
    {
        if (!_isRunning) return;
        switch (mode)
        {
            case TestMode.ManualWaypoints: UpdateManualWaypoints(); break;
            case TestMode.AutoPuzzleRoom1: UpdateRoom1Auto(); break;
            case TestMode.AutoPuzzleRoom2: UpdateRoom2Auto(); break;
            case TestMode.FullGameAuto: UpdateFullGameAuto(); break;
        }
    }

    private void UpdateManualWaypoints()
    {
        if (waypoints == null || waypoints.Length == 0) return;
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }
        if (!_agent.pathPending && _agent.remainingDistance <= waypointReachDistance)
        {
            _waitTimer = waitAtWaypoint;
            _currentIndex = (_currentIndex + 1) % waypoints.Length;
            if (waypoints[_currentIndex] != null) _agent.SetDestination(waypoints[_currentIndex].position);
        }
    }

    private void StartRoom1Auto() { _room1State = Room1State.GoToKey; if (mirrorKeyLocation != null) _agent.SetDestination(mirrorKeyLocation.position); }

    private void UpdateRoom1Auto()
    {
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }
        switch (_room1State)
        {
            case Room1State.GoToKey:
                if (Reached(mirrorKeyLocation)) { TryInteract(); _waitTimer = 1f; _room1State = Room1State.GoToCabinet; if (cabinetLocation != null) _agent.SetDestination(cabinetLocation.position); }
                break;
            case Room1State.GoToCabinet:
                if (Reached(cabinetLocation)) { TryInteract(); _waitTimer = 1f; _room1State = Room1State.GoToObjects; if (bookLocation != null) _agent.SetDestination(bookLocation.position); }
                break;
            default: UpdateManualWaypoints(); break;
        }
    }

    private void UpdateRoom2Auto() => UpdateManualWaypoints();
    private void UpdateFullGameAuto() => UpdateRoom1Auto();

    private bool Reached(Transform target) { if (target == null) return true; return !_agent.pathPending && _agent.remainingDistance <= waypointReachDistance + 0.5f; }

    private void TryInteract()
    {
        var openable = FindClosestOpenable();
        openable?.Interact();
    }

    private GameAssets.Scripts.Environment.OpenableFurniture FindClosestOpenable()
    {
        var all = FindObjectsByType<GameAssets.Scripts.Environment.OpenableFurniture>(FindObjectsSortMode.None);
        GameAssets.Scripts.Environment.OpenableFurniture closest = null;
        float bestDist = float.MaxValue;
        foreach (var o in all) { float d = Vector3.Distance(transform.position, o.transform.position); if (d < bestDist && d < 3f) { bestDist = d; closest = o; } }
        return closest;
    }
}
