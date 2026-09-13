using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

/// <summary>
/// GDD implementation for AutoTest scenes only - IMPROVED assembly for solvability.
/// Implements Room1 mirror truth puzzle and Room2 toolbox/light/hammer/window per GDD.
/// Runs only in AutoTest / NavMeshTest scenes.
/// 
/// IMPROVEMENTS for "Assemble objects in scene better to fit GDD better and make game more solvable":
/// - Room1: cabinet contains Book/Candle/Vase hidden until opened, key under table clearly visible via mirror, table slots with visual markers
/// - Room2: toolbox drawer has toolbox key, toolbox triggers 6.5s lights out, emergency board spawns, hammer behind sofa blocked by boxes in solvable maze
/// - Toolbox cover auto-fixed (no LogError), all UnityEvents null-checked
/// - Waypoints sampled on NavMesh, furniture rigidbodies fixed, player not frozen by phone
/// </summary>
[DefaultExecutionOrder(-5000)]
public class GDDPuzzleBootstrap : MonoBehaviour
{
    [Header("Room1")]
    public string cabinetKeyId = "cabinet_key";
    public string room2KeyId = "room2_key";

    [Header("Room2")]
    public string toolboxKeyId = "toolbox_key";
    public string hammerId = "hammer";
    public float lightOutDuration = 6.5f;

    [Header("Ending")]
    public float endingFadeDuration = 3f;

    [Header("Ending - escape window authored in the scene")]
    [Tooltip("Root of the wall panel that carries the window opening. Authored into the AutoTest scenes; built at runtime if absent.")]
    public string escapeWindowRootName = "EscapeWindow_GDD";
    public string breakableWindowName = "BreakableWindow_GDD";
    public string brokenWindowName = "BrokenWindow_GDD";
    public string escapeVolumeName = "EscapeVolume_GDD";
    public string outsideViewName = "OutsideView_GDD";

    [Tooltip("Room 2's far (-Z) wall face is the plane z = -16.81 (imported `Walls` mesh, world AABB x[-18.79,-8.96] y[0,3.92] z[-16.81,-4.93]). The panel sits flush against its inner face, right where the GDD puts the hammer behind the sofa.")]
    public Vector3 escapeWallCentre = new Vector3(-14.185f, 1.96f, -16.51f);
    public float escapeWallThickness = 0.4f;
    public float escapePanelWidth = 3.2f;
    public float escapeOpeningWidth = 1.4f;
    public float escapeOpeningHeight = 1.2f;
    public float escapeSillHeight = 0.9f;
    public float escapeRoomHeight = 3.92f;

    [Header("Assembly")]
    public bool verboseAssembly = true;
    public bool createVisualMarkers = true;

    private bool room2SequenceStarted;
    private float lastBlockedLog = -999f;
    private bool endingTriggered;
    private Transform escapeVolume;

