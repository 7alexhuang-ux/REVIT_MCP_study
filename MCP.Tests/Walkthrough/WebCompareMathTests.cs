using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class WebCompareMathTests
    {
        private static readonly MeterBox Unit = new MeterBox(0, 0, 0, 10, 10, 10);

        private static double Area(Vec3[] t)
        {
            var n = (t[1] - t[0]).Cross(t[2] - t[0]);
            return n.Length / 2;
        }

        [Test]
        public void Expand_AddsMarginToXyAndAsymmetricZ()
        {
            var e = new MeterBox(100, 200, 5, 140, 260, 30).Expand(10, 8, 4);
            Assert.That(new[] { e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ }, Is.EqualTo(new double[] { 90, 190, -3, 150, 270, 34 }));
            Assert.That(e.IsValid, Is.True);
        }

        [Test]
        public void TryBounds_EmptyReturnsFalse_AndUnionGrows()
        {
            Assert.That(WebCompareMath.TryBounds(new Vec3[0], out _), Is.False);
            Assert.That(WebCompareMath.TryBounds(new[] { new Vec3(1, 2, 3), new Vec3(-1, 5, 0) }, out var b), Is.True);
            var u = b.Union(new MeterBox(0, 0, 0, 9, 1, 7));
            Assert.That(new[] { u.MinX, u.MaxX, u.MinY, u.MaxY, u.MinZ, u.MaxZ }, Is.EqualTo(new double[] { -1, 9, 0, 5, 0, 7 }));
        }

        [Test]
        public void ClipTriangle_FullyInside_ReturnsSameTriangle()
        {
            var r = WebCompareMath.ClipTriangle(new Vec3(1, 1, 1), new Vec3(5, 1, 1), new Vec3(1, 5, 1), Unit);
            Assert.That(r.Count, Is.EqualTo(1));
            Assert.That(Area(r[0]), Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void ClipTriangle_FullyOutside_ReturnsEmpty()
        {
            var r = WebCompareMath.ClipTriangle(new Vec3(20, 0, 0), new Vec3(30, 0, 0), new Vec3(20, 5, 0), Unit);
            Assert.That(r, Is.Empty);
        }

        [Test]
        public void ClipTriangle_HugeTriangleCoveringBox_KeepsOnlyBoxPart()
        {
            // Flat terrain-like triangle far larger than the box: the clipped area is exactly the box footprint.
            var r = WebCompareMath.ClipTriangle(new Vec3(-1000, -1000, 4), new Vec3(1000, -1000, 4), new Vec3(0, 1000, 4), Unit);
            Assert.That(r.Sum(Area), Is.EqualTo(100).Within(1e-6));
            Assert.That(r.SelectMany(t => t).All(p => Unit.Contains(p)), Is.True);
        }

        [Test]
        public void ClipTriangle_ClipsAlongZToo()
        {
            var r = WebCompareMath.ClipTriangle(new Vec3(2, 2, -5), new Vec3(8, 2, -5), new Vec3(5, 2, 15), Unit);
            Assert.That(r.Count, Is.GreaterThan(0));
            Assert.That(r.SelectMany(t => t).All(p => p.Z >= -1e-9 && p.Z <= 10 + 1e-9), Is.True);
        }
    }
}
