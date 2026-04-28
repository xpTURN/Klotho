# Brawler Appendix G — BrawlerInputCapture

> Related: [Brawler.md](Brawler.md) §11 (Phase 8 — Callbacks)
> Target: full implementation of `BrawlerInputCapture` — InputAction wiring, properties, Dispose

---

## G-1. Role

- Configures Unity InputSystem `InputAction`s directly in code (no Input Action Asset file required)
- Captures input at every `OnPollInput` and exposes the values as properties
- **One-shot inputs** (Jump / Attack / Skill) are consumed for one tick via `ConsumeOneShot()`
- Implements `IDisposable` — releases `InputAction` resources at session shutdown

---

## G-2. Full Implementation

```csharp
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    public class BrawlerInputCapture : IDisposable
    {
        private readonly InputAction _moveAction;
        private readonly InputAction _jumpAction;
        private readonly InputAction _attackAction;
        private readonly InputAction _skill1Action;
        private readonly InputAction _skill2Action;

        // ── Read every tick ──

        public FP64 H { get; private set; }             // -1..1
        public FP64 V { get; private set; }             // -1..1
        public bool Jump { get; private set; }          // one-shot
        public bool JumpHeld { get; private set; }      // continuous
        public bool Attack { get; private set; }        // one-shot
        public int  SkillSlot { get; private set; } = -1;  // -1/0/1, one-shot
        public FPVector2 AimDirection { get; set; }     // updated externally from mouse world position

        public BrawlerInputCapture()
        {
            // Move (WASD + arrow keys + left analog stick)
            _moveAction = new InputAction("Move", InputActionType.Value);
            _moveAction.AddCompositeBinding("2DVector")
                .With("Up",    "<Keyboard>/w")
                .With("Down",  "<Keyboard>/s")
                .With("Left",  "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            _moveAction.AddCompositeBinding("2DVector")
                .With("Up",    "<Keyboard>/upArrow")
                .With("Down",  "<Keyboard>/downArrow")
                .With("Left",  "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            _moveAction.AddBinding("<Gamepad>/leftStick");

            // Jump (Space / Gamepad South)
            _jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _jumpAction.AddBinding("<Gamepad>/buttonSouth");

            // Attack (Left Click / Gamepad West)
            _attackAction = new InputAction("Attack", InputActionType.Button, "<Mouse>/leftButton");
            _attackAction.AddBinding("<Gamepad>/buttonWest");

            // Skill 0 / Skill 1 (Q / E) — current implementation is keyboard-only
            _skill1Action = new InputAction("Skill1", InputActionType.Button, "<Keyboard>/q");
            _skill2Action = new InputAction("Skill2", InputActionType.Button, "<Keyboard>/e");
        }

        // ── Called from OnPollInput ──
        public void CaptureInput()
        {
            Vector2 move = _moveAction.ReadValue<Vector2>();
            H = FP64.FromFloat(Mathf.Clamp(move.x, -1f, 1f));
            V = FP64.FromFloat(Mathf.Clamp(move.y, -1f, 1f));

            // Accumulate one-shot inputs — catches presses even when several Unity frames
            // pass between OnPollInput calls.
            if (_jumpAction.WasPressedThisFrame())   Jump = true;
            JumpHeld = _jumpAction.IsPressed();

            if (_attackAction.WasPressedThisFrame()) Attack = true;
            if (_skill1Action.WasPressedThisFrame()) SkillSlot = 0;
            if (_skill2Action.WasPressedThisFrame()) SkillSlot = 1;
        }

        public void ConsumeOneShot()
        {
            Jump = false;
            Attack = false;
            SkillSlot = -1;
            // JumpHeld, H, V are continuous values — keep them.
        }

        public void Enable()
        {
            _moveAction?.Enable();
            _jumpAction?.Enable();
            _attackAction?.Enable();
            _skill1Action?.Enable();
            _skill2Action?.Enable();
        }

        public void Disable()
        {
            _moveAction?.Disable();
            _jumpAction?.Disable();
            _attackAction?.Disable();
            _skill1Action?.Disable();
            _skill2Action?.Disable();
        }

        public void Dispose()
        {
            Disable();
            _moveAction?.Dispose();
            _jumpAction?.Dispose();
            _attackAction?.Dispose();
            _skill1Action?.Dispose();
            _skill2Action?.Dispose();
        }
    }
}
```

