namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// 3-way mutually exclusive guest join paths used by KlothoConnection / ConnectionResult / KlothoSession.Create.
    /// </summary>
    public enum JoinKind
    {
        Normal,
        LateJoin,
        Reconnect,
    }
}
