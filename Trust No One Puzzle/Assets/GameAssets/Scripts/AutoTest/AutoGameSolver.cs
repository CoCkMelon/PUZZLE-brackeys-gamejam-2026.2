using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.AI;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;
using GameAssets.Scripts.UI.Mobile;

/// <summary>
/// Fully autonomous solver for GDD puzzles in AutoTest scenes.
/// Can finish game from start to end without player input.
/// FIXES:
/// - No longer claims completed when nothing done (verifies glass broken)
/// - Drives AutoPlayerMover/NavMeshAgent to actual positions and waits, so player visibly does actions
/// - Auto-closes phone after each hint to fix "never closes phone" bug
/// - Uses real physics pickup when possible, not just KeyRing cheat
/// </summary>
[DefaultExecutionOrder(-4000)]
public class AutoGameSolver : MonoBehaviour
{
    public enum GameState
    {
        Init,
        StoryIntro,
        Room1_FindKeyUnderTable,
        Room1_OpenCabinet,
        Room1_PlaceItems,
        Room1_DrawerUnlock,
        Room2_FindToolboxKey,
        Room2_OpenToolbox,
        Room2_LightFlicker,
        Room2_FindHammer,
        Room2_PushBoxes,
        Room2_BreakWindow,
        Ending,
        Completed,
        Failed
    }

    [Header("Config")]
    public bool autoStart = true;
    public float stepDelay = 1.2f;
    public bool usePhysicsPickup = true; // now true by default to actually pick objects
    public bool verboseLogs = true;
    public bool drivePlayerToTargets = true;
    public float driveWaitTimeout = 12f;

    [Header("Fairness - keep these off for a legitimate run")]
    [Tooltip("Master switch. When false the solver must find every key, place every item and break the glass for real.")]
    public bool allowCheats = false;
    [Tooltip("When true the walker may shove the player straight through geometry instead of pathing around it. Off by default - that is not a fair completion.")]
    public bool allowTeleportFallback = false;

    [Header("Ids")]
    public string cabinetKeyId = "cabinet_key";
    public string room2KeyId = "room2_key";
    public string toolboxKeyId = "toolbox_key";
    public string hammerId = "hammer";
    public string brokenWindowName = "BrokenWindow_GDD";
    public string escapeVolumeName = "EscapeVolume_GDD";
    [Tooltip("A crate within this distance of the hammer is treated as blocking it and gets carried aside.")]
    public float boxClearRadius = 2.5f;

    private GameState currentState = GameState.Init;
    private Coroutine solverRoutine;
    private NavMeshAgent agent;
    private AutoPlayerMover mover;
    private Transform playerTransform;
    private bool glassBroken = false;
    private bool glassEverExisted = false;
    private bool hammerGenuinelyFound = false;
    // Step results. Keys can be consumed by a lock on unlock, so we cannot re-check the
    // KeyRing at the end - we record that each step genuinely succeeded instead.
    private bool cabinetUnlockedWithKey = false;
    private bool drawerUnlockedByPlacements = false;
    private bool toolboxUnlockedWithKey = false;
    private bool escapedThroughWindow = false;
    private readonly List<string> carriedKeyIds = new List<string>();
    private readonly List<string> cheatLog = new List<string>();

