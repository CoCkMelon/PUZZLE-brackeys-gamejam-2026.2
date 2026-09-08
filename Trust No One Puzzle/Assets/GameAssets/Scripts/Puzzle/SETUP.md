# Puzzle systems — wire-up

Copy `Assets/GameAssets/Scripts/Puzzle` into the Unity project (same path).

These scripts talk to existing `IInteractable`, `PlayerInteraction`, `OpenableInteractable`, and `DialogueManager`.

## 1. Player carry

On the **Player** (same object as `PlayerInteraction`):

- Add `PlayerCarry`
- Assign `Hold Point` (empty child in front of the camera)
- Optional: bind `Drop Action` (e.g. G / gamepad B)
- Optional: bind `Spatial Mode Action` to mouse middle button (or skip and use HUD)
- Assign `Player Body` (character root) and `View Camera`

No other branch in the repo (Interactions / toolbox / Mobile_Screen) has box-shoving — only pick/drop furniture. This carry layer is the storage-room mover.

## 2. Placeable objects

On each pickup:

- Collider + Rigidbody
- `PlaceableItem`
  - `Item Id` unique string (`key_red`, `photo`, `wrench`…)
  - `Display Name` for prompts
  - **Carry Style**
    - `Handheld` — keys/tools; parented, rotate with the player/camera
    - `WorldStable` — follows hold position, **does not rotate** with look
    - `Spatial` — crates/boxes in the tight storage room (see below)

Layer must be on the interaction mask used by `PlayerInteraction`. Keep colliders enabled for Spatial/WorldStable so boxes stop on walls.

### Tight storage (Spatial)

While carrying a `Spatial` item:

| Input | Effect |
| --- | --- |
| Mouse wheel **up** | Push object **away from** camera |
| Mouse wheel **down** | Pull object **toward** camera |
| **MMB hold** or mobile HUD button | Shove in player space |
| Screen **X** while shoving | Player **local Y** (up) |
| Screen **Y** while shoving | Player **local Z** (forward) |

Yaw stays locked to the **player body** (not camera pitch), so looking up/down does not tip the crate. Movement is swept against `Obstruction Mask` so boxes do not tunnel through shelves.

**Mobile:** add a UI button with `SpatialMoveHudButton` (hold = MMB). Pointer down starts shove, pointer up stops. Enable `Toggle Instead Of Hold` if you want a sticky mode.

Set `Default / Min / Max Hold Distance` per crate so large boxes cannot clip into the camera.

Layer must be on the interaction mask used by `PlayerInteraction`.

## 3. Placement validation

Empty snap targets (table outline, drawer interior, pedestal):

- Collider (can be trigger)
- `PlacementSlot`
  - `Slot Id`
  - `Required Item Id` must match a `PlaceableItem.itemId`
  - `Snap Point` child transform
  - `Allow Wrong Items` — if true, player can seat the wrong object (fires wrong-placement)
  - `Lock When Correct` — prevents removing a solved piece

Look at the slot and press Interact while carrying to place.

## 4. Drawer unlock triggers

On a manager object (or on the furniture root):

- `DrawerUnlockTrigger`
  - `Drawer Id` (used by hints)
  - `Condition`
    - **All Slots Correct** — every listed slot has the right item
    - **Any Slot Correct**
    - **Specific Item In Any Slot** — uses `Required Item Id`
  - `Required Slots` — drag `PlacementSlot`s
  - `Drawers` — drag `OpenableInteractable`s that **Start Locked**
  - `Unlock Once` — stay unlocked after first solve

Those drawers must have `OpenableInteractable.startsLocked = true`. Unlock is `OpenableInteractable.Unlock()`.

## 5. Keys and locked furniture

Any lockable script (`OpenableFurniture`, `OpenableInteractable`, `ToolBoxInteractable`)
can now be opened with a key instead of only by a puzzle event.

### The key prop

On the key object (works with **both** carry systems — `PlaceableItem` physics
carry and the old `Interactable` + `FPPCameraController` carry):

