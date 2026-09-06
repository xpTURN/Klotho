using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using System.IO;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Replay system
        private ReplaySystem _replaySystem;
        private bool _isReplayMode;

        /// <summary>
        /// Replay system instance.
        /// </summary>
        public IReplaySystem ReplaySystem => _replaySystem;

        /// <summary>
        /// Whether the engine is currently in replay playback mode.
        /// </summary>
        public bool IsReplayMode => _isReplayMode;

        #region Replay Methods

        /// <summary>
        /// How playback obtains tick 0.
        /// </summary>
        public enum ReplayInitialState
        {
            /// <summary>Restore the recorded snapshot. The default, and what a viewer wants.</summary>
            RestoreSnapshot,

            /// <summary>
            /// Rebuild tick 0 from the recorded roster, seed and config instead of trusting the recorded
            /// bytes, then report whether it matches the recorded hash. For a verifier: the snapshot in a
            /// file is the client's own claim about where the match started, and re-simulating from it
            /// proves nothing about that starting point. Requires game callbacks (OnInitializeWorld runs)
            /// and a recorded roster; without a roster this falls back to RestoreSnapshot.
            /// </summary>
            Reconstruct,
        }

        /// <summary>Whether the last <see cref="StartReplay(IReplayData, ReplayInitialState)"/> actually
        /// rebuilt tick 0 (false = the snapshot was restored, including the roster-less fallback).</summary>
        public bool ReplayTick0Reconstructed { get; private set; }

        /// <summary>Hash of the tick-0 state playback ended up with — compare against
        /// <c>IReplayMetadata.InitialStateHash</c>. Meaningful in both modes.</summary>
        public long ReplayTick0Hash { get; private set; }

        /// <summary>
        /// Hash of the state the simulation holds right now, folded exactly the way
        /// <see cref="ReplayTick0Hash"/> is — so the two are comparable, and so a value taken after
        /// playback covers every component the match result does not project.
        ///
        /// <para>Read it to compare two runs of the SAME file. It is a DIFFERENTIAL signal, not a claim
        /// check: no end hash is recorded, so there is nothing in the file to check it against. What it
        /// buys is the case the result comparison is blind to — a re-simulation that diverged mid-match
        /// can still arrive at the same winner, and then a broken playback verifies green.</para>
        /// </summary>
        public long CurrentStateHash => _simulation?.GetStateHash() ?? 0;

        /// <summary>
        /// Starts replay playback.
        /// </summary>
        public void StartReplay(IReplayData replayData) => StartReplay(replayData, ReplayInitialState.RestoreSnapshot);

        /// <summary>
        /// Starts replay playback, choosing how tick 0 is obtained.
        /// </summary>
        public void StartReplay(IReplayData replayData, ReplayInitialState initialState)
        {
            if (replayData == null)
            {
                _logger?.KError($"[KlothoEngine][Replay] Cannot start replay: null replay data");
                return;
            }

            _isReplayMode = true;
            _randomSeed = replayData.Metadata.RandomSeed;

            // Reset state
            CurrentTick = 0;
            _lastVerifiedTick = -1;
            _accumulator = 0;
            _inputBuffer.Clear();

            // Initialize simulation with the replay seed
            _simulation.Initialize();

            // Per-player verified data the recording carried. Loaded before tick 0 either way: the rebuild
            // reads it while seeding, and the restore path needs it from the first join tick onward.
            LoadReplayEntitlements(replayData.Metadata);

            // Tick 0: rebuild it, or restore what the recording carried.
            var roster = replayData.Metadata.InitialRoster;
            bool reconstruct = initialState == ReplayInitialState.Reconstruct && roster != null && roster.Count > 0;

            if (initialState == ReplayInitialState.Reconstruct && !reconstruct)
            {
                // Not an error: a recording that did not build tick 0 carries no roster (an SD client's
                // initial state came from the server). Falling back keeps playback working; the caller is
                // told so it can say which of the two it got.
                _logger?.KWarning(
                    $"[KlothoEngine][Replay] Reconstruct requested but the file carries no initial roster - falling back to the recorded snapshot. This recording did not build its own tick 0.");
            }

            if (reconstruct)
            {
                // Same construction the live path runs, through the same method — participants in the
                // recorded order, then seed, match-end, OnInitializeWorld, nav correction. Order is
                // state-hash input, which is why this calls BuildInitialWorld rather than repeating it.
                _activePlayerIds.Clear();
                for (int i = 0; i < roster.Count; i++)
                    _activePlayerIds.Add(roster[i]);

                BuildInitialWorld();
            }
            else
            {
                var snapshot = replayData.Metadata.InitialStateSnapshot;
                if (snapshot == null || snapshot.Length == 0)
                    throw new InvalidDataException(
                        "[Replay] InitialStateSnapshot missing - corrupted file or snapshot was not injected during recording");
                _simulation.RestoreFromFullState(snapshot);
            }

            ReplayTick0Reconstructed = reconstruct;
            ReplayTick0Hash = _simulation.GetStateHash();

            // The only window this check has: the world is built (so the local nav source is wired
            // and answers a real fingerprint), and nothing has been told the game started yet.
            // Later is too late -- Play() and OnGameStart are a few lines down, and refusing after
            // them leaves a half-started world behind.
            RefuseReplayOnNavMismatch(replayData.Metadata);

            // Save initial snapshot
            SaveSnapshot(0);

            // Load replay
            _replaySystem.Load(replayData, _logger);
            _replaySystem.OnTickPlayed += HandleReplayTick;
            _replaySystem.OnPlaybackFinished += HandleReplayFinished;

            State = KlothoState.Running;
            _replaySystem.Play();

            // Semantic symmetry with the normal start path - game code guards live-only behavior with IsReplayMode
            _viewCallbacks?.OnGameStart(this);
            OnGameStart?.Invoke();

            LogReplayReproductionContext(replayData.Metadata);

            _logger?.KInformation($"[KlothoEngine][Replay] started: {replayData.Metadata.TotalTicks} ticks, {replayData.Metadata.DurationMs}ms");
        }

        /// <summary>
        /// Refuses a replay recorded against DIFFERENT navigation - a different stage, or a build
        /// whose pathfinding plans other corridors (see
        /// <see cref="Deterministic.Navigation.FPNavAgentSystem.NAV_BEHAVIOUR_REVISION"/>). Without
        /// this the file loads and diverges mid-playback, and the format carries no per-tick hash
        /// to say where.
        ///
        /// <para><b>0 on either side is not a mismatch</b>, matching the sentinel every other
        /// fingerprint path uses. A local 0 is warned about rather than passed silently: on this
        /// path it usually means the check could not run (no nav source registered), which is
        /// otherwise indistinguishable from the gate not existing.</para>
        ///
        /// <para><b>Recordings that did not build their own tick 0 are warned, not refused.</b> The
        /// anchor is the mesh AT THE SNAPSHOT INSTANT, and an SD client that received its initial
        /// FullState mid-match anchored a REBAKED mesh; playback loads the base asset, so comparing
        /// them would reject a perfectly good file. <c>InitialStateTick != 0</c> marks exactly that
        /// case.</para>
        ///
        /// <para><b>This is attribution, not integrity.</b> The anchors are non-cryptographic folds
        /// and nothing is signed, so a forged file rewrites them along with the payload. What the
        /// refusal buys is dismissing an HONEST mismatch early instead of debugging a desync.</para>
        /// </summary>
        private void RefuseReplayOnNavMismatch(IReplayMetadata meta)
        {
            long recorded = meta.NavFingerprint;
            if (recorded == 0)
                return;

            long local = GetFingerprintBreakdown().Nav;
            if (local == 0)
            {
                _logger?.KWarning(
                    $"[KlothoEngine][Replay] this recording carries a navigation fingerprint " +
                    $"(0x{recorded:X16}) but this process reports none, so it was NOT checked. " +
                    $"Register the navigation system before starting playback to get the check.");
                return;
            }

            if (local == recorded)
                return;

            if (meta.InitialStateTick != 0)
            {
                _logger?.KWarning(
                    $"[KlothoEngine][Replay] navigation fingerprint differs " +
                    $"(recorded=0x{recorded:X16} local=0x{local:X16}) but this recording started " +
                    $"mid-match (tick={meta.InitialStateTick}), so its anchor describes a rebaked " +
                    $"mesh rather than the base asset. Not refused - the comparison does not apply.");
                return;
            }

            throw new InvalidDataException(
                $"[Replay] navigation fingerprint mismatch: recorded=0x{recorded:X16} " +
                $"local=0x{local:X16}. Either this replay was recorded on a different stage, or on " +
                $"a build whose navigation plans different corridors (NAV_BEHAVIOUR_REVISION). " +
                $"Re-record it, or play it back with the build and content it was recorded against.");
        }

        /// <summary>
        /// Reports what this replay was recorded against, so a divergence can be attributed rather than
        /// merely observed. The layout inputs are printed, not applied — the component layout is frozen
        /// process-wide long before a replay loads, so a mismatch here means "boot a process built this
        /// way", not "this run will fix itself".
        /// </summary>
        private void LogReplayReproductionContext(IReplayMetadata meta)
        {
            if (_logger == null) return;

            if (meta.InitialStateTick != 0)
            {
                _logger.KWarning(
                    $"[KlothoEngine][Replay] InitialStateSnapshot was taken at tick={meta.InitialStateTick}, but playback " +
                    $"starts its own clock at tick 0 while the restored frame carries the recorded tick. Command ticks and " +
                    $"progress are relative to the recorded lineage, so seek/progress on this file read off by that offset. " +
                    $"(Only SD clients that received their initial FullState mid-match produce this.)");
            }

            _logger.KInformation(
                $"[KlothoEngine][Replay] recorded against: layout=0x{meta.LayoutFingerprint:X16} " +
                $"colliders=0x{meta.StaticColliderFingerprint:X16} nav=0x{meta.NavFingerprint:X16} " +
                $"game=0x{meta.GameFingerprint:X16} (0 = not provided); snapshotHash=0x{meta.InitialStateHash:X16} " +
                $"tick={meta.InitialStateTick}; endReason={meta.EndReason}; " +
                $"prunedTypes={meta.PrunedComponentTypeIds.Count} maxCountOverrides={meta.ComponentMaxCountTypeIds.Count} " +
                $"(layout inputs are reported, NOT applied — the layout is already frozen)");

            if (meta.EndReason == ReplayEndReason.Unspecified)
            {
                _logger.KWarning(
                    $"[KlothoEngine][Replay] endReason is {ReplayEndReason.Unspecified} — whatever ended this recording " +
                    $"did not stamp one. A short replay cannot be told apart from a truncated one here.");
            }
        }

        /// <summary>
        /// Stops replay playback.
        /// </summary>
        public void StopReplay()
        {
            if (!_isReplayMode)
                return;

            _replaySystem.Stop();
            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _isReplayMode = false;
            State = KlothoState.Finished;

            _logger?.KInformation($"[KlothoEngine][Replay] stopped");
        }

        /// <summary>
        /// Pauses replay playback.
        /// </summary>
        public void PauseReplay()
        {
            if (_isReplayMode)
            {
                _replaySystem.Pause();
                State = KlothoState.Paused;
            }
        }

        /// <summary>
        /// Resumes replay playback.
        /// </summary>
        public void ResumeReplay()
        {
            if (_isReplayMode && State == KlothoState.Paused)
            {
                _replaySystem.Resume();
                State = KlothoState.Running;
            }
        }

        /// <summary>
        /// Sets the replay playback speed.
        /// </summary>
        public void SetReplaySpeed(ReplaySpeed speed)
        {
            _replaySystem.Speed = speed;
        }

        /// <summary>
        /// Seeks to a specific tick in the replay.
        /// </summary>
        public void SeekReplay(int tick)
        {
            if (!_isReplayMode)
                return;

            // Find the snapshot closest to the target tick via the simulation's own snapshot
            // history; with no history, replay seek re-simulates from tick 0.
            int startTick = 0;
            int nearest = _simulation.GetNearestRollbackTick(tick);
            if (nearest >= 0)
                startTick = nearest;

            _simulation.Rollback(startTick);

            // A backward seek must un-latch the Synced dispatch watermark so the
            // resumed HandleReplayTick re-dispatches Synced events at re-played ticks (mirror
            // Spectator.ResetToTick). GetNearestRollbackTick returns startTick <= tick, so
            // startTick - 1 < tick keeps the resumed tick above the lowered watermark.
            if (_syncedDispatchHighWaterMark >= startTick)
                _syncedDispatchHighWaterMark = startTick - 1;
            // Drop stale buffered events (pool-safe) and reset ring-wrap slot markers so the
            // resumed ClearTick does not false-fire the newer-occupant dev guard after a long
            // backward seek. Replay only plays forward from `tick` after a seek (no rollback that
            // reads earlier ticks), so wiping the whole buffer is safe.
            _eventBuffer.ClearAll();
            // ClearAll just pooled events still referenced by the collector's residue (the prior
            // HandleReplayTick leaves _collected populated). Drop those refs now so the empty-seek
            // path (startTick == tick, loop body never runs) holds no dangling pointers.
            _eventCollector.Clear();

            // Re-simulate from the nearest snapshot up to the target tick
            CurrentTick = startTick;
            var replayData = _replaySystem.CurrentReplayData;

            while (CurrentTick < tick && CurrentTick <= replayData.Metadata.TotalTicks)
            {
                var commands = replayData.GetCommandsForTick(CurrentTick);
                _tickCommandsCache.Clear();
                for (int i = 0; i < commands.Count; i++)
                    _tickCommandsCache.Add(commands[i]);
                // Open the collector so RaiseEvent stamps evt.Tick correctly and
                // any residue from the prior path is cleared (mirrors every other Tick path).
                // BeginTick is load-bearing here: it clears the entry residue before the drop loop,
                // so the loop never re-returns events already pooled by ClearAll above.
                _eventCollector.BeginTick(CurrentTick);
                _simulation.Tick(_tickCommandsCache);
                // Seek re-simulation is state-advance only — these events fall outside the resumed
                // dispatch range (HandleReplayTick re-runs from `tick`), so buffering them would
                // double-dispatch. Return them to the pool instead, or they leak (the next BeginTick
                // clears _collected with no pool return).
                for (int ei = 0; ei < _eventCollector.Count; ei++)
                    EventPool.Return(_eventCollector.Collected[ei]);
                _eventCollector.Clear();

                SaveSnapshot(CurrentTick);

                CurrentTick++;
            }

            _replaySystem.SeekToTick(tick);

            _logger?.KInformation($"[KlothoEngine][Replay] seek: tick={tick}");
        }

        /// <summary>
        /// Saves the current replay to a file.
        /// </summary>
        public void SaveReplayToFile(string filePath, bool dumpJson = false)
        {
            _replaySystem.SaveToFile(filePath, dumpJson);
        }

        /// <summary>
        /// Slices the file's roster-parallel per-player record into the table
        /// <see cref="GetPlayerEntitlement"/> falls back to when no network service can answer.
        ///
        /// <para>An empty record is normal — a match with no issuer really has none — so this simply leaves
        /// the table empty rather than treating it as a fault. A length list that disagrees with the roster
        /// never reaches here: the file layer refuses it as corruption.</para>
        /// </summary>
        private void LoadReplayEntitlements(IReplayMetadata meta)
        {
            _replayEntitlements = null;

            var roster = meta.InitialRoster;
            var lengths = meta.InitialEntitlementLengths;
            if (roster == null || lengths == null || lengths.Count == 0) return;

            var data = meta.InitialEntitlementData;
            int offset = 0;
            int count = System.Math.Min(lengths.Count, roster.Count);
            for (int i = 0; i < count; i++)
            {
                int len = lengths[i];
                if (len <= 0) continue;
                if (data == null || offset + len > data.Length) break;
                var bytes = new byte[len];
                System.Buffer.BlockCopy(data, offset, bytes, 0, len);
                SetReplayEntitlement(roster[i], bytes);
                offset += len;
            }
        }

        /// <summary>
        /// Gets the current replay data.
        /// </summary>
        public IReplayData GetCurrentReplayData()
        {
            return _replaySystem.CurrentReplayData;
        }

        /// <summary>
        /// Gets the random seed used for this game.
        /// </summary>
        public int GetRandomSeed()
        {
            return _randomSeed;
        }

        private void HandleReplayTick(int tick, System.Collections.Generic.IReadOnlyList<ICommand> commands)
        {
            // Save snapshot for seeking - per-tick save
            SaveSnapshot(tick);

            // Run the simulation with replay commands and collect events
            _tickCommandsCache.Clear();
            for (int i = 0; i < commands.Count; i++)
                _tickCommandsCache.Add(commands[i]);
            _eventCollector.BeginTick(tick);
            _simulation.Tick(_tickCommandsCache);

            // Store the collected events
            _eventBuffer.ClearTick(tick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(tick, _eventCollector.Collected[ei]);

            _lastVerifiedTick = tick;
            CurrentTick = tick + 1;
            OnTickExecuted?.Invoke(tick);
            _viewCallbacks?.OnTickExecuted(tick);
            OnTickExecutedWithState?.Invoke(tick, FrameState.Verified);
            OnFrameVerified?.Invoke(tick);

            // Dispatch all events as confirmed (replay = all verified)
            DispatchTickEvents(tick, FrameState.Verified);
        }

        private void HandleReplayFinished()
        {
            State = KlothoState.Finished;
            _isReplayMode = false;

            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _logger?.KInformation($"[KlothoEngine][Replay] playback finished");
        }

        #endregion
    }
}
