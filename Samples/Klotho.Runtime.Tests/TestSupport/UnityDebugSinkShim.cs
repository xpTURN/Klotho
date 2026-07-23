// Shim for the Unity-only KLogBuilder.AddUnityDebug() extension, which is guarded by
// UNITY_5_3_OR_NEWER in com.xpturn.klotho/Unity/Logging/KLogBuilderUnityExtensions.cs and therefore
// does not exist in the dotnet build closure. These tests were moved out of the Unity Test Runner and
// run under `dotnet test`; they only need a working logger for the code under test — none assert on
// Unity debug output — so this maps AddUnityDebug() to a no-op sink registration.
namespace xpTURN.Klotho.Logging
{
    internal static class TestUnityDebugShim
    {
        public static KLogBuilder AddUnityDebug(this KLogBuilder builder) => builder;
    }
}
