using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.Entities.Player;
using GameAssets.Scripts.Interaction;

/// <summary>
/// Fully automatic test manager - makes game play on its own.
/// Disables manual FPP, bakes NavMesh, adds AutoPlayerMover and AutoStrangerMover,
/// and auto-solves puzzles by interacting with correct items (skipping wrong hints).
/// </summary>
public class AutoTestManager : MonoBehaviour
{
    [Header("Test Settings")]
    [SerializeField] private bool autoStartTests = true;
    [SerializeField] private float testStartDelay = 1.5f;
    [SerializeField] private bool useNavMeshForPlayer = true;
    [SerializeField] private bool autoMoveStranger = true;
    [SerializeField] private bool autoSolvePuzzles = true;
    [SerializeField] private bool disableManualControllers = true;

    [Header("References (auto-found if null)")]
    [SerializeField] private GameObject playerFPP;
    [SerializeField] private GameObject strangerNPC;

    [Header("Puzzle Auto-Solve")]
    [SerializeField] private bool skipWrongHints = true;
    [SerializeField] private float puzzleActionDelay = 1f;
    [SerializeField] private bool autoUnlockAllDrawers = false;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;
    [SerializeField] private bool showOnGUI = true;

    private bool _started;

    private void Start()
    {
        if (autoStartTests) Invoke(nameof(StartTests), testStartDelay);
    }

    public void StartTests()
    {
        if (_started) return;
        _started = true;

        Log($"AutoTestManager: Starting automated tests in scene {SceneManager.GetActiveScene().name}");

        // 1. Bake NavMesh
        var baker = FindFirstObjectByType<NavMeshAutoBaker>();
        if (baker == null)
        {
            var bakerGO = new GameObject("NavMeshAutoBaker");
            baker = bakerGO.AddComponent<NavMeshAutoBaker>();
        }
        baker.TryBake();

        // 2. Setup player for auto
        if (useNavMeshForPlayer)
        {
            SetupPlayerForAuto();
        }

        // 3. Setup stranger
        if (autoMoveStranger)
        {
            SetupStrangerForAuto();
        }

        // 4. Auto-solve loop
        if (autoSolvePuzzles)
        {
            InvokeRepeating(nameof(AutoSolveStep), puzzleActionDelay, puzzleActionDelay);
            InvokeRepeating(nameof(AutoUnlockStep), puzzleActionDelay * 1.5f, puzzleActionDelay * 1.5f);
        }

        // 5. Unlock cursor
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // 6. Ensure Mobile UI exists
        EnsureMobileUI();

        Log("AutoTestManager: Fully automatic mode - manual FPP disabled, NavMeshAgent driving player");
    }

    private void SetupPlayerForAuto()
    {
        GameObject player = null;
        if (playerFPP != null) player = playerFPP;
        else
        {
            player = GameObject.FindWithTag("Player");
            if (player == null)
            {
                var cc = FindFirstObjectByType<CharacterController>();
                if (cc != null) player = cc.gameObject;
            }
            if (player == null)
            {
                var pc = FindFirstObjectByType<PlayerController>();
                if (pc != null) player = pc.gameObject;
            }
        }

        if (player == null)
        {
            LogWarning("Player not found for auto-movement!");
            return;
        }

        Log($"Found player: {player.name}");

        if (disableManualControllers)
        {
            // Disable manual components
            var charController = player.GetComponent<CharacterController>();
            if (charController != null) charController.enabled = false;

            var playerController = player.GetComponent<PlayerController>();
            if (playerController != null) playerController.enabled = false;

            var fppCamera = player.GetComponentInChildren<FPPCameraController>();
            if (fppCamera != null) fppCamera.enabled = false;
            var fppCameras = player.GetComponentsInChildren<FPPCameraController>();
            foreach (var f in fppCameras) f.enabled = false;

            var playerInteraction = player.GetComponent<PlayerInteraction>();
            if (playerInteraction != null) playerInteraction.enabled = false;
            var interactions = player.GetComponentsInChildren<PlayerInteraction>();
            foreach (var pi in interactions) pi.enabled = false;

            // Disable PlayerController in children
            var pcs = player.GetComponentsInChildren<PlayerController>();
            foreach (var pc in pcs) pc.enabled = false;
        }

        // Ensure NavMeshAgent
        var agent = player.GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            agent = player.AddComponent<NavMeshAgent>();
            Log($"Added NavMeshAgent to {player.name}");
        }
        agent.enabled = true;
        agent.speed = 3.5f;
        agent.angularSpeed = 360f;
        agent.acceleration = 12f;
        agent.stoppingDistance = 0.6f;
        agent.autoBraking = true;
        agent.updateRotation = true;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

        // Warp to NavMesh
        if (NavMesh.SamplePosition(player.transform.position, out var hit, 10f, NavMesh.AllAreas))
        {
            agent.Warp(hit.position);
            Log($"Warped player to NavMesh at {hit.position}");
        }
        else
        {
            LogWarning("Player not on NavMesh! Baking may have failed or level has no walkable area");
        }

