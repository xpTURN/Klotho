using System;
using System.Collections.Generic;

using ZLogger;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Reconnect: track disconnected players
        private readonly HashSet<int> _disconnectedPlayerIds = new HashSet<int>();
        public event Action<int> OnDisconnectedInputNeeded;

        private const int StackAllocThreshold = 256;

        #region Reconnect

        public void NotifyPlayerDisconnected(int playerId)
        {
            _disconnectedPlayerIds.Add(playerId);
        }

        public void NotifyPlayerReconnected(int playerId)
        {
            _disconnectedPlayerIds.Remove(playerId);
        }

        public void NotifyPlayerLeft(int playerId)
        {
            _activePlayerIds.Remove(playerId);
            _disconnectedPlayerIds.Remove(playerId);
            _logger?.ZLogTrace($"[KlothoEngine][Roster] PlayerLeft: playerId={playerId}, rosterCount={_activePlayerIds.Count}, CurrentTick={CurrentTick}");
        }

        public void PauseForReconnect()
        {
            _resyncState = ResyncState.Requested;
            _resyncRetryCount = 0;
            _resyncElapsedMs = 0f;
        }

        public void ForceInsertCommand(ICommand cmd)
        {
            int size = cmd.GetSerializedSize();
            Span<byte> buf = size <= StackAllocThreshold ? stackalloc byte[size] : new byte[size];
            var writer = new SpanWriter(buf);
            cmd.Serialize(ref writer);

            var cloned = _commandFactory.CreateCommand(cmd.CommandTypeId);
            var reader = new SpanReader(buf.Slice(0, writer.Position));
            cloned.Deserialize(ref reader);

            _inputBuffer.AddCommand(cloned);
        }

        // Range fill: inserts an empty command at each tick in [fromTick, toTickInclusive] for the
        // same playerId, seals each (tick, playerId) against late real overwrites, and triggers
        // TryAdvanceVerifiedChain once so the chain catches up in a single batch instead of
        // waiting for the next HandleCommandReceived. Uses the engine's command factory directly
        // — the caller does not need to manage per-tick empty instances.
        public void ForceInsertEmptyCommandsRange(int playerId, int fromTick, int toTickInclusive)
        {
            if (toTickInclusive < fromTick || _commandFactory == null) return;
            if (_engineEmptyCommandCache == null)
                _engineEmptyCommandCache = _commandFactory.CreateEmptyCommand();

            Span<byte> stackBuf = stackalloc byte[StackAllocThreshold];

            for (int t = fromTick; t <= toTickInclusive; t++)
            {
                if (_inputBuffer.HasCommandForTick(t, playerId))
                    continue;

                _commandFactory.PopulateEmpty(_engineEmptyCommandCache, playerId, t);

                int size = _engineEmptyCommandCache.GetSerializedSize();
                Span<byte> buf = size <= StackAllocThreshold ? stackBuf.Slice(0, size) : new byte[size];
                var writer = new SpanWriter(buf);
                _engineEmptyCommandCache.Serialize(ref writer);

                var cloned = _commandFactory.CreateCommand(_engineEmptyCommandCache.CommandTypeId);
                var reader = new SpanReader(buf.Slice(0, writer.Position));
                cloned.Deserialize(ref reader);

                _inputBuffer.AddCommand(cloned);
                _inputBuffer.SealEmpty(t, playerId);
            }

            // ForceInsert path does not auto-trigger chain advance — drive it here once after the
            // range is in place so 100+ tick batches catch up in a single while-loop.
            TryAdvanceVerifiedChain();
        }

        // Internal cache for ForceInsertEmptyCommandsRange — engine-owned to avoid coupling with
        // the network service's _emptyCommandCache (which is single-use per call).
        private ICommand _engineEmptyCommandCache;

        public bool HasCommand(int tick, int playerId)
        {
            return _inputBuffer.HasCommandForTick(tick, playerId);
        }

        public bool IsCommandSealed(int tick, int playerId)
        {
            return _inputBuffer.IsSealed(tick, playerId);
        }

        #endregion
    }
}