- Add `KeyItem`
  - `Key Id` — e.g. `toolbox_key` (a lock with an empty required id accepts any key)
  - `Display Name` — shown in the prompt: *"Press E To Unlock with Brass Key"*
  - `Collect On Pickup` — adds the id to the global `KeyRing` the moment the player
    picks it up, so the drawer can still be opened after the key was put down
  - `Consumed On Use` — single-use key
  - `Consume Behaviour` — `Destroy` / `Disable` / `KeepInWorld`

### The lock

On the furniture:

| Field | Meaning |
| --- | --- |
| `Starts Locked` | begins locked |
| `Unlock With Key` | player can unlock it by interacting while owning a key (off = script-only lock, same as before) |
| `Required Key Id` | must match `KeyItem.keyId`; empty = any key |
| `Key Access` | `HeldItemOnly` (must be in hands) / `KeyRingOnly` (must have been collected) / `HeldOrKeyRing` |
| `Consume Key On Unlock` | spends single-use keys |
| `Open On Unlock` | the unlocking press also opens it |
| `Relock On Close` | needs the key again every time |

Events: `OnUnlocked`, `OnLockedAttempt`, `OnOpened`, `OnClosed`, plus optional
`unlockSound` / `lockedSound`.

Script / UnityEvent API is unchanged: `Unlock()`, `Lock()`, `Open()`, `Close()`,
and the new `TryUnlockWithKey()` / `PlayerHasMatchingKey()`.
`DrawerUnlockTrigger` still works — puzzle unlocking and key unlocking can be
mixed on the same drawer.

Anything else can ask the global ring directly:

```csharp
KeyRing.Has("toolbox_key");     // owned?
KeyRing.Add("toolbox_key");     // grant from a cutscene / dialogue
KeyRing.Consume("toolbox_key"); // spend
```

## 6. Wrong hint system

On the same object as `DialogueManager` / Mobile UI:

- `WrongHintSystem`
- Fill **Hints** list:

| Field | Use |
| --- | --- |
| `Id` | unique, used to never-repeat |
| `Text` | message shown in the phone chat |
| `Is Misleading` | prefixes `[???]` vs `[hint]` |
| `Trigger On Wrong Slot Id` | send when that slot gets the wrong item |
| `Trigger On Correct Slot Id` | send when that slot is solved |
| `Trigger On Drawer Id` | send when that drawer unlocks |

Wrong placements also roll a random misleading hint (`Wrong Hint Chance`).

`DialogueManager.SendChatMessage` is used automatically if a manager exists in the scene.

## Carry physics: no penetration

`PlayerCarry` keeps the carried body fully simulated (never kinematic, never
parented) and adds three guards so items cannot sink into colliders:

1. **Damped follow servo** (`Follow Time`, `Rotation Follow Time`) — the servo
   closes the gap over time instead of demanding a one-step jump, so the requested
   motion is always something the solver can resolve.
2. **Shape sweep** (`Sweep Against Geometry`, `Contact Skin`, `Obstruction Mask`) —
   the body is swept along its requested motion each `FixedUpdate` and only the
   component pointing *into* the first blocking surface is cancelled. The
   tangential part survives, so items slide along walls instead of stopping dead.
3. **Depenetration pass** (`Resolve Overlaps`, `Depenetration Speed`) —
   `Physics.ComputePenetration` eases an item that is already intersecting
   something back out at a limited speed instead of firing it across the room.

`PlaceableItem` additionally raises solver iterations, caps
`maxDepenetrationVelocity` and lifts `maxAngularVelocity` while an item is held,
and restores every one of those values on release. Rotation is damped to 25% while
the item is in contact (turning a pressed-in object is the classic way to force it
through a wall).

`Drop When Stuck` can be turned off if you would rather have the item stay pressed
against the obstacle than be released automatically.

## Example flow

1. Toolbox key (`itemId = key`) is a `PlaceableItem`.
2. Desk slot requires `key`.
3. Putting a photo in that slot → wrong hint on the phone.
4. Putting the key in → `DrawerUnlockTrigger` (AllSlotsCorrect) unlocks the nightstand drawer.
5. Drawer unlock hint: “Something just clicked in the nightstand.”
