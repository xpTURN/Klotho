using System;

using NUnit.Framework;
using UnityEngine;

using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke: every shipped assembly resolves, and the assembly
    /// split is the one the package declares (a type that migrated across an asmdef boundary
    /// shows up here rather than as a mysterious CS0246 in a consumer project).
    /// </summary>
    [TestFixture]
    public class PackageSurfaceSmokeTests
    {
        // This project exists only to validate the package's declared minimum editor (package.json
        // "unity": "2022.3"). Running it on a newer editor still passes the other fixtures but no
        // longer proves anything about 2022, so pin the intent here.
        [Test]
        public void EditorVersion_Is2022_3_LTS()
        {
            Assert.IsTrue(Application.unityVersion.StartsWith("2022.3"),
                $"Unity2022.Tests must run on the 2022.3 LTS line (package.json declares \"unity\": \"2022.3\"); " +
                $"current editor is {Application.unityVersion}.");
        }

        [Test]
        public void KlothoPackage_IsResolvedAsUpmPackage()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ECS.EcsSimulation).Assembly);

            Assert.IsNotNull(info, "xpTURN.Klotho.Runtime is not part of a UPM package — Klotho must be consumed as a package, not loose sources.");
            Assert.AreEqual("com.xpturn.klotho", info.name);
        }

        // (type, owning assembly) — assembly names are part of the package contract.
        [TestCase(typeof(ECS.EcsSimulation), "xpTURN.Klotho.Runtime")]
        [TestCase(typeof(Core.KlothoEngine), "xpTURN.Klotho.Runtime")]
        [TestCase(typeof(Network.KlothoNetworkService), "xpTURN.Klotho.Runtime")]
        [TestCase(typeof(Deterministic.Math.FP64), "xpTURN.Klotho.Runtime")]
        [TestCase(typeof(ECS.HealthComponent), "xpTURN.Klotho.Gameplay")]
        [TestCase(typeof(Logging.KLoggerFactory), "xpTURN.Klotho.Logging")]
        [TestCase(typeof(Logging.UnityDebugSink), "xpTURN.Klotho.Logging.Unity")]
        [TestCase(typeof(KlothoSessionDriver), "xpTURN.Klotho.Runtime.Unity")]
        [TestCase(typeof(USimulationConfig), "xpTURN.Klotho.Runtime.Unity")]
        [TestCase(typeof(ECS.Json.DataAssetJsonSerializer), "xpTURN.Klotho.DataAsset.Json")]
        [TestCase(typeof(LiteNetLib.LiteNetLibTransport), "xpTURN.Klotho.LiteNetLib")]
        [TestCase(typeof(Editor.FPNavMeshExporter), "xpTURN.Klotho.Editor")]
        [TestCase(typeof(Editor.JsonToBytesConverter), "xpTURN.Klotho.Editor")]
        [TestCase(typeof(Editor.ECS.EntityComponentVisualizerWindow), "xpTURN.Klotho.Editor.ECS")]
        [TestCase(typeof(Editor.FSM.HFSMVisualizerWindow), "xpTURN.Klotho.Editor.FSM")]
        public void Type_LivesInExpectedAssembly(Type type, string expectedAssemblyName)
        {
            Assert.AreEqual(expectedAssemblyName, type.Assembly.GetName().Name);
        }

        // Newtonsoft is a package dependency (com.unity.nuget.newtonsoft-json) referenced through
        // precompiledReferences. A missing/duplicate copy breaks DataAsset.Json only at runtime,
        // so touch it from the test assembly's own reference closure.
        [Test]
        public void NewtonsoftJson_IsAvailableToKlothoAssemblies()
        {
            var jsonAsm = typeof(ECS.Json.DataAssetJsonSerializer).Assembly;
            bool referencesNewtonsoft = false;
            foreach (var reference in jsonAsm.GetReferencedAssemblies())
                if (reference.Name == "Newtonsoft.Json") referencesNewtonsoft = true;

            Assert.IsTrue(referencesNewtonsoft, "xpTURN.Klotho.DataAsset.Json lost its Newtonsoft.Json reference.");
        }

        // The Runtime asmdef sets noEngineReferences — the engine-agnostic core must stay linkable
        // outside Unity (server mirror / Godot). A stray UnityEngine reference breaks that silently.
        [Test]
        public void RuntimeCore_DoesNotReferenceUnityEngine()
        {
            var runtime = typeof(ECS.EcsSimulation).Assembly;
            foreach (var reference in runtime.GetReferencedAssemblies())
                Assert.AreNotEqual("UnityEngine.CoreModule", reference.Name,
                    "xpTURN.Klotho.Runtime must stay engine-agnostic (asmdef noEngineReferences).");
        }
    }
}
