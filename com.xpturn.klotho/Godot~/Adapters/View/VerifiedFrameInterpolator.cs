// Interpolates position/yaw between two adjacent Verified frames.
//   (a) both frames valid -> Lerp(ta, tb, VerifiedAlpha)
//   (b) only baseTick valid     -> ta value
//   (c) only baseTick+1 valid   -> tb value
//   (d) neither valid           -> SnapshotPose.Occupied is false; the caller decides
//   (e) both valid but a teleport between them -> hold ta (never lerp across a teleport)
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Godot
{
    public static class VerifiedFrameInterpolator
    {
        // One snapshot-interpolation query: pose, occupancy and teleport-in-window from a single pair of
        // ring lookups and a single RenderClock read. Splitting it across separate position and rotation
        // calls cost four ring lookups, four Occupies calls and four RenderClock assemblies per entity per
        // frame -- RenderClock rebuilds its struct on every access (IMP103 D-2/N-*).
        //
        // Occupied is the part the view cannot get anywhere else: it says whether the entity is on the
        // Verified timeline the render is actually reading, which is a different question from whether it
        // is alive on the live Predicted frame. Gating the snapshot path on the latter froze a view for the
        // whole SD lead whenever prediction destroyed an entity the server had not yet confirmed dead.
        public struct SnapshotPose
        {
            // The entity held at least one of the two interpolation endpoints -- or, failing that, some
            // Verified frame in the window's shadow (the ticks between the window and the newest Verified
            // frame), in which case the pose is the oldest such frame's, held single-endpoint style. That
            // shadow case is the spawn warmup: the view exists as soon as the newest Verified frame
            // carries the entity, while the window is still InterpolationDelayTicks behind its birth
            // (IMP103 D-2(f)).
            public bool Occupied;

            // Render pose -- lerped between the endpoints, or held at one of them.
            public Vector3    Position;
            public Quaternion Rotation;

            // The alpha=0 endpoint's pose: tick-quantized, straight out of a Verified snapshot. Unity uses
            // this for the root transform under _interpolationTarget; the Godot adapter has no such split
            // and only reads Position/Rotation, but the field is kept so both adapters answer the same
            // query (IMP103 D-2).
            public Vector3    BasePosition;
            public Quaternion BaseRotation;

            // A teleport lands between the two endpoints. Determining this needs BOTH endpoints, so a
            // window with only one occupied reports false -- there is no pair to compare.
            public bool Teleported;
        }

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
            // lands AFTER AdvanceVerifiedRenderTime has already run for the frame -- it sits at the top of
            // Update, the assignments come later in that same Update, and views read per frame. So the base
            // is stale-high for exactly one frame, and this is the check that covers it (IMP103 #13).
            if (hasA && baseTick     > engine.LastVerifiedTick) hasA = false;
            if (hasB && baseTick + 1 > engine.LastVerifiedTick) hasB = false;

            if (hasA && !Occupies(a, entity)) hasA = false;
            if (hasB && !Occupies(b, entity)) hasB = false;

            var result = default(SnapshotPose);
            if (!hasA && !hasB)                                                    // (d) -- the window holds nothing
            {
                // Before reporting empty, look PAST the window: the oldest occupied Verified frame in
                // (base + 1, LastVerifiedTick] is the pose the window will show first when it arrives --
                // for a freshly spawned entity that frame is its birth. Holding it removes the residual
                // backward jump the newest-verified fallback left: that fallback advanced with pose(L)
                // during warmup and then snapped back by max(0, delay - 2) ticks of motion when the
                // window reached the birth (steady state; a multi-tick L advance makes it larger, which
                // is why even delay 2 could jump).
                //
                // The scan starting at base + 2 is exact: a usable base + 1 would have put us in branch
                // (c), which this generalizes -- as it also generalizes the view's newest-verified
                // fallback, whose sole candidate (L) is this scan's last. Scan length is at most
                // delay + 9 (the live base can trail the target by the 10-tick snap guard), each step an
                // array lookup. Where the window is empty for any other reason, the range collapses on
                // its own: session start (L <= 0) and a backward L move (IMP103 #13) make it empty, a
                // dead entity matches nothing -- Occupies is version-aware (V-6), so a reused slot does
                // not resurrect it -- and frames the ring no longer holds skip to the oldest retained,
                // which is still the closest authoritative pose to the window. The empty return below is
                // therefore load-bearing, not defensive.
                int newest = engine.LastVerifiedTick;

                // Start no further back than the ring can answer for. The "at most delay + 9" bound above
                // holds only while the render clock and _lastVerifiedTick are in step, and for one frame
                // they are not: AdvanceVerifiedRenderTime runs at the top of Update and clamps against
                // the OLD L, while a late-join catchup or a large verified batch assigns the new one
                // later in that same Update -- and the views read it per frame. newest - baseTick can be
                // hundreds there, per snapshot view, every step a ring lookup guaranteed to miss for
                // everything the ring no longer holds. Clamping skips exactly those and changes nothing
                // else: the scan takes the first OCCUPIED frame going up, and a frame outside the ring
                // cannot be it (IMP105 P-1).
                int oldestRetained = newest - engine.SimulationConfig.GetSnapshotCapacity() + 1;
                int from = baseTick + 2;
                if (from < oldestRetained) from = oldestRetained;

                for (int t = from; t <= newest; t++)
                {
                    if (!engine.TryGetFrameAtTick(t, out var shadow) || !Occupies(shadow, entity)) continue;

                    ref readonly var held = ref shadow.GetReadOnly<TransformComponent>(entity);
                    result.Occupied = true;
                    result.Position = result.BasePosition = held.Position.ToVector3();
                    result.Rotation = result.BaseRotation = YawRotation(held.Rotation);
                    return result;
                }
                return result;                                                     // Occupied stays false
            }

            result.Occupied = true;

            if (hasA && !hasB)                                                     // (b)
            {
                ref readonly var only = ref a.GetReadOnly<TransformComponent>(entity);
                result.Position = result.BasePosition = only.Position.ToVector3();
                result.Rotation = result.BaseRotation = YawRotation(only.Rotation);
                return result;
            }
            if (!hasA && hasB)                                                     // (c)
            {
                ref readonly var only = ref b.GetReadOnly<TransformComponent>(entity);
                result.Position = result.BasePosition = only.Position.ToVector3();
                result.Rotation = result.BaseRotation = YawRotation(only.Rotation);
                return result;
            }

            ref readonly var ta = ref a.GetReadOnly<TransformComponent>(entity);
            ref readonly var tb = ref b.GetReadOnly<TransformComponent>(entity);

            result.BasePosition = ta.Position.ToVector3();
            result.BaseRotation = YawRotation(ta.Rotation);

            // (e) FIRST, before the lerp: holding ta is what puts the jump on the tick boundary. Reordering
            // this after (a) would lerp across the discontinuity and undo IMP103 V-5.
            if (Teleported(ta, tb))
            {
                result.Teleported = true;
                result.Position   = result.BasePosition;
                result.Rotation   = result.BaseRotation;
                return result;
            }

            float alpha = clock.VerifiedAlpha;                                     // (a)
            result.Position = result.BasePosition.Lerp(tb.Position.ToVector3(), alpha);
            result.Rotation = Quaternion.FromEuler(new Vector3(
                0f, Mathf.LerpAngle(ta.Rotation.ToFloat(), tb.Rotation.ToFloat(), alpha), 0f));
            return result;
        }

        // Position-only wrapper over GetSnapshotPose.
        public static Vector3 InterpolatePosition(EntityRef entity, IKlothoEngine engine, Vector3 fallbackPos)
        {
            var pose = GetSnapshotPose(entity, engine);
            return pose.Occupied ? pose.Position : fallbackPos;   // (d) -> fallback
        }

        // Rotation-only wrapper over GetSnapshotPose.
        public static Quaternion InterpolateRotation(EntityRef entity, IKlothoEngine engine, Quaternion fallbackRot)
        {
            var pose = GetSnapshotPose(entity, engine);
            return pose.Occupied ? pose.Rotation : fallbackRot;   // (d) -> fallback
        }

        // Whether this exact version of `entity` still holds the slot in `frame` and has a transform.
        // The version check is not defensive: Frame.Has/GetReadOnly forward entity.Index alone, so a
        // handle whose slot was recycled answers about the NEW occupant and reports true. Frame.TryRead's
        // remarks state the rule -- call Entities.IsAlive first for any handle that did not come from the
        // current tick -- and snapshot interpolation is that case by construction, reading past frames
        // with a handle taken at spawn. Without this a view spawned into a recycled slot renders whatever
        // used to live there for the whole interpolation-delay window, and renders it smoothly.
        private static bool Occupies(ECS.Frame frame, EntityRef entity)
            => frame.Entities.IsAlive(entity) && frame.Has<TransformComponent>(entity);

        // Whether a teleport lands between the two frames, making them unlerpable. A teleport is
        // instantaneous in the simulation, so lerping across it walks the entity through positions it
        // never occupied -- silently, because Lerp cannot tell the endpoints are discontinuous, and with
        // more bogus frames the faster the display. Holding ta (what branch (b) would return) puts the
        // jump on the tick boundary; returning tb would fire it up to a full tick early. TeleportTick
        // rides on TransformComponent so it is already in the snapshots, and this is independent of
        // EnableErrorCorrection -- unlike the view's Teleported flag, which only exists when EC runs.
        private static bool Teleported(in TransformComponent ta, in TransformComponent tb)
            => ta.TeleportTick != tb.TeleportTick;

        // TransformComponent.Rotation is a single yaw angle in radians (not a full quaternion).
        private static Quaternion YawRotation(FP64 yawRad)
            => Quaternion.FromEuler(new Vector3(0f, yawRad.ToFloat(), 0f));
    }
}
