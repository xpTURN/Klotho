namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Reason for <see cref="xpTURN.Klotho.Core.JoinFailedException"/>,
    /// raised on the guest Connect path (normal / late join). Game catch handlers should branch on
    /// Reason instead of parsing Message. Cancellation surfaces as OperationCanceledException, not a Reason.
    /// The underlying type is byte so the value can ride the wire via a cast; this enum itself is client-local.
    /// </summary>
    public enum JoinFailReason : byte
    {
        /// <summary>Local transport socket failed to start (e.g. port bind). Does not cover an unreachable host.</summary>
        TransportStartFailed = 1,
        /// <summary>Handshake did not complete within the connect timeout.</summary>
        TimedOut             = 2,
        /// <summary>Connection lost during handshake from a network failure (host down, unreachable, socket error).</summary>
        ConnectionLost       = 3,
        /// <summary>Connection rejected at the transport level (wrong connection key / protocol mismatch).</summary>
        Rejected             = 4,
        /// <summary>Host closed the connection during handshake.</summary>
        HostClosed           = 5,
        /// <summary>Other / unspecified failure.</summary>
        Unknown              = 6,

        // Server application-level join rejections, delivered via the disconnect packet payload.
        /// <summary>Server rejected: the requested room does not exist.</summary>
        RoomNotFound         = 7,
        /// <summary>Server rejected: the room is at player capacity.</summary>
        RoomFull             = 8,
        /// <summary>Server rejected: the server is at capacity.</summary>
        ServerFull           = 9,
        /// <summary>Server rejected: late join is not allowed for this room.</summary>
        LateJoinDisabled     = 10,
        /// <summary>Server rejected: the room is ending or not active.</summary>
        RoomClosing          = 11,

        // Identity validation rejections. Delivered via the disconnect packet payload (wire codes 6~11),
        // distinct from these enum values. Unlike RoomFull (which the game retries via a late join),
        // these mean the credential is bad or already spent — the game should re-acquire a lobby ticket
        // rather than blindly retrying with the same one.
        /// <summary>Identity rejected: ticket signature / format invalid.</summary>
        IdentityInvalid          = 12,
        /// <summary>Identity rejected: ticket expired.</summary>
        IdentityExpired          = 13,
        /// <summary>Identity rejected: ticket sessionId does not match this session.</summary>
        IdentitySessionMismatch  = 14,
        /// <summary>Identity rejected: lobby redeem denied (ban / sanction / nonce consumed).</summary>
        IdentityRejected         = 15,
        /// <summary>Identity rejected: a validator is configured but no ticket was provided.</summary>
        IdentityRequired         = 16,
        /// <summary>Identity rejected: validation timed out or errored.</summary>
        IdentityValidationFailed = 17,

        // Setup validation rejection. The peers' registered component-type sets differ, which is
        // state-hash input — they would diverge from tick 0, so the authority refuses before the match.
        /// <summary>Rejected: component-registry layout (registered type set) differs from the authority's.</summary>
        LayoutMismatch           = 18,
    }

    public static class JoinFailReasonExtensions
    {
        /// <summary>
        /// Maps an involuntary handshake disconnect reason to a JoinFailReason.
        /// LocalDisconnect is handled separately (not a failure) and never reaches here.
        /// </summary>
        public static JoinFailReason FromDisconnect(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.NetworkFailure:     return JoinFailReason.ConnectionLost;
                case DisconnectReason.ConnectionRejected: return JoinFailReason.Rejected;
                case DisconnectReason.RemoteDisconnect:   return JoinFailReason.HostClosed;
                default:                                  return JoinFailReason.Unknown;
            }
        }

        // Single source of truth for the server-reject subset of JoinFailReason and its disconnect-payload
        // wire byte. Both ToWireCode (server encode) and FromJoinReject (client decode) derive from this
        // table, so the two directions cannot silently diverge. The wire codes are a separate, compact
        // numbering (1~11) distinct from the enum's underlying values; 0 = Unknown/unmapped. Keep the code
        // list in JoinRejectMessage in sync with this table.
        private static readonly (JoinFailReason Reason, byte Wire)[] WireRejectMap =
        {
            (JoinFailReason.RoomNotFound,             1),
            (JoinFailReason.RoomFull,                 2),
            (JoinFailReason.ServerFull,               3),
            (JoinFailReason.LateJoinDisabled,         4),
            (JoinFailReason.RoomClosing,              5),
            (JoinFailReason.IdentityInvalid,          6),
            (JoinFailReason.IdentityExpired,          7),
            (JoinFailReason.IdentitySessionMismatch,  8),
            (JoinFailReason.IdentityRejected,         9),
            (JoinFailReason.IdentityRequired,         10),
            (JoinFailReason.IdentityValidationFailed, 11),
            (JoinFailReason.LayoutMismatch,           12),
        };

        /// <summary>
        /// Maps a server reject reason byte (carried on the disconnect packet payload) to a JoinFailReason.
        /// The wire reason codes are defined by the server's reject path and differ from these values.
        /// Inverse of <see cref="ToWireCode"/>.
        /// </summary>
        public static JoinFailReason FromJoinReject(byte wireReason)
        {
            for (int i = 0; i < WireRejectMap.Length; i++)
                if (WireRejectMap[i].Wire == wireReason)
                    return WireRejectMap[i].Reason;
            return JoinFailReason.Unknown;   // 0 and any unmapped value
        }

        /// <summary>
        /// Encodes a server-reject reason to its disconnect-payload wire byte (inverse of
        /// <see cref="FromJoinReject"/>). Non-reject reasons (transport/handshake-local) map to 0.
        /// </summary>
        public static byte ToWireCode(this JoinFailReason reason)
        {
            for (int i = 0; i < WireRejectMap.Length; i++)
                if (WireRejectMap[i].Reason == reason)
                    return WireRejectMap[i].Wire;
            return 0; // Unknown / not a wire-reject reason
        }

        /// <summary>
        /// Clamps a validator-supplied reject wire code into the identity range (6~11) at the core trust
        /// boundary. A game-implemented <see cref="IPlayerIdentityValidator"/> returns the wire byte directly
        /// (<see cref="IdentityValidationOutcome.RejectWireCode"/>); an out-of-range value would either lose
        /// the reason (decoded as Unknown) or — worse, for codes 1~5 — be decoded as a retryable room reason,
        /// making the client retry in a loop with a credential the authority already rejected. Anything
        /// outside 6~11 defaults to 9 (IdentityRejected). Bounds derive from <see cref="ToWireCode"/> so they
        /// track the wire table. Mirrors the sample's SdWireCodes.ClampIdentityCode, applied here so the guard
        /// does not depend on the validator implementing it.
        /// </summary>
        public static byte ClampIdentityWireCode(byte wireCode)
        {
            byte lo = JoinFailReason.IdentityInvalid.ToWireCode();          // 6
            byte hi = JoinFailReason.IdentityValidationFailed.ToWireCode(); // 11
            return (wireCode >= lo && wireCode <= hi)
                ? wireCode
                : JoinFailReason.IdentityRejected.ToWireCode();            // 9
        }

        /// <summary>
        /// Returns the symbolic name for a reason (e.g. "TimedOut"). Game/UI layers should
        /// localize these via their own string table; the values here are stable identifiers.
        /// </summary>
        public static string ToName(this JoinFailReason reason)
        {
            switch (reason)
            {
                case JoinFailReason.TransportStartFailed:       return "TransportStartFailed";
                case JoinFailReason.TimedOut:                   return "TimedOut";
                case JoinFailReason.ConnectionLost:             return "ConnectionLost";
                case JoinFailReason.Rejected:                   return "Rejected";
                case JoinFailReason.HostClosed:                 return "HostClosed";
                case JoinFailReason.Unknown:                    return "Unknown";
                case JoinFailReason.RoomNotFound:               return "RoomNotFound";
                case JoinFailReason.RoomFull:                   return "RoomFull";
                case JoinFailReason.ServerFull:                 return "ServerFull";
                case JoinFailReason.LateJoinDisabled:           return "LateJoinDisabled";
                case JoinFailReason.RoomClosing:                return "RoomClosing";
                case JoinFailReason.IdentityInvalid:            return "IdentityInvalid";
                case JoinFailReason.IdentityExpired:            return "IdentityExpired";
                case JoinFailReason.IdentitySessionMismatch:    return "IdentitySessionMismatch";
                case JoinFailReason.IdentityRejected:           return "IdentityRejected";
                case JoinFailReason.IdentityRequired:           return "IdentityRequired";
                case JoinFailReason.IdentityValidationFailed:   return "IdentityValidationFailed";
                default:                                        return "Unknown";
            }
        }

        /// <summary>
        /// Default English user-facing message for a reason. Games needing localization should
        /// write their own switch; this is a one-line fallback. Always returns non-null.
        /// </summary>
        public static string ToDefaultMessage(this JoinFailReason reason)
        {
            switch (reason)
            {
            case JoinFailReason.TransportStartFailed:           return "Network unavailable";
            case JoinFailReason.TimedOut:                       return "Connection timed out";
            case JoinFailReason.ConnectionLost:                 return "Could not reach the host";
            case JoinFailReason.Rejected:                       return "Connection rejected";
            case JoinFailReason.HostClosed:                     return "Host closed the connection";
            case JoinFailReason.Unknown:                        return "Join failed";
            case JoinFailReason.RoomNotFound:                   return "Room not found";
            case JoinFailReason.RoomFull:                       return "Room is full";
            case JoinFailReason.ServerFull:                     return "Server is full";
            case JoinFailReason.LateJoinDisabled:               return "Join is no longer allowed";
            case JoinFailReason.RoomClosing:                    return "Room is closing";
            case JoinFailReason.IdentityInvalid:                return "Identity could not be verified";
            case JoinFailReason.IdentityExpired:                return "Login session expired";
            case JoinFailReason.IdentitySessionMismatch:        return "Login does not match this match";
            case JoinFailReason.IdentityRejected:               return "Login rejected";
            case JoinFailReason.IdentityRequired:               return "Login required";
            case JoinFailReason.IdentityValidationFailed:       return "Login verification failed";
            default:                                            return "Join failed";
            }
        }
    }
}
