using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using GameAssets.Scripts.Puzzle;

/// <summary>
/// Manages automated testing of the full game loop using NavMesh.
/// Attach to empty GameObject in AutoTest scenes.
/// Speeds up testing by automatically moving player and NPCs through puzzles.
/// Copy of Game scene but with auto-test logic - does not edit original Game scene.
/// </summary>
public class AutoTestManager : MonoBehaviour
{
    [Header("Test Settings")]
    [SerializeField] private bool autoStartTests = true;
    [SerializeField] private float testStartDelay = 2f;
    [SerializeField] private bool useNavMeshForPlayer = true;
    [SerializeField] private bool autoMoveStranger = true;
    [SerializeField] private bool autoSolvePuzzles = true;

    [Header("References")]
    [SerializeField] private Transform playerStart;
    [SerializeField] private Transform strangerStart;
    [SerializeField] private GameObject playerFPP;
    [SerializeField] private GameObject strangerNPC;

    [Header("Puzzle Auto-Solve")]
    [SerializeField] private bool skipWrongHints = true;
    [SerializeField] private float puzzleActionDelay = 1f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    private void Start() { if (autoStartTests) Invoke(nameof(StartTests), testStartDelay); }

    public void StartTests()
    {
        Log("AutoTestManager: Starting automated tests");
        var baker = FindFirstObjectByType<NavMeshAutoBaker>();
        if (baker == null) { var bakerGO = new GameObject("NavMeshAutoBaker"); bakerGO.AddComponent<NavMeshAutoBaker>(); baker = bakerGO.GetComponent<NavMeshAutoBaker>(); }
        baker.TryBake();

        if (useNavMeshForPlayer)
        {
            var player = playerFPP ?? GameObject.FindWithTag("Player") ?? FindFirstObjectByType<CharacterController>()?.gameObject;
            if (player != null)
            {
                var autoMover = player.GetComponent<AutoPlayerMover>();
                if (autoMover == null) autoMover = player.AddComponent<AutoPlayerMover>();
                autoMover.StartAuto();
                Log($"Added AutoPlayerMover to {player.name}");
            }
            else LogWarning("Player not found for auto-movement");
        }

        if (autoMoveStranger)
        {
            var stranger = strangerNPC ?? GameObject.Find("Stranger") ?? GameObject.FindWithTag("Stranger");
            if (stranger != null)
            {
                var strangerMover = stranger.GetComponent<AutoStrangerMover>();
                if (strangerMover == null) strangerMover = stranger.AddComponent<AutoStrangerMover>();
                Log($"Added AutoStrangerMover to {stranger.name}");
            }
        }

        if (autoSolvePuzzles) InvokeRepeating(nameof(AutoSolveStep), puzzleActionDelay, puzzleActionDelay);
    }

    private void AutoSolveStep()
    {
        if (skipWrongHints) Log("Auto-solve: Skipping wrong hints, following mirror truth");
        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        foreach (var slot in slots) { if (slot.IsCorrectlyFilled) continue; Log($"Slot {slot.SlotId} not correctly filled, would auto-place correct item"); }
    }

    public void RestartTest() => SceneManager.LoadScene(SceneManager.GetActiveScene().name);

    private void Log(string msg) { if (showDebugLogs) Debug.Log($"[AutoTest] {msg}"); }
    private void LogWarning(string msg) { if (showDebugLogs) Debug.LogWarning($"[AutoTest] {msg}"); }

    private void OnGUI()
    {
        if (!showDebugLogs) return;
        GUILayout.BeginArea(new Rect(10, 10, 300, 200));
        GUILayout.Label($"AutoTest - {SceneManager.GetActiveScene().name}");
        if (GUILayout.Button("Start Auto Tests")) StartTests();
        if (GUILayout.Button("Restart Scene")) RestartTest();
        if (GUILayout.Button("Stop Auto")) { CancelInvoke(); var movers = FindObjectsByType<AutoPlayerMover>(FindObjectsSortMode.None); foreach (var m in movers) m.StopAuto(); }
        GUILayout.EndArea();
    }
}
