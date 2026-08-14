using NUnit.Framework;

using xpTURN.Klotho.ECS;

// NO NAMESPACE, deliberately. NUnit scopes a [SetUpFixture] to its own namespace and the
// namespaces below it; the fixtures this has to cover live under xpTURN.Klotho.Deterministic.*,
// xpTURN.Klotho.ECS.* and others, so only the global namespace reaches all of them.
/// <summary>
/// Assembly-wide setup. Exists for one thing: making this suite behave the same in Debug and
/// in Release.
///
/// <para><see cref="ComponentStorageRegistry"/> freezes its layout on first use and refuses a
/// conflicting recompute, because maxEntities and the override/prune sets have to be uniform
/// across a process. Fixtures legitimately need different values, so the registry offers an
/// automatic relaxation — and that relaxation used to be selected by <c>#if DEBUG</c> in the
/// runtime assembly. A Release <c>dotnet test</c> therefore linked against a runtime that did
/// NOT have it, and 170 tests across 23 fixtures failed on a freeze conflict they never see in
/// Debug. The suite was written off as "Release is just red", which in turn made the two
/// Release-ONLY gates (the byte allocation gate and the patch-speed gate) unobservable without
/// a hand-written <c>--filter</c>.</para>
///
/// <para>Opting in here instead of at compile time is what makes the two configurations agree.
/// The flag is <c>internal</c>; shipping code cannot reach it and still throws on a
/// conflicting recompute.</para>
/// </summary>
[SetUpFixture]
public class TestAssemblySetup
{
    [OneTimeSetUp]
    public void EnableCrossFixtureLayoutRecompute()
    {
        ComponentStorageRegistry.AllowLayoutRecompute = true;
    }
}
