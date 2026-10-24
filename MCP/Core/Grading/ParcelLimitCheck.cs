using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 整地方案樓板 × 坵塊法限建坵塊疊圖重疊檢查。
    /// 零 Revit 相依、純幾何，可 NUnit 測試。方法設計見
    /// docs/superpowers/specs/2026-10-05-grading-parcel-limit-check-design.md。
    ///
    /// 門檻一律引用 <see cref="ParcelSlopeMath.SteepThresholdPercent"/>／
    /// <see cref="ParcelSlopeMath.NoOpenSpaceThresholdPercent"/>——本類別不重新判定坡度分級，
    /// 只拿呼叫端已分級好的 <see cref="ParcelCellSummary.Restriction"/> 做幾何重疊。
    /// 本類別只回報「哪塊樓板壓到哪些限建坵塊、各自面積」的幾何事實，絕不下
    /// 「可否開發建築」之類的終局法律結論——那由呼叫端的 disclaimer 與
    /// domain/parcel-slope-analysis.md 負責說明。
    /// </summary>
    public static class ParcelLimitCheck
    {
        /// <summary>
        /// 重疊面積容差（平方公尺）：重疊面積須大於此值才收錄進 <see cref="ParcelFloorLimitResult.MatchedCells"/>，
        /// 用於排除樓板與坵塊邊界剛好共線相切（幾何上零面積）的「擦邊」情形
        /// （2026-10-05 主線裁決：相切／零面積一律排除，不收錄 area=0 的紀錄）。
        /// </summary>
        public const double DefaultAreaToleranceSquareMeters = 0.01;

        /// <summary>
        /// 單一樓板 vs 坵塊清單的重疊檢查。樓板輪廓與坵塊角點須同單位（公尺）、同座標系
        /// （坵塊法輸出本身就是專案內部座標系的公尺值，樓板輪廓需由呼叫端先轉換，見
        /// CommandExecutor.GradingParcelLimitCheck.cs 的英呎→公尺轉換）。
        /// </summary>
        public static ParcelFloorLimitResult CheckFloor(
            long floorId,
            IReadOnlyList<Point2D> floorOuterLoopMeters,
            IReadOnlyList<ParcelCellSummary> cells,
            double areaToleranceSquareMeters = DefaultAreaToleranceSquareMeters)
        {
            if (floorOuterLoopMeters == null || floorOuterLoopMeters.Count < 3)
            {
                throw new ArgumentException("樓板輪廓至少需要三個頂點。", nameof(floorOuterLoopMeters));
            }

            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }

            var floorArea = Polygon2D.Area(floorOuterLoopMeters);
            var floorBounds = BoundingBoxOf(floorOuterLoopMeters);

            var matched = new List<ParcelCellOverlap>();
            var openSpaceOnlyArea = 0.0;
            var excludedArea = 0.0;

            // 2026-10-05 審查修正：涵蓋面積改成「對所有格（含 None）都算重疊」，不只限建格，
            // 這樣呼叫端才能算出「樓板落在坵塊網格涵蓋範圍外」的面積（格網只蓋等高線外接範圍，
            // 樓板超出範圍、圖層選錯、或該處恰好沒有任何格子時，過去會靜默算 0 且無提示）。
            var coveredArea = 0.0;

            foreach (var cell in cells)
            {
                if (cell == null)
                {
                    continue;
                }

                if (cell.CornersMeters == null || cell.CornersMeters.Count != 4)
                {
                    continue; // 坵塊必為 4 角正方格；資料不完整的格子跳過，不中止整個檢查。
                }

                // 效能優化（非必要但建議）：bounding box 粗篩，範圍不重疊即可跳過，
                // 避免 O(樓板×坵塊) 全部都跑 Sutherland-Hodgman。
                var cellBounds = BoundingBoxOf(cell.CornersMeters);
                if (cellBounds.MaxX < floorBounds.MinX || cellBounds.MinX > floorBounds.MaxX
                    || cellBounds.MaxY < floorBounds.MinY || cellBounds.MinY > floorBounds.MaxY)
                {
                    continue;
                }

                var clipped = Clip(floorOuterLoopMeters, cell.CornersMeters);
                if (clipped.Count < 3)
                {
                    continue; // 退化（不相交或僅共線相切）。
                }

                var overlapArea = Polygon2D.Area(clipped);
                if (overlapArea <= areaToleranceSquareMeters)
                {
                    continue; // 零面積／近似相切，依裁決排除。
                }

                coveredArea += overlapArea; // None 格也算進涵蓋面積，但不進 matched／限建面積。

                if (cell.Restriction == ParcelRestriction.None)
                {
                    continue; // 只有限建格才收錄進 MatchedCells、計入限建面積。
                }

                matched.Add(new ParcelCellOverlap
                {
                    CellLabel = cell.Label,
                    Restriction = cell.Restriction,
                    SlopePercent = cell.SlopePercent,
                    OverlapAreaSquareMeters = overlapArea,
                    OverlapFractionOfFloor = floorArea > 0 ? overlapArea / floorArea : 0,
                });

                if (cell.Restriction == ParcelRestriction.OpenSpaceOnly)
                {
                    openSpaceOnlyArea += overlapArea;
                }
                else
                {
                    excludedArea += overlapArea;
                }
            }

            // 浮點誤差可能讓涵蓋面積微量超過樓板面積（例如格邊剛好貼合樓板邊界時的重複收尾），
            // 夾回 [0, floorArea] 避免「未涵蓋面積」算出負數。
            var clampedCoveredArea = Math.Max(0.0, Math.Min(coveredArea, floorArea));

            return new ParcelFloorLimitResult
            {
                FloorId = floorId,
                FloorAreaSquareMeters = floorArea,
                CoveredAreaSquareMeters = clampedCoveredArea,
                OpenSpaceOnlyOverlapAreaSquareMeters = openSpaceOnlyArea,
                ExcludedOverlapAreaSquareMeters = excludedArea,
                MatchedCells = matched,
            };
        }

        /// <summary>
        /// 樓板輪廓的「標籤安全落點」：優先用面積質心；質心落在板外（常見於 U 形等極端凹多邊形）
        /// 時，退回最近邊界點並沿該邊內法線方向內推一小段，確保落點在板內而非剛好壓在邊上。
        /// 兩者都失敗（退化多邊形）時回傳頂點平均（舊行為）作最後備援。
        /// </summary>
        internal static Point2D ComputeLabelAnchor(IReadOnlyList<Point2D> loop)
        {
            if (loop == null || loop.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(loop));
            }

            var centroid = PolygonCentroid(loop);
            const double containmentTolerance = 1e-6;
            if (Polygon2D.Contains(loop, centroid, containmentTolerance))
            {
                return centroid;
            }

            var nearest = Polygon2D.NearestBoundaryPointDetailed(loop, centroid);
            var edgeStart = loop[nearest.EdgeIndex];
            var edgeEnd = loop[(nearest.EdgeIndex + 1) % loop.Count];
            var edgeDx = edgeEnd.X - edgeStart.X;
            var edgeDy = edgeEnd.Y - edgeStart.Y;
            var edgeLength = Math.Sqrt((edgeDx * edgeDx) + (edgeDy * edgeDy));
            if (edgeLength < 1e-9)
            {
                return nearest.Point;
            }

            const double inwardStepMeters = 0.1; // 10 cm，確保落點明確在板內、不是剛好壓在邊上。
            var normalX = -edgeDy / edgeLength;
            var normalY = edgeDx / edgeLength;

            var candidateA = new Point2D(
                nearest.Point.X + (normalX * inwardStepMeters), nearest.Point.Y + (normalY * inwardStepMeters));
            if (Polygon2D.Contains(loop, candidateA, containmentTolerance))
            {
                return candidateA;
            }

            var candidateB = new Point2D(
                nearest.Point.X - (normalX * inwardStepMeters), nearest.Point.Y - (normalY * inwardStepMeters));
            return Polygon2D.Contains(loop, candidateB, containmentTolerance) ? candidateB : nearest.Point;
        }

        /// <summary>多邊形面積質心（非頂點平均）；退化（面積近零）時回頂點平均。</summary>
        internal static Point2D PolygonCentroid(IReadOnlyList<Point2D> loop)
        {
            double doubledArea = 0;
            double cx = 0;
            double cy = 0;
            for (var i = 0; i < loop.Count; i++)
            {
                var current = loop[i];
                var next = loop[(i + 1) % loop.Count];
                var cross = (current.X * next.Y) - (next.X * current.Y);
                doubledArea += cross;
                cx += (current.X + next.X) * cross;
                cy += (current.Y + next.Y) * cross;
            }

            if (Math.Abs(doubledArea) < 1e-12)
            {
                double sumX = 0, sumY = 0;
                foreach (var p in loop)
                {
                    sumX += p.X;
                    sumY += p.Y;
                }

                return new Point2D(sumX / loop.Count, sumY / loop.Count);
            }

            var factor = 1.0 / (3.0 * doubledArea);
            return new Point2D(cx * factor, cy * factor);
        }

        /// <summary>
        /// Sutherland-Hodgman 多邊形裁切：用凸多邊形 <paramref name="clipPolygon"/>（坵塊，必為凸、
        /// 4 角正方格）裁切 <paramref name="subjectPolygon"/>（樓板，允許凹多邊形）。
        /// 兩者繞向不拘——內部一律先正規化為逆時針再裁切。
        /// </summary>
        internal static IReadOnlyList<Point2D> Clip(
            IReadOnlyList<Point2D> subjectPolygon, IReadOnlyList<Point2D> clipPolygon)
        {
            if (subjectPolygon == null || subjectPolygon.Count < 3
                || clipPolygon == null || clipPolygon.Count < 3)
            {
                return Array.Empty<Point2D>();
            }

            var orientedClip = SignedArea(clipPolygon) < 0
                ? clipPolygon.Reverse().ToList()
                : clipPolygon.ToList();

            IReadOnlyList<Point2D> output = subjectPolygon;
            for (var i = 0; i < orientedClip.Count && output.Count > 0; i++)
            {
                var clipA = orientedClip[i];
                var clipB = orientedClip[(i + 1) % orientedClip.Count];
                output = ClipAgainstEdge(output, clipA, clipB);
            }

            return output;
        }

        private static IReadOnlyList<Point2D> ClipAgainstEdge(
            IReadOnlyList<Point2D> input, Point2D clipA, Point2D clipB)
        {
            var output = new List<Point2D>(input.Count + 1);
            for (var j = 0; j < input.Count; j++)
            {
                var current = input[j];
                var previous = input[(j - 1 + input.Count) % input.Count];
                var currentInside = IsInside(clipA, clipB, current);
                var previousInside = IsInside(clipA, clipB, previous);

                if (currentInside)
                {
                    if (!previousInside)
                    {
                        output.Add(IntersectEdges(previous, current, clipA, clipB));
                    }

                    output.Add(current);
                }
                else if (previousInside)
                {
                    output.Add(IntersectEdges(previous, current, clipA, clipB));
                }
            }

            return output;
        }

        /// <summary>CCW 裁切邊 a→b 的左側（含邊上，容差內）視為內側。</summary>
        private static bool IsInside(Point2D a, Point2D b, Point2D p)
        {
            var cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
            return cross >= -1e-9;
        }

        private static Point2D IntersectEdges(Point2D p1, Point2D p2, Point2D clipA, Point2D clipB)
        {
            var a1 = clipB.Y - clipA.Y;
            var b1 = clipA.X - clipB.X;
            var c1 = (a1 * clipA.X) + (b1 * clipA.Y);

            var a2 = p2.Y - p1.Y;
            var b2 = p1.X - p2.X;
            var c2 = (a2 * p1.X) + (b2 * p1.Y);

            var det = (a1 * b2) - (a2 * b1);
            if (Math.Abs(det) < 1e-12)
            {
                return p2; // 近似平行退化：不應發生於正常呼叫，回傳端點避免除以零。
            }

            var x = ((b2 * c1) - (b1 * c2)) / det;
            var y = ((a1 * c2) - (a2 * c1)) / det;
            return new Point2D(x, y);
        }

        private static double SignedArea(IReadOnlyList<Point2D> polygon)
        {
            var doubledArea = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var next = polygon[(i + 1) % polygon.Count];
                doubledArea += (current.X * next.Y) - (next.X * current.Y);
            }

            return doubledArea / 2.0;
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) BoundingBoxOf(
            IReadOnlyList<Point2D> polygon)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var p in polygon)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            return (minX, minY, maxX, maxY);
        }
    }

    /// <summary>
    /// 英呎→公尺換算：抽成不依賴 Revit API 的純函式（可測）。
    /// 1 英呎＝0.3048 公尺是國際標準定義的精確值，與 Revit <c>UnitUtils.ConvertFromInternalUnits</c>
    /// 用 <c>UnitTypeId.Meters</c> 換算的結果一致（2026-10-05 審查修正：原本的換算
    /// 〔<c>CommandExecutor.ParcelSlope.cs</c> 的 <c>ParcelFeetToMeters</c>〕因為呼叫 Revit API，
    /// 測試專案拿不到，沒有專門的等式斷言；本函式零 Revit 相依，可直接寫等式測試）。
    /// </summary>
    public static class ParcelUnitConversion
    {
        public const double FeetToMetersFactor = 0.3048;

        public static double FeetToMeters(double feet) => feet * FeetToMetersFactor;

        public static Point2D FeetPointToMeters(Point2D feet) =>
            new Point2D(FeetToMeters(feet.X), FeetToMeters(feet.Y));
    }

    /// <summary>一格坵塊供疊圖檢查用的摘要（由呼叫端從坵塊法運算結果轉出，單位公尺）。</summary>
    public sealed class ParcelCellSummary
    {
        public string Label { get; set; }

        /// <summary>四角，沿用 ParcelSlopeMath.BuildGrid 的順序；不要求特定繞向，Clip 內部會自動正規化。</summary>
        public IReadOnlyList<Point2D> CornersMeters { get; set; }

        public double SlopePercent { get; set; }

        public ParcelRestriction Restriction { get; set; }
    }

    /// <summary>單一坵塊與樓板的重疊結果。</summary>
    public sealed class ParcelCellOverlap
    {
        public string CellLabel { get; set; }

        public ParcelRestriction Restriction { get; set; }

        public double SlopePercent { get; set; }

        public double OverlapAreaSquareMeters { get; set; }

        /// <summary>重疊面積佔該樓板總面積的比例（0~1）。</summary>
        public double OverlapFractionOfFloor { get; set; }
    }

    /// <summary>單一樓板 vs 全部限建坵塊的彙總結果。</summary>
    public sealed class ParcelFloorLimitResult
    {
        public long FloorId { get; set; }

        public double FloorAreaSquareMeters { get; set; }

        /// <summary>
        /// 樓板落在坵塊網格「任一格」範圍內的面積總和（含 None 格，平方公尺）。
        /// <c>FloorAreaSquareMeters - CoveredAreaSquareMeters</c> 是落在網格涵蓋範圍外的面積——
        /// 格網只蓋等高線外接範圍，樓板超出範圍、圖層選錯、或該處沒有任何格子時，
        /// 這個差值會 &gt; 0；呼叫端據此決定是否要加 warnings（見設計稿審查修正 1）。
        /// </summary>
        public double CoveredAreaSquareMeters { get; set; }

        /// <summary>(30%,55%] 限空地格的重疊面積總和（平方公尺）。</summary>
        public double OpenSpaceOnlyOverlapAreaSquareMeters { get; set; }

        /// <summary>&gt;55% 不得計入法定空地格的重疊面積總和（平方公尺）。</summary>
        public double ExcludedOverlapAreaSquareMeters { get; set; }

        /// <summary>有壓到（Restriction != None 且重疊面積 > 容差）的坵塊清單。</summary>
        public IReadOnlyList<ParcelCellOverlap> MatchedCells { get; set; }
    }
}
