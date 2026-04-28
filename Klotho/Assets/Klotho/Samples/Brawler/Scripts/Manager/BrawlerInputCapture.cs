using System;
using UnityEngine;
using UnityEngine.InputSystem;

using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    /// <summary>
    /// Brawler keyboard/mouse input capture
    ///
    /// Input mapping:
    ///   WASD         → MoveInputCommand (XZ movement)
    ///   Space        → MoveInputCommand (JumpPressed)
    ///   Left Click   → AttackCommand (mouse direction)
    ///   Q            → UseSkillCommand (slot 0)
    ///   E            → UseSkillCommand (slot 1)
    /// </summary>
    public class BrawlerInputCapture : IDisposable
    {
        private readonly InputAction _moveAction;
        private readonly InputAction _jumpAction;
        private readonly InputAction _attackAction;
        private readonly InputAction _skill1Action;
        private readonly InputAction _skill2Action;

        public FP64 H { get; private set; }
        public FP64 V { get; private set; }
        public bool Jump { get; private set; }
        public bool JumpHeld { get; private set; }
        public bool Attack { get; private set; }
        public int SkillSlot { get; private set; } = -1;
        public FPVector2 AimDirection { get; set; }

        public BrawlerInputCapture()
        {
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

            _jumpAction   = new InputAction("Jump",   InputActionType.Button, "<Keyboard>/space");
            _jumpAction.AddBinding("<Gamepad>/buttonSouth");

            _attackAction = new InputAction("Attack", InputActionType.Button, "<Mouse>/leftButton");
            _attackAction.AddBinding("<Gamepad>/buttonWest");

            _skill1Action = new InputAction("Skill1", InputActionType.Button, "<Keyboard>/q");
            _skill2Action = new InputAction("Skill2", InputActionType.Button, "<Keyboard>/e");
        }

        public void CaptureInput()
        {
            Vector2 move = _moveAction.ReadValue<Vector2>();
            H = FP64.FromFloat(Mathf.Clamp(move.x, -1f, 1f));
            V = FP64.FromFloat(Mathf.Clamp(move.y, -1f, 1f));

            if (_jumpAction.WasPressedThisFrame()) Jump = true;
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
            _moveAction?.Dispose();
            _jumpAction?.Dispose();
            _attackAction?.Dispose();
            _skill1Action?.Dispose();
            _skill2Action?.Dispose();
        }
    }
}
