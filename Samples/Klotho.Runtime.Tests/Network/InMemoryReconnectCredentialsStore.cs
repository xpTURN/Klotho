using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// In-memory mock of IReconnectCredentialsStore for unit tests.
    /// Avoids PlayerPrefs/Unity runtime dependency.
    /// </summary>
    public class InMemoryReconnectCredentialsStore : IReconnectCredentialsStore
    {
        private PersistedReconnectCredentials _stored;

        public void Save(PersistedReconnectCredentials creds)
        {
            _stored = creds;
        }

        public PersistedReconnectCredentials Load()
        {
            return _stored;
        }

        public void Clear()
        {
            _stored = null;
        }

        public bool IsValid(PersistedReconnectCredentials creds, long nowUnixMs, string currentAppVersion)
        {
            if (creds == null)
                return false;
            if (creds.AppVersion != currentAppVersion)
                return false;
            if (nowUnixMs - creds.SavedAtUnixMs > creds.ReconnectTimeoutMs)
                return false;
            return true;
        }
    }
}
