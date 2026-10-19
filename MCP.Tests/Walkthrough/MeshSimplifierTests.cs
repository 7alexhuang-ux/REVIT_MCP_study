using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class MeshSimplifierTests
    {
        /// <summary>UV sphere, radius 1, welded (shared vertices), closed except the pole fans share single vertices.</summary>
        private static void Sphere(int rings, int segments, out List<double> positions, out List<int> indices)
        {
            positions = new List<double>();
            indices = new List<int>();
            for (var r = 0; r <= rings; r++)
            {
                var phi = Math.PI * r / rings;
                for (var s = 0; s < segments; s++)
                {
                    var theta = 2 * Math.PI * s / segments;
                    positions.Add(Math.Sin(phi) * Math.Cos(theta));
                    positions.Add(Math.Sin(phi) * Math.Sin(theta));
                    positions.Add(Math.Cos(phi));
                }
            }

            for (var r = 0; r < rings; r++)
            {
                for (var s = 0; s < segments; s++)
                {
                    int a = r * segments + s, b = r * segments + (s + 1) % segments;
                    int c = a + segments, d = b + segments;
                    indices.AddRange(new[] { a, c, b, b, c, d });
                }
            }
        }

        [Test]
        public void Simplify_ReachesTargetAndStaysOnSurface()
        {
            Sphere(100, 200, out var positions, out var indices);
            var before = indices.Count / 3;
            MeshSimplifier.Simplify(positions, indices, before / 10, out var outPositions, out var outIndices, out var source);

            var after = outIndices.Count / 3;
            Assert.That(after, Is.LessThanOrEqualTo(before / 10 + 50));
            Assert.That(after, Is.GreaterThan(before / 20));
            Assert.That(source.Count, Is.EqualTo(outPositions.Count / 3));
            for (var i = 0; i < outPositions.Count; i += 3)
            {
                var radius = Math.Sqrt(outPositions[i] * outPositions[i] + outPositions[i + 1] * outPositions[i + 1] + outPositions[i + 2] * outPositions[i + 2]);
                Assert.That(radius, Is.EqualTo(1.0).Within(0.03), "vertex drifted off the sphere");
            }

            Assert.That(outIndices.All(ix => ix >= 0 && ix < outPositions.Count / 3));
        }

        [Test]
        public void Simplify_FlatGridKeepsBorderOutline()
        {
            // 50x50 planar grid: interior collapses freely, the square outline must not shrink.
            var positions = new List<double>();
            var indices = new List<int>();
            const int n = 50;
            for (var y = 0; y <= n; y++)
            {
                for (var x = 0; x <= n; x++)
                {
                    positions.AddRange(new double[] { x, y, 0 });
                }
            }

            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                    indices.AddRange(new[] { a, b, c, b, d, c });
                }
            }

            MeshSimplifier.Simplify(positions, indices, 500, out var outPositions, out var outIndices, out _);
            Assert.That(outIndices.Count / 3, Is.LessThan(n * n * 2));
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (var i = 0; i < outPositions.Count; i += 3)
            {
                minX = Math.Min(minX, outPositions[i]); maxX = Math.Max(maxX, outPositions[i]);
                minY = Math.Min(minY, outPositions[i + 1]); maxY = Math.Max(maxY, outPositions[i + 1]);
                Assert.That(outPositions[i + 2], Is.EqualTo(0).Within(1e-9));
            }

            Assert.That(new[] { minX, minY, maxX, maxY }, Is.EqualTo(new double[] { 0, 0, n, n }).Within(1e-6));
        }

        [Test]
        public void GltfBuilder_SimplifyCurrentElement_UpdatesCounts()
        {
            Sphere(60, 120, out var positions, out var indices);
            var builder = new GltfBuilder();
            var material = builder.AddMaterial("stone", 0.5, 0.5, 0.5, 1.0);
            builder.BeginElement(new GltfElementInfo { ElementId = 1, Name = "statue" });
            var pts = new List<Vec3>();
            for (var i = 0; i < positions.Count; i += 3)
            {
                pts.Add(new Vec3(positions[i], positions[i + 1], positions[i + 2]));
            }

            builder.AddTriangles(material, pts, null, indices);
            var before = builder.CurrentElementTriangles;
            var kept = builder.SimplifyCurrentElement(before / 5);
            Assert.That(kept, Is.LessThanOrEqualTo(before / 5 + 50));
            Assert.That(builder.TriangleCount, Is.EqualTo(kept));
            builder.EndElement();
            var glb = builder.BuildGlb();
            Assert.That(glb.Length, Is.GreaterThan(0));
        }

        [Test]
        public void GltfBuilder_BoxPlaceholder_IsTwelveTriangles()
        {
            Sphere(20, 40, out var positions, out var indices);
            var builder = new GltfBuilder();
            var material = builder.AddMaterial("stone", 0.5, 0.5, 0.5, 1.0);
            builder.BeginElement(new GltfElementInfo { ElementId = 2, Name = "table" });
            var pts = new List<Vec3>();
            for (var i = 0; i < positions.Count; i += 3)
            {
                pts.Add(new Vec3(positions[i], positions[i + 1], positions[i + 2]));
            }

            builder.AddTriangles(material, pts, null, indices);
            builder.ReplaceCurrentElementWithBox();
            Assert.That(builder.TriangleCount, Is.EqualTo(12));
            builder.EndElement();
            var json = System.Text.Encoding.UTF8.GetString(builder.BuildGlb());
            StringAssert.Contains("\"placeholder\":true", json);
        }
    }
}
