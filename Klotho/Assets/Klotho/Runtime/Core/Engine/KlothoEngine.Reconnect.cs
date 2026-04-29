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

        public bool HasCommand(int tick, int playerId)
        {
            return _inputBuffer.HasCommandForTick(tick, playerId);
        }

        #endregion
    }
}
