using System.Collections.Generic;
using Xunit;

using xpTURN.Klotho.Network; // SimulationConfigMessage, MessageSerializer

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Wire round-trip for the per-component maxCount override map carried by SimulationConfigMessage on
    /// the ServerDriven server→client push: Dictionary&lt;int,int&gt; → parallel List&lt;int&gt; → wire →
    /// back to Dictionary, values preserved. Registry-layer priority (override &gt; attribute &gt;
    /// maxEntities) is exercised by the EditMode component tests.
    /// </summary>
    public sealed class ComponentMaxCountConfigWireTests
    {
        private static T RoundTrip<T>(T msg) where T : class, INetworkMessage
        {
            var ser = new MessageSerializer();
            byte[] bytes = ser.Serialize(msg);
            return ser.Deserialize(bytes, bytes.Length) as T;
        }

        [Fact]
        public void Overrides_RoundTripAcrossWire()
        {
            var config = new SimulationConfig
            {
                ComponentMaxCountOverrides = new Dictionary<int, int> { { 11, 12 }, { 25, 20 }, { 1, 24 } },
            };

            var msg = new SimulationConfigMessage();
            msg.CopyFrom(config);
            Assert.Equal(3, msg.MaxCountOverrideTypeIds.Count);
            Assert.Equal(3, msg.MaxCountOverrideValues.Count);

            var restored = RoundTrip(msg).ToSimulationConfig();

            Assert.Equal(3, restored.ComponentMaxCountOverrides.Count);
            Assert.Equal(12, restored.ComponentMaxCountOverrides[11]);
            Assert.Equal(20, restored.ComponentMaxCountOverrides[25]);
            Assert.Equal(24, restored.ComponentMaxCountOverrides[1]);
        }

        [Fact]
        public void EmptyOverrides_RoundTripsEmpty()
        {
            var msg = new SimulationConfigMessage();
            msg.CopyFrom(new SimulationConfig());   // default = empty override dict

            var restored = RoundTrip(msg).ToSimulationConfig();

            Assert.Empty(restored.ComponentMaxCountOverrides);
        }
    }
}
