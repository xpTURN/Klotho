using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// View component that toggles Attack/Skill VFX using event flow only.
    /// Decides Play/Stop via predicted/confirmed/canceled events instead of state-based SetActive polling.
    ///
    /// Unified actor handling (no actor branching):
    ///   - All actors play at OnEventPredicted AND OnEventConfirmed.
    ///     DiffRollbackEvents / DispatchVerifiedEventsPartial dedupe hash-matching pairs,
    ///     so double-play does not occur in normal cases.
    ///   - On rollback mismatch: Canceled fires Stop first, then Confirmed re-plays the corrected action.
    ///   - HandlePlay guards against stale Play (action already ended) via ActionLockTicks check —
    ///     prevents VFX leak when DiffRollback fires Confirmed Play after Synced Stop already passed
    ///     in late-rollback (rollback delay > ActionLock duration) cases.
    ///
    /// Stop timing:
    ///   - OnUpdateView polls ActionLockTicks 1→0 edge for actor-agnostic immediate Stop.
    ///     This aligns Predicted Play with Predicted Stop for all actors, preventing the
    ///     1.1~1.5x extension that would occur if Stop relied solely on Synced ActionCompletedEvent
    ///     (verified delay D ticks late vs Predicted Play time).
    ///   - OnSyncedEvent (ActionCompletedEvent) acts as a verified-time fallback Stop.
    /// </summary>
    public class CharacterActionVfxViewComponent : EntityViewComponent
    {
        [SerializeField] private GameObject _attackEffect;
        [SerializeField] private GameObject _skill00Effect;
        [SerializeField] private GameObject _skill01Effect;

        // Polling Stop state — detects ActionLockTicks edge (>0 → 0).
        private int _prevActionLockTicks;

        public override void OnActivate(FrameRef frame)
        {
            // Pool reuse handling — subscribe/unsubscribe paired in OnActivate/OnDeactivate. With only one OnInitialize call,
            // subscription is missed on re-Rent (already unsubscribed in OnDeactivate).
            Engine.OnEventPredicted += OnEventPredicted;
            Engine.OnEventConfirmed += OnEventConfirmed;
            Engine.OnEventCanceled  += OnEventCanceled;
            Engine.OnSyncedEvent    += OnSyncedEvent;

            _prevActionLockTicks = 0;
            StopAll();
        }

        public override void OnDeactivate()
        {
            if (Engine != null)
            {
                Engine.OnEventPredicted -= OnEventPredicted;
                Engine.OnEventConfirmed -= OnEventConfirmed;
                Engine.OnEventCanceled  -= OnEventCanceled;
                Engine.OnSyncedEvent    -= OnSyncedEvent;
            }

            StopAll();
        }

        // ── Unified dispatch handlers (actor-agnostic) ──

        private void OnEventPredicted(int tick, SimulationEvent evt)
        {
            HandlePlay(evt);
        }

        private void OnEventConfirmed(int tick, SimulationEvent evt)
        {
            HandlePlay(evt);
        }

        private void OnEventCanceled(int tick, SimulationEvent evt)
        {
            if (evt is AttackActionEvent atk && atk.Attacker.Index == EntityRef.Index)
                StopAll();
            else if (evt is SkillActionEvent skl && skl.Caster.Index == EntityRef.Index)
                StopAll();
        }

        private void OnSyncedEvent(int tick, SimulationEvent evt)
        {
            // ActionCompletedEvent — verified-time fallback Stop. OnUpdateView polling typically calls
            // StopAll earlier at Predicted time, so this is usually a no-op.
            if (evt is ActionCompletedEvent done && done.Actor.Index == EntityRef.Index)
                StopAll();
        }

        public override void OnUpdateView()
        {
            // Polls ActionLockTicks 1→0 edge for actor-agnostic immediate Stop.
            // Aligns Predicted Play with Predicted Stop, preventing the 1.1~1.5x extension that
            // would occur if Stop relied solely on the Synced ActionCompletedEvent (D ticks late).
            var frame = Engine.PredictedFrame.Frame;
            if (frame == null) return;
            if (!frame.Has<CharacterComponent>(EntityRef)) return;

            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(EntityRef);
            if (_prevActionLockTicks > 0 && c.ActionLockTicks <= 0)
                StopAll();
            _prevActionLockTicks = c.ActionLockTicks;
        }

        private void HandlePlay(SimulationEvent evt)
        {
            // Late-dispatch guard — skip stale Play when action has already ended in the latest frame.
            // Late rollback (rollback delay > ActionLock duration) can cause DiffRollback to fire Confirmed
            // Play after Synced Stop has already passed; without this guard the VFX would leak permanently.
            var frame = Engine.PredictedFrame.Frame;
            if (frame == null || !frame.Has<CharacterComponent>(EntityRef)) return;
            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(EntityRef);
            if (c.ActionLockTicks <= 0) return;

            if (evt is AttackActionEvent atk && atk.Attacker.Index == EntityRef.Index)
                PlayAttack(atk.AttackerPosition, atk.AimDirection);
            else if (evt is SkillActionEvent skl && skl.Caster.Index == EntityRef.Index)
                PlaySkill(skl.ClassIndex, skl.SkillSlot, skl.CasterPosition, skl.AimDirection, skl.TargetPosition);
        }

        // ── Play / Stop internal logic ──

        private void PlayAttack(xpTURN.Klotho.Deterministic.Math.FPVector2 attackerPos,
                                xpTURN.Klotho.Deterministic.Math.FPVector2 aimDir)
        {
            if (_attackEffect == null) return;
            var aimWorld = new Vector3(aimDir.x.ToFloat(), 0f, aimDir.y.ToFloat());
            var targetPos = transform.position + aimWorld * 0.6f;
            DetachAndPlace(_attackEffect, targetPos, Quaternion.identity);

            if (_skill00Effect != null) _skill00Effect.SetActive(false);
            if (_skill01Effect != null) _skill01Effect.SetActive(false);
        }

        private void PlaySkill(int classIdx, int slot,
                               xpTURN.Klotho.Deterministic.Math.FPVector2 casterPos,
                               xpTURN.Klotho.Deterministic.Math.FPVector2 aimDir,
                               xpTURN.Klotho.Deterministic.Math.FPVector2 targetPos)
        {
            // Ranged skills (Mage Slot0 impact, Rogue Slot1 throw) use targetPos, others use caster position
            bool isRanged = (classIdx == 1 && slot == 0) || (classIdx == 2 && slot == 1);
            Vector3 worldPos = isRanged
                ? new Vector3(targetPos.x.ToFloat(), 0.8f, targetPos.y.ToFloat())
                : transform.position;

            if (_attackEffect != null) _attackEffect.SetActive(false);

            if (slot == 0 && _skill00Effect != null)
            {
                DetachAndPlace(_skill00Effect, worldPos, Quaternion.identity);
                if (_skill01Effect != null) _skill01Effect.SetActive(false);
            }
            else if (slot == 1 && _skill01Effect != null)
            {
                DetachAndPlace(_skill01Effect, worldPos, Quaternion.identity);
                if (_skill00Effect != null) _skill00Effect.SetActive(false);
            }
        }

        /// <summary>
        /// Detaches VFX to scene root then sets world pos/rot. Performs SetParent(null) only once on first Play —
        /// completely blocks parent chain rotation inheritance (Sfx > Visuals > character Y-rot, etc.) to prevent rotation distortion.
        /// </summary>
        private static void DetachAndPlace(GameObject fx, Vector3 worldPos, Quaternion worldRot)
        {
            var t = fx.transform;
            if (t.parent != null)
                t.SetParent(null, worldPositionStays: true);
            t.SetPositionAndRotation(worldPos, worldRot);
            fx.SetActive(true);
        }

        private void StopAll()
        {
            if (_attackEffect  != null) _attackEffect.SetActive(false);
            if (_skill00Effect != null) _skill00Effect.SetActive(false);
            if (_skill01Effect != null) _skill01Effect.SetActive(false);
        }
    }
}
