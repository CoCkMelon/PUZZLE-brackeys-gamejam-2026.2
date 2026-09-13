using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Automatically moves stranger/ghost NPC via NavMesh for testing.
/// Attach to stranger/ghost GameObject. Requires NavMeshAgent.
/// Implements the story tour where stranger moves through rooms and disappears.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AutoStrangerMover : MonoBehaviour
{
    [Header("Tour Waypoints")]
    [SerializeField] private Transform[] tourWaypoints;
    [SerializeField] private float waitAtPoint = 2f;
    [SerializeField] private float reachDistance = 0.5f;

    [Header("Disappear Logic")]
    [SerializeField] private Transform bathroomMirrorLocation;
    [SerializeField] private float disappearDelay = 1f;
    [SerializeField] private GameObject ghostSmileyPrefab;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 1.5f;
    [SerializeField] private float runSpeed = 3f;

    private NavMeshAgent _agent;
    private int _index;
    private float _waitTimer;
    private bool _isTouring;
    private bool _hasDisappeared;

    private void Awake() { _agent = GetComponent<NavMeshAgent>(); _agent.speed = walkSpeed; _agent.angularSpeed = 180f; }
    private void Start() => StartTour();

    public void StartTour()
    {
        _isTouring = true;
        _index = 0;
        if (tourWaypoints != null && tourWaypoints.Length > 0 && tourWaypoints[0] != null)
            _agent.SetDestination(tourWaypoints[0].position);
    }

    private void Update()
    {
        if (!_isTouring || _hasDisappeared) return;
        if (_waitTimer > 0) { _waitTimer -= Time.deltaTime; return; }
        if (!_agent.pathPending && _agent.remainingDistance <= reachDistance)
        {
            _waitTimer = waitAtPoint;
            _index++;
            if (_index < tourWaypoints.Length)
            {
                if (tourWaypoints[_index] != null) _agent.SetDestination(tourWaypoints[_index].position);
            }
            else
            {
                if (bathroomMirrorLocation != null) { _agent.SetDestination(bathroomMirrorLocation.position); _index = -1; }
                else Disappear();
            }
        }
        if (_index == -1 && bathroomMirrorLocation != null && !_agent.pathPending && _agent.remainingDistance <= reachDistance) Disappear();
    }

    private void Disappear()
    {
        if (_hasDisappeared) return;
        _hasDisappeared = true;
        _isTouring = false;
        Debug.Log("[AutoStrangerMover] Stranger has no reflection! Real agent message: colleague had accident, never came.");
        if (ghostSmileyPrefab != null && bathroomMirrorLocation != null) Instantiate(ghostSmileyPrefab, bathroomMirrorLocation.position, Quaternion.identity);
        Invoke(nameof(DoDisappear), disappearDelay);
    }

    private void DoDisappear() { gameObject.SetActive(false); Debug.Log("[AutoStrangerMover] Stranger disappeared, doors locked."); }
}
