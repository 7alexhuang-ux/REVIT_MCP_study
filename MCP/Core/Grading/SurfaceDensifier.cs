using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>內部加密結果。</summary>
    public sealed class DensifyResult
    {
        public IReadOnlyList<Point2D> Points { get; set; }

        /// <summary>收斂（或停在深度／點數上限）後，各格最大的線性內插中點誤差（與 elevation 同單位）。</summary>
        public double MaxResidual { get; set; }

        /// <summary>有格子因深度或點數上限停止細分、誤差仍超過容許。</summary>
        public bool HitLimit { get; set; }
    }

    /// <summary>
    /// 非平面板底的內部加密（純函式，無 Revit 相依）。
    /// 地形三角網只在板邊界有點時，彎曲（非平面）底面內部以弦代弧，誤差會超過 2 mm 驗收門檻。
    /// 做法：以固定格距鋪點，再對每個格子用「中心點對兩條對角線端點平均、四個邊中點對邊端點平均」
    /// 估計線性內插誤差，超過容許就四分細分（加入中心與四個邊中點），直到誤差達標或深度／點數上限。
    /// 取樣點由 isInside 決定（呼叫端扣洞；含板緣，板緣上的值用來估計貼緣格的誤差）；
    /// 實際放進結果的點另須通過 canAdd（呼叫端避開板緣，板緣已有邊界點）。
    /// elevation 丟 InvalidOperationException 的點視為不可用而略過。
    /// 板緣本身的以弦代弧由 <see cref="RefineLoop"/> 處理（邊上補點，使板緣頂點本身貼合曲面）。
    /// </summary>
    public static class SurfaceDensifier
    {
        /// <summary>
        /// 非平面板底的外框／洞環加密：每條邊先切到不超過 maxSegment，再自適應二分
        /// （邊中點高程與兩端平均相差超過 tolerance 就在中點補點，至多 maxDepth 層、邊長不小於 minSegment）。
        /// 長直邊上補的點讓三角網的邊界頂點貼合曲面（直邊兩端點間線性內插會以弦代弧）。
        /// 高程取不到（InvalidOperationException）的邊不做自適應細分。回傳環保持原繞向。
        /// </summary>
        public static IReadOnlyList<Point2D> RefineLoop(
            IReadOnlyList<Point2D> loop,
            Func<double, double, double> elevation,
            double maxSegment,
            double tolerance,
            int maxDepth,
            double minSegment)
        {
            if (loop == null || loop.Count < 2)
            {
                return loop;
            }

            var result = new List<Point2D>();

            double? TryZ(Point2D p)
            {
                try
                {
                    var z = elevation(p.X, p.Y);
                    return double.IsNaN(z) || double.IsInfinity(z) ? (double?)null : z;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            void Edge(Point2D a, Point2D b, int adaptiveDepth)
            {
                var length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
                var middle = new Point2D((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
                var split = false;
                var nextDepth = adaptiveDepth;
                if (length > maxSegment)
                {
                    split = true;
                }
                else if (adaptiveDepth < maxDepth && (length / 2.0) >= minSegment)
                {
                    var za = TryZ(a);
                    var zb = TryZ(b);
                    var zm = TryZ(middle);
                    if (za.HasValue && zb.HasValue && zm.HasValue
                        && Math.Abs(zm.Value - ((za.Value + zb.Value) / 2.0)) > tolerance)
                    {
                        split = true;
                        nextDepth = adaptiveDepth + 1;
                    }
                }

                if (!split)
                {
                    result.Add(a);
                    return;
                }

                Edge(a, middle, nextDepth);
                Edge(middle, b, nextDepth);
            }

            for (var index = 0; index < loop.Count; index++)
            {
                Edge(loop[index], loop[(index + 1) % loop.Count], 0);
            }

            return result;
        }

        public static DensifyResult Build(
            Func<double, double, double> elevation,
            Func<Point2D, bool> isInside,
            Func<Point2D, bool> canAdd,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double initialStep,
            double tolerance,
            int maxDepth,
            int maxPoints)
        {
            if (elevation == null)
            {
                throw new ArgumentNullException(nameof(elevation));
            }

            if (isInside == null)
            {
                throw new ArgumentNullException(nameof(isInside));
            }

            if (canAdd == null)
            {
                throw new ArgumentNullException(nameof(canAdd));
            }

            if (initialStep <= 0 || tolerance <= 0 || maxDepth < 0 || maxPoints <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialStep), "格距、容許、深度與點數上限必須為正。");
            }

            var points = new List<Point2D>();
            var seen = new HashSet<(long, long)>();
            var cache = new Dictionary<(long, long), double?>();
            var maxResidual = 0.0;
            var hitLimit = false;

            (long, long) Key(double x, double y) =>
                ((long)Math.Round(x * 1e6), (long)Math.Round(y * 1e6));

            double? Eval(double x, double y)
            {
                var key = Key(x, y);
                if (cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                double? value = null;
                if (isInside(new Point2D(x, y)))
                {
                    try
                    {
                        var z = elevation(x, y);
                        if (!double.IsNaN(z) && !double.IsInfinity(z))
                        {
                            value = z;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        value = null;
                    }
                }

                cache[key] = value;
                return value;
            }

            void AddIfUsable(double x, double y)
            {
                if (points.Count < maxPoints && Eval(x, y).HasValue && canAdd(new Point2D(x, y))
                    && seen.Add(Key(x, y)))
                {
                    points.Add(new Point2D(x, y));
                }
            }

            double Residual(double? middle, double? first, double? second)
            {
                if (!middle.HasValue || !first.HasValue || !second.HasValue)
                {
                    return 0.0;
                }

                return Math.Abs(middle.Value - ((first.Value + second.Value) / 2.0));
            }

            void Cell(double x0, double y0, double size, int depth)
            {
                var half = size / 2.0;
                var x1 = x0 + size;
                var y1 = y0 + size;
                var xm = x0 + half;
                var ym = y0 + half;
                var z00 = Eval(x0, y0);
                var z10 = Eval(x1, y0);
                var z11 = Eval(x1, y1);
                var z01 = Eval(x0, y1);
                var zc = Eval(xm, ym);
                var zb = Eval(xm, y0);
                var zr = Eval(x1, ym);
                var zt = Eval(xm, y1);
                var zl = Eval(x0, ym);

                var error = Math.Max(
                    Math.Max(Residual(zc, z00, z11), Residual(zc, z10, z01)),
                    Math.Max(
                        Math.Max(Residual(zb, z00, z10), Residual(zr, z10, z11)),
                        Math.Max(Residual(zt, z01, z11), Residual(zl, z00, z01))));

                if (error <= tolerance)
                {
                    maxResidual = Math.Max(maxResidual, error);
                    return;
                }

                if (depth >= maxDepth || points.Count >= maxPoints)
                {
                    maxResidual = Math.Max(maxResidual, error);
                    hitLimit = true;
                    return;
                }

                AddIfUsable(xm, ym);
                AddIfUsable(xm, y0);
                AddIfUsable(x1, ym);
                AddIfUsable(xm, y1);
                AddIfUsable(x0, ym);
                Cell(x0, y0, half, depth + 1);
                Cell(xm, y0, half, depth + 1);
                Cell(x0, ym, half, depth + 1);
                Cell(xm, ym, half, depth + 1);
            }

            var columns = (int)Math.Ceiling((maxX - minX) / initialStep);
            var rows = (int)Math.Ceiling((maxY - minY) / initialStep);
            for (var column = 0; column < columns; column++)
            {
                for (var row = 0; row < rows; row++)
                {
                    var x0 = minX + (column * initialStep);
                    var y0 = minY + (row * initialStep);
                    AddIfUsable(x0, y0);
                    AddIfUsable(x0 + initialStep, y0);
                    AddIfUsable(x0, y0 + initialStep);
                    AddIfUsable(x0 + initialStep, y0 + initialStep);
                    Cell(x0, y0, initialStep, 0);
                }
            }

            return new DensifyResult { Points = points, MaxResidual = maxResidual, HitLimit = hitLimit };
        }
    }

    /// <summary>板緣取高程時的內推方向（純函式）。</summary>
    public static class BoundaryNudge
    {
        /// <summary>
        /// 點在某頂點（容差內）→ 該頂點內向角平分線（凹形的凸角、U 形等不會指到面外，凹角取反向外角平分線）；
        /// 點在邊上（容差內）→ 該邊內法線；都不是回 false。方向為單位向量。
        /// </summary>
        public static bool TryInwardDirection(
            IReadOnlyList<Point2D> loop,
            Point2D point,
            double tolerance,
            out Point2D direction)
        {
            direction = default(Point2D);
            if (loop == null || loop.Count < 3)
            {
                return false;
            }

            var toleranceSquared = tolerance * tolerance;
            var vertexIndex = -1;
            for (var index = 0; index < loop.Count; index++)
            {
                var dx = loop[index].X - point.X;
                var dy = loop[index].Y - point.Y;
                if ((dx * dx) + (dy * dy) <= toleranceSquared)
                {
                    vertexIndex = index;
                    break;
                }
            }

            if (vertexIndex >= 0)
            {
                var outward = Polygon2D.OutwardDirections(loop)[vertexIndex];
                direction = new Point2D(-outward.X, -outward.Y);
                return true;
            }

            var nearest = Polygon2D.NearestBoundaryPointDetailed(loop, point);
            if (nearest.Distance > tolerance)
            {
                return false;
            }

            var start = loop[nearest.EdgeIndex];
            var end = loop[(nearest.EdgeIndex + 1) % loop.Count];
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var length = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
            if (length < 1e-12)
            {
                return false;
            }

            var signedArea = 0.0;
            for (var index = 0; index < loop.Count; index++)
            {
                var a = loop[index];
                var b = loop[(index + 1) % loop.Count];
                signedArea += (a.X * b.Y) - (b.X * a.Y);
            }

            // CCW（signedArea>0）時邊 (dx,dy) 的內法線為 (-dy,dx)；CW 相反。
            var orientation = signedArea >= 0 ? 1.0 : -1.0;
            direction = new Point2D(-orientation * edgeY / length, orientation * edgeX / length);
            return true;
        }
    }
}