    void Awake()
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!sceneName.Contains("AutoTest") && !sceneName.Contains("NavMeshTest"))
        {
            enabled = false;
            return;
        }
        Debug.Log($"[GDDPuzzleBootstrap] Init for {sceneName} - will assemble GDD scene for solvability");
    }

    IEnumerator Start()
    {
        yield return null;
        yield return null;
        SetupAll();
    }

    void SetupAll()
    {
        Log("=== GDD Assembly Start - making game solvable per GDD ===");
        SetupPlayer();
        SetupFurnitureRigidbodies();
        SetupWrongHintSystem();
        SetupMirror();
        SetupRoom1_GDD();
        SetupRoom2_GDD();
        SetupEnding_GDD();
        SetupWaypointsForMover_GDD();
        SetupPhoneStory();
        // Re-bake NavMesh after assembly
        var baker = FindFirstObjectByType<NavMeshAutoBaker>();
        baker?.TryBake();
        Log("=== GDD Assembly Complete - scene now solvable: mirror truth, cabinet key under table, book/candle/vase placement, drawer->room2 key, toolbox->lights out 6.5s->emergency board->hammer behind sofa->boxes push->window break ===");
    }

    void Log(string msg)
    {
        if (verboseAssembly) Debug.Log($"[GDD] {msg}");
    }

    #region Waypoints - Improved NavMesh sampled

    void SetupWaypointsForMover_GDD()
    {
        var player = GameObject.FindWithTag("Player");
        if (player == null) player = GameObject.Find("Player FPP");
        if (player == null)
        {
            var cc = FindFirstObjectByType<CharacterController>();
            if (cc != null) player = cc.gameObject;
        }
        if (player == null) return;
        var mover = player.GetComponent<AutoPlayerMover>();
        if (mover == null) return;

        Transform CreateWaypoint(string name, Vector3 pos)
        {
            var existing = GameObject.Find(name);
            if (existing != null) return existing.transform;
            // Sample NavMesh for reachable position
            if (NavMesh.SamplePosition(pos, out var hit, 5f, NavMesh.AllAreas))
                pos = hit.position;
            var go = new GameObject(name);
            go.transform.position = pos;
            if (createVisualMarkers)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = name + "_Marker";
                marker.transform.SetParent(go.transform);
                marker.transform.localPosition = new Vector3(0, 0.1f, 0);
                marker.transform.localScale = new Vector3(0.3f, 0.05f, 0.3f);
                var rend = marker.GetComponent<Renderer>();
                if (rend != null) rend.material.color = Color.cyan;
                Destroy(marker.GetComponent<Collider>());
            }
            return go.transform;
        }

        var table = GameObject.Find("Table");
        var cabinet = GameObject.Find("Cabin 8") ?? GameObject.Find("Cabin 1") ?? GameObject.Find("Cabinet base");
        var drawer1 = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4") ?? GameObject.Find("Drawer base");
        var drawer2 = GameObject.Find("Drawer 2") ?? GameObject.Find("Cabin 6");
        var toolbox = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        var hammer = GameObject.Find("Hammer") ?? GameObject.Find("Hammer.001");
        var sofa = GameObject.Find("chair 2") ?? GameObject.Find("Sofa") ?? GameObject.Find("Chair");
        var window = GameObject.Find("BreakableWindow_GDD") ?? GameObject.Find("Breakable Window") ?? GameObject.Find("BreakableWindow");

        Vector3 basePos = table != null ? table.transform.position : Vector3.zero;
        if (basePos == Vector3.zero) basePos = new Vector3(0, 0, 0);

        // GDD logical flow positions
        var wpMirrorKey = CreateWaypoint("WP_MirrorKey", basePos + new Vector3(0.5f, 0, 0.8f)); // under table offset
        var wpCabinet = CreateWaypoint("WP_Cabinet", cabinet != null ? cabinet.transform.position + Vector3.forward * 1.2f : basePos + new Vector3(2, 0, 0));
        var wpTable = CreateWaypoint("WP_Table", basePos + Vector3.forward * 0.5f);
        var wpDrawer = CreateWaypoint("WP_ToolboxDrawer", drawer2 != null ? drawer2.transform.position + Vector3.forward * 1f : basePos + new Vector3(-2, 0, 0));
        var wpToolbox = CreateWaypoint("WP_Toolbox", toolbox != null ? toolbox.transform.position + Vector3.forward * 1f : basePos + new Vector3(0, 0, 2));
        var wpSofa = CreateWaypoint("WP_SofaHammer", sofa != null ? sofa.transform.position + Vector3.forward * 0.8f : (hammer != null ? hammer.transform.position + Vector3.back * 0.5f : basePos + new Vector3(-3, 0, 0)));
        // The escape window faces +Z into Room 2, so the approach waypoint must be on the
        // room side of the wall (previously this used Vector3.back and landed 1.5m outside the house).
        var wpWindow = CreateWaypoint("WP_Window", window != null ? window.transform.position + Vector3.forward * 1.2f : basePos + new Vector3(5, 0, 0));

        SetField(mover, "mirrorKeyLocation", wpMirrorKey);
        SetField(mover, "cabinetLocation", wpCabinet);
        SetField(mover, "placementTableLocation", wpTable);
        SetField(mover, "toolboxDrawerLocation", wpDrawer);
        SetField(mover, "toolboxLocation", wpToolbox);
        SetField(mover, "sofaHammerLocation", wpSofa);
        SetField(mover, "windowLocation", wpWindow);

        Log($"Waypoints assembled on NavMesh: mirrorKey={wpMirrorKey.position} cabinet={wpCabinet.position} table={wpTable.position} drawer={wpDrawer.position} toolbox={wpToolbox.position} sofa={wpSofa.position} window={wpWindow.position}");
    }

    #endregion

    #region Furniture Rigidbodies

    void SetupFurnitureRigidbodies()
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

            var rb = movingPart.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = f.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    rb = movingPart.gameObject.AddComponent<Rigidbody>();
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
            var movingRbField = typeof(OpenableFurniture).GetField("movingRigidbody", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (movingRbField != null && movingRbField.GetValue(f) == null)
            {
                movingRbField.SetValue(f, rb);
            }
        }
        if (fixedCount > 0) Log($"Fixed {fixedCount} furniture Rigidbodies");
    }

    #endregion

    #region Player

    void SetupPlayer()
    {
        var player = GameObject.FindWithTag("Player");
        if (player == null)
        {
            var cc = FindFirstObjectByType<CharacterController>();
            if (cc != null) player = cc.gameObject;
        }
        if (player == null) player = GameObject.Find("Player FPP");
        if (player == null) return;

        var agent = player.GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            agent = player.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f;
            agent.height = 1.8f;
            agent.speed = 3.5f;
            agent.angularSpeed = 360f;
            agent.acceleration = 8f;
        }

        var carry = player.GetComponent<PlayerCarry>();
        if (carry == null)
        {
            var fpp = player.GetComponentInChildren<GameAssets.Scripts.Entities.Player.FPPCameraController>();
            if (fpp != null)
            {
                carry = fpp.GetComponent<PlayerCarry>();
                if (carry == null) carry = fpp.gameObject.AddComponent<PlayerCarry>();
            }
            else
            {
                carry = player.AddComponent<PlayerCarry>();
            }
        }

        // Fix phone freeze bug
        var phone = FindFirstObjectByType<GameAssets.Scripts.UI.Mobile.MobilePhoneController>();
        if (phone != null)
        {
            var field = typeof(GameAssets.Scripts.UI.Mobile.MobilePhoneController).GetField("disableWhileOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var arr = field.GetValue(phone) as Behaviour[];
                if (arr != null)
                {
                    var filtered = new List<Behaviour>();
                    foreach (var b in arr)
                    {
                        if (b == null) continue;
                        if (b is AutoPlayerMover) continue;
                        if (b is NavMeshAgent) continue;
                        if (b is PlayerCarry) continue;
                        filtered.Add(b);
                    }
                    field.SetValue(phone, filtered.ToArray());
                }
            }
        }
    }

    #endregion

    #region Wrong Hint System

    void SetupWrongHintSystem()
    {
        var whs = FindFirstObjectByType<WrongHintSystem>();
        if (whs == null)
        {
            var go = new GameObject("WrongHintSystem_GDD");
            whs = go.AddComponent<WrongHintSystem>();
        }

        var hintsField = typeof(WrongHintSystem).GetField("hints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (hintsField != null)
        {
            var list = hintsField.GetValue(whs) as List<WrongHintSystem.HintEntry>;
            if (list == null) list = new List<WrongHintSystem.HintEntry>();
            if (list.Count < 5)
            {
                list.Clear();
                // GDD: wrong hints vs truth via mirror
                list.Add(new WrongHintSystem.HintEntry { id = "room1-key-drawer-wrong", text = "The key is in the drawer. Check the top drawer! [WRONG HINT - mirror shows truth]", isMisleading = true });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-key-table-truth", text = "TRUTH: Mirror shows key is UNDER TABLE, not drawer. Look under Table!", isMisleading = false, triggerOnCorrectSlotId = "cabinet_key_found" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-cabinet-open", text = "Cabinet opened! Inside: Book, Candle, Vase. Place them on table per MIRROR reflection, not note.", isMisleading = false, triggerOnDrawerId = "cabinet_open" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-placement-wrong", text = "Note says: Place Book left, Candle middle, Vase right. [WRONG - check mirror]", isMisleading = true, triggerOnWrongSlotId = "table_book" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-placement-truth", text = "Mirror reflection shows correct placement: Book, Candle, Vase positions are mirrored!", isMisleading = false, triggerOnCorrectSlotId = "table_book" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-drawer-unlock", text = "Correct! Drawer opened, found key to next door (room2).", isMisleading = false, triggerOnDrawerId = "room1_drawer" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-toolbox-wrong", text = "Hammer is inside toolbox. [WRONG - toolbox empty, lights will flicker]", isMisleading = true, triggerOnDrawerId = "toolbox" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-light-out", text = "Lights out! Wait 6-7 seconds. Something will change...", isMisleading = false, triggerOnDrawerId = "lights_out" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-light-back", text = "Lights back! Emergency board: hammer missing! Check behind sofa in storage.", isMisleading = false, triggerOnDrawerId = "lights_back" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-hammer-sofa", text = "Hammer behind sofa, but boxes block path. Push boxes: MMB + mouse to shove, Scroll to push/pull.", isMisleading = false, triggerOnDrawerId = "hammer_hint" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-box-puzzle", text = "Boxes block hammer. Use spatial carry: Right-click + G to grab, scroll wheel push/pull.", isMisleading = false });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-window-break", text = "Found hammer! Break window with hammer to escape! Tag: Hammer", isMisleading = false, triggerOnDrawerId = "window" });
                list.Add(new WrongHintSystem.HintEntry { id = "trust-no-one", text = "TRUST NO ONE. Mirror shows truth. Wrong hints will mislead you.", isMisleading = false });
                hintsField.SetValue(whs, list);
                Log($"WrongHintSystem assembled with {list.Count} hints (wrong vs truth per GDD)");
            }
        }
    }

    #endregion

    #region Mirror

    void SetupMirror()
    {
        var mirror = FindFirstObjectByType<PerfectMirror>();
        if (mirror == null)
        {
            var mirrorGo = GameObject.Find("Mirror");
            if (mirrorGo != null)
            {
                mirror = mirrorGo.GetComponent<PerfectMirror>();
                if (mirror == null) mirror = mirrorGo.AddComponent<PerfectMirror>();
            }
        }
        if (mirror != null)
        {
            var table = GameObject.Find("Table");
            var key = GameObject.Find("Cabinet key 2");
            if (key != null && !mirror.objectsToMirror.Contains(key)) mirror.objectsToMirror.Add(key);
            if (table != null && !mirror.objectsToMirror.Contains(table)) mirror.objectsToMirror.Add(table);
            foreach (var name in new[] { "Puzzle book", "Puzzle book (1)", "Candle_low", "Candle_low (1)", "Vase", "Vase (1)", "Book", "Candle", "Vase" })
            {
                var go = GameObject.Find(name);
                if (go != null && !mirror.objectsToMirror.Contains(go)) mirror.objectsToMirror.Add(go);
            }
            mirror.RebuildMirror();
            Log($"Mirror setup with {mirror.objectsToMirror.Count} objects to show truth");
        }
    }

    #endregion

    #region Room1 GDD - Improved

    void SetupRoom1_GDD()
    {
        Log("--- Room1 GDD Assembly: mirror truth puzzle ---");

        // 1. Cabinet key under table (truth) vs drawer (wrong hint)
        var cabinetKeyGo = GameObject.Find("Cabinet key 2") ?? GameObject.Find("cabinet_key") ?? GameObject.Find("CabinetKey");
        if (cabinetKeyGo == null)
        {
            // Create key if not exists
            cabinetKeyGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabinetKeyGo.name = "Cabinet key 2";
            cabinetKeyGo.transform.localScale = new Vector3(0.1f, 0.02f, 0.05f);
            Destroy(cabinetKeyGo.GetComponent<Collider>());
            cabinetKeyGo.AddComponent<BoxCollider>();
            var rb = cabinetKeyGo.AddComponent<Rigidbody>();
            rb.mass = 0.2f;
            Log("Created Cabinet key 2");
        }

        var placeableKey = cabinetKeyGo.GetComponent<PlaceableItem>();
        if (placeableKey == null) placeableKey = cabinetKeyGo.AddComponent<PlaceableItem>();
        SetField(placeableKey, "itemId", cabinetKeyId);
        SetField(placeableKey, "displayName", "Cabinet Key");
        SetField(placeableKey, "carryStyle", PlaceableItem.CarryStyle.Handheld);

        var keyItem = cabinetKeyGo.GetComponent<KeyItem>();
        if (keyItem == null) keyItem = cabinetKeyGo.AddComponent<KeyItem>();
        SetField(keyItem, "keyId", cabinetKeyId);
        SetField(keyItem, "displayName", "Cabinet Key");
        SetField(keyItem, "collectOnPickup", true);

        if (cabinetKeyGo.GetComponent<Collider>() == null) cabinetKeyGo.AddComponent<BoxCollider>();
        if (cabinetKeyGo.GetComponent<Rigidbody>() == null)
        {
            var rb = cabinetKeyGo.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
        }

        var table = GameObject.Find("Table");
        if (table != null)
        {
            // Place key UNDER table per mirror truth, with clearance to avoid floor/table penetration
            Vector3 underTablePos = table.transform.position + new Vector3(0.2f, -0.25f, 0.3f); // -0.25 not -0.45 to avoid floor
            // Ensure not penetrating floor - raycast down to find floor
            if (Physics.Raycast(underTablePos + Vector3.up * 1f, Vector3.down, out var hit, 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                underTablePos.y = hit.point.y + 0.08f; // 8cm above floor
            }
            // Ensure free spot
            if (!IsSpotFree(underTablePos, cabinetKeyGo.transform.localScale * 1.2f))
            {
                underTablePos = FindFreeSpotNear(table.transform.position + new Vector3(0.3f, 0.1f, 0.4f), 0.6f, cabinetKeyGo.transform.localScale);
            }
            cabinetKeyGo.transform.position = underTablePos;
            cabinetKeyGo.transform.rotation = Quaternion.identity;
            var rbKey = cabinetKeyGo.GetComponent<Rigidbody>();
            if (rbKey != null) { rbKey.isKinematic = false; rbKey.collisionDetectionMode = CollisionDetectionMode.Continuous; }
            var rend = cabinetKeyGo.GetComponent<Renderer>();
            if (rend != null) rend.material.color = Color.yellow;
            if (createVisualMarkers && GameObject.Find("KeyMarker_UnderTable") == null)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "KeyMarker_UnderTable";
                marker.transform.position = underTablePos + Vector3.down * 0.02f;
                marker.transform.localScale = new Vector3(0.2f, 0.02f, 0.2f);
                var mr = marker.GetComponent<Renderer>();
                if (mr != null) mr.material.color = new Color(1, 1, 0, 0.5f);
                Destroy(marker.GetComponent<Collider>());
            }
            Log($"Cabinet key placed UNDER TABLE at {underTablePos} per mirror truth, non-penetrating, above floor");
        }

        // 2. Book, Candle, Vase - should be inside cabinet, revealed when opened
        SetupPlaceable_GDD("Puzzle book", "book", "Book", true);
        SetupPlaceable_GDD("Puzzle book (1)", "book", "Book", false);
        SetupPlaceable_GDD("Candle_low", "candle", "Candle", true);
        SetupPlaceable_GDD("Candle_low (1)", "candle", "Candle", false);
        SetupPlaceable_GDD("Vase", "vase", "Vase", true);
        SetupPlaceable_GDD("Vase (1)", "vase", "Vase", false);

        var tableGo = GameObject.Find("Table");
        if (tableGo != null)
        {
            // Create 3 slots per GDD: Book, Candle, Vase placement per mirror
            CreateSlot_GDD(tableGo, "table_book", "book", new Vector3(-0.5f, 0.18f, 0), "BookSlot");
            CreateSlot_GDD(tableGo, "table_candle", "candle", new Vector3(0, 0.18f, 0), "CandleSlot");
            CreateSlot_GDD(tableGo, "table_vase", "vase", new Vector3(0.5f, 0.18f, 0), "VaseSlot");
            Log("Table slots created: book, candle, vase - placement per mirror reflection");
        }

        // 3. Cabinet - locked with cabinet_key, contains book/candle/vase
        var cabinetGo = GameObject.Find("Cabin 8") ?? GameObject.Find("Cabin 1") ?? GameObject.Find("Cabinet base");
        if (cabinetGo != null)
        {
            var openable = cabinetGo.GetComponent<OpenableFurniture>();
            if (openable == null) openable = cabinetGo.GetComponentInChildren<OpenableFurniture>();
            if (openable != null)
            {
                SetField(openable, "startsLocked", true);
                SetField(openable, "requiredKeyId", cabinetKeyId);
                SetField(openable, "unlockWithKey", true);
                SetField(openable, "openWhenUnlocked", true);
                if (openable.OnUnlocked == null) openable.OnUnlocked = new UnityEngine.Events.UnityEvent();
                if (openable.OnOpened == null) openable.OnOpened = new UnityEngine.Events.UnityEvent();
                openable.OnUnlocked.RemoveAllListeners();
                openable.OnUnlocked.AddListener(() =>
                {
                    PuzzleEvents.RaiseDrawerUnlocked("cabinet_open");
                    PuzzleEvents.RaiseHint(new HintMessage { text = "Cabinet opened! Found Book, Candle, Vase inside. Place them on table per MIRROR reflection (not wrong note).", isMisleading = false, sourceId = "cabinet_open" });
                    // Reveal hidden objects inside cabinet
                    RevealCabinetContents(cabinetGo);
                });
                Log($"Cabinet {cabinetGo.name} locked with {cabinetKeyId}, will reveal contents when opened");
            }
            // Initially hide book/candle/vase inside cabinet
            HideObjectsInsideCabinet(cabinetGo);
        }

        // 4. Drawer that unlocks when placements correct, contains room2 key
        var drawerGo = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4") ?? GameObject.Find("Drawer base");
        if (drawerGo != null)
        {
            var openable = drawerGo.GetComponent<OpenableFurniture>();
            if (openable == null) openable = drawerGo.AddComponent<OpenableFurniture>();
            if (openable.OnUnlocked == null) openable.OnUnlocked = new UnityEngine.Events.UnityEvent();
            if (openable.OnOpened == null) openable.OnOpened = new UnityEngine.Events.UnityEvent();
            SetField(openable, "startsLocked", true);
            SetField(openable, "unlockWithKey", false);
            SetField(openable, "openWhenUnlocked", false);

            // Remove old trigger if exists
            var oldTrigger = GameObject.Find("DrawerUnlock_Room1_GDD");
            if (oldTrigger != null) Destroy(oldTrigger);

            var triggerGo = new GameObject("DrawerUnlock_Room1_GDD");
            var trigger = triggerGo.AddComponent<DrawerUnlockTrigger>();
            if (trigger.OnUnlocked == null) trigger.OnUnlocked = new UnityEngine.Events.UnityEvent();
            if (trigger.OnRelocked == null) trigger.OnRelocked = new UnityEngine.Events.UnityEvent();
            SetField(trigger, "drawerId", "room1_drawer");
            SetField(trigger, "condition", DrawerUnlockTrigger.Condition.AllSlotsCorrect);
            var slots = new List<PlacementSlot>(FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None));
            var tableSlots = new List<PlacementSlot>();
            foreach (var s in slots)
            {
                if (s == null) continue;
                try
                {
                    if (!string.IsNullOrEmpty(s.SlotId) && s.SlotId.StartsWith("table_")) tableSlots.Add(s);
                }
                catch { }
            }
            SetField(trigger, "requiredSlots", tableSlots);
            SetField(trigger, "drawers", new List<OpenableFurniture> { openable });
            SetField(trigger, "unlockOnce", true);

            trigger.OnUnlocked.AddListener(() =>
            {
                SpawnKey_GDD(drawerGo, room2KeyId, "Room2 Key", new Vector3(0, 0.2f, 0.2f));
                PuzzleEvents.RaiseDrawerUnlocked("room1_drawer");
                PuzzleEvents.RaiseHint(new HintMessage { text = "Drawer unlocked! Correct placements per mirror! Found key to next door (room2).", isMisleading = false, sourceId = "room1_drawer" });
                Log("Room1 drawer unlocked, room2 key spawned");
            });
            Log($"Room1 drawer {drawerGo.name} setup: locked until table placements correct, will spawn {room2KeyId}");
        }
    }

    void HideObjectsInsideCabinet(GameObject cabinetGo)
    {
        string[] objNames = { "Puzzle book", "Puzzle book (1)", "Candle_low", "Candle_low (1)", "Vase", "Vase (1)" };
        for (int idx = 0; idx < objNames.Length; idx++)
        {
            var n = objNames[idx];
            var go = GameObject.Find(n);
            if (go == null) continue;
            var placeable = go.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = go.AddComponent<PlaceableItem>();
            // Use non-penetrating grid inside cabinet
            Vector3 localPos = GetNonPenetratingPositionForCabinetContent(cabinetGo, idx, new Vector3(0.2f, 0.2f, 0.2f));
            if (go.transform.parent != cabinetGo.transform)
            {
                go.transform.SetParent(cabinetGo.transform);
            }
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            var rend = go.GetComponent<Renderer>();
            if (rend != null) rend.enabled = true;
            if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.8f;
            rb.isKinematic = true;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            Log($"Cabinet content {go.name} placed inside {cabinetGo.name} at non-penetrating {localPos}");
        }
    }

    void RevealCabinetContents(GameObject cabinetGo)
    {
        string[] objNames = { "Puzzle book", "Puzzle book (1)", "Candle_low", "Candle_low (1)", "Vase", "Vase (1)" };
        for (int idx = 0; idx < objNames.Length; idx++)
        {
            var n = objNames[idx];
            var go = GameObject.Find(n);
            if (go == null) continue;
            var rend = go.GetComponent<Renderer>();
            if (rend != null) rend.enabled = true;
            go.transform.SetParent(null);
            // Find non-penetrating spot in front of cabinet, spaced
            Vector3 baseFront = cabinetGo.transform.position + cabinetGo.transform.forward * 0.9f;
            Vector3 spacedOffset = new Vector3(((idx % 3) - 1) * 0.5f, 0.4f, (idx / 3) * 0.4f);
            Vector3 frontPos = baseFront + spacedOffset;
            // Ensure free
            Vector3 size = go.transform.localScale;
            if (size.magnitude < 0.1f) size = new Vector3(0.2f, 0.2f, 0.2f);
            if (!IsSpotFree(frontPos, size * 1.2f))
            {
                frontPos = FindFreeSpotNear(baseFront, 1.0f, size);
            }
            // Sample NavMesh
            if (UnityEngine.AI.NavMesh.SamplePosition(frontPos, out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                frontPos = hit.position + Vector3.up * 0.05f;
            // Ensure above floor
            if (Physics.Raycast(frontPos + Vector3.up * 1f, Vector3.down, out var hitFloor, 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                frontPos.y = Mathf.Max(frontPos.y, hitFloor.point.y + 0.15f);
            }
            go.transform.position = frontPos;
            go.transform.rotation = Quaternion.identity;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                rb.WakeUp();
                rb.linearVelocity = Vector3.zero;
            }
            var placeable = go.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = go.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", go.name.ToLower().Contains("book") ? "book" : go.name.ToLower().Contains("candle") ? "candle" : "vase");
            Log($"Revealed {go.name} at non-penetrating {frontPos}");
        }
        Log("Cabinet contents revealed non-penetrating: Book, Candle, Vase now visible and carryable");
    }

    void SetupPlaceable_GDD(string goName, string itemId, string displayName, bool isPrimary)
    {
        var go = GameObject.Find(goName);
        if (go == null) return;
        var placeable = go.GetComponent<PlaceableItem>();
        if (placeable == null) placeable = go.AddComponent<PlaceableItem>();
        SetField(placeable, "itemId", itemId);
        SetField(placeable, "displayName", displayName);
        SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Handheld);
        SetField(placeable, "defaultHoldDistance", 1.5f);
        if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
        var rb = go.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.linearDamping = 0.5f;
            rb.angularDamping = 1f;
        }
        rb.isKinematic = false;
        // Make primary ones more visible
        if (isPrimary)
        {
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                if (itemId == "book") rend.material.color = new Color(0.6f, 0.3f, 0.1f);
                else if (itemId == "candle") rend.material.color = new Color(1f, 0.9f, 0.6f);
                else if (itemId == "vase") rend.material.color = new Color(0.4f, 0.7f, 0.9f);
            }
        }
    }

    void CreateSlot_GDD(GameObject parent, string slotId, string requiredId, Vector3 localPos, string name)
    {
        foreach (var s in FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None))
            if (s.SlotId == slotId) return;

        var slotGo = new GameObject(name + "_" + slotId);
        slotGo.transform.SetParent(parent.transform);
        // Ensure slot is above table surface to avoid penetration
        Vector3 safeLocal = localPos + Vector3.up * 0.05f;
        slotGo.transform.localPosition = safeLocal;
        slotGo.transform.localRotation = Quaternion.identity;
        // Check world free
        Vector3 worldPos = parent.transform.TransformPoint(safeLocal);
        if (!IsSpotFree(worldPos, new Vector3(0.6f, 0.25f, 0.6f)))
        {
            // Slight up offset
            worldPos += Vector3.up * 0.1f;
            safeLocal = parent.transform.InverseTransformPoint(worldPos);
            slotGo.transform.localPosition = safeLocal;
        }

        var col = slotGo.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = new Vector3(0.6f, 0.25f, 0.6f);
        var slot = slotGo.AddComponent<PlacementSlot>();
        SetField(slot, "slotId", slotId);
        SetField(slot, "requiredItemId", requiredId);
        var snap = new GameObject("SnapPoint").transform;
        snap.SetParent(slotGo.transform);
        snap.localPosition = Vector3.zero;
        SetField(slot, "snapPoint", snap);
        SetField(slot, "allowWrongItems", true);
        SetField(slot, "lockWhenCorrect", true);

        if (createVisualMarkers)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "Marker_" + slotId;
            marker.transform.SetParent(slotGo.transform);
            marker.transform.localPosition = new Vector3(0, -0.05f, 0);
            marker.transform.localScale = new Vector3(0.5f, 0.02f, 0.5f);
            var rend = marker.GetComponent<Renderer>();
            if (rend != null)
            {
                if (requiredId == "book") rend.material.color = new Color(0.6f, 0.3f, 0.1f, 0.5f);
                else if (requiredId == "candle") rend.material.color = new Color(1f, 0.9f, 0.3f, 0.5f);
                else rend.material.color = new Color(0.3f, 0.6f, 0.9f, 0.5f);
            }
            Destroy(marker.GetComponent<Collider>());
        }
    }

    #endregion

    #region Room2 GDD - Improved

    void SetupRoom2_GDD()
    {
        Log("--- Room2 GDD Assembly: toolbox, lights out 6.5s, hammer behind sofa, boxes puzzle, window break ---");

        // 1. Toolbox - improved cover handling
        var toolboxGo = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        if (toolboxGo == null)
        {
            var tb = FindFirstObjectByType<ToolBoxInteractable>();
            if (tb != null) toolboxGo = tb.gameObject;
        }
        if (toolboxGo == null)
        {
            // Create toolbox if missing
            toolboxGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            toolboxGo.name = "tool Box";
            toolboxGo.transform.position = new Vector3(2, 0.3f, 1);
            toolboxGo.transform.localScale = new Vector3(0.8f, 0.4f, 0.5f);
            Destroy(toolboxGo.GetComponent<BoxCollider>());
            toolboxGo.AddComponent<BoxCollider>();
            Log("Created tool Box");
        }

        var toolbox = toolboxGo.GetComponent<ToolBoxInteractable>();
        if (toolbox == null) toolbox = toolboxGo.AddComponent<ToolBoxInteractable>();
        if (toolbox.OnUnlocked == null) toolbox.OnUnlocked = new UnityEngine.Events.UnityEvent();
        if (toolbox.OnOpened == null) toolbox.OnOpened = new UnityEngine.Events.UnityEvent();
        if (toolbox.OnClosed == null) toolbox.OnClosed = new UnityEngine.Events.UnityEvent();
        if (toolbox.OnLockedAttempt == null) toolbox.OnLockedAttempt = new UnityEngine.Events.UnityEvent();
        SetField(toolbox, "startsLocked", true);
        SetField(toolbox, "requiredKeyId", toolboxKeyId);
        SetField(toolbox, "unlockWithKey", true);
        SetField(toolbox, "openOnUnlock", true);

        // Ensure cover exists (ToolBoxInteractable now auto-fixes, but we also ensure)
        EnsureToolboxCover(toolboxGo, toolbox);

        toolbox.OnUnlocked.RemoveAllListeners();
        toolbox.OnUnlocked.AddListener(() =>
        {
            if (!room2SequenceStarted)
            {
                room2SequenceStarted = true;
                StartCoroutine(LightFlickerSequence());
                Log("Toolbox unlocked -> starting light flicker 6.5s sequence per GDD");
            }
        });
        toolbox.OnOpened.RemoveAllListeners();
        toolbox.OnOpened.AddListener(() =>
        {
            PuzzleEvents.RaiseHint(new HintMessage { text = "Toolbox opened - EMPTY! No hammer inside (wrong hint was hammer inside). Lights flickering... Wait 6-7 sec", isMisleading = false, sourceId = "toolbox" });
        });

        // Ensure toolbox has collider and is interactable
        if (toolboxGo.GetComponent<Collider>() == null) toolboxGo.AddComponent<BoxCollider>();
        var toolboxPlaceable = toolboxGo.GetComponent<PlaceableItem>();
        if (toolboxPlaceable != null) DestroyImmediate(toolboxPlaceable); // toolbox should not be carryable

        Log($"Toolbox {toolboxGo.name} setup: locked with {toolboxKeyId}, cover={GetField<Transform>(toolbox, "cover")?.name}, triggers lights out");

        // 2. Drawer containing toolbox key (Drawer 2)
        var drawerWithKey = GameObject.Find("Drawer 2") ?? GameObject.Find("Cabin 6") ?? GameObject.Find("Drawer base");
        if (drawerWithKey == null)
        {
            drawerWithKey = GameObject.CreatePrimitive(PrimitiveType.Cube);
            drawerWithKey.name = "Drawer 2";
            drawerWithKey.transform.position = new Vector3(-1, 0.5f, 1);
            drawerWithKey.transform.localScale = new Vector3(0.6f, 0.3f, 0.5f);
            Log("Created Drawer 2 for toolbox key");
        }
        // Make drawer openable
        var drawerOpenable = drawerWithKey.GetComponent<OpenableFurniture>();
        if (drawerOpenable == null) drawerOpenable = drawerWithKey.AddComponent<OpenableFurniture>();
        if (drawerOpenable.OnUnlocked == null) drawerOpenable.OnUnlocked = new UnityEngine.Events.UnityEvent();
        if (drawerOpenable.OnOpened == null) drawerOpenable.OnOpened = new UnityEngine.Events.UnityEvent();
        SetField(drawerOpenable, "startsLocked", false); // drawer with toolbox key is not locked, easy to find per GDD
        SetField(drawerOpenable, "unlockWithKey", false);
        if (drawerWithKey.GetComponent<Collider>() == null) drawerWithKey.AddComponent<BoxCollider>();

        SpawnKey_GDD(drawerWithKey, toolboxKeyId, "Toolbox Key", new Vector3(0, 0.15f, 0.2f));
        Log($"Drawer {drawerWithKey.name} contains {toolboxKeyId}");

        // 3. Light flicker system - 6-7 seconds lights out per GDD
        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker == null)
        {
            var flickerGo = new GameObject("LightFlicker_GDD");
            flicker = flickerGo.AddComponent<LightFlickerSystem>();
            Log("Created LightFlicker_GDD");
        }
        if (flicker.OnLightsWentOut == null) flicker.OnLightsWentOut = new UnityEngine.Events.UnityEvent();
        if (flicker.OnLightsCameBackOn == null) flicker.OnLightsCameBackOn = new UnityEngine.Events.UnityEvent();
        var allLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        flicker.targetLights.Clear();
        foreach (var l in allLights) if (l.type != LightType.Directional) flicker.targetLights.Add(l);
        SetField(flicker, "lightsOutDuration", lightOutDuration);
        SetField(flicker, "triggerOnStart", false);

        flicker.OnLightsWentOut.RemoveAllListeners();
        flicker.OnLightsWentOut.AddListener(() =>
        {
            PuzzleEvents.RaiseDrawerUnlocked("lights_out");
            PuzzleEvents.RaiseHint(new HintMessage { text = "Lights OUT! Wait 6-7 seconds... Something is changing in room...", isMisleading = false, sourceId = "lights_out" });
            Log("Lights went out - 6.5s dark");
        });
        flicker.OnLightsCameBackOn.RemoveAllListeners();
        flicker.OnLightsCameBackOn.AddListener(() =>
        {
            SpawnEmergencyBoard_GDD();
            PuzzleEvents.RaiseDrawerUnlocked("lights_back");
            PuzzleEvents.RaiseHint(new HintMessage { text = "Lights BACK! Emergency board appeared: no hammer! Check behind sofa in storage. Hammer reminder spawned.", isMisleading = false, sourceId = "lights_back" });
            Log("Lights came back - emergency board spawned, hammer behind sofa");
        });
        Log($"LightFlicker setup: {flicker.targetLights.Count} lights, duration {lightOutDuration}s");

        // 4. Hammer behind sofa per GDD
        var hammerGo = GameObject.Find("Hammer") ?? GameObject.Find("Hammer.001");
        if (hammerGo == null)
        {
            hammerGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hammerGo.name = "Hammer";
            hammerGo.transform.localScale = new Vector3(0.05f, 0.3f, 0.1f);
            Log("Created Hammer");
        }
        var hammerPlaceable = hammerGo.GetComponent<PlaceableItem>();
        if (hammerPlaceable == null) hammerPlaceable = hammerGo.AddComponent<PlaceableItem>();
        SetField(hammerPlaceable, "itemId", hammerId);
        SetField(hammerPlaceable, "displayName", "Hammer");
        SetField(hammerPlaceable, "carryStyle", PlaceableItem.CarryStyle.Handheld);
        SetField(hammerPlaceable, "defaultHoldDistance", 1.2f);

        var sofa = GameObject.Find("chair 2") ?? GameObject.Find("Sofa") ?? GameObject.Find("Chair") ?? GameObject.Find("chair");
        Vector3 hammerPos;
        if (sofa != null)
        {
            // Behind the sofa, but clear of the escape wall panel: the panel occupies
            // x[-15.785,-12.585] z[-16.71,-16.31] y[0,3.92], so the old (0.8, 0.15, -1.5) offset
            // buried the hammer in Wall_Pier_Right. This offset puts it beside the sofa instead.
            hammerPos = sofa.transform.position + new Vector3(1.9f, 0.15f, -0.8f);
        }
        else
        {
            hammerPos = new Vector3(-12.2f, 0.2f, -16.0f);
        }
        // Ensure hammer not penetrating sofa or floor
        Vector3 hammerSize = hammerGo.transform.localScale;
        if (hammerSize.magnitude < 0.1f) hammerSize = new Vector3(0.05f, 0.3f, 0.1f);
        if (!IsSpotFree(hammerPos, hammerSize * 1.5f))
        {
            hammerPos = FindFreeSpotNear(hammerPos, 1.0f, hammerSize);
        }
        if (Physics.Raycast(hammerPos + Vector3.up * 1f, Vector3.down, out var hitHammerFloor, 2f, ~0, QueryTriggerInteraction.Ignore))
        {
            hammerPos.y = hitHammerFloor.point.y + 0.15f;
        }
        else
        {
            hammerPos.y = Mathf.Max(hammerPos.y, 0.15f);
        }
        hammerGo.transform.position = hammerPos;
        hammerGo.transform.rotation = Quaternion.identity;
        hammerGo.tag = "Hammer";
        if (hammerGo.GetComponent<Collider>() == null) hammerGo.AddComponent<BoxCollider>();
        var rb = hammerGo.GetComponent<Rigidbody>();
        if (rb == null) rb = hammerGo.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0.5f;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.isKinematic = false;
        var hammerRend = hammerGo.GetComponent<Renderer>();
        if (hammerRend != null) hammerRend.material.color = new Color(0.5f, 0.5f, 0.5f);

        Log($"Hammer placed behind sofa at {hammerPos} - requires pushing boxes to reach per GDD");

        // 5. Boxes puzzle - complex arrangement blocking hammer, spatial carry
        SetupBoxesPuzzle_GDD(hammerPos, sofa?.transform.position ?? Vector3.zero);

        // 6. Door to next level
        var nextDoor = GameObject.Find("Door to the next level") ?? GameObject.Find("Locked Door") ?? GameObject.Find("Door");
        if (nextDoor != null)
        {
            var openable = nextDoor.GetComponent<OpenableFurniture>();
            if (openable == null) openable = nextDoor.AddComponent<OpenableFurniture>();
            if (openable.OnUnlocked == null) openable.OnUnlocked = new UnityEngine.Events.UnityEvent();
            SetField(openable, "startsLocked", true);
            SetField(openable, "requiredKeyId", room2KeyId);
            SetField(openable, "unlockWithKey", true);
            Log($"Door {nextDoor.name} locked with {room2KeyId}");
        }
    }

    void EnsureToolboxCover(GameObject toolboxGo, ToolBoxInteractable toolbox)
    {
        var coverField = typeof(ToolBoxInteractable).GetField("cover", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Transform currentCover = null;
        if (coverField != null) currentCover = coverField.GetValue(toolbox) as Transform;
        if (currentCover != null) return;

        Transform foundCover = null;
        string[] coverNames = { "Cover", "Lid", "Top", "tool Box_Cover", "ToolBox_Cover", "CoverMesh" };
        foreach (var cname in coverNames)
        {
            var child = toolboxGo.transform.Find(cname);
            if (child != null) { foundCover = child; break; }
        }
        if (foundCover == null)
        {
            foreach (Transform child in toolboxGo.transform)
            {
                if (child.name.ToLower().Contains("bottom")) continue;
                foundCover = child;
                break;
            }
        }
        if (foundCover == null)
        {
            var dummy = new GameObject("Cover_Auto");
            dummy.transform.SetParent(toolboxGo.transform);
            dummy.transform.localPosition = new Vector3(0, 0.15f, 0);
            dummy.transform.localRotation = Quaternion.identity;
            dummy.transform.localScale = new Vector3(0.9f, 0.1f, 0.6f);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(dummy.transform);
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.localScale = Vector3.one;
            DestroyImmediate(cube.GetComponent<Collider>());
            foundCover = dummy.transform;
        }
        if (foundCover != null)
        {
            SetField(toolbox, "cover", foundCover);
            try
            {
                var closedRot = foundCover.localRotation;
                SetField(toolbox, "_closedRotation", closedRot);
                var hingeAxis = GetField<Vector3>(toolbox, "hingeAxis");
                if (hingeAxis == Vector3.zero) hingeAxis = Vector3.right;
                var openAngle = GetField<float>(toolbox, "openAngle");
                if (openAngle == 0) openAngle = -110f;
                var openRot = closedRot * Quaternion.AngleAxis(openAngle, hingeAxis);
                SetField(toolbox, "_openRotation", openRot);
            }
            catch { }
            Log($"Toolbox cover ensured: {foundCover.name}");
        }
    }

    void SetupBoxesPuzzle_GDD(Vector3 hammerPos, Vector3 sofaPos)
    {
        string[] boxNames = { "crate_2.004", "crate_2.005", "crate_2.006", "crate_2.007", "crate_2.009", "Plastic Crate.009", "Plastic Crate.010" };
        List<GameObject> boxes = new List<GameObject>();
        foreach (var bName in boxNames)
        {
            var boxGo = GameObject.Find(bName);
            if (boxGo != null) boxes.Add(boxGo);
        }

        while (boxes.Count < 5)
        {
            var newBox = GameObject.CreatePrimitive(PrimitiveType.Cube);
            newBox.name = $"Box_GDD_{boxes.Count}";
            newBox.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
            boxes.Add(newBox);
        }

        Vector3 center = (sofaPos + hammerPos) * 0.5f;
        center.y = 0.35f;
        Vector3 dirToHammer = (hammerPos - sofaPos);
        dirToHammer.y = 0;
        if (dirToHammer == Vector3.zero) dirToHammer = Vector3.forward;
        dirToHammer.Normalize();

        for (int i = 0; i < boxes.Count; i++)
        {
            var boxGo = boxes[i];
            var placeable = boxGo.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = boxGo.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", "box_" + boxGo.name);
            SetField(placeable, "displayName", "Box (push MMB+mouse, scroll push/pull)");
            SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Spatial);
            SetField(placeable, "defaultHoldDistance", 2.2f);
            SetField(placeable, "minHoldDistance", 0.6f);
            SetField(placeable, "maxHoldDistance", 4f);
            var col = boxGo.GetComponent<Collider>();
            if (col == null) col = boxGo.AddComponent<BoxCollider>();
            var rb2 = boxGo.GetComponent<Rigidbody>();
            if (rb2 == null) rb2 = boxGo.AddComponent<Rigidbody>();
            rb2.mass = 3.5f;
            rb2.linearDamping = 1.5f;
            rb2.angularDamping = 3f;
            rb2.isKinematic = false;
            rb2.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb2.interpolation = RigidbodyInterpolation.Interpolate;

            // Calculate non-penetrating position
            Vector3 targetPos;
            if (i < 3)
            {
                // First 3 boxes block path but with clearance
                targetPos = sofaPos + dirToHammer * (1.0f + i * 0.7f) + new Vector3(Random.Range(-0.2f, 0.2f), 0.35f, Random.Range(-0.2f, 0.2f));
            }
            else
            {
                float angle = (i * 70f) * Mathf.Deg2Rad;
                float radius = 1.0f + (i % 2) * 0.3f;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
                targetPos = center + offset + new Vector3(0, 0.35f, 0);
            }

            // Ensure not penetrating
            Vector3 size = boxGo.transform.localScale;
            if (!IsSpotFree(targetPos, size * 1.1f))
            {
                targetPos = FindFreeSpotNear(targetPos, 1.2f, size);
            }
            // Ensure on NavMesh or at least above floor
            if (Physics.Raycast(targetPos + Vector3.up * 2f, Vector3.down, out var hitFloor, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                targetPos.y = hitFloor.point.y + size.y * 0.5f + 0.05f;
            }
            else
            {
                targetPos.y = 0.35f;
            }

            boxGo.transform.position = targetPos;
            boxGo.transform.rotation = Quaternion.identity;

            var rend = boxGo.GetComponent<Renderer>();
            if (rend != null) rend.material.color = new Color(0.7f, 0.5f, 0.3f);
        }
        Log($"Boxes puzzle assembled non-penetrating: {boxes.Count} boxes blocking hammer, spaced to avoid collider penetration, spatial carry");
    }

    #endregion

    #region Ending

    void SetupEnding_GDD()
    {
        Log("--- Ending GDD Assembly: real window opening in Room 2's far wall ---");

        var root = GameObject.Find(escapeWindowRootName);
        if (root == null)
        {
            Log($"{escapeWindowRootName} is not authored in this scene - building the wall + opening at runtime");
            root = BuildEscapeWall();
        }

        var glass = root != null ? root.GetComponentInChildren<Glass>() : FindFirstObjectByType<Glass>();
        if (glass == null)
        {
            Log("ERROR: no Glass component on the escape window - ending cannot be completed");
            return;
        }

        if (glass.OnBroken == null) glass.OnBroken = new UnityEngine.Events.UnityEvent();
        if (glass.GetComponent<Collider>() == null) glass.gameObject.AddComponent<BoxCollider>();
        SetField(glass, "breakThreshold", 1.5f);
        SetField(glass, "requiredTag", "Hammer");
        SetField(glass, "useGlassMaterial", true);
        SetField(glass, "spawnFractureOnBreak", true);
        SetField(glass, "fracturePieces", 15);
        SetField(glass, "fractureForce", 6f);

        // Link the shattered-frame visual (4 colliderless bars -> the hole stays open)
        var broken = root != null ? FindChildRecursive(root.transform, brokenWindowName) : null;
        if (broken == null)
        {
            var foundByName = GameObject.Find(brokenWindowName);
            if (foundByName != null) broken = foundByName.transform;
        }
        if (broken != null)
        {
            broken.gameObject.SetActive(false);
            SetField(glass, "brokenWindow", broken.gameObject);
        }
        else
        {
            Debug.LogWarning($"[GDD] {brokenWindowName} missing - the window will shatter but show no broken frame", this);
        }

        // "Outside" backing so looking through the pane reads as outdoors, not the imported wall behind it
        var outside = root != null ? FindChildRecursive(root.transform, outsideViewName) : null;
        if (outside != null)
        {
            var or = outside.GetComponent<Renderer>();
            if (or != null) or.material.color = new Color(0.45f, 0.65f, 0.95f);
        }

        // Escape trigger sits in the opening; the player only reaches it once the glass is gone
        escapeVolume = root != null ? FindChildRecursive(root.transform, escapeVolumeName) : null;
        if (escapeVolume != null)
        {
            var col = escapeVolume.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }
        else
        {
            Debug.LogWarning($"[GDD] {escapeVolumeName} missing - escape will still fire on glass break", this);
        }

        glass.OnBroken.RemoveAllListeners();
        glass.OnBroken.AddListener(OnWindowBroken);

            // FIX: Ensure player starts at valid free NavMesh spot, not inside wall (fixes wall stare video)
            try
            {
                var player = GameObject.FindWithTag("Player");
                if (player == null) player = GameObject.Find("Player FPP");
                if (player == null)
                {
                    var cc = FindFirstObjectByType<CharacterController>();
                    if (cc != null) player = cc.gameObject;
                }
                if (player != null)
                {
                    Vector3 startPos = new Vector3(0, 1f, 0);
                    // Try to find free spot near table or near 0,1,0
                    var table = GameObject.Find("Table");
                    if (table != null) startPos = table.transform.position + new Vector3(0, 1f, -2f);
                    // Sample NavMesh
                    if (UnityEngine.AI.NavMesh.SamplePosition(startPos, out var hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
                        startPos = hit.position;
                    else
                        startPos = FindFreeSpotNear(startPos, 2f, new Vector3(0.6f, 1.8f, 0.6f));
                    player.transform.position = startPos;
                    var agent = player.GetComponent<UnityEngine.AI.NavMeshAgent>();
                    if (agent != null && agent.isOnNavMesh) agent.Warp(startPos);
                    else if (agent != null)
                    {
                        if (UnityEngine.AI.NavMesh.SamplePosition(startPos, out var hit2, 5f, UnityEngine.AI.NavMesh.AllAreas))
                            agent.Warp(hit2.position);
                    }
                    Log($"Player warped to valid start {startPos} to avoid wall stare");
                }
            }
            catch (System.Exception e) { Log($"Player warp fix exception: {e.Message}"); }

        Log($"Ending window ready: {glass.name} at {glass.transform.position} in the wall plane, opening " +
            $"{escapeOpeningWidth}x{escapeOpeningHeight}m, sill {escapeSillHeight}m, fracture " +
            $"{GetField<int>(glass, "fracturePieces")} pieces, escape volume {(escapeVolume != null ? "present" : "MISSING")}");
    }

    void OnWindowBroken()
    {
        if (endingTriggered) return;
        endingTriggered = true;
        StartCoroutine(EndingSequence());
        Log("Window broken - fracture spawned, broken frame shown, hole is open, ending per GDD");
    }

    /// <summary>
    /// Alternate win condition: if the player physically walks through the opening once the
    /// glass is gone, count that as the escape too - not just the OnBroken event.
    /// </summary>
    void Update()
    {
        if (endingTriggered || escapeVolume == null) return;
        var player = GameObject.FindWithTag("Player");
        if (player == null) return;
        var col = escapeVolume.GetComponent<Collider>();
        if (col == null) return;
        if (!col.bounds.Contains(player.transform.position)) return;

        var glass = FindFirstObjectByType<Glass>();
        if (glass != null)
        {
            // The volume straddles the pane, so throttle this - the player will be standing at
            // the window most of the time.
            if (Time.time - lastBlockedLog > 2f)
            {
                lastBlockedLog = Time.time;
                Log("Player is at the window but the glass is still intact - break it to escape");
            }
            return;
        }

        Log("Player stepped through the broken window - escaped");
        endingTriggered = true;
        StartCoroutine(EndingSequence());
    }

    static Transform FindChildRecursive(Transform parent, string name)
    {
        foreach (Transform c in parent)
        {
            if (c.name == name) return c;
            var deep = FindChildRecursive(c, name);
            if (deep != null) return deep;
        }
        return null;
    }

    /// <summary>
    /// Builds a wall panel with a genuine opening in it. The sill/header/piers are four
    /// separate boxes, so the gap between them is a real hole you can see and walk at -
    /// the previous version only drew a decorative frame over a solid wall.
    /// </summary>
    GameObject BuildEscapeWall()
    {
        var root = new GameObject(escapeWindowRootName);
        root.transform.position = escapeWallCentre;

        float headY = escapeSillHeight + escapeOpeningHeight;

        GameObject WallPiece(string name, Vector3 localPos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            return go;
        }

        float cy = escapeSillHeight + escapeOpeningHeight * 0.5f - escapeWallCentre.y;
        WallPiece("Wall_Sill", new Vector3(0, escapeSillHeight * 0.5f - escapeWallCentre.y, 0),
                  new Vector3(escapePanelWidth, escapeSillHeight, escapeWallThickness));
        WallPiece("Wall_Header", new Vector3(0, (headY + escapeRoomHeight) * 0.5f - escapeWallCentre.y, 0),
                  new Vector3(escapePanelWidth, escapeRoomHeight - headY, escapeWallThickness));
        float pierW = (escapePanelWidth - escapeOpeningWidth) * 0.5f;
        WallPiece("Wall_Pier_Left", new Vector3(-(escapeOpeningWidth + pierW) * 0.5f, cy, 0),
                  new Vector3(pierW, escapeOpeningHeight, escapeWallThickness));
        WallPiece("Wall_Pier_Right", new Vector3((escapeOpeningWidth + pierW) * 0.5f, cy, 0),
                  new Vector3(pierW, escapeOpeningHeight, escapeWallThickness));

        // Frame around the opening
        const float ft = 0.08f;
        WallPiece("WindowFrame_Bottom", new Vector3(0, escapeSillHeight - escapeWallCentre.y, 0.02f),
                  new Vector3(escapeOpeningWidth + ft * 2, ft, 0.24f));
        WallPiece("WindowFrame_Top", new Vector3(0, headY - escapeWallCentre.y, 0.02f),
                  new Vector3(escapeOpeningWidth + ft * 2, ft, 0.24f));
        WallPiece("WindowFrame_Left", new Vector3(-(escapeOpeningWidth + ft) * 0.5f, cy, 0.02f),
                  new Vector3(ft, escapeOpeningHeight + ft, 0.24f));
        WallPiece("WindowFrame_Right", new Vector3((escapeOpeningWidth + ft) * 0.5f, cy, 0.02f),
                  new Vector3(ft, escapeOpeningHeight + ft, 0.24f));

        // Breakable pane filling the opening
        var pane = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pane.name = breakableWindowName;
        pane.transform.SetParent(root.transform, false);
        pane.transform.localPosition = new Vector3(0, cy, 0);
        pane.transform.localScale = new Vector3(escapeOpeningWidth, escapeOpeningHeight, 0.04f);
        pane.AddComponent<Glass>();

        // Shattered frame shown after the break - deliberately colliderless so the hole stays open
        var broken = new GameObject(brokenWindowName);
        broken.transform.SetParent(root.transform, false);
        broken.transform.localPosition = new Vector3(0, cy, 0.1f);
        foreach (var side in new[] { "Top", "Bottom", "Left", "Right" })
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "BrokenFrame_" + side;
            bar.transform.SetParent(broken.transform, false);
            bool horizontal = side == "Top" || side == "Bottom";
            bar.transform.localScale = horizontal
                ? new Vector3(escapeOpeningWidth + 0.1f, ft, 0.06f)
                : new Vector3(ft, escapeOpeningHeight + ft, 0.06f);
            float off = (escapeOpeningHeight + ft) * 0.5f;
            bar.transform.localPosition = side == "Top" ? new Vector3(0, off, 0)
                : side == "Bottom" ? new Vector3(0, -off, 0)
                : new Vector3(side == "Left" ? -(escapeOpeningWidth + ft) * 0.5f : (escapeOpeningWidth + ft) * 0.5f, 0, 0);
            Destroy(bar.GetComponent<Collider>());
        }
        broken.SetActive(false);

        // Sky backing just in front of the imported wall behind the pane
        var outside = GameObject.CreatePrimitive(PrimitiveType.Cube);
        outside.name = outsideViewName;
        outside.transform.SetParent(root.transform, false);
        outside.transform.localPosition = new Vector3(0, cy, -0.18f);
        outside.transform.localScale = new Vector3(escapeOpeningWidth + 0.04f, escapeOpeningHeight + 0.04f, 0.02f);

        // Escape trigger inside the opening
        var vol = GameObject.CreatePrimitive(PrimitiveType.Cube);
        vol.name = escapeVolumeName;
        vol.transform.SetParent(root.transform, false);
        vol.transform.localPosition = new Vector3(0, cy, 0.06f);
        vol.transform.localScale = new Vector3(escapeOpeningWidth - 0.04f, escapeOpeningHeight - 0.04f, 0.8f);
        DestroyImmediate(vol.GetComponent<Renderer>());
        vol.GetComponent<Collider>().isTrigger = true;

        Log($"Built wall panel at {escapeWallCentre} with a real {escapeOpeningWidth}x{escapeOpeningHeight}m opening");
        return root;
    }

    #endregion


    #region Phone Story

    void SetupPhoneStory()
    {
        StartCoroutine(StoryMessages());
    }

    IEnumerator StoryMessages()
    {
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Agent: Sending colleague for house viewing. 10min.", isMisleading = false, sourceId = "story01" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger: Hi, I'm agent's colleague. Tour? [No reflection in bathroom mirror!]", isMisleading = false, sourceId = "story02" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Objects moving/disappearing after you pass rooms...", isMisleading = false, sourceId = "story03" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Bathroom: Stranger has NO REFLECTION in mirror! Impostor!", isMisleading = false, sourceId = "story04" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Real Agent: Accident! My colleague never came. WHO IS THERE?!", isMisleading = false, sourceId = "story05" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger disappears. Doors locked. Hide in small room near exit. Solve puzzles to escape.", isMisleading = false, sourceId = "story06" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "TRUST NO ONE. Mirror shows truth. Wrong hints will mislead. Follow mirror, not notes.", isMisleading = false, sourceId = "intro" });
    }

    IEnumerator LightFlickerSequence()
    {
        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker != null) flicker.TriggerRoom2LightsOutSequence();
        yield return new WaitForSeconds(lightOutDuration + 1f);
    }

    IEnumerator EndingSequence()
    {
        PuzzleEvents.RaiseHint(new HintMessage { text = "Window broken! Escaping... [Illustration: Player running outside, smiley ghost watches from house]", isMisleading = false, sourceId = "ending" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Illustration: Player running outside, smiley ghost watches from house window.", isMisleading = false, sourceId = "ending-visual" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Phone notification: Escaped but house watches... You feel eyes on you.", isMisleading = false, sourceId = "ending-phone" });
        yield return new WaitForSeconds(endingFadeDuration);
        PuzzleEvents.RaiseHint(new HintMessage { text = "FADE TO BLACK. Game Complete. TRUST NO ONE.", isMisleading = false, sourceId = "ending-fade" });
    }

    #endregion

    #region Helpers - Anti-penetration for solvable placement

    Vector3 FindFreeSpotNear(Vector3 origin, float searchRadius, Vector3 objectSize, int attempts = 15)
    {
        // Try to find a spot not penetrating colliders
        for (int i = 0; i < attempts; i++)
        {
            Vector3 candidate = origin + new Vector3(
                Random.Range(-searchRadius, searchRadius),
                0.1f,
                Random.Range(-searchRadius, searchRadius)
            );
            // Ensure on NavMesh or at least not inside collider
            if (IsSpotFree(candidate, objectSize))
            {
                // Also sample NavMesh if possible
                if (UnityEngine.AI.NavMesh.SamplePosition(candidate, out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    candidate = hit.position + Vector3.up * 0.05f;
                }
                return candidate;
            }
        }
        // Fallback: origin with slight up offset
        return origin + Vector3.up * 0.2f;
    }

    bool IsSpotFree(Vector3 pos, Vector3 size)
    {
        // Check if box at pos with size overlaps any collider (except triggers)
        var halfExtents = size * 0.5f;
        var cols = Physics.OverlapBox(pos, halfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in cols)
        {
            if (c == null) continue;
            // Ignore small triggers and player
            if (c.isTrigger) continue;
            if (c.gameObject.CompareTag("Player")) continue;
            if (c.transform.IsChildOf(transform)) continue;
            // If overlap is significant, not free
            return false;
        }
        return true;
    }

    Vector3 GetNonPenetratingPositionForCabinetContent(GameObject cabinetGo, int index, Vector3 baseSize)
    {
        // Place objects inside cabinet with spacing to avoid penetration
        // Cabinet interior is roughly 0.5x0.5x0.5, place in grid
        float spacing = 0.35f;
        float x = ((index % 3) - 1) * spacing;
        float z = ((index / 3) - 0.5f) * spacing;
        float y = 0.25f + (index % 2) * 0.15f;
        Vector3 localPos = new Vector3(x, y, z);
        Vector3 worldPos = cabinetGo.transform.TransformPoint(localPos);
        // Ensure free
        if (!IsSpotFree(worldPos, baseSize))
        {
            worldPos = FindFreeSpotNear(cabinetGo.transform.position + cabinetGo.transform.forward * 0.3f + Vector3.up * 0.3f, 0.8f, baseSize);
            localPos = cabinetGo.transform.InverseTransformPoint(worldPos);
        }
        return localPos;
    }

    #endregion

    #region Helpers


    void SpawnKey_GDD(GameObject parent, string keyId, string displayName, Vector3 localOffset)
    {
        if (GameObject.Find(keyId) != null) return;
        var keyGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        keyGo.name = keyId;
        keyGo.transform.localScale = new Vector3(0.12f, 0.03f, 0.06f);
        Destroy(keyGo.GetComponent<Collider>());
        var col = keyGo.AddComponent<BoxCollider>();
        col.isTrigger = false;
        var rb = keyGo.AddComponent<Rigidbody>();
        rb.mass = 0.2f;
        rb.isKinematic = false;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        keyGo.transform.SetParent(parent.transform);
        // Ensure localOffset is above surface to avoid penetration
        Vector3 safeOffset = localOffset + Vector3.up * 0.1f;
        // Check if spot is free in world
        Vector3 worldCandidate = parent.transform.TransformPoint(safeOffset);
        if (!IsSpotFree(worldCandidate, keyGo.transform.localScale))
        {
            worldCandidate = FindFreeSpotNear(parent.transform.position + Vector3.up * 0.3f + parent.transform.forward * 0.3f, 0.5f, keyGo.transform.localScale);
            safeOffset = parent.transform.InverseTransformPoint(worldCandidate);
        }
        keyGo.transform.localPosition = safeOffset;
        keyGo.transform.localRotation = Quaternion.identity;

        var placeable = keyGo.AddComponent<PlaceableItem>();
        SetField(placeable, "itemId", keyId);
        SetField(placeable, "displayName", displayName);
        SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Handheld);

        var keyItem = keyGo.AddComponent<KeyItem>();
        SetField(keyItem, "keyId", keyId);
        SetField(keyItem, "displayName", displayName);
        SetField(keyItem, "collectOnPickup", true);

        var rend = keyGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = Color.yellow;
        Log($"Spawned key {keyId} ({displayName}) in {parent.name} at {localOffset}");
    }

    void SpawnEmergencyBoard_GDD()
    {
        if (GameObject.Find("EmergencyBoard_GDD") != null) return;
        var boardGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        boardGo.name = "EmergencyBoard_GDD";
        boardGo.transform.position = new Vector3(0, 1.5f, 3);
        boardGo.transform.localScale = new Vector3(1.2f, 0.6f, 0.05f);
        var rend = boardGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = Color.red;
        // Add text marker
        if (createVisualMarkers)
        {
            var textGo = new GameObject("EmergencyText");
            textGo.transform.SetParent(boardGo.transform);
            textGo.transform.localPosition = new Vector3(0, 0, -0.03f);
            // Could add TextMeshPro but keep simple
        }
        Log("Emergency board spawned: says no hammer, check behind sofa");
    }

    void SetField(object obj, string fieldName, object value)
    {
        if (obj == null) return;
        var type = obj.GetType();
        var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (field != null)
        {
            try { field.SetValue(obj, value); return; } catch { }
        }
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

    #endregion
}