---

## G-3. Input Mapping Table

| Action | Keyboard | Gamepad | Type |
|---|---|---|---|
| Move (H/V) | WASD, arrow keys | LeftStick | Continuous (Value) |
| Jump | Space | South (A / Cross) | One-shot Button |
| JumpHeld | Space held | South held | Continuous Button |
| Attack | Left mouse click | West (X / Square) | One-shot Button |
| Skill0 | Q | (not bound) | One-shot Button |
| Skill1 | E | (not bound) | One-shot Button |

> Gamepad bindings for Skill0/1 are not included in the current implementation. Add them as needed, e.g., `_skill1Action.AddBinding("<Gamepad>/buttonNorth")`.

**AimDirection**: Not bound by this class. Compute the mouse world position from `BrawlerGameController` (or anywhere outside `BrawlerInputCapture`) and inject it via `input.AimDirection = ...`. For gamepads, convert the RightStick `Vector2` to `FPVector2`.

```csharp
// E.g., in BrawlerGameController.Update
Vector3 mouseWorld = RaycastMouseOnGround();
FPVector3 selfPos  = _session.Engine.LocalPlayerPosition;
FPVector2 aim = new FPVector2(
    FP64.FromFloat(mouseWorld.x - selfPos.x.ToFloat()),
    FP64.FromFloat(mouseWorld.z - selfPos.z.ToFloat()));
_input.AimDirection = aim.Normalized;
```

---

## G-4. Usage Flow

```
Unity Frame N
├── _input.CaptureInput()       ← accumulates WasPressedThisFrame()
├── ...                          ← multiple Unity frames may pass
│
└── KlothoEngine.Update
    └── execute tick T (40 ticks/s)
        └── OnPollInput(playerId, T, sender)
            ├── _input.CaptureInput()
            ├── sender.Send(MoveInputCommand)  ← H/V/JumpPressed/JumpHeld
            ├── if Attack    → sender.Send(AttackCommand { AimDirection })
            ├── if SkillSlot→ sender.Send(UseSkillCommand { SkillSlot, AimDirection })
            └── _input.ConsumeOneShot()        ← reset Jump/Attack/SkillSlot
```

**Important**: Either call `CaptureInput()` manually before `OnPollInput`, or call it once at the top of every `OnPollInput`. With a 40 ticks/s game and 60 FPS rendering, a tick is 25 ms and a Unity frame is 16.7 ms, so roughly ~1.5 frames may sit between two ticks. The `WasPressedThisFrame()` value must be accumulated across ticks to avoid losing inputs.

---

## G-5. Caveats

- **Determinism**: `BrawlerInputCapture` lives in the Unity (non-deterministic) layer. Only its outputs (`MoveInputCommand`, etc.) feed into the deterministic path. The `Mathf.Clamp` and float→FP64 conversion at capture time run only inside the **local peer**, and only the resulting commands are sent over the network — so this is safe.
- **Avoid losing one-shot inputs**: `OnPollInput` fires once per tick, but with Unity at 60 fps and ticks at 40 Hz that's about once every 1.5 Unity frames. Accumulate `WasPressedThisFrame` until `ConsumeOneShot` runs to avoid dropping inputs.
- **Spectator mode**: Spectators don't send input, so `OnPollInput` is not invoked. `CaptureInput` is not called either; handle Unity-only logic such as UI toggles separately.
- **Replay mode**: When `engine.IsReplayMode == true`, `OnPollInput` is not invoked. Skip `CaptureInput` / `ConsumeOneShot` as well.
- **Dispose**: Always call from scene unload or `BrawlerGameController.OnDestroy()`. InputActions are not GC-managed and require explicit disposal.

---

## G-6. Extension — Local 4-Player Split-Screen

The current implementation captures **only one local player**. For local split-screen with 2–4 players:

1. Create multiple `BrawlerInputCapture` instances — bind each to a different device (Gamepad N).
2. In `OnPollInput(playerId, ...)`, pick the right instance based on `playerId`.
3. Use `InputBinding.groups` in `InputAction.AddBinding` to separate groups (e.g., `"Gamepad0"`, `"Gamepad1"`).

The Klotho engine's `ISimulationCallbacks.OnPollInput` is invoked for every local player, so this branching is all that's required.
