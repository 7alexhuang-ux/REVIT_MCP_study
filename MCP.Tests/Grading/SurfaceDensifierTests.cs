using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// 非平面（彎曲）板底內部加密：加密後的點集（再加板緣點）做 Delaunay 三角剖分，
    /// 區域內任意點的線性內插高程與曲面真值的差必須 &lt; 2 mm（Revit 內部單位英呎，2 mm ≈ 0.00656 ft；
    /// 本測試用更嚴的 0.002 單位）。三角剖分由測試獨立實作（暴力空圓檢查），不依賴被測的格子誤差估計。
    /// 另測板緣內推方向：L／U 形凸角必須指向材料內，不可朝形心（會指到面外）。
    /// </summary>
    [TestFixture]
    public class SurfaceDensifierTests
    {
        private const double MaxAllowedError = 0.002;

        private static List<Point2D> Square(double size) => new List<Point2D>
        {
            new Point2D(0, 0), new Point2D(size, 0), new Point2D(size, size), new Point2D(0, size)
        };

        private static List<Point2D> BoundaryPoints(double size, double spacing)
        {
            var result = new List<Point2D>();
            for (var t = 0.0; t <= size + 1e-9; t += spacing)
            {
                result.Add(new Point2D(t, 0));
                result.Add(new Point2D(t, size));
                result.Add(new Point2D(0, t));
                result.Add(new Point2D(size, t));
            }

            return result;
        }

        private static double? DelaunayInterpolate(
            IReadOnlyList<Point2D> points, Func<double, double, double> surface, Point2D target)
        {
            var near = points
                .OrderBy(q => ((q.X - target.X) * (q.X - target.X)) + ((q.Y - target.Y) * (q.Y - target.Y)))
                .Take(14)
                .ToList();
            double? best = null;
            var bestRadius = double.MaxValue;
            for (var i = 0; i < near.Count; i++)
            {
                for (var j = i + 1; j < near.Count; j++)
                {
                    for (var k = j + 1; k < near.Count; k++)
                    {
                        var a = near[i];
                        var b = near[j];
                        var c = near[k];
                        var det = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
                        if (Math.Abs(det) < 1e-12)
                        {
                            continue;
                        }

                        var l1 = (((b.Y - c.Y) * (target.X - c.X)) + ((c.X - b.X) * (target.Y - c.Y))) / det;
                        var l2 = (((c.Y - a.Y) * (target.X - c.X)) + ((a.X - c.X) * (target.Y - c.Y))) / det;
                        var l3 = 1 - l1 - l2;
                        if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9)
                        {
                            continue;
                        }

                        // 外接圓。
                        var d = 2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));
                        if (Math.Abs(d) < 1e-12)
                        {
                            continue;
                        }

                        var ux = (((a.X * a.X) + (a.Y * a.Y)) * (b.Y - c.Y)
                            + ((b.X * b.X) + (b.Y * b.Y)) * (c.Y - a.Y)
                            + ((c.X * c.X) + (c.Y * c.Y)) * (a.Y - b.Y)) / d;
                        var uy = (((a.X * a.X) + (a.Y * a.Y)) * (c.X - b.X)
                            + ((b.X * b.X) + (b.Y * b.Y)) * (a.X - c.X)
                            + ((c.X * c.X) + (c.Y * c.Y)) * (b.X - a.X)) / d;
                        var radiusSquared = ((a.X - ux) * (a.X - ux)) + ((a.Y - uy) * (a.Y - uy));
                        if (radiusSquared >= bestRadius)
                        {
                            continue;
                        }

                        var empty = true;
                        foreach (var other in points)
                        {
                            var distanceSquared = ((other.X - ux) * (other.X - ux)) + ((other.Y - uy) * (other.Y - uy));
                            if (distanceSquared < radiusSquared - 1e-9)
                            {
                                empty = false;
                                break;
                            }
                        }

                        if (!empty)
                        {
                            continue;
                        }

                        bestRadius = radiusSquared;
                        best = (l1 * surface(a.X, a.Y)) + (l2 * surface(b.X, b.Y)) + (l3 * surface(c.X, c.Y));
                    }
                }
            }

            return best;
        }

        private static void AssertDensifiedSurfaceWithinTolerance(Func<double, double, double> surface, double size)
        {
            var polygon = Square(size);
            var footprint = new FloorFootprint { FloorId = 1, OuterLoop = polygon, BottomElevationAt = surface };
            const double margin = 0.05;

            var result = SurfaceDensifier.Build(
                surface,
                point => footprint.CoversOwnRegion(point, 1e-6),
                point => footprint.NearestBoundary(point).Distance > margin,
                0, 0, size, size,
                initialStep: 1.5,
                tolerance: 0.0005,
                maxDepth: 4,
                maxPoints: 20000);

            Assert.That(result.HitLimit, Is.False, "細分應在深度上限內達標");
            Assert.That(result.MaxResidual, Is.LessThanOrEqualTo(0.0005));
            Assert.That(result.Points.Count, Is.GreaterThan(100));
            Assert.That(
                result.Points.All(p => p.X > margin && p.X < size - margin && p.Y > margin && p.Y < size - margin),
                Is.True, "加密點避開板緣");

            // 網格頂點＝內部加密點＋加密後的板緣頂點（直邊也補點；只有 4 個角點時邊上以弦代弧）。
            var refinedLoop = SurfaceDensifier.RefineLoop(
                polygon, surface, maxSegment: 1.5, tolerance: 0.0005, maxDepth: 6, minSegment: 0.1);
            Assert.That(refinedLoop.Count, Is.GreaterThan(polygon.Count), "直邊需要補點");
            var meshPoints = result.Points.Concat(refinedLoop).ToList();
            var random = new Random(12345);
            var checkedCount = 0;
            var worst = 0.0;
            for (var n = 0; n < 150; n++)
            {
                var target = new Point2D(
                    margin + (random.NextDouble() * (size - (2 * margin))),
                    margin + (random.NextDouble() * (size - (2 * margin))));
                var interpolated = DelaunayInterpolate(meshPoints, surface, target);
                if (!interpolated.HasValue)
                {
                    continue;
                }

                checkedCount++;
                worst = Math.Max(worst, Math.Abs(interpolated.Value - surface(target.X, target.Y)));
            }

            Assert.That(checkedCount, Is.GreaterThan(100), "多數取樣點應找得到包含它的 Delaunay 三角形");
            Assert.That(worst, Is.LessThan(MaxAllowedError), $"最差內插誤差 {worst}");
        }

        [Test]
        public void Paraboloid_DensifiedMesh_InterpolationErrorBelowTolerance()
        {
            AssertDensifiedSurfaceWithinTolerance(
                (x, y) => 0.004 * (((x - 15) * (x - 15)) + ((y - 15) * (y - 15))), 30);
        }

        [Test]
        public void Cylinder_DensifiedMesh_InterpolationErrorBelowTolerance()
        {
            // 橢圓柱面（沿 y 軸、x 向半徑 24 的拱）：彎曲坡道的典型形態；曲率不小，需要細分。
            AssertDensifiedSurfaceWithinTolerance(
                (x, y) => 20 - Math.Sqrt((12 * 12) - ((x - 15) * (x - 15)) * 0.25), 30);
        }

        [Test]
        public void PlanarSurface_NeedsNoRefinement_OnlyGridPoints()
        {
            var polygon = Square(30);
            var footprint = new FloorFootprint { FloorId = 1, OuterLoop = polygon };
            var result = SurfaceDensifier.Build(
                (x, y) => (0.1 * x) + (0.05 * y),
                point => footprint.CoversOwnRegion(point, 1e-6),
                point => true,
                0, 0, 30, 30, 1.5, 0.0005, 4, 20000);

            Assert.That(result.HitLimit, Is.False);
            Assert.That(result.MaxResidual, Is.LessThan(1e-9));
            Assert.That(result.Points.Count, Is.EqualTo(21 * 21), "平面只需初始格點（21×21 個節點），不細分");
        }

        [Test]
        public void HoleRegion_AndFailingEvaluations_AreSkipped()
        {
            var floor = new FloorFootprint
            {
                FloorId = 1,
                OuterLoop = Square(30),
                Holes = new List<IReadOnlyList<Point2D>>
                {
                    new List<Point2D> { new Point2D(10, 10), new Point2D(20, 10), new Point2D(20, 20), new Point2D(10, 20) }
                },
                BottomElevationAt = (x, y) =>
                {
                    if (x > 25)
                    {
                        throw new InvalidOperationException("求交失敗");
                    }

                    return 0.01 * x * x;
                }
            };

            var result = SurfaceDensifier.Build(
                floor.BottomElevationAt,
                point => floor.CoversOwnRegion(point, 1e-6),
                point => true,
                0, 0, 30, 30, 1.5, 0.0005, 4, 20000);

            Assert.That(result.Points.Any(p => p.X > 10.001 && p.X < 19.999 && p.Y > 10.001 && p.Y < 19.999), Is.False, "洞內不加點");
            Assert.That(result.Points.Any(p => p.X > 25), Is.False, "取不到高程的點不加");
            Assert.That(result.Points.Count, Is.GreaterThan(50));
        }

        [Test]
        public void MaxPoints_LimitsCount_AndFlagsHitLimit()
        {
            var polygon = Square(30);
            var footprint = new FloorFootprint { FloorId = 1, OuterLoop = polygon };
            var result = SurfaceDensifier.Build(
                (x, y) => Math.Sin(x) * Math.Cos(y),
                point => footprint.CoversOwnRegion(point, 1e-6),
                point => true,
                0, 0, 30, 30, 1.5, 0.0005, 4, 500);

            Assert.That(result.Points.Count, Is.LessThanOrEqualTo(500));
            Assert.That(result.HitLimit, Is.True);
        }

        // ---- 板緣內推方向 ----

        private static List<Point2D> UShape() => new List<Point2D>
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(7, 10),
            new Point2D(7, 3), new Point2D(3, 3), new Point2D(3, 10), new Point2D(0, 10)
        };

        [Test]
        public void InwardDirection_AtUShapeConvexCorner_PointsIntoMaterial_UnlikeCentroid()
        {
            var loop = UShape();
            var floor = new FloorFootprint { FloorId = 1, OuterLoop = loop };
            var vertex = new Point2D(3, 10); // U 形左臂內側頂角：形心 (5, 4.4) 落在凹槽（板外）
            const double eps = 0.02;

            // 舊做法：朝形心內推 → 落在凹槽內（板外）。
            var centroid = new Point2D(5.0, 4.4);
            var length = Math.Sqrt(((centroid.X - vertex.X) * (centroid.X - vertex.X))
                + ((centroid.Y - vertex.Y) * (centroid.Y - vertex.Y)));
            var oldNudge = new Point2D(
                vertex.X + ((centroid.X - vertex.X) / length * eps),
                vertex.Y + ((centroid.Y - vertex.Y) / length * eps));
            Assert.That(floor.CoversOwnRegion(oldNudge, 1e-6), Is.False, "舊做法會指到板外");

            Assert.That(BoundaryNudge.TryInwardDirection(loop, vertex, 1e-6, out var direction), Is.True);
            Assert.That(Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y)), Is.EqualTo(1).Within(1e-9));
            var nudged = new Point2D(vertex.X + (direction.X * eps), vertex.Y + (direction.Y * eps));
            Assert.That(floor.CoversOwnRegion(nudged, 1e-6), Is.True, "內向角平分線指向材料內");
            Assert.That(direction.X, Is.LessThan(0));
            Assert.That(direction.Y, Is.LessThan(0));
        }

        [Test]
        public void InwardDirection_AtLShapeReflexCorner_PointsIntoMaterial()
        {
            // L 形凹角 (4,4)：材料在 x<4 或 y<4，內向應是 (-1,-1)/√2。
            var loop = new List<Point2D>
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 4),
                new Point2D(4, 4), new Point2D(4, 10), new Point2D(0, 10)
            };
            var floor = new FloorFootprint { FloorId = 1, OuterLoop = loop };

            Assert.That(BoundaryNudge.TryInwardDirection(loop, new Point2D(4, 4), 1e-6, out var direction), Is.True);
            Assert.That(direction.X, Is.EqualTo(-Math.Sqrt(0.5)).Within(1e-9));
            Assert.That(direction.Y, Is.EqualTo(-Math.Sqrt(0.5)).Within(1e-9));
            Assert.That(floor.CoversOwnRegion(new Point2D(4 + (0.02 * direction.X), 4 + (0.02 * direction.Y)), 1e-6), Is.True);
        }

        [Test]
        public void InwardDirection_OnEdge_IsInwardNormal_ForBothWindings_AndFalseOffBoundary()
        {
            var ccw = Square(10);
            var cw = Square(10).AsEnumerable().Reverse().ToList();

            Assert.That(BoundaryNudge.TryInwardDirection(ccw, new Point2D(5, 0), 1e-6, out var d1), Is.True);
            Assert.That(d1.X, Is.EqualTo(0).Within(1e-9));
            Assert.That(d1.Y, Is.EqualTo(1).Within(1e-9));

            Assert.That(BoundaryNudge.TryInwardDirection(cw, new Point2D(10, 5), 1e-6, out var d2), Is.True);
            Assert.That(d2.X, Is.EqualTo(-1).Within(1e-9));
            Assert.That(d2.Y, Is.EqualTo(0).Within(1e-9));

            Assert.That(BoundaryNudge.TryInwardDirection(ccw, new Point2D(5, 5), 1e-6, out _), Is.False);
        }

        [Test]
        public void InwardDirection_OnHoleRing_PointsAwayFromHole_NotIntoIt()
        {
            // 環形板：外框 0..20、洞 5..15。洞緣點的「內向」要進入板材（遠離洞）。
            var outer = Square(20);
            var hole = new List<Point2D> { new Point2D(5, 5), new Point2D(15, 5), new Point2D(15, 15), new Point2D(5, 15) };
            var holes = new List<IReadOnlyList<Point2D>> { hole };
            var ring = new FloorFootprint { FloorId = 1, OuterLoop = outer, Holes = holes };
            const double eps = 0.02;

            // 洞緣邊上（左邊 x=5）：朝 -x（遠離洞）。
            Assert.That(BoundaryNudge.TryInwardDirection(outer, holes, new Point2D(5, 10), 1e-6, out var onEdge), Is.True);
            Assert.That(onEdge.X, Is.EqualTo(-1).Within(1e-9));
            Assert.That(onEdge.Y, Is.EqualTo(0).Within(1e-9));
            Assert.That(ring.CoversOwnRegion(new Point2D(5 + (eps * onEdge.X), 10), 1e-6), Is.True);

            // 洞角（5,5）：朝 (-1,-1)/√2，落在板材內。
            Assert.That(BoundaryNudge.TryInwardDirection(outer, holes, new Point2D(5, 5), 1e-6, out var corner), Is.True);
            Assert.That(corner.X, Is.EqualTo(-Math.Sqrt(0.5)).Within(1e-9));
            Assert.That(corner.Y, Is.EqualTo(-Math.Sqrt(0.5)).Within(1e-9));
            Assert.That(ring.CoversOwnRegion(new Point2D(5 + (eps * corner.X), 5 + (eps * corner.Y)), 1e-6), Is.True);

            // 外框邊上仍朝外框內；只看外框的舊版本對洞緣點回 false（改走形心備援會推進洞中央）。
            Assert.That(BoundaryNudge.TryInwardDirection(outer, holes, new Point2D(10, 0), 1e-6, out var outerEdge), Is.True);
            Assert.That(outerEdge.Y, Is.EqualTo(1).Within(1e-9));
            Assert.That(BoundaryNudge.TryInwardDirection(outer, new Point2D(5, 10), 1e-6, out _), Is.False);
        }

        [Test]
        public void OriginalOuterLoop_KeepsEdgePolicyMatching_AndOutlineEdge_OnOriginalEdges()
        {
            // 非平面板緣補點後（OuterLoop 被細分成 1.5 m 子線段），邊坡率政策與指紋仍要對原始邊。
            var original = Square(30);
            var refined = SurfaceDensifier.RefineLoop(
                original, (x, y) => 0.0, maxSegment: 1.5, tolerance: 0.0005, maxDepth: 6, minSegment: 0.1);
            Assert.That(refined.Count, Is.GreaterThan(original.Count));

            var footprint = new FloorFootprint { FloorId = 5, OuterLoop = refined, OriginalOuterLoop = original };
            var policies = new[] { EdgeSlopePolicies.FingerprintFromSegment(original[0], original[1], 12.0) };

            var (ratesOriginal, unmatchedOriginal) =
                EdgeSlopePolicies.MatchEdgeRates(footprint.OutlineLoop, policies, 2.0);
            Assert.That(unmatchedOriginal, Is.Empty);
            Assert.That(ratesOriginal.Count, Is.EqualTo(4));
            Assert.That(ratesOriginal[0], Is.EqualTo(12.0));

            // 反證：拿補點後的子線段去比對，政策對不上（被忽略）。
            var (_, unmatchedRefined) = EdgeSlopePolicies.MatchEdgeRates(footprint.OuterLoop, policies, 2.0);
            Assert.That(unmatchedRefined, Is.Not.Empty);

            var field = new BoundaryRateField(footprint.OutlineLoop, ratesOriginal, 2.0);
            Assert.That(field.RateAt(new Point2D(15, -0.5)), Is.EqualTo(12.0).Within(1e-9));

            // 取邊（含 SplitRegions 後）回傳原始邊，指紋才對得上。
            foreach (var region in footprint.SplitRegions())
            {
                Assert.That(region.OutlineLoop, Is.SameAs(original));
            }

            var edge = footprint.NearestOutlineEdge(new Point2D(15, -0.5), 1e-6);
            Assert.That(edge.RegionIndex, Is.EqualTo(0));
            Assert.That(edge.EdgeIndex, Is.EqualTo(0));
            Assert.That(edge.Start.X, Is.EqualTo(0));
            Assert.That(edge.End.X, Is.EqualTo(30));
        }

        [Test]
        public void PointGridIndex_MatchesLinearScan_AndIsFast()
        {
            const double tolerance = 0.003;
            var random = new Random(7);
            var points = Enumerable.Range(0, 20000)
                .Select(_ => new Point2D(random.NextDouble() * 200, random.NextDouble() * 200))
                .ToList();
            var index = new PointGridIndex(tolerance);
            foreach (var point in points)
            {
                index.Add(point.X, point.Y);
            }

            Assert.That(index.Count, Is.EqualTo(points.Count));
            for (var n = 0; n < 300; n++)
            {
                // 一半取已存在點附近（跨格邊界也要命中），一半取隨機點。
                var probe = n % 2 == 0
                    ? new Point2D(points[n * 7].X + (tolerance * 0.9 * ((n % 4) - 1.5) / 1.5), points[n * 7].Y)
                    : new Point2D(random.NextDouble() * 200, random.NextDouble() * 200);
                var expected = points.Any(p =>
                    ((p.X - probe.X) * (p.X - probe.X)) + ((p.Y - probe.Y) * (p.Y - probe.Y)) <= tolerance * tolerance);
                Assert.That(index.HasNear(probe.X, probe.Y), Is.EqualTo(expected), $"n={n}");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var hits = 0;
            for (var n = 0; n < 20000; n++)
            {
                if (index.HasNear(points[n].X, points[n].Y))
                {
                    hits++;
                }
            }

            stopwatch.Stop();
            Assert.That(hits, Is.EqualTo(20000));
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000), "2 萬次查詢（線性掃描約 4 億次比較）應遠低於 1 秒");
        }

        [Test]
        public void PartialCellStraddlingBoundary_IsEstimatedFromInsidePoints_AndFlagsHitLimit()
        {
            // 只有跨邊界那一格（x∈[19.5,21]，邊界在 x=20.5）有曲率：其餘格為平面。
            // 舊版本任何缺點的組合都當 0 誤差，這格既不細分也不警告；現在只用區域內的取樣點估誤差。
            double Surface(double x, double y) => x >= 19.6 ? 0.1 * (y - 0.75) * (y - 0.75) : 0.0;

            var result = SurfaceDensifier.Build(
                Surface,
                point => point.X <= 20.5 && point.Y >= 0 && point.Y <= 1.5,
                point => true,
                0, 0, 21, 1.5,
                initialStep: 1.5, tolerance: 0.0005, maxDepth: 0, maxPoints: 1000);

            Assert.That(result.HitLimit, Is.True, "跨邊界格誤差超標、已達深度上限，必須警告");
            Assert.That(result.MaxResidual, Is.GreaterThan(0.0005));

            // 同一格若允許細分則會細分（加入中心與邊中點）。
            var refined = SurfaceDensifier.Build(
                Surface,
                point => point.X <= 20.5 && point.Y >= 0 && point.Y <= 1.5,
                point => true,
                0, 0, 21, 1.5,
                initialStep: 1.5, tolerance: 0.0005, maxDepth: 3, maxPoints: 1000);
            Assert.That(refined.Points.Count, Is.GreaterThan(result.Points.Count));
        }

        [Test]
        public void FullyOutsideCell_IsZeroError_NoWarning()
        {
            var result = SurfaceDensifier.Build(
                (x, y) => x * x,
                point => point.X <= 1.0,
                point => true,
                0, 0, 6, 1.5,
                initialStep: 1.5, tolerance: 0.0005, maxDepth: 0, maxPoints: 100);

            // 只有 x≤1 的點可用；落在區域外的格（x≥1.5）可用點為 0，誤差當 0，不應警告。
            // 第一格（x∈[0,1.5]）可用點只落在 x=0、0.75 兩個 x 位置（對 x 線性可完全擬合），誤差 0。
            Assert.That(result.HitLimit, Is.False);
        }

        [Test]
        public void BuildStepRings_DoesNotAbort_WhenNonPlanarFaceCannotEvaluate()
        {
            var flat = new FloorFootprint
            {
                FloorId = 1,
                OuterLoop = new List<Point2D> { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10) },
                BottomElevationAt = (x, y) => 0
            };
            var curved = new FloorFootprint
            {
                FloorId = 2,
                OuterLoop = new List<Point2D> { new Point2D(10, 0), new Point2D(20, 0), new Point2D(20, 10), new Point2D(10, 10) },
                BottomElevationAt = (x, y) => throw new InvalidOperationException("求交失敗"),
                RequiresInteriorDensification = true
            };

            IReadOnlyList<IReadOnlyList<Point2D?>> rings = null;
            Assert.DoesNotThrow(() => rings = FootprintCoverage.BuildStepRings(new[] { flat, curved }, 0.03, 1e-3, 0.005));
            Assert.That(rings, Is.Not.Null);
        }
    }
}
