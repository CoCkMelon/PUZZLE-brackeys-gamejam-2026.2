using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using GameAssets.Scripts.Environment;
using GameAssets.Scripts.Interaction;
using GameAssets.Scripts.Puzzle;

/// <summary>
/// GDD implementation for AutoTest scenes only.
/// Implements Room1 mirror truth puzzle and Room2 toolbox/light/hammer/window.
/// Runs only in AutoTest / NavMeshTest scenes.
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

    private bool room2SequenceStarted;
    private bool endingTriggered;

    void Awake()
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!sceneName.Contains("AutoTest") && !sceneName.Contains("NavMeshTest"))
        {
            enabled = false;
            return;
        }
        Debug.Log($"[GDDPuzzleBootstrap] Init for {sceneName}");
    }

    IEnumerator Start()
    {
        yield return null;
        yield return null;
        SetupAll();
    }

    void SetupAll()
    {
        SetupPlayer();
        SetupFurnitureRigidbodies();
        SetupWrongHintSystem();
        SetupMirror();
        SetupRoom1();
        SetupRoom2();
        SetupEnding();
        SetupWaypointsForMover();
        SetupPhoneStory();
        Debug.Log("[GDDPuzzleBootstrap] Setup complete - scene assembled");
    }

    void SetupWaypointsForMover()
    {
        // Assemble waypoints for AutoPlayerMover so it doesn't need manual assignment
        var player = GameObject.FindWithTag("Player");
        if (player == null) player = GameObject.Find("Player FPP");
        if (player == null) return;
        var mover = player.GetComponent<AutoPlayerMover>();
        if (mover == null) return;

        Transform CreateWaypoint(string name, Vector3 pos)
        {
            var existing = GameObject.Find(name);
            if (existing != null) return existing.transform;
            var go = new GameObject(name);
            go.transform.position = pos;
            return go.transform;
        }

        var table = GameObject.Find("Table");
        var cabinet = GameObject.Find("Cabin 8") ?? GameObject.Find("Cabin 1");
        var drawer1 = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4");
        var drawer2 = GameObject.Find("Drawer 2") ?? GameObject.Find("Cabin 6");
        var toolbox = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        var hammer = GameObject.Find("Hammer") ?? GameObject.Find("Hammer.001");
        var sofa = GameObject.Find("chair 2");
        var window = GameObject.Find("BreakableWindow_GDD") ?? GameObject.Find("Breakable Window");

        Vector3 basePos = table != null ? table.transform.position : Vector3.zero;

        var wpMirrorKey = CreateWaypoint("WP_MirrorKey", basePos + new Vector3(0, 0, 0.5f));
        var wpCabinet = CreateWaypoint("WP_Cabinet", cabinet != null ? cabinet.transform.position + Vector3.forward : basePos + new Vector3(2, 0, 0));
        var wpTable = CreateWaypoint("WP_Table", basePos);
        var wpDrawer = CreateWaypoint("WP_ToolboxDrawer", drawer2 != null ? drawer2.transform.position + Vector3.forward : basePos + new Vector3(-2, 0, 0));
        var wpToolbox = CreateWaypoint("WP_Toolbox", toolbox != null ? toolbox.transform.position + Vector3.forward : basePos + new Vector3(0, 0, 2));
        var wpSofa = CreateWaypoint("WP_SofaHammer", sofa != null ? sofa.transform.position + Vector3.forward : (hammer != null ? hammer.transform.position : basePos + new Vector3(-3, 0, 0)));
        var wpWindow = CreateWaypoint("WP_Window", window != null ? window.transform.position + Vector3.back : basePos + new Vector3(5, 0, 0));

        // Assign via reflection to private fields
        SetField(mover, "mirrorKeyLocation", wpMirrorKey);
        SetField(mover, "cabinetLocation", wpCabinet);
        SetField(mover, "placementTableLocation", wpTable);
        SetField(mover, "toolboxDrawerLocation", wpDrawer);
        SetField(mover, "toolboxLocation", wpToolbox);
        SetField(mover, "sofaHammerLocation", wpSofa);
        SetField(mover, "windowLocation", wpWindow);

        Debug.Log($"[GDD] Waypoints assembled for AutoPlayerMover: mirrorKey={wpMirrorKey.position}, cabinet={wpCabinet.position}, table={wpTable.position}, drawer={wpDrawer.position}, toolbox={wpToolbox.position}, sofa={wpSofa.position}, window={wpWindow.position}");
    }

    void SetupFurnitureRigidbodies()
    {
        // Fix: OpenableFurniture useJoint requires Rigidbody, add if missing to silence warnings
        var allFurniture = FindObjectsByType<OpenableFurniture>(FindObjectsSortMode.None);
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
                // Check if furniture itself has rb
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
                    rb.isKinematic = true; // will be managed by OpenableFurniture lock state
                    Debug.Log($"[GDD] Added Rigidbody to {f.name} movingPart {movingPart.name} to fix joint warning");
                }
            }

            // Ensure movingRigidbody field points to this rb
            var movingRbField = typeof(OpenableFurniture).GetField("movingRigidbody", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (movingRbField != null && movingRbField.GetValue(f) == null)
            {
                movingRbField.SetValue(f, rb);
            }
        }
    }

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

        // FIX: Ensure MobilePhoneController doesn't disable AutoPlayerMover / NavMeshAgent / PlayerCarry
        // This was causing phone to stay open and player to freeze after reaching cabinet
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
                        // Never disable auto-movement components
                        if (b is AutoPlayerMover) continue;
                        if (b is NavMeshAgent) continue;
                        if (b is PlayerCarry) continue;
                        filtered.Add(b);
                    }
                    field.SetValue(phone, filtered.ToArray());
                    Debug.Log($"[GDD] Filtered MobilePhone disableWhileOpen: removed AutoPlayerMover/NavMeshAgent, now {filtered.Count} behaviours");
                }
            }
        }
    }

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
                list.Add(new WrongHintSystem.HintEntry { id = "room1-key-drawer-wrong", text = "The key is in the drawer. Check the top drawer! [WRONG HINT]", isMisleading = true });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-key-table-truth", text = "Mirror shows truth: key is UNDER TABLE, not drawer.", isMisleading = false, triggerOnCorrectSlotId = "cabinet_key_found" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-cabinet-open", text = "Cabinet opened! Found Book, Candle, Vase. Place them on table.", isMisleading = false, triggerOnDrawerId = "cabinet_open" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-placement-wrong", text = "Place Book left, Candle middle, Vase right. Trust me. [WRONG]", isMisleading = true, triggerOnWrongSlotId = "table_book" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-placement-truth", text = "Mirror reflection shows correct placement.", isMisleading = false, triggerOnCorrectSlotId = "table_book" });
                list.Add(new WrongHintSystem.HintEntry { id = "room1-drawer-unlock", text = "Drawer opened, found key to next door.", isMisleading = false, triggerOnDrawerId = "room1_drawer" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-toolbox-wrong", text = "Hammer is inside toolbox. [WRONG - empty]", isMisleading = true, triggerOnDrawerId = "toolbox" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-light-out", text = "Lights out! Wait 6-7 seconds.", isMisleading = false, triggerOnDrawerId = "lights_out" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-light-back", text = "Lights back! Emergency board, hammer missing. Check behind sofa.", isMisleading = false, triggerOnDrawerId = "lights_back" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-hammer-sofa", text = "Hammer behind sofa, push boxes. MMB + mouse to shove.", isMisleading = false, triggerOnDrawerId = "hammer_hint" });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-box-puzzle", text = "Boxes block hammer. Scroll wheel push/pull, MMB hold + drag.", isMisleading = false });
                list.Add(new WrongHintSystem.HintEntry { id = "room2-window-break", text = "Break window with hammer to escape!", isMisleading = false, triggerOnDrawerId = "window" });
                list.Add(new WrongHintSystem.HintEntry { id = "trust-no-one", text = "TRUST NO ONE. Mirror shows truth.", isMisleading = false });
                hintsField.SetValue(whs, list);
            }
        }
    }

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
            foreach (var name in new[] { "Puzzle book", "Puzzle book (1)", "Candle_low", "Candle_low (1)", "Vase", "Vase (1)" })
            {
                var go = GameObject.Find(name);
                if (go != null && !mirror.objectsToMirror.Contains(go)) mirror.objectsToMirror.Add(go);
            }
            mirror.RebuildMirror();
        }
    }

    void SetupRoom1()
    {
        var cabinetKeyGo = GameObject.Find("Cabinet key 2");
        if (cabinetKeyGo != null)
        {
            var placeable = cabinetKeyGo.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = cabinetKeyGo.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", cabinetKeyId);
            SetField(placeable, "displayName", "Cabinet Key");

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
                cabinetKeyGo.transform.position = table.transform.position + new Vector3(0, -0.4f, 0.3f);
            }
        }

        SetupPlaceable("Puzzle book", "book", "Book");
        SetupPlaceable("Puzzle book (1)", "book", "Book");
        SetupPlaceable("Candle_low", "candle", "Candle");
        SetupPlaceable("Candle_low (1)", "candle", "Candle");
        SetupPlaceable("Vase", "vase", "Vase");
        SetupPlaceable("Vase (1)", "vase", "Vase");

        var tableGo = GameObject.Find("Table");
        if (tableGo != null)
        {
            CreateSlot(tableGo, "table_book", "book", new Vector3(-0.4f, 0.15f, 0), "BookSlot");
            CreateSlot(tableGo, "table_candle", "candle", new Vector3(0, 0.15f, 0), "CandleSlot");
            CreateSlot(tableGo, "table_vase", "vase", new Vector3(0.4f, 0.15f, 0), "VaseSlot");
        }

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
                if (openable.OnUnlocked == null) openable.OnUnlocked = new UnityEngine.Events.UnityEvent();
                openable.OnUnlocked.AddListener(() =>
                {
                    PuzzleEvents.RaiseDrawerUnlocked("cabinet_open");
                    PuzzleEvents.RaiseHint(new HintMessage { text = "Cabinet opened! Found Book, Candle, Vase.", isMisleading = false, sourceId = "cabinet_open" });
                });
            }
        }

        var drawerGo = GameObject.Find("Drawer 1") ?? GameObject.Find("Cabin 4") ?? GameObject.Find("Drawer base");
        if (drawerGo != null)
        {
            var openable = drawerGo.GetComponent<OpenableFurniture>();
            if (openable == null) openable = drawerGo.AddComponent<OpenableFurniture>();
            if (openable.OnUnlocked == null) openable.OnUnlocked = new UnityEngine.Events.UnityEvent();
            if (openable.OnOpened == null) openable.OnOpened = new UnityEngine.Events.UnityEvent();
            SetField(openable, "startsLocked", true);
            SetField(openable, "unlockWithKey", false);

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
                SpawnKey(drawerGo, room2KeyId, "Room2 Key", new Vector3(0, 0.2f, 0));
                PuzzleEvents.RaiseDrawerUnlocked("room1_drawer");
                PuzzleEvents.RaiseHint(new HintMessage { text = "Drawer unlocked! Found key to next door.", isMisleading = false, sourceId = "room1_drawer" });
            });
        }
    }

    void SetupRoom2()
    {
        var toolboxGo = GameObject.Find("tool Box") ?? GameObject.Find("Tool Box");
        if (toolboxGo == null)
        {
            var tb = FindFirstObjectByType<ToolBoxInteractable>();
            if (tb != null) toolboxGo = tb.gameObject;
        }
        if (toolboxGo != null)
        {
            var toolbox = toolboxGo.GetComponent<ToolBoxInteractable>();
            bool wasNew = false;
            if (toolbox == null)
            {
                toolbox = toolboxGo.AddComponent<ToolBoxInteractable>();
                wasNew = true;
            }
            if (toolbox.OnUnlocked == null) toolbox.OnUnlocked = new UnityEngine.Events.UnityEvent();
            if (toolbox.OnOpened == null) toolbox.OnOpened = new UnityEngine.Events.UnityEvent();
            if (toolbox.OnClosed == null) toolbox.OnClosed = new UnityEngine.Events.UnityEvent();
            if (toolbox.OnLockedAttempt == null) toolbox.OnLockedAttempt = new UnityEngine.Events.UnityEvent();
            SetField(toolbox, "startsLocked", true);
            SetField(toolbox, "requiredKeyId", toolboxKeyId);
            SetField(toolbox, "unlockWithKey", true);

            // FIX: No cover transform assigned on tool Box! -> assign cover
            var coverField = typeof(ToolBoxInteractable).GetField("cover", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Transform currentCover = null;
            if (coverField != null) currentCover = coverField.GetValue(toolbox) as Transform;
            if (currentCover == null)
            {
                Transform foundCover = null;
                // Search common names
                string[] coverNames = { "Cover", "Lid", "Top", "tool Box_Cover", "ToolBox_Cover", "CoverMesh", "tool Box Lid" };
                foreach (var cname in coverNames)
                {
                    var child = toolboxGo.transform.Find(cname);
                    if (child != null) { foundCover = child; break; }
                    var go = GameObject.Find(cname);
                    if (go != null && go.transform.IsChildOf(toolboxGo.transform)) { foundCover = go.transform; break; }
                }
                // Search any child with MeshRenderer that is not bottom
                if (foundCover == null)
                {
                    foreach (Transform child in toolboxGo.transform)
                    {
                        if (child.name.ToLower().Contains("bottom")) continue;
                        if (child.GetComponent<MeshRenderer>() != null || child.GetComponentInChildren<MeshRenderer>() != null)
                        {
                            foundCover = child;
                            break;
                        }
                    }
                }
                // If still not found, create dummy cover
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
                    // Remove collider from visual cube to avoid duplicate
                    DestroyImmediate(cube.GetComponent<Collider>());
                    foundCover = dummy.transform;
                    Debug.Log($"[GDD] Created dummy cover for toolbox {toolboxGo.name}");
                }

                if (foundCover != null)
                {
                    SetField(toolbox, "cover", foundCover);
                    Debug.Log($"[GDD] Assigned cover {foundCover.name} to toolbox {toolboxGo.name}");

                    // Re-init closed/open rotation because Awake already ran and failed
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
                        SetField(toolbox, "_isLocked", true);
                        Debug.Log($"[GDD] Re-initialized toolbox rotations closed={closedRot} open={openRot}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[GDD] Failed to re-init toolbox rotations: {ex.Message}");
                    }
                }
            }

            toolbox.OnUnlocked.AddListener(() =>
            {
                if (!room2SequenceStarted)
                {
                    room2SequenceStarted = true;
                    StartCoroutine(LightFlickerSequence());
                }
            });
            toolbox.OnOpened.AddListener(() =>
            {
                PuzzleEvents.RaiseHint(new HintMessage { text = "Toolbox empty! No hammer. Lights flickering...", isMisleading = false, sourceId = "toolbox" });
            });
        }

        var drawerWithKey = GameObject.Find("Drawer 2") ?? GameObject.Find("Cabin 6") ?? GameObject.Find("Drawer base");
        if (drawerWithKey != null)
        {
            SpawnKey(drawerWithKey, toolboxKeyId, "Toolbox Key", new Vector3(0, 0.1f, 0));
        }

        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker == null)
        {
            var flickerGo = new GameObject("LightFlicker_GDD");
            flicker = flickerGo.AddComponent<LightFlickerSystem>();
            if (flicker.OnLightsWentOut == null) flicker.OnLightsWentOut = new UnityEngine.Events.UnityEvent();
            if (flicker.OnLightsCameBackOn == null) flicker.OnLightsCameBackOn = new UnityEngine.Events.UnityEvent();
            var allLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var l in allLights) if (l.type != LightType.Directional) flicker.targetLights.Add(l);
            SetField(flicker, "lightsOutDuration", lightOutDuration);
            SetField(flicker, "triggerOnStart", false);
        }
        else
        {
            if (flicker.OnLightsWentOut == null) flicker.OnLightsWentOut = new UnityEngine.Events.UnityEvent();
            if (flicker.OnLightsCameBackOn == null) flicker.OnLightsCameBackOn = new UnityEngine.Events.UnityEvent();
        }

        flicker.OnLightsWentOut.AddListener(() =>
        {
            PuzzleEvents.RaiseDrawerUnlocked("lights_out");
        });
        flicker.OnLightsCameBackOn.AddListener(() =>
        {
            SpawnEmergencyBoard();
            PuzzleEvents.RaiseDrawerUnlocked("lights_back");
            PuzzleEvents.RaiseHint(new HintMessage { text = "Emergency tools board: no hammer! Check behind sofa.", isMisleading = false, sourceId = "lights_back" });
        });

        var hammerGo = GameObject.Find("Hammer");
        if (hammerGo != null)
        {
            var placeable = hammerGo.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = hammerGo.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", hammerId);
            SetField(placeable, "displayName", "Hammer");
            SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Handheld);

            var sofa = GameObject.Find("chair 2") ?? GameObject.Find("Sofa") ?? GameObject.Find("Table");
            if (sofa != null)
            {
                hammerGo.transform.position = sofa.transform.position + new Vector3(0.5f, 0.1f, -1.2f);
            }
            hammerGo.tag = "Hammer";
            if (hammerGo.GetComponent<Collider>() == null) hammerGo.AddComponent<BoxCollider>();
            var rb = hammerGo.GetComponent<Rigidbody>();
            if (rb == null) rb = hammerGo.AddComponent<Rigidbody>();
            rb.mass = 1f;
        }

        string[] boxNames = { "crate_2.004", "crate_2.005", "crate_2.006", "crate_2.007", "crate_2.009", "Plastic Crate.009", "Plastic Crate.010" };
        foreach (var bName in boxNames)
        {
            var boxGo = GameObject.Find(bName);
            if (boxGo == null) continue;
            var placeable = boxGo.GetComponent<PlaceableItem>();
            if (placeable == null) placeable = boxGo.AddComponent<PlaceableItem>();
            SetField(placeable, "itemId", "box_" + bName);
            SetField(placeable, "displayName", "Box");
            SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Spatial);
            SetField(placeable, "defaultHoldDistance", 2f);
            SetField(placeable, "minHoldDistance", 0.5f);
            SetField(placeable, "maxHoldDistance", 4f);
            if (boxGo.GetComponent<Collider>() == null) boxGo.AddComponent<BoxCollider>();
            var rb2 = boxGo.GetComponent<Rigidbody>();
            if (rb2 == null) rb2 = boxGo.AddComponent<Rigidbody>();
            rb2.mass = 5f;
            rb2.linearDamping = 1f;
            rb2.angularDamping = 2f;
        }

        var nextDoor = GameObject.Find("Door to the next level") ?? GameObject.Find("Locked Door");
        if (nextDoor != null)
        {
            var openable = nextDoor.GetComponent<OpenableFurniture>();
            if (openable == null) openable = nextDoor.AddComponent<OpenableFurniture>();
            SetField(openable, "startsLocked", true);
            SetField(openable, "requiredKeyId", room2KeyId);
            SetField(openable, "unlockWithKey", true);
        }
    }

    void SetupEnding()
    {
        var glass = FindFirstObjectByType<Glass>();
        if (glass == null)
        {
            var winGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            winGo.name = "BreakableWindow_GDD";
            winGo.transform.position = new Vector3(5, 1, 0);
            winGo.transform.localScale = new Vector3(0.1f, 2, 2);
            glass = winGo.AddComponent<Glass>();
            if (glass.OnBroken == null) glass.OnBroken = new UnityEngine.Events.UnityEvent();
            var broken = GameObject.CreatePrimitive(PrimitiveType.Cube);
            broken.name = "BrokenWindow_GDD";
            broken.transform.position = winGo.transform.position;
            broken.transform.localScale = new Vector3(0.1f, 2, 2);
            var mr = broken.GetComponent<Renderer>();
            if (mr != null) mr.material.color = Color.gray;
            broken.SetActive(false);
            SetField(glass, "brokenWindow", broken);
            SetField(glass, "breakThreshold", 2f);
            SetField(glass, "requiredTag", "Hammer");
        }
        else
        {
            if (glass.OnBroken == null) glass.OnBroken = new UnityEngine.Events.UnityEvent();
        }

        var brokenField = typeof(Glass).GetField("brokenWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (brokenField != null && brokenField.GetValue(glass) == null)
        {
            var brokenGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            brokenGo.name = "BrokenWindow_Auto";
            brokenGo.transform.position = glass.transform.position;
            brokenGo.transform.localScale = new Vector3(1, 0.1f, 1);
            brokenGo.SetActive(false);
            brokenField.SetValue(glass, brokenGo);
        }

        glass.OnBroken.AddListener(() =>
        {
            if (!endingTriggered)
            {
                endingTriggered = true;
                StartCoroutine(EndingSequence());
            }
        });
    }

    void SetupPhoneStory()
    {
        StartCoroutine(StoryMessages());
    }

    IEnumerator StoryMessages()
    {
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Agent: Sending colleague for house viewing. 10min.", isMisleading = false, sourceId = "story01" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger: Hi, I'm agent's colleague. Tour?", isMisleading = false, sourceId = "story02" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Objects moving/disappearing after you pass rooms...", isMisleading = false, sourceId = "story03" });
        yield return new WaitForSeconds(2f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Bathroom: Stranger has NO REFLECTION in mirror!", isMisleading = false, sourceId = "story04" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Real Agent: Accident! My colleague never came. WHO IS THERE?!", isMisleading = false, sourceId = "story05" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Stranger disappears. Doors locked. Hide in small room near exit.", isMisleading = false, sourceId = "story06" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "TRUST NO ONE. Mirror shows truth.", isMisleading = false, sourceId = "intro" });
    }

    IEnumerator LightFlickerSequence()
    {
        var flicker = FindFirstObjectByType<LightFlickerSystem>();
        if (flicker != null) flicker.TriggerRoom2LightsOutSequence();
        yield return new WaitForSeconds(lightOutDuration + 1f);
    }

    IEnumerator EndingSequence()
    {
        PuzzleEvents.RaiseHint(new HintMessage { text = "Window broken! Escaping...", isMisleading = false, sourceId = "ending" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Illustration: Player running outside, smiley ghost watches.", isMisleading = false, sourceId = "ending-visual" });
        yield return new WaitForSeconds(1f);
        PuzzleEvents.RaiseHint(new HintMessage { text = "Phone notification: Escaped but house watches...", isMisleading = false, sourceId = "ending-phone" });
        yield return new WaitForSeconds(endingFadeDuration);
        PuzzleEvents.RaiseHint(new HintMessage { text = "FADE TO BLACK. Game Complete. TRUST NO ONE.", isMisleading = false, sourceId = "ending-fade" });
    }

    void SetupPlaceable(string goName, string itemId, string displayName)
    {
        var go = GameObject.Find(goName);
        if (go == null) return;
        var placeable = go.GetComponent<PlaceableItem>();
        if (placeable == null) placeable = go.AddComponent<PlaceableItem>();
        SetField(placeable, "itemId", itemId);
        SetField(placeable, "displayName", displayName);
        SetField(placeable, "carryStyle", PlaceableItem.CarryStyle.Handheld);
        if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
        if (go.GetComponent<Rigidbody>() == null)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
        }
    }

    void CreateSlot(GameObject parent, string slotId, string requiredId, Vector3 localPos, string name)
    {
        foreach (var s in FindObjectsByType<PlacementSlot>(FindObjectsSortMode.None))
            if (s.SlotId == slotId) return;

        var slotGo = new GameObject(name + "_" + slotId);
        slotGo.transform.SetParent(parent.transform);
        slotGo.transform.localPosition = localPos;
        slotGo.transform.localRotation = Quaternion.identity;
        var col = slotGo.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = new Vector3(0.5f, 0.2f, 0.5f);
        var slot = slotGo.AddComponent<PlacementSlot>();
        SetField(slot, "slotId", slotId);
        SetField(slot, "requiredItemId", requiredId);
        var snap = new GameObject("SnapPoint").transform;
        snap.SetParent(slotGo.transform);
        snap.localPosition = Vector3.zero;
        SetField(slot, "snapPoint", snap);
        SetField(slot, "allowWrongItems", true);
        SetField(slot, "lockWhenCorrect", true);
    }

    void SpawnKey(GameObject parent, string keyId, string displayName, Vector3 localOffset)
    {
        if (GameObject.Find(keyId) != null) return;
        var keyGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        keyGo.name = keyId;
        keyGo.transform.localScale = new Vector3(0.1f, 0.02f, 0.05f);
        Destroy(keyGo.GetComponent<Collider>());
        keyGo.AddComponent<BoxCollider>();
        var rb = keyGo.AddComponent<Rigidbody>();
        rb.mass = 0.2f;
        keyGo.transform.SetParent(parent.transform);
        keyGo.transform.localPosition = localOffset;
        keyGo.transform.localRotation = Quaternion.identity;

        var placeable = keyGo.AddComponent<PlaceableItem>();
        SetField(placeable, "itemId", keyId);
        SetField(placeable, "displayName", displayName);

        var keyItem = keyGo.AddComponent<KeyItem>();
        SetField(keyItem, "keyId", keyId);
        SetField(keyItem, "displayName", displayName);
        SetField(keyItem, "collectOnPickup", true);

        var rend = keyGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = Color.yellow;
    }

    void SpawnEmergencyBoard()
    {
        if (GameObject.Find("EmergencyBoard_GDD") != null) return;
        var boardGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        boardGo.name = "EmergencyBoard_GDD";
        boardGo.transform.position = new Vector3(0, 1.5f, 3);
        boardGo.transform.localScale = new Vector3(1, 0.5f, 0.05f);
        var rend = boardGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = Color.red;
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
}
