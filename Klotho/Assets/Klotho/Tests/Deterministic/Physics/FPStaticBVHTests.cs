using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPStaticBVHTests
    {
        static FPPhysicsBody MakeStaticSphere(int id, FPVector3 position, FP64 radius)
        {
            var body = new FPPhysicsBody();
            body.id = id;
            body.rigidBody = FPRigidBody.CreateStatic();
            body.collider = FPCollider.FromSphere(new FPSphereShape(radius, position));
            body.position = position;
            body.rotation = FPQuaternion.Identity;
            return body;
        }

        static FPStaticCollider MakeStaticCollider(int id, FPVector3 position, FP64 radius)
        {
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromSphere(new FPSphereShape(radius, position)),
                meshData = default,
                isTrigger = false
            };
        }

        static FPBounds3 Bounds(FPVector3 center, FP64 size)
        {
            return new FPBounds3(center, new FPVector3(size, size, size));
        }

        #region Build Determinism

        [Test]
        public void Build_PathA_SameInputTwice_SameOverlapResults()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.Zero,        FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(1, new FPVector3(FP64.FromInt(5),  FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(2, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One),
            };
            int[] indices = { 0, 1, 2 };

            var bvh1 = FPStaticBVH.Build(bodies, indices);
            var bvh2 = FPStaticBVH.Build(bodies, indices);

            var out1 = new List<int>();
            var out2 = new List<int>();
            FPBounds3 query = Bounds(FPVector3.Zero, FP64.FromInt(20));
            bvh1.OverlapAABB(query, out1);
            bvh2.OverlapAABB(query, out2);

            out1.Sort();
            out2.Sort();
            Assert.AreEqual(out1.Count, out2.Count);
            for (int i = 0; i < out1.Count; i++)
                Assert.AreEqual(out1[i], out2[i]);
        }

        [Test]
        public void Build_PathB_SameInputTwice_SameOverlapResults()
        {
            var colliders = new[]
            {
                MakeStaticCollider(10, new FPVector3(FP64.Zero,        FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticCollider(11, new FPVector3(FP64.FromInt(5),  FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticCollider(12, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One),
            };

            var bvh1 = FPStaticBVH.Build(colliders, 3);
            var bvh2 = FPStaticBVH.Build(colliders, 3);

            var out1 = new List<int>();
            var out2 = new List<int>();
            FPBounds3 query = Bounds(FPVector3.Zero, FP64.FromInt(20));
            bvh1.OverlapAABB(query, out1);
            bvh2.OverlapAABB(query, out2);

            out1.Sort();
            out2.Sort();
            Assert.AreEqual(out1.Count, out2.Count);
            for (int i = 0; i < out1.Count; i++)
                Assert.AreEqual(out1[i], out2[i]);
        }

        #endregion

        #region OverlapAABB Accuracy

        [Test]
        public void OverlapAABB_EmptyBVH_ReturnsNothing()
        {
            var bvh = default(FPStaticBVH);
            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(100)), output);
            Assert.AreEqual(0, output.Count);
        }

        [Test]
        public void OverlapAABB_NoOverlap_ReturnsNothing()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 });

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(2)), output);
            Assert.AreEqual(0, output.Count);
        }

        [Test]
        public void OverlapAABB_SingleBody_ReturnsBodyIndex()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, FPVector3.Zero, FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 });

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(4)), output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(0, output[0]);
        }

        [Test]
        public void OverlapAABB_PartialOverlap_ReturnsOnlyOverlapping()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.Zero,        FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(1, new FPVector3(FP64.FromInt(3),  FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(2, new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0, 1, 2 });

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(10)), output);
            output.Sort();
            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(0, output[0]);
            Assert.AreEqual(1, output[1]);
        }

        [Test]
        public void OverlapAABB_AllBodies_ReturnsAll()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.Zero,       FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(1, new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(2, new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0, 1, 2 });

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(20)), output);
            output.Sort();
            Assert.AreEqual(3, output.Count);
            Assert.AreEqual(0, output[0]);
            Assert.AreEqual(1, output[1]);
            Assert.AreEqual(2, output[2]);
        }

        #endregion

        #region RayCast

        [Test]
        public void RayCast_EmptyBVH_ReturnsFalse()
        {
            var bvh = default(FPStaticBVH);
            bool hit = bvh.RayCast(new FPRay3(FPVector3.Zero, FPVector3.Right), out _, out _);
            Assert.IsFalse(hit);
        }

        [Test]
        public void RayCast_Miss_ReturnsFalse()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 });

            bool hit = bvh.RayCast(
                new FPRay3(FPVector3.Zero, FPVector3.Up),
                out _, out _);
            Assert.IsFalse(hit);
        }

        [Test]
        public void RayCast_Hit_ReturnsLeafIndexAndPositiveT()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 });

            bool hit = bvh.RayCast(
                new FPRay3(FPVector3.Zero, FPVector3.Right),
                out int leafIndex, out FP64 t);

            Assert.IsTrue(hit);
            Assert.AreEqual(0, leafIndex);
            Assert.IsTrue(t > FP64.Zero);
        }

        [Test]
        public void RayCast_TwoTargets_ReturnsNearest()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.FromInt(5),  FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticSphere(1, new FPVector3(FP64.FromInt(20), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0, 1 });

            bool hit = bvh.RayCast(
                new FPRay3(FPVector3.Zero, FPVector3.Right),
                out int leafIndex, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0, leafIndex);
        }

        #endregion

        #region Mixed Source (Path C)

        [Test]
        public void Build_PathC_BodyLeafPositive_ColliderLeafNegative()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero), FP64.One),
            };
            var colliders = new[]
            {
                MakeStaticCollider(10, new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 }, colliders, 1);

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(10)), output);
            output.Sort();

            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(~0, output[0]);  // collider: leafIndex = ~0 = -1
            Assert.AreEqual(0,  output[1]);  // body:     leafIndex = 0
        }

        [Test]
        public void Build_PathC_NullColliders_NullSafe()
        {
            var bodies = new[]
            {
                MakeStaticSphere(0, FPVector3.Zero, FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, new[] { 0 }, null, 0);

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(4)), output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(0, output[0]);
        }

        [Test]
        public void Build_PathC_MultipleColliders_LeafIndicesEncoded()
        {
            var bodies = new FPPhysicsBody[0];
            var colliders = new[]
            {
                MakeStaticCollider(10, new FPVector3(FP64.Zero,       FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticCollider(11, new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), FP64.One),
                MakeStaticCollider(12, new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.Zero), FP64.One),
            };
            var bvh = FPStaticBVH.Build(bodies, System.Array.Empty<int>(), colliders, 3);

            var output = new List<int>();
            bvh.OverlapAABB(Bounds(FPVector3.Zero, FP64.FromInt(20)), output);
            output.Sort();

            Assert.AreEqual(3, output.Count);
            Assert.AreEqual(~2, output[0]);  // -3
            Assert.AreEqual(~1, output[1]);  // -2
            Assert.AreEqual(~0, output[2]);  // -1
        }

        #endregion
    }
}
