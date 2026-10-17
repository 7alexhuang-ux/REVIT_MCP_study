using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class CameraPathMathTests
    {
        private static CameraStation Station(int order, double x, double y, double z, double fx, double fy, double fz = 0)
        {
            return new CameraStation
            {
                Name = "WALK-" + order.ToString("00"),
                Order = order,
                Eye = new Vec3(x, y, z),
                Forward = new Vec3(fx, fy, fz),
                Up = new Vec3(0, 0, 1)
            };
        }

        [TestCase("WALK-01", "WALK-", true, 1)]
        [TestCase("walk-12", "WALK-", true, 12)]
        [TestCase("  WALK-3 ", "WALK-", true, 3)]
        [TestCase("WALK-RENDER-TMP", "WALK-", false, 0)]
        [TestCase("WALK-01 copy", "WALK-", false, 0)]
        [TestCase("XWALK-01", "WALK-", false, 0)]
        [TestCase("站-05", "站-", true, 5)]
        [TestCase("WalkRender_TMP", "WALK-", false, 0)]
        public void TryParseStationOrder_AcceptsOnlyPrefixPlusDigits(string name, string prefix, bool ok, int order)
        {
            Assert.That(CameraPathMath.TryParseStationOrder(name, prefix, out var parsed), Is.EqualTo(ok));
            Assert.That(parsed, Is.EqualTo(order));
        }

        [Test]
        public void BuildPath_TwoStations_StraightLineUniformSpacing()
        {
            var path = CameraPathMath.BuildPath(new[]
            {
                Station(1, 0, 0, 1.55, 1, 0),
                Station(2, 10, 0, 1.55, 1, 0)
            }, 20);

            Assert.That(path.Samples, Has.Count.EqualTo(21));
            Assert.That(path.TotalLength, Is.EqualTo(10).Within(1e-6));
            for (var i = 0; i < path.Samples.Count; i++)
            {
                Assert.That(path.Samples[i].P.X, Is.EqualTo(i * 0.5).Within(1e-6));
                Assert.That(path.Samples[i].P.Y, Is.EqualTo(0).Within(1e-9));
                Assert.That(path.Samples[i].T, Is.EqualTo(i / 20.0).Within(1e-12));
            }

            Assert.That(path.StationSampleIndices, Is.EqualTo(new[] { 0, 20 }));
        }

        [Test]
        public void BuildPath_LShape_UniformArcSpacingAndPassesThroughStations()
        {
            var stations = new[]
            {
                Station(1, 0, 0, 1.55, 1, 0),
                Station(2, 6, 0, 1.55, 1, 0),
                Station(3, 8, 0, 1.55, 0.7071, 0.7071),
                Station(4, 8, 2, 1.55, 0, 1),
                Station(5, 8, 10, 1.55, 0, 1)
            };
            var path = CameraPathMath.BuildPath(stations, 24);
            var samples = path.Samples;

            Assert.That(samples, Has.Count.EqualTo(4 * 24 + 1));
            var expectedStep = path.TotalLength / (samples.Count - 1);
            for (var i = 1; i < samples.Count; i++)
            {
                var chord = (samples[i].P - samples[i - 1].P).Length;
                Assert.That(chord, Is.EqualTo(expectedStep).Within(expectedStep * 0.02), $"step {i}");
                Assert.That(samples[i].S, Is.GreaterThan(samples[i - 1].S));
            }

            // First and last samples coincide with the end stations.
            Assert.That((samples[0].P - stations[0].Eye).Length, Is.LessThan(1e-9));
            Assert.That((samples[samples.Count - 1].P - stations[4].Eye).Length, Is.LessThan(1e-6));

            // Every station lies within one step of its nearest sample.
            for (var k = 0; k < stations.Length; k++)
            {
                var index = path.StationSampleIndices[k];
                Assert.That((samples[index].P - stations[k].Eye).Length, Is.LessThan(expectedStep * 0.6), $"station {k}");
            }

            // Path length is at least the polyline chord length through the stations.
            var chordSum = 0.0;
            for (var k = 1; k < stations.Length; k++)
            {
                chordSum += (stations[k].Eye - stations[k - 1].Eye).Length;
            }

            Assert.That(path.TotalLength, Is.GreaterThanOrEqualTo(chordSum - 1e-6));
        }

        [Test]
        public void BuildPath_CameraFramesAreOrthonormalAndMatchStationsAtStations()
        {
            var stations = new[]
            {
                Station(1, 0, 0, 1.55, 1, 0),
                Station(2, 5, 0, 1.55, 0, 1),
                Station(3, 5, 5, 1.55, -1, 0)
            };
            var path = CameraPathMath.BuildPath(stations, 12);

            foreach (var sample in path.Samples)
            {
                Assert.That(sample.Fwd.Length, Is.EqualTo(1).Within(1e-9));
                Assert.That(sample.Up.Length, Is.EqualTo(1).Within(1e-9));
                Assert.That(Math.Abs(sample.Fwd.Dot(sample.Up)), Is.LessThan(1e-9));
            }

            Assert.That(path.Samples[0].Fwd.Dot(new Vec3(1, 0, 0)), Is.GreaterThan(0.9999));
            Assert.That(path.Samples[path.Samples.Count - 1].Fwd.Dot(new Vec3(-1, 0, 0)), Is.GreaterThan(0.9999));

            // Heading turns monotonically from +X through +Y to -X (no flip back).
            var previousAngle = -1.0;
            foreach (var sample in path.Samples)
            {
                var angle = Math.Atan2(sample.Fwd.Y, sample.Fwd.X);
                if (angle < -1e-6)
                {
                    angle += 2 * Math.PI;
                }

                Assert.That(angle, Is.GreaterThanOrEqualTo(previousAngle - 1e-3));
                previousAngle = angle;
            }
        }

        [Test]
        public void BuildPath_RejectsInvalidInput()
        {
            Assert.Throws<ArgumentException>(() => CameraPathMath.BuildPath(new[] { Station(1, 0, 0, 0, 1, 0) }, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.BuildPath(new[]
            {
                Station(1, 0, 0, 0, 1, 0), Station(2, 1, 0, 0, 1, 0)
            }, 0));
            Assert.Throws<ArgumentException>(() => CameraPathMath.BuildPath(new[]
            {
                Station(1, 3, 3, 1, 1, 0), Station(2, 3, 3, 1, 1, 0)
            }, 10));
        }

        [Test]
        public void OrthonormalizeCamera_TiltedUpBecomesPerpendicular()
        {
            var frame = CameraPathMath.OrthonormalizeCamera(new Vec3(1, 0, -0.2), new Vec3(0, 0, 1));
            Assert.That(Math.Abs(frame.Forward.Dot(frame.Up)), Is.LessThan(1e-12));
            Assert.That(frame.Up.Z, Is.GreaterThan(0.9));

            var lookingDown = CameraPathMath.OrthonormalizeCamera(new Vec3(0, 0, -1), new Vec3(0, 0, 1));
            Assert.That(lookingDown.Up.Length, Is.EqualTo(1).Within(1e-9));
            Assert.That(Math.Abs(lookingDown.Forward.Dot(lookingDown.Up)), Is.LessThan(1e-9));
        }

        [Test]
        public void FieldOfViewFromCropBox_SymmetricFrustum()
        {
            var tanX = Math.Tan(40 * Math.PI / 180);
            var tanY = Math.Tan(25 * Math.PI / 180);
            var near = 0.5;
            var far = 120.0;
            // Min corner lies on the back clip plane, projected from the front plane corner.
            var max = new Vec3(near * tanX, near * tanY, -near);
            var min = new Vec3(-far * tanX, -far * tanY, -far);

            var fov = CameraPathMath.FieldOfViewFromCropBox(min, max, Vec3.Zero);
            Assert.That(fov.IsValid, Is.True);
            Assert.That(fov.IsSymmetric, Is.True);
            Assert.That(fov.FovXDeg, Is.EqualTo(80).Within(1e-9));
            Assert.That(fov.FovYDeg, Is.EqualTo(50).Within(1e-9));
            Assert.That(fov.Aspect, Is.EqualTo(tanX / tanY).Within(1e-9));
        }

        [Test]
        public void FieldOfViewFromCropBox_UnscaledMinCornerStillSymmetric()
        {
            var tanY = Math.Tan(30 * Math.PI / 180);
            var max = new Vec3(0.1 * tanY * 16 / 9, 0.1 * tanY, -0.1);
            var min = new Vec3(-max.X, -max.Y, -100);
            var fov = CameraPathMath.FieldOfViewFromCropBox(min, max, Vec3.Zero);
            Assert.That(fov.IsSymmetric, Is.True);
            Assert.That(fov.FovYDeg, Is.EqualTo(60).Within(1e-9));
            Assert.That(fov.Aspect, Is.EqualTo(16.0 / 9).Within(1e-9));
        }

        [Test]
        public void FieldOfViewFromCropBox_EyeOffsetAndInvalidBoxes()
        {
            var tanY = Math.Tan(20 * Math.PI / 180);
            // Crop frame origin sits 2 units behind the eye along the view direction.
            var eye = new Vec3(0, 0, -2);
            var max = new Vec3(3 * tanY, 3 * tanY, -5);
            var min = new Vec3(-50 * tanY, -50 * tanY, -52);
            var fov = CameraPathMath.FieldOfViewFromCropBox(min, max, eye);
            Assert.That(fov.FovYDeg, Is.EqualTo(40).Within(1e-9));
            Assert.That(fov.IsSymmetric, Is.True);

            var behind = CameraPathMath.FieldOfViewFromCropBox(new Vec3(-1, -1, -1), new Vec3(1, 1, 1), Vec3.Zero);
            Assert.That(behind.IsValid, Is.False);

            var offCenter = CameraPathMath.FieldOfViewFromCropBox(new Vec3(-0.2, -1, -10), new Vec3(1, 1, -1), Vec3.Zero);
            Assert.That(offCenter.IsValid, Is.True);
            Assert.That(offCenter.IsSymmetric, Is.False);
        }

        [TestCase(50, 16.0 / 9.0)]
        [TestCase(50.0, 1.7778)]
        [TestCase(30, 1.0)]
        [TestCase(90, 1.0)]
        [TestCase(1, 4.0 / 3.0)]
        [TestCase(170, 21.0 / 9.0)]
        [TestCase(120, 0.5)]
        public void CropBoxFromFieldOfView_RoundTripsWithFieldOfViewFromCropBox(double fovYDeg, double aspect)
        {
            var (min, max) = CameraPathMath.CropBoxFromFieldOfView(fovYDeg, aspect);
            var fov = CameraPathMath.FieldOfViewFromCropBox(min, max, Vec3.Zero);

            Assert.That(fov.IsValid, Is.True);
            Assert.That(fov.IsSymmetric, Is.True);
            Assert.That(fov.FovYDeg, Is.EqualTo(fovYDeg).Within(1e-9));
            Assert.That(fov.Aspect, Is.EqualTo(aspect).Within(1e-9));
        }

        [Test]
        public void CropBoxFromFieldOfView_CustomNearFarStillRoundTrips()
        {
            var (min, max) = CameraPathMath.CropBoxFromFieldOfView(65, 1.5, 0.5, 250.0);
            var fov = CameraPathMath.FieldOfViewFromCropBox(min, max, Vec3.Zero);

            Assert.That(fov.IsSymmetric, Is.True);
            Assert.That(fov.FovYDeg, Is.EqualTo(65).Within(1e-9));
            Assert.That(fov.Aspect, Is.EqualTo(1.5).Within(1e-9));
            Assert.That(max.Z, Is.EqualTo(-0.5).Within(1e-12));
            Assert.That(min.Z, Is.EqualTo(-250.0).Within(1e-12));
        }

        [Test]
        public void CropBoxFromFieldOfView_DefaultNearFarMatchManifestConvention()
        {
            var (min, max) = CameraPathMath.CropBoxFromFieldOfView(50, 1.7778);
            Assert.That(max.Z, Is.EqualTo(-1.0).Within(1e-12));
            Assert.That(min.Z, Is.EqualTo(-1000.0).Within(1e-12));
        }

        [Test]
        public void CropBoxFromFieldOfView_RejectsInvalidInput()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(180, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(-10, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, 1, 0, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, 1, -1, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, 1, 10, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraPathMath.CropBoxFromFieldOfView(50, 1, 10, 10));
        }
    }
}
