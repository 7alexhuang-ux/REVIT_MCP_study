using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 擾動遮罩 → 封閉邊界環萃取（純函式，無 Revit API 相依）。
    /// 實機教訓（2026-07-08）：範圍線用「逐板重建坡腳環」會多環交疊、跳過處連弦、飄出地形；
    /// 使用者裁決範圍線必須是「一條封閉、不自交的完整曲線」。本類改為量測結果：
    /// 每個擾動節點視為以節點為中心、邊長一步距的方格，取其聯集輪廓——
    /// 天生封閉、不自交，多板聯集／逐邊坡率／上限截止／地形邊界全部自動正確。
    /// </summary>
    public static class DisturbanceContour
    {
        /// <summary>
        /// 萃取擾動遮罩的邊界環。mask[ix, iy]=true 表示節點 (originX+ix*step, originY+iy*step) 已擾動。
        /// 回傳世界座標封閉環（未重複首點），已合併共線點；外環與內孔環都會回傳。
        /// 對角相觸的棋盤角以左轉優先解歧，保證環不自交。
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<Point2D>> ExtractBoundaryLoops(
            bool[,] mask, double originX, double originY, double step)
        {
            if (mask == null)
            {
                throw new ArgumentNullException(nameof(mask));
            }

            if (step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step), "步距必須大於 0。");
            }

            var countX = mask.GetLength(0);
            var countY = mask.GetLength(1);

            bool Filled(int ix, int iy) =>
                ix >= 0 && iy >= 0 && ix < countX && iy < countY && mask[ix, iy];

            // 邊以「雙倍格座標」整數點表示（節點中心=2i，方格角=2i±1），避免浮點鍵值。
            // 方向讓擾動區永遠在左側：下邊 +x、右邊 +y、上邊 -x、左邊 -y。
            var outgoing = new Dictionary<(int X, int Y), List<(int X, int Y)>>();

            void AddEdge(int fromX, int fromY, int toX, int toY)
            {
                var key = (fromX, fromY);
                if (!outgoing.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>();
                    outgoing[key] = list;
                }

                list.Add((toX, toY));
            }

            for (var ix = 0; ix < countX; ix++)
            {
                for (var iy = 0; iy < countY; iy++)
                {
                    if (!mask[ix, iy])
                    {
                        continue;
                    }

                    var cx = 2 * ix;
                    var cy = 2 * iy;
                    if (!Filled(ix, iy - 1)) { AddEdge(cx - 1, cy - 1, cx + 1, cy - 1); } // 下
                    if (!Filled(ix + 1, iy)) { AddEdge(cx + 1, cy - 1, cx + 1, cy + 1); } // 右
                    if (!Filled(ix, iy + 1)) { AddEdge(cx + 1, cy + 1, cx - 1, cy + 1); } // 上
                    if (!Filled(ix - 1, iy)) { AddEdge(cx - 1, cy + 1, cx - 1, cy - 1); } // 左
                }
            }

            var loops = new List<IReadOnlyList<Point2D>>();
            while (outgoing.Count > 0)
            {
                var start = outgoing.Keys.First();
                var loop = new List<(int X, int Y)>();
                var current = start;
                var incomingDirection = (X: 0, Y: 0);
                while (true)
                {
                    if (!outgoing.TryGetValue(current, out var candidates) || candidates.Count == 0)
                    {
                        break; // 理論上不會發生（每條邊必有後繼）；防禦性中止。
                    }

                    // 棋盤角一個頂點有兩條出邊：取相對來向「最左轉」者，讓兩個對角區塊各自成環。
                    var next = candidates.Count == 1 || incomingDirection == (0, 0)
                        ? candidates[0]
                        : candidates.OrderByDescending(candidate => TurnScore(incomingDirection, current, candidate)).First();
                    candidates.Remove(next);
                    if (candidates.Count == 0)
                    {
                        outgoing.Remove(current);
                    }

                    loop.Add(current);
                    incomingDirection = (next.X - current.X, next.Y - current.Y);
                    current = next;
                    if (current == start)
                    {
                        break;
                    }
                }

                if (loop.Count >= 4)
                {
                    loops.Add(MergeCollinear(loop)
                        .Select(point => new Point2D(
                            originX + (point.X / 2.0 * step),
                            originY + (point.Y / 2.0 * step)))
                        .ToList());
                }
            }

            return loops;
        }

        /// <summary>
        /// 差值場等值線萃取（marching squares＋線性內插）。field[ix,iy] 為節點差值
        /// （呼叫端放 |原Z−設計Z|），iso 為門檻；「擾動」= field &gt; iso。double.NaN = 該節點未取樣
        /// （超出地形，一律視為未擾動；與 NaN 相鄰邊的交點退到該邊中點）。
        /// 回傳世界座標封閉環（未重複首點）；節點 (ix,iy) 世界座標 = (originX+ix*step, originY+iy*step)。
        /// 環方向規約：與 ExtractBoundaryLoops 相同（外環讓擾動區在左側）。
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<Point2D>> ExtractIsoContourLoops(
            double[,] field, double iso, double originX, double originY, double step)
        {
            if (field == null)
            {
                throw new ArgumentNullException(nameof(field));
            }

            if (step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step), "步距必須大於 0。");
            }

            var countX = field.GetLength(0);
            var countY = field.GetLength(1);

            // 網格外一律視為 NaN（未取樣）→ 與陣列內 NaN 走同一套「未擾動」規則，
            // 讓貼邊擾動區也能靠「退中點」內插自然閉合（不需另外特判邊界）。
            double NodeValue(int ix, int iy) =>
                ix >= 0 && iy >= 0 && ix < countX && iy < countY ? field[ix, iy] : double.NaN;

            // NaN 與任何值比較恆為 false，故未取樣節點自動視為未擾動，無需另外判斷。
            bool Disturbed(double value) => value > iso;

            double InterpParam(double a, double b)
            {
                if (double.IsNaN(a) || double.IsNaN(b))
                {
                    return 0.5;
                }

                var denominator = b - a;
                if (Math.Abs(denominator) < 1e-12)
                {
                    return 0.5;
                }

                var t = (iso - a) / denominator;
                return Math.Max(0, Math.Min(1, t));
            }

            var pointCache = new Dictionary<(bool IsHorizontal, int X, int Y), Point2D>();

            Point2D EdgePoint(bool isHorizontal, int ex, int ey)
            {
                var key = (isHorizontal, ex, ey);
                if (pointCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                Point2D point;
                if (isHorizontal)
                {
                    // H(ex,ey)：節點 (ex,ey)-(ex+1,ey) 之間的內插交點。
                    var t = InterpParam(NodeValue(ex, ey), NodeValue(ex + 1, ey));
                    point = new Point2D(originX + ((ex + t) * step), originY + (ey * step));
                }
                else
                {
                    // V(ex,ey)：節點 (ex,ey)-(ex,ey+1) 之間的內插交點。
                    var t = InterpParam(NodeValue(ex, ey), NodeValue(ex, ey + 1));
                    point = new Point2D(originX + (ex * step), originY + ((ey + t) * step));
                }

                pointCache[key] = point;
                return point;
            }

            var outgoing = new Dictionary<(bool IsHorizontal, int X, int Y), (bool IsHorizontal, int X, int Y)>();

            void AddSegment((bool IsHorizontal, int X, int Y) from, (bool IsHorizontal, int X, int Y) to)
            {
                outgoing[from] = to;
            }

            // 網格向外多掃一圈虛擬 cell（ix/iy 含 -1 與 countX-1/countY-1 之外一格），
            // 讓貼到陣列邊緣的擾動區能對到 NaN 虛擬節點、內插退中點而閉合。
            for (var ix = -1; ix < countX; ix++)
            {
                for (var iy = -1; iy < countY; iy++)
                {
                    var bl = Disturbed(NodeValue(ix, iy));
                    var br = Disturbed(NodeValue(ix + 1, iy));
                    var tr = Disturbed(NodeValue(ix + 1, iy + 1));
                    var tl = Disturbed(NodeValue(ix, iy + 1));

                    var caseIndex = (bl ? 1 : 0) | (br ? 2 : 0) | (tr ? 4 : 0) | (tl ? 8 : 0);
                    if (caseIndex == 0 || caseIndex == 15)
                    {
                        continue;
                    }

                    var bottom = (true, ix, iy);
                    var top = (true, ix, iy + 1);
                    var left = (false, ix, iy);
                    var right = (false, ix + 1, iy);

                    // 標準 marching squares case 表（CCW、擾動在左，與 ExtractBoundaryLoops 同規約）：
                    // 單一角擾動 → 反向（繞出角落三角形）；單一角未擾動（三角擾動）→ 正向；
                    // 兩角相鄰擾動（半格） → 依方向直接連兩側邊；鞍點（case 5/10）用四角平均值消歧。
                    switch (caseIndex)
                    {
                        case 1: AddSegment(bottom, left); break;
                        case 2: AddSegment(right, bottom); break;
                        case 4: AddSegment(top, right); break;
                        case 8: AddSegment(left, top); break;
                        case 14: AddSegment(left, bottom); break;
                        case 13: AddSegment(bottom, right); break;
                        case 11: AddSegment(right, top); break;
                        case 7: AddSegment(top, left); break;
                        case 3: AddSegment(right, left); break;
                        case 12: AddSegment(left, right); break;
                        case 6: AddSegment(top, bottom); break;
                        case 9: AddSegment(bottom, top); break;
                        case 5:
                        {
                            var center = (NodeValue(ix, iy) + NodeValue(ix + 1, iy)
                                + NodeValue(ix + 1, iy + 1) + NodeValue(ix, iy + 1)) / 4.0;
                            if (Disturbed(center))
                            {
                                AddSegment(bottom, right);
                                AddSegment(top, left);
                            }
                            else
                            {
                                AddSegment(bottom, left);
                                AddSegment(top, right);
                            }

                            break;
                        }

                        case 10:
                        {
                            var center = (NodeValue(ix, iy) + NodeValue(ix + 1, iy)
                                + NodeValue(ix + 1, iy + 1) + NodeValue(ix, iy + 1)) / 4.0;
                            if (Disturbed(center))
                            {
                                AddSegment(left, bottom);
                                AddSegment(right, top);
                            }
                            else
                            {
                                AddSegment(right, bottom);
                                AddSegment(left, top);
                            }

                            break;
                        }
                    }
                }
            }

            var loops = new List<IReadOnlyList<Point2D>>();
            while (outgoing.Count > 0)
            {
                var start = outgoing.Keys.First();
                var loopKeys = new List<(bool IsHorizontal, int X, int Y)>();
                var current = start;
                while (true)
                {
                    if (!outgoing.TryGetValue(current, out var next))
                    {
                        break; // 理論上每個交點恰有一條出邊；防禦性中止。
                    }

                    outgoing.Remove(current);
                    loopKeys.Add(current);
                    current = next;
                    if (current == start)
                    {
                        break;
                    }
                }

                if (loopKeys.Count >= 3)
                {
                    loops.Add(loopKeys
                        .Select(key => EdgePoint(key.IsHorizontal, key.X, key.Y))
                        .ToList());
                }
            }

            return loops;
        }

        /// <summary>左轉優先評分：左轉 2、直行 1、右轉 0（相對進入方向）。</summary>
        private static int TurnScore((int X, int Y) incoming, (int X, int Y) from, (int X, int Y) to)
        {
            var outgoing = (X: to.X - from.X, Y: to.Y - from.Y);
            var cross = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
            if (cross > 0)
            {
                return 2; // 左轉
            }

            return cross == 0 ? 1 : 0;
        }

        private static List<(int X, int Y)> MergeCollinear(List<(int X, int Y)> loop)
        {
            var merged = new List<(int X, int Y)>();
            var count = loop.Count;
            for (var index = 0; index < count; index++)
            {
                var previous = loop[(index - 1 + count) % count];
                var current = loop[index];
                var next = loop[(index + 1) % count];
                var incoming = (X: current.X - previous.X, Y: current.Y - previous.Y);
                var outgoing = (X: next.X - current.X, Y: next.Y - current.Y);
                if ((incoming.X * outgoing.Y) - (incoming.Y * outgoing.X) != 0)
                {
                    merged.Add(current); // 只保留轉角點
                }
            }

            return merged;
        }

        /// <summary>
        /// 封閉環循環移動平均平滑（2026-07-10 二輪使用者回報「還是很多鋸齒」）：
        /// 等值線內插消掉了取樣格階梯，殘餘鋸齒來自差值場在門檻附近的三角化微差雜訊
        /// （值在 iso 上下抖動→等值線抖動）。每點取前後 window 點的平均壓掉高頻抖動；
        /// 真實轉折（跨多步距的方向變化）幅度遠大於雜訊，平滑後仍保留。
        /// </summary>
        public static IReadOnlyList<Point2D> SmoothClosedLoop(IReadOnlyList<Point2D> loop, int window)
        {
            if (loop == null)
            {
                throw new ArgumentNullException(nameof(loop));
            }

            if (window <= 0 || loop.Count <= 4)
            {
                return loop;
            }

            var count = loop.Count;
            var smoothed = new List<Point2D>(count);
            for (var index = 0; index < count; index++)
            {
                double sumX = 0;
                double sumY = 0;
                var samples = 0;
                for (var offset = -window; offset <= window; offset++)
                {
                    var neighbor = loop[((index + offset) % count + count) % count];
                    sumX += neighbor.X;
                    sumY += neighbor.Y;
                    samples++;
                }

                smoothed.Add(new Point2D(sumX / samples, sumY / samples));
            }

            return smoothed;
        }

        /// <summary>
        /// 封閉環 Douglas-Peucker 簡化：把 1 步距階梯斜線收斂成近似直線（容差內），大幅減段數。
        /// 以環上最遠點對為錨拆成兩段折線各自簡化，保證封閉且不退化。
        /// </summary>
        public static IReadOnlyList<Point2D> SimplifyLoop(IReadOnlyList<Point2D> loop, double tolerance)
        {
            if (loop == null)
            {
                throw new ArgumentNullException(nameof(loop));
            }

            if (tolerance <= 0 || loop.Count <= 4)
            {
                return loop;
            }

            // 找最遠點對當錨。
            var anchorA = 0;
            var anchorB = 0;
            var maxDistanceSquared = -1.0;
            for (var i = 0; i < loop.Count; i++)
            {
                for (var j = i + 1; j < loop.Count; j++)
                {
                    var dx = loop[i].X - loop[j].X;
                    var dy = loop[i].Y - loop[j].Y;
                    var distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared > maxDistanceSquared)
                    {
                        maxDistanceSquared = distanceSquared;
                        anchorA = i;
                        anchorB = j;
                    }
                }
            }

            var firstHalf = SliceLoop(loop, anchorA, anchorB);
            var secondHalf = SliceLoop(loop, anchorB, anchorA);
            var simplified = new List<Point2D>();
            simplified.AddRange(DouglasPeucker(firstHalf, tolerance));
            simplified.RemoveAt(simplified.Count - 1);
            simplified.AddRange(DouglasPeucker(secondHalf, tolerance));
            simplified.RemoveAt(simplified.Count - 1);
            return simplified;
        }

        private static List<Point2D> SliceLoop(IReadOnlyList<Point2D> loop, int fromIndex, int toIndex)
        {
            var slice = new List<Point2D>();
            var index = fromIndex;
            while (true)
            {
                slice.Add(loop[index]);
                if (index == toIndex)
                {
                    break;
                }

                index = (index + 1) % loop.Count;
            }

            return slice;
        }

        private static List<Point2D> DouglasPeucker(List<Point2D> points, double tolerance)
        {
            if (points.Count <= 2)
            {
                return new List<Point2D>(points);
            }

            var maxDistance = -1.0;
            var maxIndex = 0;
            var first = points[0];
            var last = points[points.Count - 1];
            for (var index = 1; index < points.Count - 1; index++)
            {
                var distance = DistancePointToSegment(points[index], first, last);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    maxIndex = index;
                }
            }

            if (maxDistance <= tolerance)
            {
                return new List<Point2D> { first, last };
            }

            var left = DouglasPeucker(points.GetRange(0, maxIndex + 1), tolerance);
            var right = DouglasPeucker(points.GetRange(maxIndex, points.Count - maxIndex), tolerance);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static double DistancePointToSegment(Point2D point, Point2D a, Point2D b)
        {
            var abX = b.X - a.X;
            var abY = b.Y - a.Y;
            var lengthSquared = (abX * abX) + (abY * abY);
            if (lengthSquared < 1e-12)
            {
                var dx0 = point.X - a.X;
                var dy0 = point.Y - a.Y;
                return Math.Sqrt((dx0 * dx0) + (dy0 * dy0));
            }

            var t = Math.Max(0, Math.Min(1,
                (((point.X - a.X) * abX) + ((point.Y - a.Y) * abY)) / lengthSquared));
            var dx = point.X - (a.X + (t * abX));
            var dy = point.Y - (a.Y + (t * abY));
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }
}
