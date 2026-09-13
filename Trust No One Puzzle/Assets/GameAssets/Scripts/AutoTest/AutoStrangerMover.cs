using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Automatically moves stranger/ghost NPC via NavMesh for testing.
/// Fully automatic - disables any manual AI.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AutoStrangerMover : MonoBehaviour
{
    [Header("Tour Waypoints")]
    [SerializeField] private Transform[] tourWaypoints;
    [SerializeField] private float waitAtPoint = 2f;
    [SerializeField] private float reachDistance = 0.6f;

    [Header("Disappear Logic")]
    [SerializeField] private Transform bathroomMirrorLocation;
    [SerializeField] private float disappearDelay = 1f;
    [SerializeField] private GameObject ghostSmileyPrefab;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 1.5f;

    private NavMeshAgent _agent;
    private int _index;
    private float _waitTimer;
    private bool _isTouring;
    private bool _hasDisappeared;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _agent.speed = walkSpeed;
        _agent.angularSpeed = 180f;
        _agent.stoppingDistance = reachDistance;

        // Disable any manual controllers on stranger
        var ccs = GetComponents<CharacterController>();
        foreach (var cc in ccs) cc.enabled = false;
    }

    private void Start()
    {
        if (NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
            _agent.Warp(hit.position);

        StartTour();
    }

    public void StartTour()
    {
        _isTouring = true;
        _hasDisappeared = false;
        _index = 0;
        if (tourWaypoints != null && tourWaypoints.Length > 0 && tourWaypoints[0] != null)
        {
            SetDest(tourWaypoints[0].position);
        }
        else
        {
            // Auto-find some points: entrance, mirror, etc.
            var triggers = FindObjectsByType<PhoneMessageTrigger>(FindObjectsSortMode.None);
            if (triggers.Length > 0)
            {
                SetDest(triggers[0].transform.position);
            }
        }
    }

    private void SetDest(Vector3 pos)
    {
        if (!_agent.isOnNavMesh) return;
        if (NavMesh.SamplePosition(pos, out var hit, 5f, NavMesh.AllAreas))
            pos = hit.position;
        _agent.SetDestination(pos);
    }

    private void Update()
    {
        if (!_isTouring || _hasDisappeared) return;
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }

        if (!_agent.isOnNavMesh) return;

        if (!_agent.pathPending && _agent.remainingDistance <= reachDistance + 0.2f)
        {
            _waitTimer = waitAtPoint;
            _index++;
            if (tourWaypoints != null && _index < tourWaypoints.Length)
            {
                if (tourWaypoints[_index] != null) SetDest(tourWaypoints[_index].position);
            }
            else
            {
                if (bathroomMirrorLocation != null && _index == (tourWaypoints?.Length ?? 0))
                {
                    SetDest(bathroomMirrorLocation.position);
                    _index = -1; // will disappear next
                }
                else if (_index == -1 && bathroomMirrorLocation != null)
                {
                    Disappear();
                }
                else if (tourWaypoints == null || tourWaypoints.Length == 0)
                {
                    // No waypoints, just disappear after tour
                    Disappear();
                }
            }
        }

        if (_index == -1 && bathroomMirrorLocation != null && !_agent.pathPending && _agent.remainingDistance <= reachDistance + 0.5f)
            Disappear();
    }

    private void Disappear()
    {
        if (_hasDisappeared) return;
        _hasDisappeared = true;
        _isTouring = false;
        Debug.Log("[AutoStrangerMover] Stranger has no reflection! Real agent message: colleague had accident, never came. Doors locked.");
        if (ghostSmileyPrefab != null && bathroomMirrorLocation != null)
            Instantiate(ghostSmileyPrefab, bathroomMirrorLocation.position + Vector3.up, Quaternion.identity);
        Invoke(nameof(DoDisappear), disappearDelay);
    }

    private void DoDisappear()
    {
        Debug.Log("[AutoStrangerMover] Stranger disappeared, doors locked, player must hide in small room near exit.");
        gameObject.SetActive(false);
    }
}
