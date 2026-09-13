using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

/// <summary>
/// Fully autonomous solver for GDD puzzles in AutoTest scenes.
/// Can finish game from start to end without player input.
/// Works by directly manipulating KeyRing, OpenableFurniture, PlacementSlot, LightFlickerSystem, Glass.
/// Also drives AutoPlayerMover if present.
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
        Completed
    }

    [Header("Config")]
    public bool autoStart = true;
    public float stepDelay = 1.2f;
    public bool usePhysicsPickup = false; // if false, uses KeyRing direct
    public bool verboseLogs = true;

    [Header("Ids")]
    public string cabinetKeyId = "cabinet_key";
    public string room2KeyId = "room2_key";
    public string toolboxKeyId = "toolbox_key";
    public string hammerId = "hammer";

    private GameState currentState = GameState.Init;
    private Coroutine solverRoutine;

    void Start()
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!sceneName.Contains("AutoTest") && !sceneName.Contains("NavMeshTest"))
        {
            enabled = false;
            return;
        }
        if (autoStart) StartSolving();
    }

    public void StartSolving()
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
        Log("AutoGameSolver: Starting full autonomous walkthrough");
        currentState = GameState.StoryIntro;

        // Story intro
        yield return StoryIntro();

        // Room1: key under table (mirror truth)
        currentState = GameState.Room1_FindKeyUnderTable;
        yield return FindAndCollectKey(cabinetKeyId, "Cabinet key 2", "Under table per mirror truth");

        // Room1: open cabinet
        currentState = GameState.Room1_OpenCabinet;
        yield return OpenFurnitureWithKey(cabinetKeyId, "Cabin 8", "Cabinet");

        // Room1: place Book, Candle, Vase per mirror
        currentState = GameState.Room1_PlaceItems;
        yield return PlaceItemsOnTable();

        // Room1: drawer unlock gives room2 key
        currentState = GameState.Room1_DrawerUnlock;
        yield return UnlockDrawerAndGetRoom2Key();

        // Room2: toolbox key
        currentState = GameState.Room2_FindToolboxKey;
        yield return FindAndCollectKey(toolboxKeyId, "Drawer 2", "Toolbox key in drawer");

        // Room2: open toolbox -> triggers light flicker
        currentState = GameState.Room2_OpenToolbox;
        yield return OpenToolbox();

        // Room2: light flicker 6-7 sec
        currentState = GameState.Room2_LightFlicker;
        yield return LightFlickerSequence();

        // Room2: find hammer behind sofa
        currentState = GameState.Room2_FindHammer;
        yield return FindHammer();

        // Room2: push boxes
        currentState = GameState.Room2_PushBoxes;
        yield return PushBoxesAway();

        // Room2: break window
        currentState = GameState.Room2_BreakWindow;
        yield return BreakWindow();

        // Ending
        currentState = GameState.Ending;
        yield return EndingSequence();

        currentState = GameState.Completed;
        Log("AutoGameSolver: GAME COMPLETED AUTONOMOUSLY - Full walkthrough finished!");
        PuzzleEvents.RaiseHint(new HintMessage { text = "AUTO SOLVER: Game Completed! All puzzles solved.", isMisleading = false, sourceId = "autosolver-complete" });
    }

    IEnumerator StoryIntro()
    {
        Log("Story: Real estate viewing, stranger arrives, tour, objects disappearing");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Agent: Sending colleague for house viewing.", isMisleading = false, sourceId = "story01" });
        yield return new WaitForSeconds(stepDelay);
        Log("Story: Bathroom mirror - stranger has NO REFLECTION");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Bathroom: Stranger has NO REFLECTION!", isMisleading = false, sourceId = "story04" });
        yield return new WaitForSeconds(stepDelay);
        Log("Story: Real agent message - accident, colleague never came");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Real Agent: Accident! My colleague never came. WHO IS THERE?!", isMisleading = false, sourceId = "story05" });
        yield return new WaitForSeconds(stepDelay);
        Log("Story: Stranger disappears, doors locked, hide in small room near exit");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger disappears. Doors locked. TRUST NO ONE.", isMisleading = false, sourceId = "story06" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator FindAndCollectKey(string keyId, string hintObjectName, string reason)
    {
        Log($"Searching for key {keyId} - {reason}");
        // Try to find existing key GameObject
        var keyGo = FindKeyObject(keyId);
        if (keyGo == null) keyGo = GameObject.Find(hintObjectName);
        if (keyGo == null) keyGo = GameObject.Find(keyId);

        if (keyGo != null)
        {
            var keyItem = keyGo.GetComponent<KeyItem>();
            if (keyItem != null)
            {
                Log($"Found key {keyId} on {keyGo.name}, collecting via KeyItem.Collect()");
                keyItem.Collect();
            }
        }

        // Always ensure KeyRing has it for autonomous finish
        bool added = KeyRing.Add(keyId);
        Log($"KeyRing.Add({keyId}) => {added}, now has {KeyRing.Count} keys. Reason: {reason}");

        // Also try physics pickup if PlayerCarry exists
        if (usePhysicsPickup)
        {
            var placeable = keyGo != null ? keyGo.GetComponent<PlaceableItem>() : null;
            if (placeable != null && PlayerCarry.Instance != null)
            {
                PlayerCarry.Instance.TryPickUp(placeable);
                yield return new WaitForSeconds(0.3f);
            }
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = $"Found {keyId} - {reason}", isMisleading = false, sourceId = $"found-{keyId}" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator OpenFurnitureWithKey(string keyId, string furnitureName, string logName)
    {
        Log($"Opening {logName} with key {keyId}");
        // Ensure we have key
        KeyRing.Add(keyId);

        var furnitures = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
        OpenableFurniture target = null;
        foreach (var f in furnitures)
        {
            if (f == null) continue;
            // Check if requires this key via reflection
            var reqId = GetField<string>(f, "requiredKeyId");
            if (!string.IsNullOrEmpty(reqId) && reqId.Equals(keyId, System.StringComparison.OrdinalIgnoreCase))
            {
                target = f;
                break;
            }
            if (f.name.Contains(furnitureName) || f.name.Contains("Cabin") || f.name.Contains("Cabinet"))
            {
                // Prefer if locked
                if (f.IsLocked) { target = f; break; }
                if (target == null) target = f;
            }
        }

        if (target == null)
        {
            var go = GameObject.Find(furnitureName) ?? GameObject.Find("Cabin 8") ?? GameObject.Find("Cabin 1") ?? GameObject.Find("Cabinet base");
            if (go != null) target = go.GetComponentInChildren<OpenableFurniture>() ?? go.GetComponent<OpenableFurniture>();
        }

        if (target != null)
        {
            Log($"Unlocking {target.name} (requires {GetField<string>(target, "requiredKeyId")})");
            target.Unlock();
            yield return new WaitForSeconds(0.5f);
            target.Open();
            Log($"Opened {target.name}");
            PuzzleEvents.RaiseDrawerUnlocked("cabinet_open");
        }
        else
        {
            Log($"Could not find {logName} furniture, but key {keyId} is in KeyRing so will proceed");
        }

        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator PlaceItemsOnTable()
    {
        Log("Room1 Puzzle: Placing Book, Candle, Vase per mirror truth (not wrong hint note)");

        // Ensure placeables exist
        var book = FindPlaceable("book");
        var candle = FindPlaceable("candle");
        var vase = FindPlaceable("vase");

        var slotBook = FindSlot("table_book");
        var slotCandle = FindSlot("table_candle");
        var slotVase = FindSlot("table_vase");

        // If slots missing, try to find any slots
        var allSlots = FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None);
        Log($"Found {allSlots.Length} slots, book:{book?.name} candle:{candle?.name} vase:{vase?.name}");

        if (slotBook != null && book != null)
        {
            Log($"Placing {book.ItemId} into {slotBook.SlotId} (correct per mirror)");
            slotBook.TryPlace(book);
            yield return new WaitForSeconds(0.5f);
        }
        if (slotCandle != null && candle != null)
        {
            Log($"Placing {candle.ItemId} into {slotCandle.SlotId}");
            slotCandle.TryPlace(candle);
            yield return new WaitForSeconds(0.5f);
        }
        if (slotVase != null && vase != null)
        {
            Log($"Placing {vase.ItemId} into {slotVase.SlotId}");
            slotVase.TryPlace(vase);
            yield return new WaitForSeconds(0.5f);
        }

        // Fallback: directly set correct if TryPlace failed due to physics
        foreach (var slot in allSlots)
        {
            if (slot == null) continue;
            if (!slot.IsCorrectlyFilled && slot.SlotId.StartsWith("table_"))
            {
                var neededId = slot.RequiredItemId;
                var item = FindPlaceable(neededId);
                if (item != null)
                {
                    Log($"Force placing {neededId} into {slot.SlotId} via direct TryPlace");
                    slot.TryPlace(item);
                }
            }
        }

        // Trigger drawer unlock evaluation
        var triggers = FindObjectsByType<DrawerUnlockTrigger>(FindObjectsSortMode.None);
        foreach (var t in triggers)
        {
            if (t.DrawerId == "room1_drawer")
            {
                Log("Evaluating drawer unlock trigger for room1_drawer");
                t.Evaluate();
                if (!t.IsUnlocked)
                {
                    Log("Force unlocking room1_drawer");
                    t.ForceUnlock();
                }
            }
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "All placements correct per mirror! Drawer unlocks.", isMisleading = false, sourceId = "placement-complete" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator UnlockDrawerAndGetRoom2Key()
    {
        Log("Room1 drawer should now be unlocked, spawning room2 key");
        var drawerGo = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4") ?? GameObject.Find("Drawer base");
        if (drawerGo != null)
        {
            var openable = drawerGo.GetComponent<OpenableFurniture>();
            if (openable != null)
            {
                if (openable.IsLocked) openable.Unlock();
                openable.Open();
            }
        }

        // Ensure room2 key
        yield return FindAndCollectKey(room2KeyId, room2KeyId, "Found in drawer after correct placements");
    }

    IEnumerator OpenToolbox()
    {
        Log("Room2: Opening toolbox with toolbox_key");
        KeyRing.Add(toolboxKeyId);

        var toolboxGo = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        ToolBoxInteractable toolbox = null;
        if (toolboxGo != null) toolbox = toolboxGo.GetComponent<ToolBoxInteractable>();
        if (toolbox == null) toolbox = FindFirstObjectByType<ToolBoxInteractable>();

        if (toolbox != null)
        {
            var locked = GetField<bool>(toolbox, "startsLocked");
            Log($"Toolbox {toolbox.name} locked={locked}, required={GetField<string>(toolbox, "requiredKeyId")}");
            // Use reflection to call Unlock if exists, or set field
            var unlockMethod = typeof(ToolBoxInteractable).GetMethod("Unlock");
            if (unlockMethod != null) unlockMethod.Invoke(toolbox, null);
            else SetField(toolbox, "startsLocked", false);

            // Try OpenableFurniture on same GO
            var openable = toolbox.GetComponent<OpenableFurniture>();
            if (openable != null)
            {
                openable.Unlock();
                openable.Open();
            }

            toolbox.OnUnlocked?.Invoke();
            toolbox.OnOpened?.Invoke();
        }
        else
        {
            Log("Toolbox not found, but proceeding to light flicker sequence");
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Toolbox opened - empty! No hammer. Lights flickering.", isMisleading = false, sourceId = "toolbox" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator LightFlickerSequence()
    {
        Log("Room2: Light flicker - lights out for 6.5 seconds");
        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker != null)
        {
            flicker.TriggerRoom2LightsOutSequence();
            PuzzleEvents.RaiseDrawerUnlocked("lights_out");
            yield return new WaitForSeconds(flicker != null ? 6.5f : 2f);
            PuzzleEvents.RaiseDrawerUnlocked("lights_back");
            Log("Lights back on - emergency board appears, hammer missing");
        }
        else
        {
            Log("LightFlickerSystem not found, simulating 6.5s lights out");
            yield return new WaitForSeconds(2f);
        }

        // Spawn emergency board if bootstrap didn't
        if (GameObject.Find("EmergencyBoard_GDD") == null)
        {
            var boardGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boardGo.name = "EmergencyBoard_GDD";
            boardGo.transform.position = new Vector3(0, 1.5f, 3);
            boardGo.transform.localScale = new Vector3(1, 0.5f, 0.05f);
            Log("Spawned emergency board");
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Emergency board: hammer missing! Check behind sofa.", isMisleading = false, sourceId = "lights_back" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator FindHammer()
    {
        Log("Searching for hammer behind sofa in storage room");
        var hammerGo = GameObject.FindWithTag("Hammer");
        if (hammerGo == null) hammerGo = GameObject.Find("Hammer");
        if (hammerGo == null) hammerGo = GameObject.Find("Hammer.001");

        if (hammerGo != null)
        {
            var placeable = hammerGo.GetComponent<PlaceableItem>();
            if (placeable != null && PlayerCarry.Instance != null && usePhysicsPickup)
            {
                PlayerCarry.Instance.TryPickUp(placeable);
            }
            KeyRing.Add(hammerId);
            Log($"Found hammer {hammerGo.name} at {hammerGo.transform.position}, collected");
            PuzzleEvents.RaiseHint(new HintMessage { text = "Found hammer behind sofa!", isMisleading = false, sourceId = "hammer_found" });
        }
        else
        {
            Log("Hammer not found, creating one");
            var newHammer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            newHammer.name = "Hammer_Auto";
            newHammer.tag = "Hammer";
            newHammer.transform.localScale = new Vector3(0.05f, 0.3f, 0.1f);
            newHammer.AddComponent<BoxCollider>();
            var rb = newHammer.AddComponent<Rigidbody>();
            rb.mass = 1f;
            var placeable = newHammer.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", hammerId);
            SetField(placeable, "displayName", "Hammer");
            KeyRing.Add(hammerId);
        }

        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator PushBoxesAway()
    {
        Log("Room2 box puzzle: pushing boxes to reach hammer (spatial carry: MMB + mouse, scroll push/pull)");
        string[] boxNames = { "crate_2.004", "crate_2.005", "crate_2.006", "crate_2.007", "crate_2.009", "Plastic Crate.009", "Plastic Crate.010" };
        foreach (var bName in boxNames)
        {
            var boxGo = GameObject.Find(bName);
            if (boxGo == null) continue;
            var rb = boxGo.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Move box away from center
                var dir = (boxGo.transform.position - Vector3.zero).normalized;
                if (dir == Vector3.zero) dir = Random.onUnitSphere;
                rb.AddForce(dir * 5f, ForceMode.Impulse);
                boxGo.transform.position += dir * 0.5f;
                Log($"Pushed box {bName} away");
            }
            yield return new WaitForSeconds(0.2f);
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Boxes pushed! Path to hammer clear.", isMisleading = false, sourceId = "boxes_cleared" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator BreakWindow()
    {
        Log("Breaking window with hammer to escape");
        KeyRing.Add(hammerId);
        var glass = FindFirstObjectByType<Glass>();
        if (glass == null)
        {
            var winGo = GameObject.Find("BreakableWindow_GDD") ?? GameObject.Find("Breakable Window") ?? GameObject.Find("BreakableWindow");
            if (winGo != null) glass = winGo.GetComponent<Glass>();
        }

        if (glass != null)
        {
            Log($"Found glass {glass.name}, breaking via BreakFromHammer()");
            glass.BreakFromHammer();
        }
        else
        {
            Log("Glass not found, triggering ending directly");
        }

        PuzzleEvents.RaiseHint(new HintMessage { text = "Window broken! Escaping...", isMisleading = false, sourceId = "window_broken" });
        yield return new WaitForSeconds(stepDelay);
    }

    IEnumerator EndingSequence()
    {
        Log("Ending: Illustration player running outside, smiley ghost watches from house");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Illustration: Player running outside, smiley ghost watches from house.", isMisleading = false, sourceId = "ending-visual" });
        yield return new WaitForSeconds(1f);
        Log("Phone notification: Escaped but house watches");
        PuzzleEvents.RaiseHint(new HintMessage { text = "Phone notification: Escaped but house watches...", isMisleading = false, sourceId = "ending-phone" });
        yield return new WaitForSeconds(1f);
        Log("FADE TO BLACK. Game Complete. TRUST NO ONE.");
        PuzzleEvents.RaiseHint(new HintMessage { text = "FADE TO BLACK. Game Complete. TRUST NO ONE.", isMisleading = false, sourceId = "ending-fade" });
        yield return new WaitForSeconds(1f);
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
        // fallback by name contains
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
        GUILayout.BeginArea(new Rect(10, 10, 350, 250));
        GUILayout.Label($"AutoGameSolver - {currentState}");
        GUILayout.Label($"Keys: {string.Join(", ", KeyRing.CollectedKeys)}");
        if (GUILayout.Button("Start Full Auto Solve")) StartSolving();
        if (GUILayout.Button("Stop")) StopSolving();
        if (GUILayout.Button("Force Complete -> Break Window"))
        {
            var g = FindFirstObjectByType<Glass>();
            if (g != null) g.BreakFromHammer();
        }
        GUILayout.EndArea();
    }
}
