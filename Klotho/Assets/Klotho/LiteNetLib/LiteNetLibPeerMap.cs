using System;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;
using LiteNetLib;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// peerId ↔ NetPeer bidirectional mapping.
    /// </summary>
    public class LiteNetLibPeerMap
    {
        ILogger _logger;
        ConcurrentDictionary<int, LiteNetPeer> _idToPeer = new(Environment.ProcessorCount, 64);
        ConcurrentDictionary<LiteNetPeer, int> _peerToId = new(Environment.ProcessorCount, 64);

        public LiteNetLibPeerMap(ILogger logger)
        {
            _logger = logger;
        }

        internal int Register(LiteNetPeer peer)
        {
            if (!_idToPeer.TryAdd(peer.Id, peer))
            {
                _logger?.ZLogError($"[LiteNetLibPeerMap] Peer already registered id={peer.Id}");
                return -1;
            }

            if (!_peerToId.TryAdd(peer, peer.Id))
            {
                _idToPeer.TryRemove(peer.Id, out _);
                _logger?.ZLogError($"[LiteNetLibPeerMap] Peer object already registered (id={peer.Id})");
                return -1;
            }

            return peer.Id;
        }

        internal void Unregister(LiteNetPeer peer)
        {
            if (!_idToPeer.TryRemove(peer.Id, out _))
            {
                _logger?.ZLogWarning($"[LiteNetLibPeerMap] Unregister failed: id={peer.Id} not found in _idToPeer");
            }

            if (!_peerToId.TryRemove(peer, out _))
            {
                _logger?.ZLogWarning($"[LiteNetLibPeerMap] Unregister failed: peer object (id={peer.Id}) not found in _peerToId");
            }
        }

        internal bool TryGetPeer(int id, out LiteNetPeer peer)
        {
            return _idToPeer.TryGetValue(id, out peer);
        }

        internal bool TryGetId(LiteNetPeer peer, out int id)
        {
            return _peerToId.TryGetValue(peer, out id);
        }

        internal void Clear()
        {
            _idToPeer.Clear();
            _peerToId.Clear();
        }
    }
}