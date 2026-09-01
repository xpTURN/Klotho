using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Interpolates position/rotation between two adjacent Verified frames.
    /// Used as the interpolation source for entities with ViewFlags.EnableSnapshotInterpolation enabled.
    ///
    /// Branches:
    ///   (a) Both frames valid → Lerp(ta, tb, alpha)
    ///   (b) Only baseTick valid     → ta value (corresponds to alpha=0)
    ///   (c) Only baseTick+1 valid   → tb value (corresponds to alpha=1)
    ///   (d) Neither available or entity missing → SnapshotPose.Occupied is false; the caller decides
    ///   (e) Both valid but a teleport lands between them → hold ta (never lerp across a teleport)
    /// </summary>
    public static class VerifiedFrameInterpolator
    {
        /// <summary>
        /// One snapshot-interpolation query. Answers everything the view needs about the current render
        /// window at once, from a single pair of ring lookups and a single <c>RenderClock</c> read.
        ///
        /// Splitting this across separate position and rotation calls cost four ring lookups, four
        /// <see cref="Occupies"/> calls and four RenderClock assemblies per entity per frame — RenderClock
        /// is a property that rebuilds its struct on every access, and each function read
        /// <c>VerifiedBaseTick</c> and <c>VerifiedAlpha</c> from it separately.
        ///
        /// <see cref="Occupied"/> is the part the view cannot get anywhere else: it says whether the
        /// entity is on the Verified timeline the render is actually reading, which is a different
        /// question from whether it is alive on the live Predicted frame. Gating the snapshot path on the
        /// latter froze a view for the whole SD lead whenever prediction destroyed an entity the server
        /// had not yet confirmed dead.
        /// </summary>
        public struct SnapshotPose
        {
            /// <summary>
            /// The entity held at least one of the two interpolation endpoints — or, failing that, some
            /// Verified frame in the window's shadow (the ticks between the window and the newest
            /// Verified frame), in which case the pose is the oldest such frame's, held single-endpoint
            /// style. That shadow case is the spawn warmup: the view exists as soon as the newest
            /// Verified frame carries the entity, while the window is still InterpolationDelayTicks
            /// behind its birth.
            /// </summary>
            public bool Occupied;

            /// <summary>Render pose — lerped between the endpoints, or held at one of them.</summary>
            public Vector3    Position;
            public Quaternion Rotation;

            /// <summary>
            /// The alpha=0 endpoint's pose: tick-quantized, straight out of a Verified snapshot. This is
            /// what the root transform wants under <c>_interpolationTarget</c> — the interpolated child
            /// is at most one tick ahead of it, whereas the live Predicted pose the root used to get is a
            /// prediction the verified path exists to avoid.
            /// </summary>
            public Vector3    BasePosition;
            public Quaternion BaseRotation;

            /// <summary>
            /// A teleport lands between the two endpoints. Determining this needs BOTH endpoints, so a
            /// window with only one occupied reports false — there is no pair to compare.
            /// </summary>
            public bool Teleported;
        }

        /// <summary>
        /// Resolves the render window into a <see cref="SnapshotPose"/>. Branches (a)-(e) below carry the
        /// same meanings the two wrapper methods documented; nothing about the interpolation changed.
        /// </summary>
        public static SnapshotPose GetSnapshotPose(EntityRef entity, IKlothoEngine engine)
        {
            var clock = engine.RenderClock;
            int baseTick = clock.VerifiedBaseTick;
            bool hasA = engine.TryGetFrameAtTick(baseTick,     out var a);
            bool hasB = engine.TryGetFrameAtTick(baseTick + 1, out var b);

            // A slot past the verified boundary is not a Verified endpoint, whatever the ring hands back:
            // FrameRingBuffer has no notion of verified, so it returns predicted, rollback-mutable frames
            // without complaint. The render clock's clamp normally keeps base + 1 <= LastVerifiedTick, but
            // it cannot in two places: at LastVerifiedTick <= 0 the max(0, ...) floor breaks the derivation,
            // and a backward move of LastVerifiedTick (desync ladder, FullState restore, spectator resync)
            // lands AFTER AdvanceVerifiedRenderTime has already run for the frame — it sits at the top of
            // Update, the assignments come later in that same Update, and views read in LateUpdate. So the
            // base is stale-high for exactly one frame, and this is the check that covers it.
            if (hasA && baseTick     > engine.LastVerifiedTick) hasA = false;
            if (hasB && baseTick + 1 > engine.LastVerifiedTick) hasB = false;

            // If the entity does not occupy the frame's slot, treat that slot as invalid.
            if (hasA && !Occupies(a, entity)) hasA = false;
            if (hasB && !Occupies(b, entity)) hasB = false;

            var result = default(SnapshotPose);
            if (!hasA && !hasB)                                                    // (d) — the window holds nothing
            {
                // Before reporting empty, look PAST the window: the oldest occupied Verified frame in
                // (base + 1, LastVerifiedTick] is the pose the window will show first when it arrives —
                // for a freshly spawned entity that frame is its birth. Holding it removes the residual
                // backward jump the newest-verified fallback left: that fallback advanced with pose(L)
                // during warmup and then snapped back by max(0, delay - 2) ticks of motion when the
                // window reached the birth (steady state; a multi-tick L advance makes it larger, which
                // is why even delay 2 could jump). Both the render AND the root take this pose — the
                // root's contract is the window's alpha=0 endpoint, i.e. it follows the render, and it
                // used to take the very same backward jump.
                //
                // The scan starting at base + 2 is exact: a usable base + 1 would have put us in branch
                // (c), which this generalizes — as it also generalizes the view's newest-verified
                // fallback, whose sole candidate (L) is this scan's last. Scan length is at most
                // delay + 9 (the live base can trail the target by the 10-tick snap guard), each step an
                // array lookup. Where the window is empty for any other reason, the range collapses on
                // its own: session start (L <= 0) and a backward L move make it empty, a
                // dead entity matches nothing — Occupies is version-aware, so a reused slot does
                // not resurrect it — and frames the ring no longer holds skip to the oldest retained,
                // which is still the closest authoritative pose to the window. The empty return below is
                // therefore load-bearing, not defensive.
                int newest = engine.LastVerifiedTick;

                // Start no further back than the ring can answer for. The "at most delay + 9" bound above
                // holds only while the render clock and _lastVerifiedTick are in step, and for one frame
                // they are not: AdvanceVerifiedRenderTime runs at the top of Update and clamps against
                // the OLD L, while a late-join catchup or a large verified batch assigns the new one
                // later in that same Update — and the views read it in LateUpdate. newest - baseTick can
                // be hundreds there, per snapshot view, every step a ring lookup that is guaranteed to
                // miss for everything the ring no longer holds. Clamping skips exactly those and changes
                // nothing else: the scan takes the first OCCUPIED frame going up, and a frame outside the
                // ring cannot be it.
                int oldestRetained = newest - engine.SimulationConfig.GetSnapshotCapacity() + 1;
                int from = baseTick + 2;
                if (from < oldestRetained) from = oldestRetained;

                for (int t = from; t <= newest; t++)
                {
                    if (!engine.TryGetFrameAtTick(t, out var shadow) || !Occupies(shadow, entity)) continue;

                    ref readonly var held = ref shadow.GetReadOnly<TransformComponent>(entity);
                    result.Occupied = true;
                    result.Position = result.BasePosition = ToVector3(held.Position);
                    result.Rotation = result.BaseRotation = ToYawRotation(held.Rotation);
                    return result;
                }
                return result;                                                     // Occupied stays false
            }

            result.Occupied = true;

            if (hasA && !hasB)                                                     // (b)
            {
                ref readonly var only = ref a.GetReadOnly<TransformComponent>(entity);
                result.Position = result.BasePosition = ToVector3(only.Position);
                result.Rotation = result.BaseRotation = ToYawRotation(only.Rotation);
                return result;
            }
            if (!hasA && hasB)                                                     // (c)
            {
                ref readonly var only = ref b.GetReadOnly<TransformComponent>(entity);
                result.Position = result.BasePosition = ToVector3(only.Position);
                result.Rotation = result.BaseRotation = ToYawRotation(only.Rotation);
                return result;
            }

            ref readonly var ta = ref a.GetReadOnly<TransformComponent>(entity);
            ref readonly var tb = ref b.GetReadOnly<TransformComponent>(entity);

            result.BasePosition = ToVector3(ta.Position);
            result.BaseRotation = ToYawRotation(ta.Rotation);

            // (e) FIRST, before the lerp: holding ta is what puts the jump on the tick boundary. Reordering
            // this after (a) would lerp across the discontinuity, which is what the branch prevents.
            if (Teleported(ta, tb))
            {
                result.Teleported = true;
                result.Position   = result.BasePosition;
                result.Rotation   = result.BaseRotation;
                return result;
            }

            float alpha = clock.VerifiedAlpha;                                     // (a)
            result.Position = Vector3.Lerp(result.BasePosition, ToVector3(tb.Position), alpha);
            float yawA = ta.Rotation.ToFloat() * Mathf.Rad2Deg;
            float yawB = tb.Rotation.ToFloat() * Mathf.Rad2Deg;
            result.Rotation = Quaternion.Euler(0f, Mathf.LerpAngle(yawA, yawB, alpha), 0f);
            return result;
        }

        /// <summary>Position-only wrapper over <see cref="GetSnapshotPose"/>. Kept for callers that only need the pose.</summary>
        public static Vector3 InterpolatePosition(EntityRef entity, IKlothoEngine engine, Vector3 fallbackPos)
        {
            var pose = GetSnapshotPose(entity, engine);
            return pose.Occupied ? pose.Position : fallbackPos;   // (d) → fallback
        }

        /// <summary>Rotation-only wrapper over <see cref="GetSnapshotPose"/>.</summary>
        public static Quaternion InterpolateRotation(EntityRef entity, IKlothoEngine engine, Quaternion fallbackRot)
        {
            var pose = GetSnapshotPose(entity, engine);
            return pose.Occupied ? pose.Rotation : fallbackRot;   // (d) → fallback
        }

        /// <summary>
        /// Whether <paramref name="entity"/> — this exact version of it — still holds the slot in
        /// <paramref name="frame"/> and carries a transform to read.
        ///
        /// The version check is not defensive: <c>Frame.Has</c> and <c>GetReadOnly</c> forward
        /// <c>entity.Index</c> alone, so a handle whose slot was recycled <b>answers about the new
        /// occupant</b> and reports true. <c>Frame.TryRead</c>'s remarks state the rule that follows —
        /// call <c>Entities.IsAlive</c> first for any handle that did not come from the current tick —
        /// and snapshot interpolation is that case by construction: it reads frames from the past with a
        /// handle taken at spawn. Without this, a view spawned into a recycled slot renders whatever used
        /// to live there for the whole interpolation-delay window, and it renders it smoothly, so nothing
        /// about the picture says it is wrong.
        /// </summary>
        private static bool Occupies(ECS.Frame frame, EntityRef entity)
            => frame.Entities.IsAlive(entity) && frame.Has<TransformComponent>(entity);

        /// <summary>
        /// Whether a teleport lands between the two frames, making them unlerpable.
        ///
        /// A teleport is instantaneous in the simulation, so interpolating across it walks the entity
        /// through positions it never occupied — the one thing teleport handling exists to prevent, and
        /// it happens here silently because <c>Lerp</c> has no idea the endpoints are discontinuous. The
        /// count of bogus frames scales with framerate × tick interval, so a faster display shows more
        /// of them, not fewer.
        ///
        /// Holding <c>ta</c> (what branch (b) would have returned) is what puts the jump on the tick
        /// boundary: the render clock reaches the teleport tick exactly when the window advances past it,
        /// and the frame after that lerps between two post-teleport endpoints. Returning <c>tb</c>
        /// instead would fire the jump up to a full tick early.
        ///
        /// <c>TeleportTick</c> lives on TransformComponent, so it is in the snapshots already; the game
        /// stamps it with the current tick when it teleports something. This needs no engine support and
        /// is independent of EnableErrorCorrection — unlike the Teleported flag in the view, which only
        /// exists when error correction runs.
        /// </summary>
        private static bool Teleported(in TransformComponent ta, in TransformComponent tb)
            => ta.TeleportTick != tb.TeleportTick;

        private static Vector3 ToVector3(in FPVector3 v)
            => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());

        private static Quaternion ToYawRotation(FP64 yawRad)
            => Quaternion.Euler(0f, yawRad.ToFloat() * Mathf.Rad2Deg, 0f);
    }
}
