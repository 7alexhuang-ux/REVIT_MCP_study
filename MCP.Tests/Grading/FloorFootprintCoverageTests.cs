using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// 樓板板底覆蓋：洞不屬於該板、多板重疊取最低板底、斜面板底高程隨 XY 變化、多朝下面。
    /// 對應整地在真實模型的兩個失敗：板有開口（嵌入地下室板）被誤判重疊衝突；
    /// 斜板／多面板被拒（沒有單一平面底面）。
    /// </summary>
    [TestFixture]
    public class FloorFootprintCoverageTests
    {
        private const double Tolerance = 1e-3;

        private static List<Point2D> Rect(double x0, double y0, double x1, double y1) => new List<Point2D>
        {
            new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1)
        };

        private static FloorFootprint Flat(
            long id, List<Point2D> outer, double z, params List<Point2D>[] holes) => new FloorFootprint
        {
            FloorId = id,
            OuterLoop = outer,
            Holes = holes.Select(hole => (IReadOnlyList<Point2D>)hole).ToList(),
            BottomElevationAt = (x, y) => z
        };

        [Test]
        public void HoleInterior_IsNotCoveredByThatFloor()
        {
            var floor = Flat(1, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15));

            Assert.That(floor.CoversXY(new Point2D(10, 10), Tolerance), Is.False, "洞內不屬於該板");
            Assert.That(floor.CoversXY(new Point2D(2, 2), Tolerance), Is.True, "洞外板面");
            Assert.That(floor.CoversXY(new Point2D(5, 10), Tolerance), Is.True, "洞邊界仍是板緣");
            Assert.That(floor.CoversXY(new Point2D(0, 10), Tolerance), Is.True, "外框邊界");
            Assert.That(floor.CoversXY(new Point2D(25, 10), Tolerance), Is.False, "外框外");
        }

        [Test]
        public void PointInHole_NoControllingFloor_WhenNothingFillsTheHole()
        {
            var floors = new[] { Flat(1, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15)) };

            Assert.That(
                FootprintCoverage.TryLowestBottomZ(floors, new Point2D(10, 10), Tolerance, out _), Is.False);
        }

        [Test]
        public void BasementSlabInsideOpening_ControlsHoleAndOuterSlabControlsRest_NoConflict()
        {
            // 一樓板 z=0 中間開口，開口內嵌地下室板 z=-2（使用者案例 315196 / 314871）。
            var ground = Flat(315196, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15));
            var basement = Flat(314871, Rect(5, 5, 15, 15), -2);
            var floors = new[] { ground, basement };

            Assert.That(FootprintCoverage.TryLowestBottomZ(floors, new Point2D(10, 10), Tolerance, out var holeZ), Is.True);
            Assert.That(holeZ, Is.EqualTo(-2).Within(1e-9));
            Assert.That(FootprintCoverage.TryLowestBottomZ(floors, new Point2D(2, 2), Tolerance, out var ringZ), Is.True);
            Assert.That(ringZ, Is.EqualTo(0).Within(1e-9));

            // 地下室板比開口小一圈時，扣洞後兩板不重疊、不算高程衝突。
            var insetBasement = Flat(314872, Rect(6, 6, 14, 14), -2);
            Assert.That(
                FootprintCoverage.HasElevationConflict(ground, insetBasement, Tolerance, Tolerance), Is.False);
        }

        [Test]
        public void OverlappingFloorsAtDifferentElevations_TakeLowest_RegardlessOfOrder()
        {
            var upper = Flat(1, Rect(0, 0, 10, 10), 3);
            var lower = Flat(2, Rect(5, 5, 15, 15), -1);
            var point = new Point2D(7, 7); // 兩板重疊處

            Assert.That(FootprintCoverage.TryLowestBottomZ(new[] { upper, lower }, point, Tolerance, out var a), Is.True);
            Assert.That(FootprintCoverage.TryLowestBottomZ(new[] { lower, upper }, point, Tolerance, out var b), Is.True);
            Assert.That(a, Is.EqualTo(-1).Within(1e-9));
            Assert.That(b, Is.EqualTo(-1).Within(1e-9));

            Assert.That(FootprintCoverage.TryLowestBottomZ(new[] { upper, lower }, new Point2D(2, 2), Tolerance, out var onlyUpper), Is.True);
            Assert.That(onlyUpper, Is.EqualTo(3).Within(1e-9));
            Assert.That(FootprintCoverage.TryLowestBottomZ(new[] { upper, lower }, new Point2D(14, 14), Tolerance, out var onlyLower), Is.True);
            Assert.That(onlyLower, Is.EqualTo(-1).Within(1e-9));
            Assert.That(FootprintCoverage.TryLowestBottomZ(new[] { upper, lower }, new Point2D(30, 30), Tolerance, out _), Is.False);
        }

        [Test]
        public void SlopedBottomFace_ElevationFollowsXY()
        {
            // 朝下斜面：法向量 (-sinθ, 0, -cosθ)（朝下且朝 -x），平面 z = 2 - x·tanθ，tanθ = 0.2。
            var theta = Math.Atan(0.2);
            var nx = -Math.Sin(theta);
            var nz = -Math.Cos(theta);

            double Z(double x, double y) => FootprintCoverage.PlaneZ(0, 0, 2, nx, 0, nz, x, y);

            Assert.That(Z(0, 0), Is.EqualTo(2).Within(1e-9));
            Assert.That(Z(10, 0), Is.EqualTo(2 - (10 * 0.2)).Within(1e-9));
            Assert.That(Z(10, 7), Is.EqualTo(Z(10, 0)).Within(1e-9), "沿等高線方向不變");
            Assert.That(Z(-5, 3), Is.EqualTo(2 + 1).Within(1e-9));

            var sloped = new FloorFootprint
            {
                FloorId = 9,
                OuterLoop = Rect(0, 0, 10, 10),
                BottomElevationAt = Z
            };
            Assert.That(sloped.TryLowestBottomZ(new Point2D(5, 5), Tolerance, out var z), Is.True);
            Assert.That(z, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void VerticalFace_PlaneZ_IsNaN()
        {
            Assert.That(double.IsNaN(FootprintCoverage.PlaneZ(0, 0, 0, 1, 0, 0, 1, 1)), Is.True);
        }

        [Test]
        public void NaNBottomInsideProjection_Throws_WithFloorIdAndPoint_NotSilentlySkipped()
        {
            var broken = new FloorFootprint
            {
                FloorId = 4242,
                OuterLoop = Rect(0, 0, 10, 10),
                BottomElevationAt = (x, y) => double.NaN
            };
            var good = Flat(2, Rect(0, 0, 10, 10), 1);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                FootprintCoverage.TryLowestBottomZ(new[] { broken, good }, new Point2D(5, 5), Tolerance, out _));
            Assert.That(ex.Message, Does.Contain("4242"));
            Assert.That(ex.Message, Does.Contain("5.000"));

            // 點在板投影外時不會呼叫高程函式，不受影響。
            Assert.That(
                FootprintCoverage.TryLowestBottomZ(new[] { broken }, new Point2D(50, 50), Tolerance, out _), Is.False);
        }

        [Test]
        public void StepRings_BasementFlushWithOpening_PutsHigherSidePointsBesideSharedEdge()
        {
            // 地下室板外框剛好等於一樓開口：共邊頂點取較低板底（-2），
            // 一樓板側緊貼共邊一圈點必須存在（目標＝一樓板底 0），否則地形從共邊斜到下一個頂點挖出楔形。
            var ground = Flat(315196, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15));
            var basement = Flat(314871, Rect(5, 5, 15, 15), -2);
            var floors = new[] { ground, basement };
            const double inset = 0.03;

            var rings = FootprintCoverage.BuildStepRings(floors, inset, Tolerance, 0.005);

            var points = rings.SelectMany(ring => ring).Where(p => p.HasValue).Select(p => p.Value).ToList();
            Assert.That(points, Is.Not.Empty);
            foreach (var q in points)
            {
                Assert.That(FootprintCoverage.TryLowestBottomZ(floors, q, Tolerance, out var z), Is.True);
                Assert.That(z, Is.EqualTo(0).Within(1e-9), "階差環點落在較高的一樓板側，目標＝一樓板底");
                Assert.That(basement.CoversOwnRegion(q, Tolerance), Is.False, "不可落進較低的地下室板內");
                // 緊貼共邊：距洞緣（＝地下室外框）≤ inset，遠小於一般頂點間距。
                Assert.That(ground.NearestBoundary(q).Distance, Is.LessThanOrEqualTo(inset + 1e-9));
            }

            // 共邊四個角、每邊兩端都有環點：環連續，可刻成封閉摺線。
            Assert.That(rings.Any(ring => ring.All(p => p.HasValue)), Is.True);
        }

        [Test]
        public void StepRings_StepBetweenFacesOfSameFloor_RaisesHigherFaceOnly()
        {
            var floor = Flat(7, Rect(0, 0, 10, 10), 0);
            floor.ExtraRegions = new[] { Flat(7, Rect(10, 0, 20, 10), -1.5) };
            var regions = floor.SplitRegions();

            var rings = FootprintCoverage.BuildStepRings(regions, 0.03, Tolerance, 0.005);
            var points = rings.SelectMany(ring => ring).Where(p => p.HasValue).Select(p => p.Value).ToList();

            Assert.That(points, Is.Not.Empty);
            Assert.That(points.All(p => p.X < 10), Is.True, "只在較高（左）面加環");
            Assert.That(points.Any(p => p.X > 9.9), Is.True, "環點貼在共邊旁");
        }

        [Test]
        public void StepRings_StackedOverlap_AddsPointsOutsideLowerSlabInsideUpperSlab()
        {
            var upper = Flat(1, Rect(0, 0, 20, 20), 3);
            var lower = Flat(2, Rect(5, 5, 15, 15), -1);
            var floors = new[] { upper, lower };

            var rings = FootprintCoverage.BuildStepRings(floors, 0.03, Tolerance, 0.005);
            var points = rings.SelectMany(ring => ring).Where(p => p.HasValue).Select(p => p.Value).ToList();

            Assert.That(points, Is.Not.Empty);
            Assert.That(points.All(p => !lower.CoversOwnRegion(p, Tolerance)), Is.True);
            Assert.That(points.All(p => upper.CoversOwnRegion(p, Tolerance)), Is.True);
        }

        [Test]
        public void StepRings_SingleFlatOrSlopedSlab_NoStepRing()
        {
            var flat = Flat(1, Rect(0, 0, 10, 10), 0);
            Assert.That(FootprintCoverage.BuildStepRings(new[] { flat }, 0.03, Tolerance, 0.005), Is.Empty);

            // 單一斜板：邊界內外偏移點與邊界在同一平面上，不是階差。
            var sloped = new FloorFootprint
            {
                FloorId = 2,
                OuterLoop = Rect(0, 0, 10, 10),
                BottomElevationAt = (x, y) => 0.2 * x
            };
            Assert.That(FootprintCoverage.BuildStepRings(new[] { sloped }, 0.03, Tolerance, 0.005), Is.Empty);
        }

        [Test]
        public void NearestOutlineEdge_SkipsEdgeSharedByTwoFaces_AndReportsRegion()
        {
            var floor = Flat(7, Rect(0, 0, 10, 10), 0);
            floor.ExtraRegions = new[] { Flat(7, Rect(10, 0, 20, 10), -1.5) };

            // 參考點在共邊上：共邊是內部邊，應改選最近的外緣邊。
            var edge = floor.NearestOutlineEdge(new Point2D(10, 4), Tolerance);
            Assert.That(edge.RegionIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(edge.Start.X == 10 && edge.End.X == 10, Is.False, "不可選到共用的內部邊");

            // 點在右面外緣旁 → 選到右面（RegionIndex 1）的邊。
            var right = floor.NearestOutlineEdge(new Point2D(20.5, 5), Tolerance);
            Assert.That(right.RegionIndex, Is.EqualTo(1));
            Assert.That(right.Distance, Is.EqualTo(0.5).Within(1e-9));

            // 單一面＝等同外框最近邊。
            var single = Flat(1, Rect(0, 0, 10, 10), 0).NearestOutlineEdge(new Point2D(5, -1), Tolerance);
            Assert.That(single.RegionIndex, Is.EqualTo(0));
            Assert.That(single.Distance, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void MergeFloorResults_SumsAreasAndMergesCellsByLabel()
        {
            var first = new ParcelFloorLimitResult
            {
                FloorId = 1,
                FloorAreaSquareMeters = 60,
                CoveredAreaSquareMeters = 60,
                OpenSpaceOnlyOverlapAreaSquareMeters = 10,
                ExcludedOverlapAreaSquareMeters = 5,
                MatchedCells = new List<ParcelCellOverlap>
                {
                    new ParcelCellOverlap { CellLabel = "A1", Restriction = ParcelRestriction.OpenSpaceOnly, OverlapAreaSquareMeters = 10 },
                    new ParcelCellOverlap { CellLabel = "B2", Restriction = ParcelRestriction.ExcludedFromOpenSpace, OverlapAreaSquareMeters = 5 }
                }
            };
            var second = new ParcelFloorLimitResult
            {
                FloorId = 1,
                FloorAreaSquareMeters = 40,
                CoveredAreaSquareMeters = 30,
                OpenSpaceOnlyOverlapAreaSquareMeters = 6,
                ExcludedOverlapAreaSquareMeters = 0,
                MatchedCells = new List<ParcelCellOverlap>
                {
                    new ParcelCellOverlap { CellLabel = "A1", Restriction = ParcelRestriction.OpenSpaceOnly, OverlapAreaSquareMeters = 6 }
                }
            };

            var merged = ParcelLimitCheck.MergeFloorResults(1, new[] { first, second });

            Assert.That(merged.FloorAreaSquareMeters, Is.EqualTo(100));
            Assert.That(merged.CoveredAreaSquareMeters, Is.EqualTo(90));
            Assert.That(merged.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(16));
            Assert.That(merged.ExcludedOverlapAreaSquareMeters, Is.EqualTo(5));
            Assert.That(merged.MatchedCells.Count, Is.EqualTo(2));
            var a1 = merged.MatchedCells.Single(cell => cell.CellLabel == "A1");
            Assert.That(a1.OverlapAreaSquareMeters, Is.EqualTo(16));
            Assert.That(a1.OverlapFractionOfFloor, Is.EqualTo(0.16).Within(1e-12));
        }

        [Test]
        public void MultiFaceFloor_LowestFaceWins_AndSplitRegionsFlattens()
        {
            // 一片樓板兩個朝下面：左半 z=0、右半 z=-1.5（階差板底）；另有共邊交界處。
            var primary = Flat(7, Rect(0, 0, 10, 10), 0);
            var extra = Flat(7, Rect(10, 0, 20, 10), -1.5);
            primary.ExtraRegions = new[] { extra };

            Assert.That(primary.CoversXY(new Point2D(15, 5), Tolerance), Is.True, "覆蓋含其他朝下面");
            Assert.That(primary.TryLowestBottomZ(new Point2D(15, 5), Tolerance, out var rightZ), Is.True);
            Assert.That(rightZ, Is.EqualTo(-1.5).Within(1e-9));
            Assert.That(primary.TryLowestBottomZ(new Point2D(5, 5), Tolerance, out var leftZ), Is.True);
            Assert.That(leftZ, Is.EqualTo(0).Within(1e-9));
            Assert.That(primary.TryLowestBottomZ(new Point2D(10, 5), Tolerance, out var seamZ), Is.True);
            Assert.That(seamZ, Is.EqualTo(-1.5).Within(1e-9), "共邊處取最低");

            var regions = primary.SplitRegions();
            Assert.That(regions.Count, Is.EqualTo(2));
            Assert.That(regions.All(region => region.ExtraRegions == null || region.ExtraRegions.Count == 0), Is.True);
            Assert.That(regions.All(region => region.FloorId == 7), Is.True);
        }

        [Test]
        public void OwnRegionArea_SubtractsHoles()
        {
            var floor = Flat(1, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15));
            Assert.That(floor.OwnRegionArea(), Is.EqualTo(400 - 100).Within(1e-9));
        }

        [Test]
        public void NearestBoundary_ConsidersHoleEdge()
        {
            var floor = Flat(1, Rect(0, 0, 20, 20), 0, Rect(5, 5, 15, 15));

            var inHole = floor.NearestBoundary(new Point2D(10, 6)); // 距洞下緣 1、距外框 6
            Assert.That(inHole.OnHole, Is.True);
            Assert.That(inHole.Distance, Is.EqualTo(1).Within(1e-9));

            var nearOuter = floor.NearestBoundary(new Point2D(1, 10)); // 距外框 1、距洞 4
            Assert.That(nearOuter.OnHole, Is.False);
            Assert.That(nearOuter.Distance, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void AllLoops_OuterFirstThenHoles()
        {
            var floor = Flat(1, Rect(0, 0, 20, 20), 0, Rect(2, 2, 4, 4), Rect(8, 8, 10, 10));
            Assert.That(floor.AllLoops().Count(), Is.EqualTo(3));
            Assert.That(floor.AllLoops().First(), Is.SameAs(floor.OuterLoop));
        }

        [Test]
        public void HasElevationConflict_StillDetectsStackedSlabsAtDifferentElevations()
        {
            // 自動納入鄰接樓板時用：上下疊板且不等高仍視為衝突（只有主流程不再報錯）。
            var first = Flat(1, Rect(0, 0, 10, 10), 0);
            var second = Flat(2, Rect(0, 0, 10, 10), -3);
            var sameZ = Flat(3, Rect(0, 0, 10, 10), 0);

            Assert.That(FootprintCoverage.HasElevationConflict(first, second, Tolerance, 0.005), Is.True);
            Assert.That(FootprintCoverage.HasElevationConflict(first, sameZ, Tolerance, 0.005), Is.False);
        }
    }
}
