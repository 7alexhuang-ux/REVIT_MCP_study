using System;
using System.IO;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class PlanProjectionTests
    {
        [Test]
        public void ModelToPixel_AxisAlignedCrop_MapsCornersWithYDown()
        {
            var m = PlanProjection.ModelToPixel(0, 0, 1, 0, 0, 1, 0, 0, 10, 5, 2000, 1000);

            AssertPoint(m.Apply(0, 0), 0, 1000);
            AssertPoint(m.Apply(10, 5), 2000, 0);
            AssertPoint(m.Apply(5, 2.5), 1000, 500);
            AssertPoint(m.Apply(0, 5), 0, 0);
        }

        [Test]
        public void ModelToPixel_OffsetOriginAndNegativeCropMin()
        {
            // Crop frame origin at model (100, 50); crop spans local [-4, 4] x [-3, 3].
            var m = PlanProjection.ModelToPixel(100, 50, 1, 0, 0, 1, -4, -3, 4, 3, 800, 600);
            AssertPoint(m.Apply(100, 50), 400, 300);
            AssertPoint(m.Apply(96, 53), 0, 0);
            AssertPoint(m.Apply(104, 47), 800, 600);
        }

        [TestCase(0.0)]
        [TestCase(30.0)]
        [TestCase(-117.5)]
        public void ModelToPixel_RotatedView_RoundTripsThroughInverse(double degrees)
        {
            var r = degrees * Math.PI / 180;
            var bx = (Math.Cos(r), Math.Sin(r));
            var by = (-Math.Sin(r), Math.Cos(r));
            var m = PlanProjection.ModelToPixel(12.3, -4.5, bx.Item1, bx.Item2, by.Item1, by.Item2, -20, -8, 35, 17, 2000, 909);
            var inverse = m.Inverse();

            foreach (var (x, y) in new[] { (0.0, 0.0), (12.3, -4.5), (-7.25, 31.0), (40.0, 2.0) })
            {
                var pixel = m.Apply(x, y);
                var back = inverse.Apply(pixel.X, pixel.Y);
                Assert.That(back.X, Is.EqualTo(x).Within(1e-9));
                Assert.That(back.Y, Is.EqualTo(y).Within(1e-9));
            }

            // The crop frame origin (local 0,0) lands where the crop rectangle says it should.
            var origin = m.Apply(12.3, -4.5);
            Assert.That(origin.X, Is.EqualTo(20.0 / 55.0 * 2000).Within(1e-9));
            Assert.That(origin.Y, Is.EqualTo(17.0 / 25.0 * 909).Within(1e-9));
        }

        [Test]
        public void ModelToPixel_RejectsDegenerateInput()
        {
            Assert.Throws<ArgumentException>(() => PlanProjection.ModelToPixel(0, 0, 1, 0, 0, 1, 0, 0, 0, 5, 100, 100));
            Assert.Throws<ArgumentException>(() => PlanProjection.ModelToPixel(0, 0, 1, 0, 0, 1, 0, 0, 5, 5, 0, 100));
        }

        [Test]
        public void ToArray_UsesContractOrder()
        {
            var a = new Affine2D(1, 2, 3, 4, 5, 6).ToArray();
            Assert.That(a, Is.EqualTo(new double[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void ProjectToFrame_DepthPositiveTowardViewer()
        {
            // Section looking north (+Y): viewer stands south, so the normal toward the viewer is -Y.
            var origin = new Vec3(0, 0, 0);
            var right = new Vec3(1, 0, 0);
            var up = new Vec3(0, 0, 1);
            var normal = new Vec3(0, -1, 0);

            var inFront = PlanProjection.ProjectToFrame(new Vec3(2, -3, 1.5), origin, right, up, normal);
            Assert.That(inFront.U, Is.EqualTo(2).Within(1e-12));
            Assert.That(inFront.V, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(inFront.Depth, Is.EqualTo(3).Within(1e-12));

            var beyond = PlanProjection.ProjectToFrame(new Vec3(2, 4, 1.5), origin, right, up, normal);
            Assert.That(beyond.Depth, Is.EqualTo(-4).Within(1e-12));
        }

        [TestCase(10.0, 5.0, 2000, 1000, 0.0)]
        [TestCase(16.0, 9.0, 1280, 720, 0.0)]
        [TestCase(10.0, 5.0, 2000, 1100, 0.0909)]
        public void AspectMismatch_ReportsRelativeDifference(double cw, double ch, int w, int h, double expected)
        {
            Assert.That(PlanProjection.AspectMismatch(cw, ch, w, h), Is.EqualTo(expected).Within(1e-3));
        }

        [Test]
        public void TryReadPngSize_ParsesIhdr()
        {
            var bytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                0x00, 0x00, 0x07, 0xD0, // 2000
                0x00, 0x00, 0x05, 0x74, // 1396
                0x08, 0x06, 0x00, 0x00, 0x00
            };
            using (var stream = new MemoryStream(bytes))
            {
                Assert.That(PlanProjection.TryReadPngSize(stream, out var w, out var h), Is.True);
                Assert.That(w, Is.EqualTo(2000));
                Assert.That(h, Is.EqualTo(1396));
            }

            bytes[1] = 0x00;
            using (var broken = new MemoryStream(bytes))
            {
                Assert.That(PlanProjection.TryReadPngSize(broken, out _, out _), Is.False);
            }

            using (var shortStream = new MemoryStream(new byte[] { 0x89, 0x50 }))
            {
                Assert.That(PlanProjection.TryReadPngSize(shortStream, out _, out _), Is.False);
            }
        }

        private static void AssertPoint((double X, double Y) actual, double x, double y)
        {
            Assert.That(actual.X, Is.EqualTo(x).Within(1e-9));
            Assert.That(actual.Y, Is.EqualTo(y).Within(1e-9));
        }
    }
}
