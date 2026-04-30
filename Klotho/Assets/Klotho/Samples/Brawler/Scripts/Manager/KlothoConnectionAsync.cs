using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Samples.Brawler
{
    /// <summary>
    /// Samples utility that wraps KlothoConnection (callback-based Runtime API) with UniTask.
    /// Runtime asmdef does not reference UniTask, so it is provided in the Samples layer.
    ///
    /// Scope: Normal Join (ConnectAsync), Late Join (same Connect path during Playing),
    /// cold-start Reconnect (ReconnectAsync).
    /// warm reconnect (process-survival reconnect) is handled in KlothoNetworkService directly.
    /// </summary>
    public static class KlothoConnectionAsync
    {
        /// <summary>
        /// Connects to the host, completes handshake + SimulationConfig reception, then returns ConnectionResult.
        /// Throws Exception on failure, OperationCanceledException on cancellation.
        /// preJoinMessage: leading message to send before PlayerJoinMessage (e.g., RoomHandshakeMessage for SD multi-room).
        ///   If null, only PlayerJoinMessage is sent (P2P / SD single room).
        /// </summary>
        public static UniTask<ConnectionResult> ConnectAsync(
            INetworkTransport transport, string host, int port,
            CancellationToken ct = default, ILogger logger = null,
            NetworkMessageBase preJoinMessage = null,
            IDeviceIdProvider deviceIdProvider = null)
        {
            var tcs = new UniTaskCompletionSource<ConnectionResult>();

            var connection = KlothoConnection.Connect(
                transport, host, port,
                onCompleted: result => tcs.TrySetResult(result),
                onFailed: reason => tcs.TrySetException(new Exception(reason)),
                logger: logger,
                preJoinMessage: preJoinMessage,
                deviceIdProvider: deviceIdProvider);

            // On ct cancellation, clean up connection + cancel TCS
            var ctRegistration = ct.Register(() =>
            {
                connection?.Dispose();
                tcs.TrySetCanceled();
            });

            // Call Update every frame — terminates on IsCompleted or ct cancellation
            PumpAsync(connection, ct, ctRegistration).Forget();

            return tcs.Task;
        }

        /// <summary>
        /// Cold-start Reconnect wrapper. Connects to creds.RemoteAddress:RemotePort and presents
        /// the persisted credentials. On reject (reason 1/2/3/4) or timeout, throws Exception with the
        /// reason string ("Reconnect rejected: InvalidMagic" / "AlreadyConnected" / etc.).
        /// </summary>
        public static UniTask<ConnectionResult> ReconnectAsync(
            INetworkTransport transport, PersistedReconnectCredentials creds,
            CancellationToken ct = default, ILogger logger = null)
        {
            var tcs = new UniTaskCompletionSource<ConnectionResult>();

            var connection = KlothoConnection.Reconnect(
                transport, creds,
                onCompleted: result => tcs.TrySetResult(result),
                onFailed: reason => tcs.TrySetException(new Exception(reason)),
                logger: logger);

            var ctRegistration = ct.Register(() =>
            {
                connection?.Dispose();
                tcs.TrySetCanceled();
            });

            PumpAsync(connection, ct, ctRegistration).Forget();

            return tcs.Task;
        }

        private static async UniTaskVoid PumpAsync(
            KlothoConnection connection,
            CancellationToken ct,
            CancellationTokenRegistration ctRegistration)
        {
            try
            {
                while (!connection.IsCompleted && !ct.IsCancellationRequested)
                {
                    connection.Update();
                    await UniTask.Yield(PlayerLoopTiming.Update, ct).SuppressCancellationThrow();
                }
            }
            finally
            {
                ctRegistration.Dispose();
            }
        }
    }
}
