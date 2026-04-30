using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Persisted credentials for cold-start Reconnect (app-restart recovery).
    /// Saved at Phase = Playing entry and discarded on session end / reject / expiry / build-version mismatch.
    /// </summary>
    [Serializable]
    public class PersistedReconnectCredentials
    {
        public string RemoteAddress;
        public int RemotePort;
        public long SessionMagic;
        public int LocalPlayerId;
        public long SavedAtUnixMs;
        public int ReconnectTimeoutMs;
        public string RoomName;
        public int RoomId = -1;     // SD multi-room routing identifier; -1 means single-room / P2P
        public string AppVersion;
        public string DeviceId;
    }

    /// <summary>
    /// Persistence interface for cold-start Reconnect credentials.
    /// Decouples NetworkService / KlothoConnection from any Unity-specific storage backend.
    /// Production: PlayerPrefsReconnectCredentialsStore (Unity asmdef).
    /// Tests: InMemoryReconnectCredentialsStore.
    /// </summary>
    public interface IReconnectCredentialsStore
    {
        /// <summary>
        /// Persist the credentials. Overwrites any previous value.
        /// </summary>
        void Save(PersistedReconnectCredentials creds);

        /// <summary>
        /// Returns the stored credentials, or null if none.
        /// </summary>
        PersistedReconnectCredentials Load();

        /// <summary>
        /// Discard any stored credentials.
        /// </summary>
        void Clear();

        /// <summary>
        /// Whether the supplied credentials are usable for cold-start Reconnect right now.
        /// Checks expiry (now - SavedAtUnixMs &lt;= ReconnectTimeoutMs) and build-version match (creds.AppVersion == currentAppVersion).
        /// </summary>
        bool IsValid(PersistedReconnectCredentials creds, long nowUnixMs, string currentAppVersion);
    }
}
