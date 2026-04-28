using System;
using System.Collections.Generic;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// JIT warmup registry. Pre-executes registered methods to reduce initial latency.
    /// </summary>
    public static class WarmupRegistry
    {
        private static readonly List<Action> _warmups = new List<Action>();
        private static readonly byte[] _dummyBuffer = new byte[4096];

        public static void RegisterCommand<T>(int typeId) where T : CommandBase, new()
        {
            _warmups.Add(() =>
            {
                var cmd = CommandPool.Get<T>();
                int size = cmd.GetSerializedSize();
                if (size > 0 && size <= _dummyBuffer.Length)
                {
                    var writer = new SpanWriter(_dummyBuffer);
                    cmd.Serialize(ref writer);
                }
                CommandPool.Return(cmd);
            });
        }

        public static void RegisterEvent<T>() where T : SimulationEvent, new()
        {
            _warmups.Add(() =>
            {
                var dispatcher = new EventDispatcher(null, 0);
                var evt = new T();
                Action<int, T> dummy = static (_, _) => { };
                dispatcher.Dispatch<T>(dummy, 0, evt, "warmup");
                evt.GetContentHash();
            });
        }

        public static void RegisterDataAsset(int typeId)
        {
            _warmups.Add(() =>
            {
                if (!ECS.DataAssetTypeRegistry.IsRegistered(typeId))
                    return;

                // Serialize path warmup: create dummy instance → Serialize → Deserialize
                var writer = new SpanWriter(_dummyBuffer);
                // Warm up deserialization with a dummy AssetId=0
                writer.WriteInt32(0); // AssetId placeholder
                var reader = new SpanReader(_dummyBuffer.AsSpan(0, 4));
                try { ECS.DataAssetTypeRegistry.Deserialize(typeId, ref reader); } catch { }
            });
        }

        public static void RegisterMessage<T>() where T : NetworkMessageBase, new()
        {
            _warmups.Add(() =>
            {
                var msg = new T();
                int size = msg.GetSerializedSize();
                if (size > 0 && size <= _dummyBuffer.Length)
                {
                    var writer = new SpanWriter(_dummyBuffer);
                    msg.Serialize(ref writer);
                    var reader = new SpanReader(_dummyBuffer.AsSpan(0, size));
                    msg.Deserialize(ref reader);
                }
            });
        }

        public static void RunAll()
        {
#if !ENABLE_IL2CPP
            ModuleInitializerHelper.EnsureAll();

            foreach (var warmup in _warmups)
                warmup();
#endif
        }
    }
}