        // Ensure AutoPlayerMover
        var autoMover = player.GetComponent<AutoPlayerMover>();
        if (autoMover == null)
        {
            autoMover = player.AddComponent<AutoPlayerMover>();
            Log($"Added AutoPlayerMover to {player.name}");
        }
        autoMover.enabled = true;
        autoMover.StartAuto();
    }

    private void SetupStrangerForAuto()
    {
        GameObject stranger = null;
        if (strangerNPC != null) stranger = strangerNPC;
        else
        {
            stranger = GameObject.Find("Stranger");
            if (stranger == null) stranger = GameObject.FindWithTag("Stranger");
            if (stranger == null)
            {
                var all = FindObjectsByType<Transform>(FindObjectsSortMode.None);
                foreach (var t in all)
                {
                    if (t.name.ToLower().Contains("stranger")) { stranger = t.gameObject; break; }
                }
            }
        }

        if (stranger == null)
        {
            Log("Stranger not found, skipping stranger auto");
            return;
        }

        Log($"Found stranger: {stranger.name}");

        var agent = stranger.GetComponent<NavMeshAgent>();
        if (agent == null) agent = stranger.AddComponent<NavMeshAgent>();
        agent.enabled = true;
        agent.speed = 1.5f;

        if (NavMesh.SamplePosition(stranger.transform.position, out var hit, 10f, NavMesh.AllAreas))
            agent.Warp(hit.position);

        var strangerMover = stranger.GetComponent<AutoStrangerMover>();
        if (strangerMover == null)
        {
            strangerMover = stranger.AddComponent<AutoStrangerMover>();
            Log($"Added AutoStrangerMover to {stranger.name}");
        }
    }

    private void EnsureMobileUI()
    {
        // Check if MobileHud and MobilePhone exist, if not log warning (they should be in scene)
        var hud = GameObject.Find("MobileHud");
        var phone = GameObject.Find("MobilePhone");
        if (hud == null || phone == null)
        {
            LogWarning("MobileHud or MobilePhone not found in scene - phone bubbles may not show. Add them manually or via AutoTest scene setup");
        }
    }

    private void AutoSolveStep()
    {
        if (skipWrongHints)
        {
            // Log only, actual skipping is done by AutoPlayerMover finding correct slots
            // WrongHintSystem messages are misleading, we ignore them by going to correct item
        }

        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        int unsolved = 0;
        foreach (var slot in slots)
        {
            if (!slot.IsCorrectlyFilled) unsolved++;
        }
        if (unsolved > 0) Log($"Auto-solve: {unsolved} slots not correctly filled, AutoPlayerMover will auto-place");

        // Try to auto-collect keys near player
        var player = GameObject.FindWithTag("Player");
        if (player == null) player = FindFirstObjectByType<CharacterController>()?.gameObject;
        if (player != null)
        {
            var keyItems = FindObjectsByType<KeyItem>(FindObjectsSortMode.None);
            foreach (var k in keyItems)
            {
                if (Vector3.Distance(player.transform.position, k.transform.position) < 3f)
                {
                    // Auto interact
                    var interactable = k.GetComponent<IInteractable>();
                    interactable?.OnInteract();
                }
            }
        }
    }

    private void AutoUnlockStep()
    {
        if (autoUnlockAllDrawers)
        {
            var drawers = FindObjectsByType<DrawerUnlockTrigger>(FindObjectsSortMode.None);
            foreach (var d in drawers) d.ForceUnlock();
        }

        // If player has hammer and is near window, break it
        var player = GameObject.FindWithTag("Player");
        if (player != null)
        {
            var carry = PlayerCarry.Instance;
            bool hasHammer = false;
            if (carry != null && carry.IsCarrying)
            {
                if (carry.HeldItem != null && carry.HeldItem.ItemId.ToLower().Contains("hammer")) hasHammer = true;
                if (carry.HeldInteractable != null && carry.HeldInteractable.name.ToLower().Contains("hammer")) hasHammer = true;
            }
            if (hasHammer)
            {
                var glasses = FindObjectsByType<Glass>(FindObjectsSortMode.None);
                foreach (var g in glasses)
                {
                    if (Vector3.Distance(player.transform.position, g.transform.position) < 4f)
                    {
                        g.BreakFromHammer();
                        Log("Auto broke glass with hammer!");
                    }
                }
            }
        }
    }

    public void RestartTest()
    {
        Log("Restarting scene");
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    private void Log(string msg) { if (showDebugLogs) Debug.Log($"[AutoTest] {msg}"); }
    private void LogWarning(string msg) { if (showDebugLogs) Debug.LogWarning($"[AutoTest] {msg}"); }

    private void OnGUI()
    {
        if (!showOnGUI || !showDebugLogs) return;
        GUILayout.BeginArea(new Rect(10, 10, 350, 250));
        GUILayout.Label($"<b>AutoTest - {SceneManager.GetActiveScene().name}</b>");
        GUILayout.Label($"Mode: Full Auto - Manual FPP DISABLED");
        GUILayout.Label($"NavMesh: {(NavMesh.CalculateTriangulation().vertices.Length > 0 ? "Baked" : "Not baked")}");
        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        int solved = 0;
        foreach (var s in slots) if (s.IsCorrectlyFilled) solved++;
        GUILayout.Label($"Puzzles: {solved}/{slots.Length} slots solved");
        if (GUILayout.Button("Start Auto Tests (Full Auto)")) StartTests();
        if (GUILayout.Button("Restart Scene")) RestartTest();
        if (GUILayout.Button("Stop Auto"))
        {
            CancelInvoke();
            var movers = FindObjectsByType<AutoPlayerMover>(FindObjectsSortMode.None);
            foreach (var m in movers) m.StopAuto();
        }
        if (GUILayout.Button("Force Unlock All Drawers"))
        {
            var drawers = FindObjectsByType<DrawerUnlockTrigger>(FindObjectsSortMode.None);
            foreach (var d in drawers) d.ForceUnlock();
        }
        GUILayout.EndArea();
    }
}
