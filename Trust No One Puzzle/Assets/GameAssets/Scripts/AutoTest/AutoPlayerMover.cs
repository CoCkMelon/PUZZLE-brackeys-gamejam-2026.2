using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Automatically moves player via NavMeshAgent for testing puzzles.
/// Attach to Player FPP or TPP. Requires NavMeshAgent.
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

    [Header("Waypoints")]
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

    private NavMeshAgent _agent;
    private int _currentIndex;
    private float _waitTimer;
    private bool _isRunning;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
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
        if (mode == TestMode.ManualWaypoints && waypoints != null && waypoints.Length > 0 && waypoints[0] != null)
            _agent.SetDestination(waypoints[0].position);
        else
            StartRoom1Auto();
    }

    public void StopAuto() { _isRunning = false; _agent.ResetPath(); }

    private void Update()
    {
        if (!_isRunning) return;
        switch (mode)
        {
            case TestMode.ManualWaypoints: UpdateManualWaypoints(); break;
            default: UpdateAutoPuzzle(); break;
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

    private void StartRoom1Auto()
    {
        if (mirrorKeyLocation != null) _agent.SetDestination(mirrorKeyLocation.position);
        else if (waypoints != null && waypoints.Length > 0 && waypoints[0] != null) _agent.SetDestination(waypoints[0].position);
    }

    private void UpdateAutoPuzzle()
    {
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }
        if (!_agent.pathPending && _agent.remainingDistance <= waypointReachDistance + 0.5f)
        {
            _waitTimer = waitAtWaypoint;
            // Cycle through assigned locations in order for full game auto
            Transform next = null;
            if (mode == TestMode.FullGameAuto || mode == TestMode.AutoPuzzleRoom1)
            {
                if (_currentIndex == 0) next = mirrorKeyLocation;
                else if (_currentIndex == 1) next = cabinetLocation;
                else if (_currentIndex == 2) next = placementTableLocation;
                else if (_currentIndex == 3) next = toolboxDrawerLocation;
                else if (_currentIndex == 4) next = toolboxLocation;
                else if (_currentIndex == 5) next = sofaHammerLocation;
                else if (_currentIndex == 6) next = windowLocation;
            }
            else if (mode == TestMode.AutoPuzzleRoom2)
            {
                if (_currentIndex == 0) next = toolboxDrawerLocation;
                else if (_currentIndex == 1) next = toolboxLocation;
                else if (_currentIndex == 2) next = sofaHammerLocation;
                else if (_currentIndex == 3) next = windowLocation;
            }

            if (next == null && waypoints != null && waypoints.Length > 0)
            {
                _currentIndex = (_currentIndex + 1) % waypoints.Length;
                next = waypoints[_currentIndex];
            }
            else
            {
                _currentIndex++;
            }

            if (next != null) _agent.SetDestination(next.position);
            TryInteract();
        }
    }

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