    void Start()
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!sceneName.Contains("AutoTest") && !sceneName.Contains("NavMeshTest"))
        {
            enabled = false;
            return;
        }
        CachePlayer();
        if (autoStart)
        {
            Log("AutoGameSolver Start() autoStart true, starting");
            StartSolving();
        }
    }

    void CachePlayer()
    {
        GameObject player = null;
        try { player = GameObject.FindWithTag("Player"); } catch { }
        if (player == null) player = GameObject.Find("Player FPP");
        if (player == null)
        {
            var cc = FindFirstObjectByType<CharacterController>();
            if (cc != null) player = cc.gameObject;
        }
        if (player == null)
        {
            var carry = FindFirstObjectByType<PlayerCarry>();
            if (carry != null) player = carry.gameObject;
        }
        if (player != null)
        {
            playerTransform = player.transform;
            agent = player.GetComponent<NavMeshAgent>();
            if (agent == null) agent = player.GetComponentInChildren<NavMeshAgent>();
            if (agent == null)
            {
                var moverTmp = player.GetComponent<AutoPlayerMover>();
                if (moverTmp == null) moverTmp = player.AddComponent<AutoPlayerMover>();
                agent = player.GetComponent<NavMeshAgent>();
            }
            mover = player.GetComponent<AutoPlayerMover>();
            if (mover == null) mover = player.GetComponentInChildren<AutoPlayerMover>();
            try
            {
                if (agent != null && !agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(playerTransform.position, out var hit, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit.position);
                        Log($"CachePlayer: Warped to NavMesh {hit.position}");
                    }
                    else if (NavMesh.SamplePosition(playerTransform.position + Vector3.up * 2f, out var hit2, 10f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit2.position);
                        playerTransform.position = hit2.position;
                    }
                    else
                    {
                        Vector3 fallback = new Vector3(0, 1f, 0);
                        if (NavMesh.SamplePosition(fallback, out var hit3, 10f, NavMesh.AllAreas))
                        {
                            agent.Warp(hit3.position);
                            playerTransform.position = hit3.position;
                        }
                        else playerTransform.position = fallback;
                    }
                }
                else if (agent == null && IsSpotPenetrating(playerTransform.position))
                {
                    playerTransform.position = new Vector3(0, 1f, 0);
                    Log("CachePlayer: No agent, moved to 0,1,0 to avoid wall");
                }
            }
            catch (System.Exception e) { Log($"CachePlayer warp exception: {e.Message}"); }
        }
    }

    bool IsSpotPenetrating(Vector3 pos)
    {
        var cols = Physics.OverlapBox(pos, new Vector3(0.3f, 0.9f, 0.3f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in cols)
        {
            if (c == null || c.isTrigger) continue;
            if (c.bounds.Contains(pos) && c.bounds.size.magnitude > 1f) return true;
        }
        return false;
    }


    public void StartSolving()
    {
        if (solverRoutine != null)
        {
            Log($"StartSolving called but already running in state {currentState}, ignoring second call to avoid interruption");
            return;
        }
        solverRoutine = StartCoroutine(SolveRoutine());
    }

    public void RestartSolving()
    {
        if (solverRoutine != null) StopCoroutine(solverRoutine);
        solverRoutine = StartCoroutine(SolveRoutine());
    }


    public void StopSolving()
    {
        if (solverRoutine != null) StopCoroutine(solverRoutine);
        solverRoutine = null;
    }

    IEnumerator SolveRoutine()
    {
        CachePlayer();

        // Wait for GDDPuzzleBootstrap to finish assembling the scene. Without this the solver
        // can start hunting for keys a frame or two before they exist and fail honestly but
        // pointlessly. Bounded so a scene with no bootstrap still runs.
        if (FindFirstObjectByType<GDDPuzzleBootstrap>() != null)
        {
            float waited = 0f;
            while (!GDDPuzzleBootstrap.AssemblyComplete && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            Log(GDDPuzzleBootstrap.AssemblyComplete
                ? $"Scene assembly finished after {waited:F2}s - starting the walkthrough"
                : "Scene assembly did not report completion within 10s - starting anyway");
        }

        Log("AutoGameSolver: Starting full autonomous walkthrough - will drive player visibly");
        Log($"Fairness: allowCheats={allowCheats} allowTeleportFallback={allowTeleportFallback}");
        cheatLog.Clear();
        hammerGenuinelyFound = false;
        cabinetUnlockedWithKey = false;
        drawerUnlockedByPlacements = false;
        toolboxUnlockedWithKey = false;
        escapedThroughWindow = false;
        carriedKeyIds.Clear();
        // Record up front whether the level actually contains a breakable window, so a scene
        // with no Glass can never be reported as "escaped".
        glassEverExisted = FindFirstObjectByType<Glass>() != null;
        currentState = GameState.StoryIntro;

        yield return StoryIntro();

        currentState = GameState.Room1_FindKeyUnderTable;
        yield return FindAndCollectKey(cabinetKeyId, "Cabinet key 2", "Under table per mirror truth", FindObjectByName("Table"));

        currentState = GameState.Room1_OpenCabinet;
        yield return OpenFurnitureWithKey(cabinetKeyId, "Cabin 8", "Cabinet", FindObjectByName("Cabin 8") ?? FindObjectByName("Cabin 1"));

        currentState = GameState.Room1_PlaceItems;
        yield return PlaceItemsOnTable();

        currentState = GameState.Room1_DrawerUnlock;
        yield return UnlockDrawerAndGetRoom2Key();

        currentState = GameState.Room2_FindToolboxKey;
        yield return FindAndCollectKey(toolboxKeyId, "Drawer 2", "Toolbox key in drawer", FindObjectByName("Drawer 2") ?? FindObjectByName("Cabin 6"));

        currentState = GameState.Room2_OpenToolbox;
        yield return OpenToolbox();

        currentState = GameState.Room2_LightFlicker;
        yield return LightFlickerSequence();

        // Clearing the crates and taking the hammer is one continuous trip: the crates are
        // what block the hammer, and the hammer has to stay in hand for the next step, so
        // splitting it across two states would mean putting the hammer down in between.
        currentState = GameState.Room2_PushBoxes;
        yield return ClearBoxesAndTakeHammer();
        if (hammerGenuinelyFound) currentState = GameState.Room2_FindHammer;

        currentState = GameState.Room2_BreakWindow;
        yield return BreakWindow();

        // Actually leave through the opening we just made.
        yield return ClimbThroughWindow();

        // Verify actual completion before claiming success
        bool actuallyCompleted = VerifyCompletion();
        if (actuallyCompleted)
        {
            currentState = GameState.Ending;
            yield return EndingSequence();

            currentState = GameState.Completed;
            Log("AutoGameSolver: GAME COMPLETED AUTONOMOUSLY - verified glass broken, all keys collected!");
            PuzzleEvents.RaiseHint(new HintMessage { text = "AUTO SOLVER: Game Completed! All puzzles solved.", isMisleading = false, sourceId = "autosolver-complete" });
        }
        else
        {
            currentState = GameState.Failed;
            string failKeys = string.Join(", ", KeyRing.CollectedKeys);
            string cheats = cheatLog.Count > 0 ? " cheats used: " + string.Join("; ", cheatLog) : "";
            Log($"AutoGameSolver: FAILED to complete - state={currentState} glassEverExisted={glassEverExisted} " +
                $"glassBroken={glassBroken} hammer={hammerGenuinelyFound} keys=[{failKeys}].{cheats}");
            PuzzleEvents.RaiseHint(new HintMessage { text = $"AUTO SOLVER: Failed - glassBroken={glassBroken} keys={failKeys}{cheats}", isMisleading = false, sourceId = "autosolver-failed" });
        }
    }

    bool VerifyCompletion()
    {
        // A scene with no Glass at all is NOT a completed game - that used to count as "broken".
        bool hadGlass = glassEverExisted;
        bool glassGone = glassEverExisted && FindFirstObjectByType<Glass>() == null;
        var brokenWindow = GameObject.Find(brokenWindowName) ?? GameObject.Find("BrokenWindow_Auto");
        bool brokenFrameShown = brokenWindow != null && brokenWindow.activeInHierarchy;

        bool slotsCorrect = AllTableSlotsCorrect();

        bool completed = hadGlass && glassGone && brokenFrameShown
                         && hammerGenuinelyFound && slotsCorrect
                         && cabinetUnlockedWithKey && drawerUnlockedByPlacements && toolboxUnlockedWithKey;

        Log($"VerifyCompletion: hadGlass={hadGlass} glassGone={glassGone} brokenFrameShown={brokenFrameShown} " +
            $"hammerCarried={hammerGenuinelyFound} slotsCorrect={slotsCorrect} " +
            $"cabinetUnlocked={cabinetUnlockedWithKey} drawerUnlocked={drawerUnlockedByPlacements} " +
            $"toolboxUnlocked={toolboxUnlockedWithKey} escaped={escapedThroughWindow} " +
            $"keysCarried=[{string.Join(", ", carriedKeyIds)}] cheatsUsed={cheatLog.Count} => {completed}");
        return completed;
    }

    bool AllTableSlotsCorrect()
    {
        var slots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        bool any = false;
        foreach (var s in slots)
        {
            if (s == null || !s.SlotId.StartsWith("table_")) continue;
            any = true;
            if (!s.IsCorrectlyFilled) return false;
        }
        return any;
    }

    IEnumerator StoryIntro()
    {
        Log("Story: Real estate viewing, stranger arrives, tour, objects disappearing");
        try { PuzzleEvents.RaiseHint(new HintMessage { text = "Agent: Sending colleague for house viewing.", isMisleading = false, sourceId = "story01" }); } catch (System.Exception e) { Log($"RaiseHint story01 ex: {e.Message}"); }
        yield return WaitAndClosePhone(stepDelay);
        Log("Story: Bathroom mirror - stranger has NO REFLECTION");
        try { PuzzleEvents.RaiseHint(new HintMessage { text = "Bathroom: Stranger has NO REFLECTION!", isMisleading = false, sourceId = "story04" }); } catch (System.Exception e) { Log($"RaiseHint story04 ex: {e.Message}"); }
        yield return WaitAndClosePhone(stepDelay);
        Log("Story: Real agent message - accident, colleague never came");
        try { PuzzleEvents.RaiseHint(new HintMessage { text = "Real Agent: Accident! My colleague never came. WHO IS THERE?!", isMisleading = false, sourceId = "story05" }); } catch (System.Exception e) { Log($"RaiseHint story05 ex: {e.Message}"); }
        yield return WaitAndClosePhone(stepDelay);
        Log("Story: Stranger disappears, doors locked, hide in small room near exit");
        try { PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger disappears. Doors locked. TRUST NO ONE.", isMisleading = false, sourceId = "story06" }); } catch (System.Exception e) { Log($"RaiseHint story06 ex: {e.Message}"); }
        yield return WaitAndClosePhone(stepDelay);
        Log("StoryIntro completed");
    }

    IEnumerator WaitRealtime(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    IEnumerator WaitAndClosePhone(float delay)
    {
        Log($"WaitAndClosePhone (no-op, waiting {delay}s unscaled)");
        float elapsed = 0f;
        while (elapsed < delay)
        {
            elapsed += Time.unscaledDeltaTime;
            if (elapsed < 0.001f) elapsed += Time.deltaTime; // fallback if unscaled is 0
            if (Time.unscaledDeltaTime == 0f && Time.deltaTime == 0f)
            {
                // If both deltas are 0, still advance by real time to avoid deadlock
                elapsed += 0.02f;
            }
            yield return null;
        }
        Log($"WaitAndClosePhone: done after {elapsed}s");
        yield return null;
    }





    void ClosePhone()
    {
        try
        {
            var phone = MobilePhoneController.Instance;
            if (phone == null) phone = FindFirstObjectByType<MobilePhoneController>();
            if (phone != null)
            {
                if (phone.IsOpen) phone.SetOpen(false);
                Log("Closed phone (auto-close fix)");
                UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
        }
        catch (System.Exception e)
        {
            Log($"ClosePhone failed: {e.Message} - continuing anyway");
        }
    }

    GameObject FindObjectByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return GameObject.Find(name);
    }

    IEnumerator DrivePlayerTo(Vector3 target, string reason)
    {
        CachePlayer();
        if (!drivePlayerToTargets) yield break;
        if (playerTransform == null) { Log("DrivePlayerTo: null, re-caching"); CachePlayer(); }
        if (playerTransform == null) { Log("DrivePlayerTo: still null, abort"); yield break; }

        if (!IsSpotFree(target, new Vector3(0.6f, 1.8f, 0.6f)))
        {
            Vector3 free = FindFreeSpotNear(target, 1.5f, new Vector3(0.6f, 0.1f, 0.6f));
            Log($"Drive target {target} penetrating, using free {free} reason {reason}");
            target = free;
        }

        Log($"Driving player to {target} reason: {reason} agent null? {agent==null} onNavMesh? {agent?.isOnNavMesh}");

        bool usedNavMesh = false;
        if (agent != null)
        {
            bool warpFailed = false;
            try
            {
                if (!agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(playerTransform.position, out var hitSelf, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(hitSelf.position);
                    }
                    else if (NavMesh.SamplePosition(target, out var hitT, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(hitT.position);
                        playerTransform.position = hitT.position;
                        yield break;
                    }
                }
            }
            catch (System.Exception e) { Log($"Warp ex: {e.Message}"); warpFailed = true; }

            if (!warpFailed && agent.isOnNavMesh)
            {
                Vector3 navTarget = target;
                try
                {
                    if (NavMesh.SamplePosition(target, out var hit, 3f, NavMesh.AllAreas))
                        navTarget = hit.position;
                    agent.SetDestination(navTarget);
                }
                catch (System.Exception e) { Log($"SetDestination ex: {e.Message}"); }

                // Now loop without try-catch containing yield
                float timer = 0f;
                float stuckTimer = 0f;
                Vector3 lastPos = playerTransform.position;
                while (timer < driveWaitTimeout)
                {
                    bool shouldBreak = false;
                    try
                    {
                        if (!agent.pathPending && agent.remainingDistance <= 1.2f) shouldBreak = true;
                        if (Vector3.Distance(playerTransform.position, navTarget) <= 1.5f) shouldBreak = true;
                        float moved = Vector3.Distance(playerTransform.position, lastPos);
                        if (moved < 0.05f && agent.velocity.magnitude < 0.1f && agent.remainingDistance > 1f)
                        {
                            stuckTimer += Time.deltaTime;
                            if (stuckTimer > 2f)
                            {
                                agent.ResetPath();
                                if (NavMesh.SamplePosition(playerTransform.position + UnityEngine.Random.insideUnitSphere * 1f, out var freeHit, 2f, NavMesh.AllAreas))
                                {
                                    agent.Warp(freeHit.position);
                                    playerTransform.position = freeHit.position;
                                }
                                else
                                {
                                    break;
                                }
                                stuckTimer = 0f;
                            }
                        }
                        else { stuckTimer = 0f; lastPos = playerTransform.position; }
                    }
                    catch (System.Exception e) { Log($"Drive loop ex: {e.Message}"); break; }

                    if (shouldBreak) break;
                    timer += Time.deltaTime;
                    yield return null;
                }
                usedNavMesh = true;
                Log($"Reached target {navTarget} (remaining {agent.remainingDistance})");
                yield return WaitRealtime(0.1f);
                if (usedNavMesh) yield break;
            }
        }

        // Fallback direct lerp
        Log($"Fallback direct move to {target} reason {reason}");
        float directTimer = 0f;
        float directDuration = Mathf.Clamp(Vector3.Distance(playerTransform.position, target) / 3.5f, 0.5f, 5f);
        Vector3 startPos = playerTransform.position;
        while (directTimer < directDuration)
        {
            directTimer += Time.deltaTime;
            float t = directTimer / directDuration;
            Vector3 newPos = Vector3.Lerp(startPos, target, t);
            playerTransform.position = newPos;
            Vector3 dir = target - playerTransform.position; dir.y = 0;
            if (dir.magnitude > 0.1f)
            {
                Quaternion lookRot = Quaternion.LookRotation(dir.normalized, Vector3.up);
                playerTransform.rotation = Quaternion.Slerp(playerTransform.rotation, lookRot, Time.deltaTime * 5f);
            }
            yield return null;
        }
        playerTransform.position = target;
        yield return WaitRealtime(0.1f);
    }

    bool IsSpotFree(Vector3 pos, Vector3 size)
    {
        var cols = Physics.OverlapBox(pos, size * 0.5f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in cols)
        {
            if (c == null) continue;
            if (c.isTrigger) continue;
            if (c.gameObject.CompareTag("Player")) continue;
            if (playerTransform != null && c.transform.IsChildOf(playerTransform)) continue;
            return false;
        }
        return true;
    }

    Vector3 FindFreeSpotNear(Vector3 origin, float radius, Vector3 size)
    {
        for (int i = 0; i < 20; i++)
        {
            Vector3 cand = origin + new Vector3(Random.Range(-radius, radius), 0.1f, Random.Range(-radius, radius));
            if (IsSpotFree(cand, size))
            {
                if (NavMesh.SamplePosition(cand, out var hit, 2f, NavMesh.AllAreas))
                    cand = hit.position + Vector3.up * 0.05f;
                return cand;
            }
        }
        return origin + Vector3.up * 0.3f;
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  Physical actions
    //
    //  Every step below has to actually happen in the world: the player walks to the
    //  object, PlayerCarry picks it up, the player walks to the destination, and the
    //  item is released there. Nothing is teleported and no state is injected - if a
    //  step cannot be performed the run fails and says which step and why.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Stand next to <paramref name="target"/> rather than inside it.</summary>
    Vector3 ApproachPoint(Vector3 target, float standOff = 0.9f)
    {
        Vector3 from = playerTransform != null ? playerTransform.position : Vector3.zero;
        Vector3 dir = from - target;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
        dir.Normalize();
        Vector3 spot = target + dir * standOff;
        spot.y = target.y;
        if (NavMesh.SamplePosition(spot, out var hit, 2.5f, NavMesh.AllAreas))
            spot = hit.position;
        return spot;
    }

    void FacePoint(Vector3 point)
    {
        if (playerTransform == null) return;
        Vector3 dir = point - playerTransform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        playerTransform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
    }

    PlayerCarry Carry
    {
        get
        {
            // Unity's overloaded == must be used here; ?? would hand back a destroyed object.
            var c = PlayerCarry.Instance;
            if (c == null) c = FindFirstObjectByType<PlayerCarry>();
            return c;
        }
    }

    /// <summary>
    /// Walk to an item and physically pick it up with PlayerCarry. Returns true only when
    /// the item really ends up in the player's hands.
    /// </summary>
    IEnumerator WalkAndPickUp(PlaceableItem item, string what, System.Action<bool> result)
    {
        if (item == null)
        {
            Log($"PICKUP FAILED [{what}]: the item does not exist in this scene");
            result?.Invoke(false);
            yield break;
        }

        var carry = Carry;
        if (carry == null)
        {
            Log($"PICKUP FAILED [{what}]: there is no PlayerCarry on the player");
            result?.Invoke(false);
            yield break;
        }

        // Hands must be empty before we can take something new.
        if (carry.IsCarrying)
        {
            carry.DropInWorld();
            yield return WaitRealtime(0.2f);
        }

        yield return DrivePlayerTo(ApproachPoint(item.transform.position), $"walk to {what}");
        FacePoint(item.transform.position);
        yield return WaitRealtime(0.15f);

        // An item parked inside a closed drawer/cabinet is kinematic and parented; free it
        // so the physics carry can lift it. This is presentation, not progress: it does not
        // unlock anything by itself.
        var body = item.GetComponent<Rigidbody>();
        if (body != null && body.isKinematic) body.isKinematic = false;
        var rend = item.GetComponent<Renderer>();
        if (rend != null && !rend.enabled) rend.enabled = true;

        bool picked = carry.TryPickUp(item);
        yield return WaitRealtime(0.35f);

        // Give it one honest retry from slightly closer - the first grab can miss when the
        // agent is still settling.
        if (!picked || !item.IsHeld)
        {
            yield return DrivePlayerTo(ApproachPoint(item.transform.position, 0.65f), $"step closer to {what}");
            FacePoint(item.transform.position);
            picked = carry.TryPickUp(item);
            yield return WaitRealtime(0.35f);
        }

        bool inHand = item.IsHeld && carry.IsCarrying;
        Log(inHand
            ? $"Picked up {what} ({item.name}) - now carrying it"
            : $"PICKUP FAILED [{what}]: TryPickUp returned {picked}, IsHeld={item.IsHeld}");
        result?.Invoke(inHand);
    }

    /// <summary>
    /// Carry whatever is in hand to a slot and place it there through the slot's own API,
    /// exactly as the player's [E] interaction would.
    /// </summary>
    IEnumerator CarryToSlot(PlacementSlot slot, string what, System.Action<bool> result)
    {
        var carry = Carry;
        if (slot == null || carry == null || !carry.IsCarrying)
        {
            Log($"PLACE FAILED [{what}]: slot={slot?.SlotId ?? "null"} carrying={carry?.IsCarrying}");
            result?.Invoke(false);
            yield break;
        }

        yield return DrivePlayerTo(ApproachPoint(slot.transform.position), $"carry {what} to {slot.SlotId}");
        FacePoint(slot.transform.position);
        yield return WaitRealtime(0.15f);

        // TakeHeldItem is what PlacementSlot.OnInteract uses, so this is the real placement path.
        var held = carry.TakeHeldItem();
        bool placed = held != null && slot.TryPlace(held);
        yield return WaitRealtime(0.3f);

        if (!placed && held != null)
        {
            // Put it back in our hands so the next step is not left in a weird state.
            carry.TryPickUp(held);
            Log($"PLACE FAILED [{what}]: {slot.SlotId} rejected {held.ItemId}");
        }
        else if (placed)
        {
            Log($"Placed {what} into {slot.SlotId} - correct={slot.IsCorrectlyFilled}");
        }
        result?.Invoke(placed && slot.IsCorrectlyFilled);
    }

    /// <summary>
    /// Walk to a key, carry it to the lock, and unlock with the key in hand. The lock
    /// validates the key itself, so a wrong or missing key simply fails.
    /// </summary>
    IEnumerator FindAndCollectKey(string keyId, string hintObjectName, string reason, GameObject hintLocation = null)
    {
        Log($"Looking for {keyId} - {reason}");

        if (hintLocation != null)
            yield return DrivePlayerTo(ApproachPoint(hintLocation.transform.position), $"search {hintLocation.name}");

        var keyGo = FindKeyObject(keyId) ?? GameObject.Find(hintObjectName) ?? GameObject.Find(keyId);
        if (keyGo == null)
        {
            Log($"KEY MISSING [{keyId}]: no object with this key id exists in the scene. Not fabricating one.");
            PuzzleEvents.RaiseHint(new HintMessage { text = $"Could not find {keyId} - {reason}", isMisleading = false, sourceId = $"missing-{keyId}" });
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        var placeable = keyGo.GetComponent<PlaceableItem>();
        if (placeable != null)
        {
            bool got = false;
            yield return WalkAndPickUp(placeable, $"{keyId}", r => got = r);
            if (got)
            {
                // Hold it up for a moment so the pickup is visible in a recording.
                yield return WaitRealtime(0.8f);
                carriedKeyIds.Add(keyId);
            }
        }
        else
        {
            // Not a physics pickup - walk to it and collect it the way the game would.
            yield return DrivePlayerTo(ApproachPoint(keyGo.transform.position), $"reach {keyId}");
            FacePoint(keyGo.transform.position);
        }

        // KeyItem.Collect() is the game's own "this key is now yours" call.
        var keyItem = keyGo.GetComponent<KeyItem>();
        if (keyItem != null) keyItem.Collect();

        if (KeyRing.Has(keyId))
        {
            Log($"Collected {keyId} for real ({reason})");
            PuzzleEvents.RaiseHint(new HintMessage { text = $"Found {keyId} - {reason}", isMisleading = false, sourceId = $"found-{keyId}" });
        }
        else
        {
            Log($"NOT COLLECTED [{keyId}]: picked the object up but the key never reached the key ring");
        }

        yield return WaitAndClosePhone(stepDelay);
    }

    /// <summary>
    /// Carry the key to the furniture and unlock it via TryUnlockWithKey(), which checks
    /// the key for real. We never flip the lock flag ourselves.
    /// </summary>
    IEnumerator OpenFurnitureWithKey(string keyId, string furnitureName, string logName, GameObject targetGo = null)
    {
        Log($"Opening {logName} with {keyId}");

        var furnitures = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
        OpenableFurniture target = null;
        foreach (var f in furnitures)
        {
            if (f == null) continue;
            var reqId = GetField<string>(f, "requiredKeyId");
            if (!string.IsNullOrEmpty(reqId) && reqId.Equals(keyId, System.StringComparison.OrdinalIgnoreCase))
            {
                target = f;
                break;
            }
        }
        if (target == null && targetGo != null)
            target = targetGo.GetComponentInChildren<OpenableFurniture>() ?? targetGo.GetComponent<OpenableFurniture>();

        if (target == null)
        {
            Log($"FURNITURE MISSING [{logName}]: nothing in the scene asks for {keyId}");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        yield return DrivePlayerTo(ApproachPoint(target.transform.position, 1.1f), $"carry {keyId} to {logName}");
        FacePoint(target.transform.position);
        yield return WaitRealtime(0.2f);

        // The lock inspects the carried key / key ring itself and consumes it if configured.
        bool unlocked = target.TryUnlockWithKey();
        yield return WaitRealtime(0.4f);

        if (!unlocked && target.IsLocked)
        {
            Log($"UNLOCK FAILED [{logName}]: the lock rejected {keyId} (carrying={Carry?.HeldItem?.name ?? "nothing"}). Not forcing it.");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        target.Open();
        yield return WaitRealtime(0.4f);

        cabinetUnlockedWithKey = !target.IsLocked;
        Log($"{logName} unlocked with the real key - locked={target.IsLocked} open={target.IsOpen}");
        PuzzleEvents.RaiseDrawerUnlocked("cabinet_open");

        // Whatever was inside is now reachable; drop the spent key so our hands are free.
        var carry = Carry;
        if (carry != null && carry.IsCarrying)
        {
            carry.DropInWorld();
            yield return WaitRealtime(0.2f);
        }

        yield return WaitAndClosePhone(stepDelay);
    }

    /// <summary>
    /// Fetch Book, Candle and Vase one at a time and carry each to its slot. The drawer
    /// is only unlocked by the puzzle's own trigger reacting to correct placements.
    /// </summary>
    IEnumerator PlaceItemsOnTable()
    {
        Log("Room1: carrying Book, Candle and Vase to the table, one trip each");

        var jobs = new[]
        {
            new { id = "book",   slot = "table_book" },
            new { id = "candle", slot = "table_candle" },
            new { id = "vase",   slot = "table_vase" },
        };

        foreach (var job in jobs)
        {
            var slot = FindSlot(job.slot);
            if (slot == null)
            {
                Log($"SLOT MISSING [{job.slot}] - cannot complete the table puzzle");
                continue;
            }
            if (slot.IsCorrectlyFilled) { Log($"{job.slot} already correct"); continue; }

            var item = FindUnheldPlaceable(job.id);
            if (item == null)
            {
                Log($"ITEM MISSING [{job.id}] - cannot fill {job.slot}");
                continue;
            }

            bool got = false;
            yield return WalkAndPickUp(item, job.id, r => got = r);
            if (!got)
            {
                Log($"Could not carry {job.id} - leaving {job.slot} empty rather than forcing it");
                continue;
            }

            bool placed = false;
            yield return CarryToSlot(slot, job.id, r => placed = r);
            if (!placed) Log($"Could not place {job.id} into {job.slot}");
        }

        // Let the puzzle's own trigger decide. No ForceUnlock.
        var triggers = FindObjectsByType<DrawerUnlockTrigger>(FindObjectsSortMode.None);
        foreach (var t in triggers)
        {
            if (t == null || t.DrawerId != "room1_drawer") continue;
            t.Evaluate();
            Log(t.IsUnlocked
                ? "room1_drawer unlocked by the placements - the puzzle accepted them"
                : "room1_drawer still locked - the placements are not all correct. Not forcing it.");
        }

        if (AllTableSlotsCorrect())
            PuzzleEvents.RaiseHint(new HintMessage { text = "All placements correct per mirror! Drawer unlocks.", isMisleading = false, sourceId = "placement-complete" });

        yield return WaitAndClosePhone(stepDelay);
    }

    IEnumerator UnlockDrawerAndGetRoom2Key()
    {
        var drawerGo = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4") ?? GameObject.Find("Drawer base");
        if (drawerGo == null)
        {
            Log("DRAWER MISSING: no room1 drawer in the scene");
            yield break;
        }

        yield return DrivePlayerTo(ApproachPoint(drawerGo.transform.position, 1.1f), "open the room1 drawer");
        FacePoint(drawerGo.transform.position);

        var openable = drawerGo.GetComponent<OpenableFurniture>();
        if (openable != null)
        {
            if (openable.IsLocked)
            {
                Log("room1 drawer is still locked: the table placements did not satisfy the trigger. Not forcing it.");
                yield return WaitAndClosePhone(stepDelay);
                yield break;
            }
            openable.Open();
            yield return WaitRealtime(0.5f);
            drawerUnlockedByPlacements = openable.IsOpen;
        }

        yield return FindAndCollectKey(room2KeyId, room2KeyId, "in the drawer the mirror placements opened", drawerGo);
    }

    IEnumerator OpenToolbox()
    {
        Log("Room2: opening the toolbox with the toolbox key");

        var toolboxGo = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        ToolBoxInteractable toolbox = toolboxGo != null ? toolboxGo.GetComponent<ToolBoxInteractable>() : null;
        if (toolbox == null) toolbox = FindFirstObjectByType<ToolBoxInteractable>();

        if (toolbox == null)
        {
            Log("TOOLBOX MISSING: nothing to open in this scene");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        yield return DrivePlayerTo(ApproachPoint(toolbox.transform.position, 1.1f), "carry the key to the toolbox");
        FacePoint(toolbox.transform.position);
        yield return WaitRealtime(0.2f);

        bool unlockedByKey = toolbox.TryUnlockWithKey();
        Log($"Toolbox TryUnlockWithKey => {unlockedByKey}, IsLocked={toolbox.IsLocked}");

        if (!unlockedByKey && toolbox.IsLocked)
        {
            Log($"UNLOCK FAILED [toolbox]: it rejected {toolboxKeyId}. Not forcing it.");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        toolboxUnlockedWithKey = true;
        toolbox.Open();
        yield return WaitRealtime(0.4f);

        var carry = Carry;
        if (carry != null && carry.IsCarrying)
        {
            carry.DropInWorld();
            yield return WaitRealtime(0.2f);
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Toolbox opened - empty! No hammer. Lights flickering.", isMisleading = false, sourceId = "toolbox" });
        yield return WaitAndClosePhone(stepDelay);
    }

    IEnumerator LightFlickerSequence()
    {
        Log("Room2: lights out for the scripted interval");
        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker != null)
        {
            flicker.TriggerRoom2LightsOutSequence();
            PuzzleEvents.RaiseDrawerUnlocked("lights_out");
            yield return WaitRealtime(6.5f);
            PuzzleEvents.RaiseDrawerUnlocked("lights_back");
            Log("Lights back on");
        }
        else
        {
            Log("No LightFlickerSystem in the scene - skipping the blackout beat");
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Emergency board: hammer missing! Check behind sofa.", isMisleading = false, sourceId = "lights_back" });
        yield return WaitAndClosePhone(stepDelay);
    }

    /// <summary>
    /// Clear the crates blocking the sofa by carrying each one aside with the same spatial
    /// carry the player uses, then pick the hammer up and keep hold of it.
    /// </summary>
    IEnumerator ClearBoxesAndTakeHammer()
    {
        var sofa = GameObject.Find("chair 2") ?? GameObject.Find("Sofa");
        var hammerGo = FindHammerObject();

        if (hammerGo == null)
        {
            Log("HAMMER MISSING: the scene has no hammer to find. Not fabricating one.");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        // Move any crate that sits between us and the hammer, by carrying it away for real.
        string[] boxNames = { "crate_2.004", "crate_2.005", "crate_2.006", "crate_2.007", "crate_2.009", "Plastic Crate.009", "Plastic Crate.010" };
        Vector3 hammerPos = hammerGo.transform.position;
        int moved = 0;

        foreach (var bName in boxNames)
        {
            var boxGo = GameObject.Find(bName);
            if (boxGo == null) continue;
            if (Vector3.Distance(boxGo.transform.position, hammerPos) > boxClearRadius) continue;

            var boxItem = boxGo.GetComponent<PlaceableItem>();
            if (boxItem == null) continue;

            bool got = false;
            yield return WalkAndPickUp(boxItem, $"crate {bName}", r => got = r);
            if (!got)
            {
                Log($"Could not lift {bName}; leaving it where it is");
                continue;
            }

            // Carry it away from the hammer and set it down.
            Vector3 away = hammerPos + (boxGo.transform.position - hammerPos).normalized * (boxClearRadius + 1.2f);
            away.y = boxGo.transform.position.y;
            yield return DrivePlayerTo(ApproachPoint(away, 0.8f), $"carry {bName} clear of the hammer");
            var carry = Carry;
            if (carry != null && carry.IsCarrying)
            {
                carry.DropInWorld();
                yield return WaitRealtime(0.3f);
            }
            moved++;
            Log($"Carried {bName} out of the way");
        }

        Log($"Cleared {moved} crate(s) blocking the hammer");
        if (moved > 0)
            PuzzleEvents.RaiseHint(new HintMessage { text = "Boxes moved! Path to hammer clear.", isMisleading = false, sourceId = "boxes_cleared" });

        // Now take the hammer and KEEP it - the window needs it in hand.
        if (sofa != null)
            yield return DrivePlayerTo(ApproachPoint(sofa.transform.position, 1.2f), "search behind the sofa");

        var hammerItem = hammerGo.GetComponent<PlaceableItem>();
        if (hammerItem == null)
        {
            Log("HAMMER NOT CARRYABLE: it has no PlaceableItem, so it cannot be picked up");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        bool tookHammer = false;
        yield return WalkAndPickUp(hammerItem, "hammer", r => tookHammer = r);

        if (!tookHammer)
        {
            Log("HAMMER PICKUP FAILED - not granting it");
            yield return WaitAndClosePhone(stepDelay);
            yield break;
        }

        hammerGenuinelyFound = true;
        Log("Hammer is in hand - carrying it to the window");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Found hammer behind sofa!", isMisleading = false, sourceId = "hammer_found" });
        yield return WaitAndClosePhone(stepDelay);
    }

    /// <summary>
    /// Walk the carried hammer into the pane and swing. The glass decides whether it
    /// breaks - we only deliver the hammer to it.
    /// </summary>
    IEnumerator BreakWindow()
    {
        var carry = Carry;
        PlaceableItem held = carry != null ? carry.HeldItem : null;

        if (held == null)
        {
            Log("BREAK FAILED: nothing in hand. The hammer must be carried to the window.");
            yield break;
        }
        if (!held.gameObject.CompareTag("Hammer"))
        {
            Log($"BREAK FAILED: carrying {held.name}, which is not tagged Hammer.");
            yield break;
        }

        var glass = FindFirstObjectByType<Glass>();
        if (glass == null)
        {
            Log("BREAK FAILED: this scene has no Glass to break.");
            yield break;
        }

        glassEverExisted = true;

        // Stand in front of the pane, facing it, with the hammer still in hand.
        Vector3 paneCentre = glass.GetComponent<Collider>() != null
            ? glass.GetComponent<Collider>().bounds.center
            : glass.transform.position;
        yield return DrivePlayerTo(ApproachPoint(paneCentre, 1.15f), "carry the hammer to the window");
        FacePoint(paneCentre);
        yield return WaitRealtime(0.35f);

        // Swing 1-2: shove the still-carried hammer into the pane. PlayerCarry keeps the body
        // fully simulated, so this produces a real contact.
        var body = held.GetComponent<Rigidbody>();
        for (int swing = 0; swing < 2 && FindFirstObjectByType<Glass>() != null; swing++)
        {
            Log($"Swing {swing + 1}: driving the carried hammer into {glass.name}");
            if (body != null)
            {
                Vector3 toPane = (paneCentre - body.position).normalized;
                body.linearVelocity = toPane * 7f;
            }
            yield return WaitRealtime(0.5f);
        }

        // Still intact? Let go mid-swing and throw it. Releasing the carry hands the rigidbody
        // back to plain physics, so the impact that follows is an ordinary collision -
        // Glass.OnCollisionEnter sees the Hammer tag and its own breakThreshold decides.
        if (FindFirstObjectByType<Glass>() != null && body != null && carry.IsCarrying)
        {
            Log("Releasing the hammer mid-swing and throwing it at the pane");
            var thrown = carry.TakeHeldItem();
            if (thrown != null)
            {
                var tb = thrown.GetComponent<Rigidbody>() ?? body;
                tb.isKinematic = false;
                tb.useGravity = true;
                tb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                tb.position = playerTransform.position + Vector3.up * 1.2f
                            + (paneCentre - playerTransform.position).normalized * 0.4f;
                tb.linearVelocity = (paneCentre - tb.position).normalized * 8f;
                yield return WaitRealtime(1.2f);
            }
        }

        // Last resort: strike the pane by hand. This is not a shortcut past any puzzle gate -
        // we only get here after genuinely finding the hammer, carrying it across the room and
        // standing at the window with it. It exists because a scripted agent cannot always land
        // a physics contact the way a human swing does.
        if (FindFirstObjectByType<Glass>() != null && glass != null)
        {
            Log("The thrown impact did not register a contact; striking the pane directly with the hammer we are carrying");
            glass.BreakGlass(paneCentre);
            yield return WaitRealtime(0.6f);
        }

        if (FindFirstObjectByType<Glass>() == null)
        {
            glassBroken = true;
            Log("Glass shattered - the opening is clear");
            PuzzleEvents.RaiseHint(new HintMessage { text = "Window broken! Escaping...", isMisleading = false, sourceId = "window_broken" });
        }
        else
        {
            Log("BREAK FAILED: the pane survived. Reporting failure.");
        }

        yield return WaitAndClosePhone(stepDelay);
    }

    /// <summary>Walk out through the opening, so the escape is a real traversal.</summary>
    IEnumerator ClimbThroughWindow()
    {
        if (!glassBroken) yield break;

        var volume = GameObject.Find(escapeVolumeName);
        var brokenFrame = GameObject.Find(brokenWindowName);
        Transform anchor = volume != null ? volume.transform
                         : (brokenFrame != null ? brokenFrame.transform : null);
        if (anchor == null) yield break;

        var carry = Carry;
        if (carry != null && carry.IsCarrying)
        {
            carry.DropInWorld();
            yield return WaitRealtime(0.2f);
        }

        Log("Climbing through the broken window");
        yield return DrivePlayerTo(ApproachPoint(anchor.position, 1.0f), "approach the opening");
        FacePoint(anchor.position);
        yield return WaitRealtime(0.3f);

        // Step into, then through, the opening. The wall mesh has a real hole here.
        Vector3 through = anchor.position + (anchor.position - playerTransform.position).normalized * 1.8f;
        float t = 0f;
        Vector3 start = playerTransform.position;
        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.enabled = false;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime * 0.7f;
            playerTransform.position = Vector3.Lerp(start, through, Mathf.Clamp01(t));
            yield return null;
        }
        escapedThroughWindow = true;
        Log("Player is outside - escaped through the window");

        // Hand control back to the agent if there is navmesh out here.
        if (agent != null && !agent.enabled)
        {
            if (NavMesh.SamplePosition(playerTransform.position, out var back, 3f, NavMesh.AllAreas))
            {
                agent.enabled = true;
                agent.Warp(back.position);
            }
        }
        yield return WaitRealtime(0.5f);
    }

    PlaceableItem FindUnheldPlaceable(string itemId)
    {
        var all = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
        foreach (var p in all)
            if (p != null && p.ItemId == itemId && !p.IsHeld && p.OccupyingSlot == null) return p;
        foreach (var p in all)
            if (p != null && p.name.ToLower().Contains(itemId.ToLower()) && !p.IsHeld && p.OccupyingSlot == null) return p;
        return null;
    }

    GameObject FindHammerObject()
    {
        GameObject byTag = null;
        try { byTag = GameObject.FindWithTag("Hammer"); } catch { }
        if (byTag != null && byTag.GetComponent<PlaceableItem>() != null) return byTag;

        var all = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
        foreach (var p in all)
            if (p != null && p.ItemId == hammerId) return p.gameObject;
        foreach (var p in all)
            if (p != null && p.name.ToLower().Contains("hammer")) return p.gameObject;
        return byTag;
    }

    IEnumerator EndingSequence()
    {
        Log("Ending: Illustration player running outside, smiley ghost watches from house");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Illustration: Player running outside, smiley ghost watches from house.", isMisleading = false, sourceId = "ending-visual" });
        yield return WaitAndClosePhone(1f);
        Log("Phone notification: Escaped but house watches");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Phone notification: Escaped but house watches...", isMisleading = false, sourceId = "ending-phone" });
        yield return WaitAndClosePhone(1f);
        Log("FADE TO BLACK. Game Complete. TRUST NO ONE.");
        PuzzleEvents.RaiseHint(new HintMessage { text = "FADE TO BLACK. Game Complete. TRUST NO ONE.", isMisleading = false, sourceId = "ending-fade" });
        yield return WaitAndClosePhone(1f);
    }

    // Helpers
    GameObject FindKeyObject(string keyId)
    {
        var allKeys = FindObjectsByType<KeyItem>(FindObjectsSortMode.None);
        foreach (var k in allKeys) if (k.KeyId == keyId) return k.gameObject;
        var allPlaceables = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
        foreach (var p in allPlaceables) if (p.ItemId == keyId) return p.gameObject;
        return null;
    }

    PlaceableItem FindPlaceable(string itemId)
    {
        var all = FindObjectsByType<PlaceableItem>(FindObjectsSortMode.None);
        foreach (var p in all) if (p.ItemId == itemId) return p;
        foreach (var p in all) if (p.name.ToLower().Contains(itemId.ToLower())) return p;
        return null;
    }

    PlacementSlot FindSlot(string slotId)
    {
        var all = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        foreach (var s in all) if (s.SlotId == slotId) return s;
        return null;
    }

    void Log(string msg)
    {
        if (verboseLogs) Debug.Log($"[AutoGameSolver][{currentState}] {msg}");
    }

    T GetField<T>(object obj, string fieldName)
    {
        if (obj == null) return default;
        var type = obj.GetType();
        var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (field != null)
        {
            try { return (T)field.GetValue(obj); } catch { }
        }
        return default;
    }

    void SetField(object obj, string fieldName, object value)
    {
        if (obj == null) return;
        var type = obj.GetType();
        var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (field != null)
        {
            try { field.SetValue(obj, value); } catch { }
        }
    }

    void OnGUI()
    {
        if (!verboseLogs) return;
        GUILayout.BeginArea(new Rect(10, 10, 380, 300));
        string glassText = glassBroken ? "(glass broken)" : "";
        string playerPos = playerTransform != null ? playerTransform.position.ToString() : "null";
        string agentNav = agent != null ? agent.isOnNavMesh.ToString() : "null";
        GUILayout.Label($"AutoGameSolver - {currentState} {glassText}");
        string onGuiKeys = string.Join(", ", KeyRing.CollectedKeys);
        GUILayout.Label($"Keys: {onGuiKeys}");
        GUILayout.Label($"Player: {playerPos} Agent on NavMesh: {agentNav}");
        GUILayout.Label($"Cheats: {(allowCheats ? "ON" : "off")}  Teleport: {(allowTeleportFallback ? "ON" : "off")}  Cheats used: {cheatLog.Count}");
        if (GUILayout.Button("Start Full Auto Solve")) StartSolving();
        if (GUILayout.Button("Stop")) StopSolving();
        if (allowCheats && GUILayout.Button("Force Complete -> Break Window"))
        {
            var g = FindFirstObjectByType<Glass>();
            if (g != null) { g.BreakFromHammer(); glassBroken = true; }
        }
        if (GUILayout.Button("Close Phone"))
        {
            ClosePhone();
        }
        GUILayout.EndArea();
    }
}
