using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Keeps the installed navmesh equal to what the frame says it should be, every tick.
    ///
    /// <para><b>The invariant.</b> At the start of every tick, before agents update:
    /// <c>installed mesh == Bake(snapshot, { p : p.EffectiveTick &lt;= frame.Tick &lt; p.RemovalEffectiveTick })</c>.
    /// Stating it as an invariant rather than as an event is what makes four different situations one
    /// case — moving forward normally, re-executing after a rollback, waking up on a FullState, and
    /// spectating or replaying. A swap performed as an event only handles the first.</para>
    ///
    /// <para><b>Why an event is not enough.</b> A delayed swap fires on a tick the client has already
    /// run predictively, and a rollback rewinds the frame WITHOUT rewinding the navmesh — nothing in
    /// the engine restores derived state. The old frame would then re-execute on the new mesh.
    /// Correcting from the frame every tick removes the failure instead of detecting it.</para>
    ///
    /// <para><b>Correcting must not write frame state.</b> A correction can run on a tick whose first
    /// execution did not correct anything, so anything hashed that it writes would make the replay
    /// diverge from the original run — the very thing it exists to prevent. So the correction only
    /// swaps the reference, and the reseed happens only on a BOUNDARY tick, a condition that is a
    /// function of frame state and therefore reproduces identically on every peer and every
    /// re-execution.</para>
    ///
    /// <para><b>Ordering.</b> Register this ahead of whatever drives the agents, so they see the
    /// corrected mesh. Command handlers run before every update system, so they must not read the
    /// navmesh — validating a placement is a function of the snapshot and the placement set, not of
    /// the installed mesh (see <see cref="FPNavMeshRebaker.TryPreviewPlacements"/>). The driver
    /// cannot check its own registration order: it does not know what else is registered. That check
    /// belongs to the game's own wiring tests.</para>
    ///
    /// <para><b>What stays the game's.</b> The placement table and the destroy
    /// (<see cref="IFPNavMeshPlacementSource"/>), the install and reseed
    /// (<see cref="IFPNavMeshInstaller"/>), subscribing <see cref="AdvanceSlice"/> to whatever the
    /// host calls a frame boundary, and calling <see cref="CorrectNow"/> once at world init and once
    /// after a FullState apply. Everything else is here.</para>
    /// </summary>
    public sealed class FPNavMeshRebakeDriver : ISystem
    {
        /// <summary>
        /// One cached mesh and the context that owns it. Two of these are what let the driver hold
        /// the pair of meshes a boundary bounces between.
        ///
        /// <para>A slot is not a cache entry beside a context, it IS the context's committed output.
        /// <c>CommitSwap</c> retires the mesh it replaces, and the mesh it replaces is exactly this
        /// slot's <see cref="Mesh"/> — so the two fields move in one statement
        /// (<see cref="CommitInto"/>) and there is no state in which the cache can hand out retired
        /// storage. That is also why one context cannot hold two: the second commit would recycle the
        /// first mesh out from under the cache.</para>
        ///
        /// <para><see cref="Key"/> is the rebake input itself, not a hash of it. The mesh is a pure
        /// function of (snapshot, placements, rules); the snapshot and rules are fixed here, so
        /// comparing the placements element by element makes a hit EXACT rather than probable. A
        /// digest would have been enough for "did it change" and is not enough to choose a mesh —
        /// see <see cref="DigestAt"/>, which stays a digest and stays out of that decision.</para>
        /// </summary>
        private sealed class MeshSlot
        {
            public readonly FPNavMeshRebakeContext Context;
            public FPNavMesh Mesh;
            public readonly FPBuildingPlacement[] Key;
            public int KeyCount;
            public long Tag;
            public int LastUsed;

            public MeshSlot(FPNavMeshRebakeContext context, int capacity)
            {
                Context = context;
                Key = new FPBuildingPlacement[capacity];
            }
        }

        private readonly IFPNavMeshPlacementSource _source;
        private readonly IFPNavMeshInstaller _installer;
        private readonly FPBuildingPlacementRules _rules;
        private readonly int _sliceBudgetUnits;
        private readonly int _slotCount;

        private MeshSlot[] _slots;
        private MeshSlot _installedSlot;
        private MeshSlot _taskSlot;
        private int _useClock;

        private long _installedTag;
        private bool _installed;

        // The one frame walk per tick lands here; everything else is derived from it.
        private readonly FPNavMeshTimedPlacement[] _table;
        private int _tableCount;
        private bool _isBoundary;
        private int _nextBoundary;

        // The sorted active set — the rebake input and the cache key. Built only when the digest says
        // the set changed, which is what keeps a quiet tick down to one walk plus an integer compare.
        private readonly FPBuildingPlacement[] _active;
        private readonly int[] _sortKeys;

        private FPNavMeshRebakeTask _task;
        private long _taskTag;
        private bool _taskDone;
        private readonly FPBuildingPlacement[] _taskKey;
        private int _taskKeyCount;

        /// <param name="sliceBudgetUnits">
        /// Work units per frame for <see cref="AdvanceSlice"/>. There is no universal right answer —
        /// the shipped sample measured 20000 as at or within noise of the best across 12.8k…205k
        /// triangle stages, and a game on a different stage size should measure its own rather than
        /// inherit that one. Smaller spends more steps for the same total; larger starts chaining
        /// phases back together.
        /// </param>
        /// <param name="slotCount">
        /// How many meshes to keep live. 2 is the measured optimum for a boundary that is crossed
        /// back and forth (a two-cycle: 147 installs needed 8 rebakes with two slots and 128 with
        /// one), and more than 2 bought nothing. It is a parameter rather than a constant because the
        /// second slot's resident cost scales with the stage — measured at +26.6 MB on a 205k
        /// triangle stage, negligible on a small one. 1 disables the cache.
        /// </param>
        public FPNavMeshRebakeDriver(
            IFPNavMeshPlacementSource source, IFPNavMeshInstaller installer,
            FPBuildingPlacementRules rules = default,
            int sliceBudgetUnits = 20000, int slotCount = 2)
        {
            if (source == null)
                throw new System.ArgumentException("FPNavMeshRebakeDriver: source is null");
            if (installer == null)
                throw new System.ArgumentException("FPNavMeshRebakeDriver: installer is null");
            if (sliceBudgetUnits <= 0)
                throw new System.ArgumentException(
                    "FPNavMeshRebakeDriver: sliceBudgetUnits must be positive");
            if (slotCount < 1)
                throw new System.ArgumentException("FPNavMeshRebakeDriver: slotCount must be >= 1");

            int cap = source.Capacity;
            if (cap <= 0)
                throw new System.ArgumentException(
                    "FPNavMeshRebakeDriver: source.Capacity must be positive. It is the STORAGE " +
                    "bound — how many placements a frame can hold, tombstones included — not the " +
                    "number the game lets stand.");

            _source = source;
            _installer = installer;
            _rules = rules;
            _sliceBudgetUnits = sliceBudgetUnits;
            _slotCount = slotCount;

            _table = new FPNavMeshTimedPlacement[cap];
            _active = new FPBuildingPlacement[cap];
            _sortKeys = new int[cap];
            _taskKey = new FPBuildingPlacement[cap];
        }

        /// <summary>
        /// Gives the driver the stage it bakes against, and builds its contexts.
        ///
        /// <para>Contexts of the driver's own, over the SAME snapshot — the expensive half is the
        /// snapshot and it is shared. They are not the game's: a task holds its context's pool across
        /// frames and the pool refuses overlapping use, so sharing one with a path that validates
        /// placements synchronously would put a trial rebake straight into a live slice. And the patch
        /// chain wants a single installer, so everything installed goes through these.</para>
        ///
        /// <para>Null clears, which turns <see cref="Update"/> into a no-op — for a stage the game
        /// does not support placement on.</para>
        /// </summary>
        public void SetSnapshot(FPNavMeshRebakeSnapshot snapshot)
        {
            _installedSlot = null;
            _taskSlot = null;
            _task = null;
            _taskDone = false;
            if (snapshot == null)
            {
                _slots = null;
                return;
            }

            _slots = new MeshSlot[_slotCount];
            for (int i = 0; i < _slotCount; i++)
                _slots[i] = new MeshSlot(new FPNavMeshRebakeContext(snapshot), _table.Length);
        }

        // ── diagnostics ─────────────────────────────────────────────────────────

        /// <summary>
        /// Counters, peer-local and outside frame state. Slicing is invisible from the state — the
        /// mesh is the same whether it happened or not — so without these there is no way to tell a
        /// working slicer from one that never starts a task, and a test of it would pass for the
        /// wrong reason.
        /// </summary>
        public int SlicedFrames { get; private set; }

        /// <summary>NOT evidence that slicing worked: the boundary finishes a task that has not
        /// finished itself, so an install comes from the task whether any slice ran or not.</summary>
        public int TaskInstalls { get; private set; }

        /// <summary>How often the budget failed to get there in time — the spike this exists to
        /// remove, and the number a budget is calibrated against.</summary>
        public int BoundaryFinishes { get; private set; }

        public int RebuildInstalls { get; private set; }
        public int CacheHits { get; private set; }
        public int CacheMisses { get; private set; }
        public int Reseeds { get; private set; }

        /// <summary>Must stay at zero on a peer that never rolls back and never joins: a correction
        /// on an ordinary forward tick means the invariant was not holding in the first place.</summary>
        public int Corrections { get; private set; }

        public bool HasPendingRebake => _task != null;

        /// <summary>
        /// Which in-flight task this is, counting up from 1. Never reused.
        ///
        /// <para>Exists because "is a task pending" cannot express the property that matters. A
        /// correction that rebuilds must not kill the task the slices are accumulating into — and
        /// <see cref="HasPendingRebake"/> reads true either way, since the tick's own
        /// <see cref="Update"/> starts a fresh task right after the correction. Only identity
        /// separates "the task survived" from "a different task exists now".</para>
        /// </summary>
        public int TaskId { get; private set; }
        private int _nextTaskId = 1;

        /// <summary>
        /// Whether the per-frame heartbeat is already subscribed, and how many places tried.
        ///
        /// <para>The guard lives here rather than at the call sites because a host usually has
        /// several and they do not know about each other — a session may wire from world init AND
        /// from game start, and a client gets its door again on every reconnect. Subscribing twice is
        /// not a louder version of subscribing once: it spends the frame budget N times per frame,
        /// which turns the feature into the spike it exists to remove, and it does so silently — the
        /// mesh is identical either way.</para>
        ///
        /// <para>The driver does not subscribe itself. It would have to know the host's engine type,
        /// and this layer deliberately does not.</para>
        /// </summary>
        public bool SliceHeartbeatWired { get; private set; }

        /// <summary>What makes the guard observable. Attempts ≥ 2 with exactly one subscription IS
        /// the evidence.</summary>
        public int HeartbeatClaimAttempts { get; private set; }

        /// <summary>True on the first call only. Every later caller is told the seat is taken.</summary>
        public bool TryClaimSliceHeartbeat()
        {
            HeartbeatClaimAttempts++;
            if (SliceHeartbeatWired)
                return false;
            SliceHeartbeatWired = true;
            return true;
        }

        // ── frame pacing ────────────────────────────────────────────────────────

        /// <summary>
        /// Advances an in-flight rebake by one frame's worth of work. Wire to whatever the host calls
        /// a frame boundary.
        ///
        /// <para><b>Per frame, not per tick.</b> A catching-up client runs many ticks in one frame, so
        /// a tick-paced budget would spend eleven frames' work in one and stall the frame it was meant
        /// to protect. The delay is denominated in ticks and the work in frames; they are deliberately
        /// different clocks.</para>
        ///
        /// <para>Nothing here touches frame state, and nothing here decides anything. The mesh is
        /// installed by <see cref="Update"/> on the tick the frame says, whether this ran or not — if
        /// the slices did not finish in time, the boundary finishes the job synchronously and the
        /// result is the same mesh. That is what keeps a peer-local, wall-clock-paced input out of the
        /// deterministic path.</para>
        /// </summary>
        public void AdvanceSlice(float deltaTime)
        {
            if (_task == null || _taskDone)
                return;
            SlicedFrames++;
            try
            {
                StepFaultForTests?.Invoke();
                _taskDone = _task.Step(_sliceBudgetUnits);
            }
            catch (System.Exception e)
            {
                // Slicing is SKIPPABLE by design — with no task the boundary rebuilds synchronously
                // and produces the same mesh, which is the invariant slicing was built on. So the
                // repair is to drop the task, and dropping it is strictly better than the two things
                // that happen if we do not.
                //
                // Leaving it is what the caller-side catch used to do (a subscriber's throw is
                // swallowed by the frame-boundary event), and it leaves the task MID-PHASE with its
                // pool held: `_taskDone` is assigned FROM Step, so a throw skips the assignment and
                // the flag stays false. The boundary then re-drives that same task through
                // `while (!_taskDone) Step(int.MaxValue)` and gets one of two outcomes — the same
                // throw again, which escapes the tick (nothing on that path catches it) and stops
                // `frame.Tick++` so the tick re-executes forever; or a resumed walk over corrupted
                // intermediate state, which yields a mesh no other peer has while the state hash
                // still matches.
                //
                // Caught here rather than at the caller for three reasons: only the driver owns the
                // task and the pool, so only the driver can drop them correctly; every caller is
                // covered at once (the engine, a game that still wires this by hand, tests); and the
                // engine stays ignorant of rebake failure semantics.
                DiscardTask();
                SliceFaults++;
                _lastSliceFault = e;
            }
        }

        /// <summary>
        /// How many slices ended in an exception. Must be 0 — a non-zero value does not break
        /// consistency (the boundary rebuilds synchronously and installs the same mesh) but it means
        /// a core defect, so it is worth reading in a live match.
        /// </summary>
        public int SliceFaults { get; private set; }

        /// <summary>
        /// DEBUG-only fault injection for the slice path. There is no other way in: <c>Step</c> is
        /// the rebaker's and a test cannot make real geometry throw on demand.
        /// </summary>
        internal System.Action StepFaultForTests;

        /// <summary>
        /// The exception the last dropped slice carried, waiting for a frame to report it through.
        ///
        /// <para><see cref="AdvanceSlice"/> runs on the FRAME boundary and has no
        /// <c>Frame</c> — so it has no logger, and every other error this type raises goes through
        /// <c>frame.Logger</c>. Rather than take a logger in the constructor (a public API change for
        /// a diagnostic), the fault is parked here and the next <see cref="Update"/> reports it in
        /// the tick context the other errors share.</para>
        /// </summary>
        private System.Exception _lastSliceFault;

        // ── the pump ────────────────────────────────────────────────────────────

        public void Update(ref Frame frame)
        {
            if (_slots == null)
                return;

            // A slice that threw was dropped on the frame boundary, where there is no logger. Report
            // it here, in the same tick context as every other error this type raises.
            if (_lastSliceFault != null)
            {
                System.Exception fault = _lastSliceFault;
                _lastSliceFault = null;
                frame.Logger?.KError(
                    $"[FPNavMeshRebakeDriver] a sliced rebake threw and was dropped at tick={frame.Tick} " +
                    $"(sliceFaults={SliceFaults}). Consistency is intact — the boundary rebuilds " +
                    $"synchronously and installs the same mesh — but this is a core defect, not a " +
                    $"recoverable condition. {fault}");
            }

            SurveyFrame(ref frame);

            // ── the mesh: derived state, and skippable ──────────────────────────
            CorrectNow(ref frame, countAsCorrection: !_isBoundary, surveyed: true);

            // ── the reseed: a HASHED FRAME WRITE, and not skippable ─────────────
            // Gated on the boundary alone, which is a pure function of frame state, so every
            // execution of this tick performs it — including every re-execution after a rollback.
            //
            // It used to ride along inside the swap, and that was a real divergence found by four
            // live matches. The install is gated on peer-local state that does NOT roll back: once a
            // boundary had run, a rollback landing on or after it re-executed the tick with the tag
            // already matching, the whole block was skipped, and the frame kept the triangle index it
            // had from BEFORE the swap. The authority, which never rolls back, reseeded once and kept
            // the right value.
            //
            // The invariant to hold on to: derived state may be skipped when it is already right;
            // frame state may not, because "already right" is a question about THIS peer's history
            // and the frame does not know it.
            if (_isBoundary)
            {
                _installer.Reseed(ref frame);
                Reseeds++;
            }

            // Every tick, not just boundaries: the due predicate is <=, and the point of the looser
            // predicate is that something has to look for a straggler.
            _source.DestroyDue(ref frame, frame.Tick);

            StartOrKeepPendingTask(ref frame);
        }

        /// <summary>
        /// Re-derives the installed mesh from the frame, right now.
        ///
        /// <para>For the two moments <see cref="Update"/> cannot cover. One is world init: the
        /// initial full-state's nav fingerprint is sampled before tick 0 runs, so a peer that has not
        /// installed anything yet reports a mismatch against a peer that has. The other is
        /// immediately after a full state is applied, where the receive path compares fingerprints
        /// synchronously — waiting for the next tick is too late, and that report is one-shot, so a
        /// false one consumes the report a real divergence would have needed.</para>
        ///
        /// <para>Never writes frame state: it installs and nothing else. That is what lets it run on
        /// a tick whose first execution installed nothing.</para>
        /// </summary>
        public void CorrectNow(ref Frame frame) => CorrectNow(ref frame, true, surveyed: false);

        private void CorrectNow(ref Frame frame, bool countAsCorrection, bool surveyed)
        {
            if (_slots == null)
                return;
            if (!surveyed)
                SurveyFrame(ref frame);

            long expected = DigestAt(frame.Tick);
            if (_installed && expected == _installedTag)
                return;

            // ① the rebake already in flight for this set. First because it is finished or nearly so
            // and because installing it anchors that context's patch chain to the live mesh — not
            // because it is cheaper than a hit, which it is not.
            MeshSlot taskSlot = _taskSlot;
            FPNavMesh mesh = TakeTask(expected);
            if (mesh != null)
            {
                Install(ref frame, mesh, taskSlot, _taskKey, _taskKeyCount, expected,
                    countAsCorrection);
                return;
            }

            // Built once and used twice: to answer the cache exactly, and — on a miss — as the
            // rebake input.
            int count = BuildActiveSet(ref frame, frame.Tick);

            // ② a mesh this driver already built for exactly this set.
            MeshSlot cached = FindCached(_active, count);
            if (cached != null)
            {
                CacheHits++;
                cached.LastUsed = ++_useClock;

                // No CommitSwap. Nothing was produced, so there is nothing to announce and nothing to
                // retire — and calling it would throw, since this mesh is not that context's most
                // recent output. That is also why a hit costs no rebake: the mesh never left the slot
                // that owns it.
                _installer.Install(ref frame, cached.Mesh);
                _installedSlot = cached;

                if (countAsCorrection)
                    Corrections++;
                _installedTag = expected;
                _installed = true;
                return;
            }

            // ③ neither — build it.
            CacheMisses++;
            MeshSlot slot = SlotForRebuild();
            mesh = Rebuild(ref frame, slot, count);
            if (mesh == null)
                return;
            Install(ref frame, mesh, slot, _active, count, expected, countAsCorrection);
        }

        private void Install(
            ref Frame frame, FPNavMesh mesh, MeshSlot slot,
            FPBuildingPlacement[] key, int keyCount, long expected, bool countAsCorrection)
        {
            // Swap FIRST, commit after — the order the rebaker's contract spells out. CommitSwap does
            // not install `mesh`, it RETIRES the one `mesh` replaces, and a retired mesh's arrays go
            // back to the pool for the next rebake to borrow. Committing first leaves the still-live
            // mesh recycled for the width of one statement, and if the install throws it stays
            // recycled for good — the nav system still pointing at storage the next rebake will
            // overwrite. This way a throwing install merely leaves the mesh uncommitted, which the
            // next rebake self-heals.
            _installer.Install(ref frame, mesh);
            CommitInto(slot, mesh, key, keyCount, expected);

            if (countAsCorrection)
                Corrections++;
            _installedTag = expected;
            _installed = true;
        }

        /// <summary>
        /// Commits the mesh to its slot's context and makes it that slot's cache entry — one statement
        /// apart, and that adjacency is load-bearing.
        ///
        /// <para><c>CommitSwap</c> retires this context's PREVIOUS mesh, and that mesh is what
        /// <see cref="MeshSlot.Mesh"/> is currently pointing at. Retire it without replacing the entry
        /// in the same breath and the next hit hands out storage the pool has already given away: in
        /// DEBUG the mesh throws on the first read, in Release the next rebake quietly overwrites it
        /// under the agents. Nothing about the tag or the key protects against that — it is a lifetime
        /// bug, not an identity one, so it would survive a perfect cache key. This is the only place
        /// the two may be written.</para>
        /// </summary>
        private void CommitInto(
            MeshSlot slot, FPNavMesh mesh, FPBuildingPlacement[] key, int keyCount, long tag)
        {
            slot.Context.CommitSwap(mesh);
            slot.Mesh = mesh;
            System.Array.Copy(key, slot.Key, keyCount);
            slot.KeyCount = keyCount;
            slot.Tag = tag;
            slot.LastUsed = ++_useClock;
            _installedSlot = slot;
        }

        /// <summary>
        /// The self-heal path: no slot holds what the frame says, so build it. Slower, never wrong —
        /// this is what covers a joiner, a spectator, a seek, and a cache eviction with one mechanism
        /// instead of four hooks.
        ///
        /// <para><b>It touches the task only when it has to.</b> <see cref="SlotForRebuild"/> hands
        /// back a slot the task is not holding whenever there is more than one, which removes the
        /// conflict instead of resolving it — and removing it is most of the point, since the discard
        /// is what kept a predicting client's slices from ever accumulating. With a single slot there
        /// is no other slot to hand back, so the discard below is the only thing between a
        /// synchronous rebake and the pool a slice is holding: one pool refuses overlapping use, and
        /// that refusal is DEBUG-only, so in a release build the corruption would be silent.</para>
        /// </summary>
        private FPNavMesh Rebuild(ref Frame frame, MeshSlot slot, int count)
        {
            // At the USE site rather than at the slot choice: it then holds for a single-slot driver
            // and for any later caller of this method, and the choice stays free of side effects.
            if (_task != null && ReferenceEquals(_taskSlot, slot))
                DiscardTask();

            RebuildInstalls++;
            try
            {
                if (!FPNavMeshRebaker.TryRebakePlacements(
                        slot.Context, _active, out FPNavMesh mesh, out _,
                        frame.Logger, _rules, count))
                    return null;
                return mesh;
            }
            catch (System.ArgumentException e)
            {
                frame.Logger?.KError(
                    $"[FPNavMeshRebakeDriver] rebuild failed at tick={frame.Tick}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Keeps a rebake in flight for the NEXT boundary, so the frames between a placement and its
        /// effective tick are where the work happens instead of all of it landing on one tick.
        ///
        /// <para>Started from frame state and identified by digest, which is what makes it survive
        /// prediction: after a rollback the frame may describe a different next boundary, and then the
        /// digest no longer matches and the task is dropped. It is never consulted for what to install
        /// — only ever offered as a mesh someone else already decided to want.</para>
        /// </summary>
        private void StartOrKeepPendingTask(ref Frame frame)
        {
            if (_nextBoundary == int.MaxValue)
            {
                DiscardTask();      // nothing pending — do not hold a pool for a set nobody wants
                return;
            }

            long wanted = DigestAt(_nextBoundary);

            // Already built. Slicing toward a mesh that exists spends the whole budget for nothing,
            // and it is the COMMON case rather than a corner: a client crosses each boundary many
            // times, and every crossing after the first asks for a set some slot already holds.
            // Without this the task is restarted on every one of them and consumed again by the
            // boundary — and because the task is offered before the cache, the restarted task would
            // keep winning and the cache would never get to answer.
            //
            // Digest only, deliberately. This decides whether to BUILD, never which mesh to install,
            // so a collision here costs one synchronous rebake at the boundary — exactly what
            // happened before the cache existed — while the install path still verifies the set
            // element by element.
            if (IsCachedTag(wanted))
            {
                DiscardTask();
                return;
            }

            if (_task != null && _taskTag == wanted)
                return;             // already building exactly this

            DiscardTask();

            int count = BuildActiveSet(ref frame, _nextBoundary);
            MeshSlot slot = SlotForTask();
            try
            {
                if (!FPNavMeshRebaker.TryBeginRebakePlacements(
                        slot.Context, _active, out _task, out _,
                        frame.Logger, _rules, count))
                {
                    // Refused. Not an error here: the set was validated when the placement was
                    // accepted, and the boundary will refuse it identically and log there.
                    _task = null;
                    return;
                }
            }
            catch (System.ArgumentException)
            {
                _task = null;
                return;
            }
            _taskSlot = slot;
            _taskTag = wanted;
            _taskDone = false;
            TaskId = _nextTaskId++;
            System.Array.Copy(_active, _taskKey, count);
            _taskKeyCount = count;
        }

        /// <summary>
        /// The in-flight mesh, if it happens to be the one wanted — finishing it here when the slices
        /// did not get there in time. Returns null when it was built for a different set, and then the
        /// caller rebuilds; identical output either way, which is the property that lets the budget be
        /// a guess.
        /// </summary>
        private FPNavMesh TakeTask(long wantedTag)
        {
            if (_task == null || _taskTag != wantedTag)
                return null;

            if (!_taskDone)
            {
                BoundaryFinishes++;     // the slices did not get there; pay the remainder now
                while (!_taskDone)
                    _taskDone = _task.Step(int.MaxValue);
            }

            FPNavMesh mesh = _task.Result;
            if (mesh == null)               // refused mid-flight — let the caller take the slow path
            {
                DiscardTask();
                return null;
            }
            _task.Install();                // announces to the context; only now may it patch from this
            _task = null;
            TaskInstalls++;
            return mesh;
        }

        private void DiscardTask()
        {
            if (_task == null)
                return;
            _task.Discard();
            _task = null;
            _taskDone = false;
        }

        // ── slot choice ─────────────────────────────────────────────────────────

        /// <summary>
        /// Where the next boundary's mesh gets built: the slot NOT holding what is installed now.
        ///
        /// <para>This is what keeps the pair. Installing the task's mesh commits its context, and a
        /// commit retires that context's previous mesh — so building in the slot that holds the
        /// current set would destroy exactly the mesh the next rollback comes back for. Building in
        /// the other one retires something older instead, and the slots end up holding the two sets
        /// the boundary bounces between.</para>
        /// </summary>
        private MeshSlot SlotForTask()
            => _installedSlot != null ? Other(_installedSlot) : SlotForRebuild();

        /// <summary>
        /// Where a miss builds. Not simply the LRU: a task holds its context's pool for as long as it
        /// lives and the pool refuses overlapping use, so a rebuild while one is in flight has to go
        /// to another slot.
        ///
        /// <para>That constraint is the reason for the second context as much as the cache is. With
        /// one, the only way to rebuild during a task was to kill the task — and that is what held a
        /// predicting client's slices to one per task, because a correction happens several times per
        /// boundary and each one killed the task the slices were accumulating into.</para>
        ///
        /// <para>The forced choice can evict the partner the LRU would have kept. That costs an extra
        /// miss and cannot cost anything else, because a slot's mesh and its key are written in the
        /// same statement as the commit that retires the old one.</para>
        /// </summary>
        private MeshSlot SlotForRebuild()
        {
            if (_task != null && _taskSlot != null && _slots.Length > 1)
                return Other(_taskSlot);

            MeshSlot lru = _slots[0];
            for (int i = 1; i < _slots.Length; i++)
                if (_slots[i].LastUsed < lru.LastUsed)
                    lru = _slots[i];
            return lru;
        }

        /// <summary>The least recently used slot that is not <paramref name="slot"/>. With one slot
        /// there is no other, and the caller has already checked.</summary>
        private MeshSlot Other(MeshSlot slot)
        {
            MeshSlot best = null;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (ReferenceEquals(_slots[i], slot))
                    continue;
                if (best == null || _slots[i].LastUsed < best.LastUsed)
                    best = _slots[i];
            }
            return best ?? slot;
        }

        /// <summary>Whether some slot was built for this digest. See the call site for why a digest is
        /// enough HERE and is not enough to pick a mesh.</summary>
        private bool IsCachedTag(long tag)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Mesh != null && _slots[i].Tag == tag)
                    return true;
            return false;
        }

        /// <summary>
        /// The slot built from exactly this set, or null. Compares the rebake input itself, so a hit
        /// means the mesh is the one a rebake would have produced rather than probably it.
        /// </summary>
        private MeshSlot FindCached(FPBuildingPlacement[] set, int count)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                MeshSlot s = _slots[i];
                if (s.Mesh != null && SameSet(s.Key, s.KeyCount, set, count))
                    return s;
            }
            return null;
        }

        /// <summary>
        /// Element-by-element equality of two rebake inputs. Both are sorted by Sequence, so equal
        /// sets compare equal position by position.
        ///
        /// <para>Sequence itself is deliberately NOT compared: the mesh is a function of the geometry,
        /// so two sets that place the same shapes in the same spots under different numbers must hit.
        /// Comparing it would only turn hits into misses — and comparing it INSTEAD of the geometry
        /// would be the actual bug, because a rollback can hand the same Sequence to a different
        /// placement.</para>
        /// </summary>
        private static bool SameSet(
            FPBuildingPlacement[] a, int aCount, FPBuildingPlacement[] b, int bCount)
        {
            if (aCount != bCount)
                return false;
            for (int i = 0; i < aCount; i++)
            {
                ref readonly var x = ref a[i];
                ref readonly var y = ref b[i];
                if (x.ShapeId != y.ShapeId || x.Orientation != y.Orientation
                    || x.CentreX.RawValue != y.CentreX.RawValue
                    || x.CentreZ.RawValue != y.CentreZ.RawValue
                    || x.Y.RawValue != y.Y.RawValue
                    // Retain belongs here for a harder reason than the fields above it. This
                    // comparison decides which CACHED MESH to install, and the cache is per-peer
                    // local history — so a mode left out here hands a carved mesh to a retain set
                    // on the peer that happens to hold one, while the peer that does not rebuilds
                    // correctly. The two navmeshes then differ with the state hash agreeing, which
                    // is the same silent shape the Sequence audit exists to prevent.
                    || x.Retain != y.Retain)
                    return false;
            }
            return true;
        }

        // ── derivation: one frame walk, everything else from the table ───────────

        /// <summary>
        /// The single frame walk. Fills the table and derives the two tick facts from it — whether
        /// this tick is a boundary, and which tick the next one is.
        ///
        /// <para>Doing it in one pass is not a micro-optimisation, it is what makes the seam cheaper
        /// than what it replaces: the shape it replaced asked the frame four separate questions per
        /// tick (the digest, the boundary predicate, the next boundary, the due set) and walked the
        /// same components for each. The sort is the only part that is not free, and it happens only
        /// when the digest says the set changed.</para>
        /// </summary>
        private void SurveyFrame(ref Frame frame)
        {
            _tableCount = FPNavMeshPlacementTableOps.Collect(_source, ref frame, _table);
            FPNavMeshPlacementTableOps.DeriveBoundaries(
                _table, _tableCount, frame.Tick, out _isBoundary, out _nextBoundary);
        }

        /// <summary>
        /// Identifies the active set at a tick, from the table. Order-independent by construction: it
        /// sums per-entry terms, so it does not depend on how the game happened to enumerate.
        ///
        /// <para>A DIGEST, and used only to answer "did it change". It is a sum, so collisions are
        /// easy to construct, and that is tolerable exactly because a collision here costs one skipped
        /// comparison that the next tick repeats. It is never used to choose a mesh — see
        /// <see cref="SameSet"/>, which compares the set itself.</para>
        /// </summary>
        private long DigestAt(int atTick)
            => FPNavMeshPlacementTableOps.Digest(_table, _tableCount, atTick);

        /// <summary>
        /// Fills <c>_active</c> with the set live at a tick, sorted by Sequence, and returns the count.
        /// This is the rebake input and the cache key.
        /// </summary>
        private int BuildActiveSet(ref Frame frame, int atTick)
            => FPNavMeshPlacementTableOps.BuildActiveSet(
                ref frame, _table, _tableCount, atTick, _active, _sortKeys,
                skipSequence: -1, out _);
    }
}
