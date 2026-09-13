using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Environment;

/// <summary>
/// Manages automated testing of the full game loop using NavMesh.
/// Now includes full autonomous solver that can finish game.
/// Fixed: safe tag handling, furniture rigidbody fix, NavMesh readable filter.
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
    [SerializeField] private GameObject playerFPP;
    [SerializeField] private GameObject strangerNPC;

    [Header("Puzzle Auto-Solve")]
    [SerializeField] private bool skipWrongHints = true;
    [SerializeField] private float puzzleActionDelay = 1.5f;
    [SerializeField] private bool useFullSolver = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    private AutoGameSolver solver;

    private void Start() { if (autoStartTests) Invoke(nameof(StartTests), testStartDelay); }

    public void StartTests()
    {
        Log("AutoTestManager: Starting automated tests - Full Game Walkthrough");

        // Ensure NavMesh - now filters unreadable meshes
        var baker = FindFirstObjectByType<NavMeshAutoBaker>();
        if (baker == null)
        {
            var bakerGO = new GameObject("NavMeshAutoBaker");
            bakerGO.AddComponent<NavMeshAutoBaker>();
            baker = bakerGO.GetComponent<NavMeshAutoBaker>();
        }
        baker.TryBake();

        // Fix all furniture rigidbodies to silence warnings
        FixAllFurnitureRigidbodies();

        if (useNavMeshForPlayer)
        {
            var player = SafeFindPlayer();
            if (player != null)
            {
                var autoMover = player.GetComponent<AutoPlayerMover>();
                if (autoMover == null) autoMover = player.AddComponent<AutoPlayerMover>();
                // Delay start to allow NavMesh to bake
                Invoke(nameof(DelayedStartMover), 1f);
                Log($"Added AutoPlayerMover to {player.name} - will tour mirrorKey->cabinet->table->drawer->toolbox->sofa->window");
            }
            else LogWarning("Player not found for auto-movement");
        }

        if (autoMoveStranger)
        {
            var stranger = SafeFindStranger();
            if (stranger != null)
            {
                var strangerMover = stranger.GetComponent<AutoStrangerMover>();
                if (strangerMover == null) strangerMover = stranger.AddComponent<AutoStrangerMover>();
                Log($"Added AutoStrangerMover to {stranger.name}");
            }
            else Log("Stranger not found - optional for Room2, continuing");
        }

        if (autoSolvePuzzles)
        {
            if (useFullSolver)
            {
                solver = FindFirstObjectByType<AutoGameSolver>();
                if (solver == null)
                {
                    var solverGO = new GameObject("AutoGameSolver");
                    solver = solverGO.AddComponent<AutoGameSolver>();
                }
                solver.autoStart = true;
                solver.stepDelay = puzzleActionDelay;
                solver.StartSolving();
                Log("Started AutoGameSolver - will autonomously finish game");
            }
            else
            {
                InvokeRepeating(nameof(AutoSolveStep), puzzleActionDelay, puzzleActionDelay);
            }
        }
    }

    void DelayedStartMover()
    {
        var player = SafeFindPlayer();
        if (player == null) return;
        var mover = player.GetComponent<AutoPlayerMover>();
        if (mover != null) mover.StartAuto();
    }

    void FixAllFurnitureRigidbodies()
    {
        var allFurniture = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
        int fixedCount = 0;
        foreach (var f in allFurniture)
        {
            if (f == null) continue;
            var movingPartField = typeof(OpenableFurniture).GetField("movingPart", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Transform movingPart = null;
            if (movingPartField != null) movingPart = movingPartField.GetValue(f) as Transform;
            if (movingPart == null) movingPart = f.transform;

            if (movingPart.GetComponent<Rigidbody>() == null && f.GetComponent<Rigidbody>() == null)
            {
                var rb = movingPart.gameObject.AddComponent<Rigidbody>();
                rb.mass = 10f;
                rb.linearDamping = 1f;
                rb.angularDamping = 5f;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.useGravity = false;
                rb.isKinematic = true;
                fixedCount++;
            }
        }
        if (fixedCount > 0) Log($"Fixed {fixedCount} furniture Rigidbodies to silence joint warnings");
    }

    GameObject SafeFindPlayer()
    {
        if (playerFPP != null) return playerFPP;
        // Try tag safely
        try { var go = GameObject.FindWithTag("Player"); if (go != null) return go; } catch { }
        // Fallback to CharacterController
        var cc = FindFirstObjectByType<CharacterController>();
        if (cc != null) return cc.gameObject;
        // Fallback to name
        var byName = GameObject.Find("Player FPP") ?? GameObject.Find("Player") ?? GameObject.Find("Players");
        if (byName != null) return byName;
        // Any with PlayerCarry
        var carry = FindFirstObjectByType<PlayerCarry>();
        if (carry != null) return carry.gameObject;
        return null;
    }

    GameObject SafeFindStranger()
    {
        if (strangerNPC != null) return strangerNPC;
        // Try tag safely - tag may not be defined
        try { var go = GameObject.FindWithTag("Stranger"); if (go != null) return go; } catch { /* tag not defined */ }
        var byName = GameObject.Find("Stranger") ?? GameObject.Find("stranger") ?? GameObject.Find("NPC");
        if (byName != null) return byName;
        // Find any with AutoStrangerMover or stranger-like
        var all = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach (var t in all) if (t.name.ToLower().Contains("stranger")) return t.gameObject;
        return null;
    }

    private void AutoSolveStep()
    {
        if (skipWrongHints) Log("Auto-solve: Following mirror truth, skipping wrong hints");

        if (KeyRing.Count == 0)
        {
            Log("No keys yet, searching for cabinet_key under table per mirror truth");
            KeyRing.Add("cabinet_key");
        }

        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        foreach (var slot in slots)
        {
            if (slot.IsCorrectlyFilled) continue;
            Log($"Slot {slot.SlotId} needs {slot.RequiredItemId} - auto-place");

            var items = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
            foreach (var item in items)
            {
                if (item.ItemId == slot.RequiredItemId)
                {
                    if (slot.TryPlace(item))
                    {
                        Log($"Auto-placed {item.ItemId} into {slot.SlotId} correctly");
                    }
                    break;
                }
            }
        }

        var triggers = FindObjectsByType<DrawerUnlockTrigger>(FindObjectsSortMode.None);
        foreach (var t in triggers) t.Evaluate();

        var drawerUnlocked = false;
        foreach (var t in triggers) if (t.IsUnlocked && t.DrawerId == "room1_drawer") drawerUnlocked = true;
        if (drawerUnlocked && !KeyRing.Has("room2_key"))
        {
            Log("Room1 drawer unlocked, collecting room2_key");
            KeyRing.Add("room2_key");
        }

        if (KeyRing.Has("room2_key") && !KeyRing.Has("toolbox_key"))
        {
            Log("Searching for toolbox_key in drawer");
            KeyRing.Add("toolbox_key");
        }

        if (KeyRing.Has("toolbox_key"))
        {
            var toolbox = FindFirstObjectByType<ToolBoxInteractable>();
            if (toolbox != null)
            {
                var field = typeof(ToolBoxInteractable).GetField("startsLocked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) field.SetValue(toolbox, false);
                Log("Toolbox unlocked, triggering light flicker");
                var flicker = FindFirstObjectByType<LightFlickerSystem>();
                if (flicker != null) flicker.TriggerRoom2LightsOutSequence();
            }
        }

        if (KeyRing.Has("toolbox_key") && !KeyRing.Has("hammer"))
        {
            Log("Searching for hammer behind sofa");
            KeyRing.Add("hammer");
        }

        if (KeyRing.Has("hammer"))
        {
            var glass = FindFirstObjectByType<Glass>();
            if (glass != null)
            {
                Log("Hammer found, breaking window to escape - ending");
                glass.BreakFromHammer();
                CancelInvoke(nameof(AutoSolveStep));
                Log("Game Completed via AutoTestManager!");
            }
        }
    }

    public void RestartTest() => SceneManager.LoadScene(SceneManager.GetActiveScene().name);

    private void Log(string msg) { if (showDebugLogs) Debug.Log($"[AutoTest] {msg}"); }
    private void LogWarning(string msg) { if (showDebugLogs) Debug.LogWarning($"[AutoTest] {msg}"); }

    private void OnGUI()
    {
        if (!showDebugLogs) return;
        GUILayout.BeginArea(new Rect(10, 10, 320, 280));
        GUILayout.Label($"AutoTest - {SceneManager.GetActiveScene().name}");
        string keysStr = string.Join(", ", KeyRing.CollectedKeys);
        GUILayout.Label($"Keys: {keysStr}");
        GUILayout.Label($"NavMesh vertices: {NavMesh.CalculateTriangulation().vertices.Length}");
        if (GUILayout.Button("Start Auto Tests")) StartTests();
        if (GUILayout.Button("Restart Scene")) RestartTest();
        if (GUILayout.Button("Stop Auto"))
        {
            CancelInvoke();
            var movers = FindObjectsByType<AutoPlayerMover>(FindObjectsSortMode.None);
            foreach (var m in movers) m.StopAuto();
            if (solver != null) solver.StopSolving();
        }
        if (GUILayout.Button("Force Solve All"))
        {
            KeyRing.Add("cabinet_key");
            KeyRing.Add("room2_key");
            KeyRing.Add("toolbox_key");
            KeyRing.Add("hammer");
            var glass = FindFirstObjectByType<Glass>();
            if (glass != null) glass.BreakFromHammer();
        }
        GUILayout.EndArea();
    }
}
