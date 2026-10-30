using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 坵塊法平均坡度計算（建築技術規則建築設計施工編第 261、262 條）。
    /// 純函式、零 Revit 相依，可 NUnit 測試。方法定義見 domain/parcel-slope-analysis.md。
    /// </summary>
    public static class ParcelSlopeMath
    {
        /// <summary>
        /// 法定圓周率。第 261 條原文明訂「π：圓周率（3.14）」。
        /// <b>不得改用 Math.PI</b>——送審計算書必須能被審查單位以 3.14 逐格手算複核。
        /// 見 domain/parcel-slope-analysis.md §3.1。
        /// </summary>
        public const double LegalPi = 3.14;

        /// <summary>坵塊邊長法定上限（公尺）：第 261 條「每邊長不大於二十五公尺」。</summary>
        public const double MaxCellSizeMeters = 25.0;

        /// <summary>§262 第 1 項第 1 款「坡度陡峭」門檻（%）。</summary>
        public const double SteepThresholdPercent = 30.0;

        /// <summary>§262 第 2 項「不得計入法定空地」門檻（%）。</summary>
        public const double NoOpenSpaceThresholdPercent = 55.0;

        /// <summary>
        /// 坵塊平均坡度：S(%) = n·π·h / (8·L) × 100。
        /// </summary>
        /// <param name="intersectionCount">n：等高線與坵塊<b>四邊</b>交點數之總和（非單邊）。</param>
        /// <param name="contourIntervalMeters">h：等高線<b>首曲線</b>間距（公尺），須 &gt; 0。</param>
        /// <param name="cellSizeMeters">
        /// L：坵塊<b>單邊</b>邊長（公尺）。<b>不是四邊總長</b>——用 4L 會讓坡度低估為 1/4。
        /// 須 &gt; 0 且 ≤ 25（法定上限）。
        /// </param>
        /// <returns>平均坡度（百分比，非負）。</returns>
        public static double SlopePercent(int intersectionCount, double contourIntervalMeters, double cellSizeMeters)
        {
            if (intersectionCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(intersectionCount), intersectionCount, "交點數不得為負。");
            }

            if (!IsPositiveFinite(contourIntervalMeters))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(contourIntervalMeters), contourIntervalMeters,
                    "等高線首曲線間距必須是大於 0 的有限數值；此值須由使用者輸入，不得預設或推測。");
            }

            if (!IsPositiveFinite(cellSizeMeters))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters, "坵塊邊長必須是大於 0 的有限數值。");
            }

            if (cellSizeMeters > MaxCellSizeMeters)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters,
                    $"坵塊邊長不得超過法定上限 {MaxCellSizeMeters} 公尺"
                    + "（建築技術規則建築設計施工編第 261 條第一款第（一）目）。");
            }

            return intersectionCount * LegalPi * contourIntervalMeters / (8.0 * cellSizeMeters) * 100.0;
        }

        /// <summary>
        /// 依 §262 判定該坵塊坡度落在哪一段法律效果。
        /// 邊界右閉：≤30 無限制；(30,55] 限空地；&gt;55 不得計入法定空地。
        /// </summary>
        public static ParcelRestriction Classify(double slopePercent)
        {
            if (double.IsNaN(slopePercent) || double.IsInfinity(slopePercent))
            {
                throw new ArgumentException("坡度百分比必須是有限數值。", nameof(slopePercent));
            }

            if (slopePercent <= SteepThresholdPercent)
            {
                return ParcelRestriction.None;
            }

            return slopePercent <= NoOpenSpaceThresholdPercent
                ? ParcelRestriction.OpenSpaceOnly
                : ParcelRestriction.ExcludedFromOpenSpace;
        }

        /// <summary>標示文字。用於坵塊圖標註與計算書欄位。</summary>
        public static string RestrictionLabel(ParcelRestriction restriction)
        {
            switch (restriction)
            {
                case ParcelRestriction.None:
                    return string.Empty;
                case ParcelRestriction.OpenSpaceOnly:
                    return "限空地／不得配置建築物";
                case ParcelRestriction.ExcludedFromOpenSpace:
                    return "不得計入法定空地";
                default:
                    throw new ArgumentOutOfRangeException(nameof(restriction), restriction, "未知的限制類別。");
            }
        }

        /// <summary>
        /// 產生旋轉後的坵塊網格中心點與四角座標。
        /// 旋轉中心固定為網格範圍中心（見 domain §3.4），避免旋轉後網格飛離基地。
        /// </summary>
        /// <param name="minX">網格涵蓋範圍最小 X（公尺）。</param>
        /// <param name="minY">網格涵蓋範圍最小 Y（公尺）。</param>
        /// <param name="maxX">網格涵蓋範圍最大 X（公尺）。</param>
        /// <param name="maxY">網格涵蓋範圍最大 Y（公尺）。</param>
        /// <param name="cellSizeMeters">坵塊邊長（公尺），須 ≤ 25。</param>
        /// <param name="rotationDegrees">網格旋轉角（度，逆時針為正）。</param>
        public static IReadOnlyList<ParcelCell> BuildGrid(
            double minX, double minY, double maxX, double maxY,
            double cellSizeMeters, double rotationDegrees)
        {
            return BuildGrid(minX, minY, maxX, maxY, cellSizeMeters, rotationDegrees, 0.0, 0.0);
        }

        /// <summary>
        /// 同上，另可平移網格起點。
        /// <paramref name="originOffsetXMeters"/>／<paramref name="originOffsetYMeters"/> 是「網格自身座標軸」上的平移量
        /// （先平移、再繞固定中心旋轉），所以旋轉後平移半格仍是沿格線方向的半格。
        /// 旋轉中心維持「未平移網格」的中心，不隨平移移動。兩者皆為 0 時結果與舊版逐位元相同。
        /// 平移後會自動多補幾圈格，確保仍蓋滿原範圍。
        /// </summary>
        public static IReadOnlyList<ParcelCell> BuildGrid(
            double minX, double minY, double maxX, double maxY,
            double cellSizeMeters, double rotationDegrees,
            double originOffsetXMeters, double originOffsetYMeters)
        {
            if (double.IsNaN(originOffsetXMeters) || double.IsInfinity(originOffsetXMeters)
                || double.IsNaN(originOffsetYMeters) || double.IsInfinity(originOffsetYMeters))
            {
                throw new ArgumentException("原點平移量必須是有限數值。", nameof(originOffsetXMeters));
            }

            if (!IsPositiveFinite(cellSizeMeters))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters, "坵塊邊長必須是大於 0 的有限數值。");
            }

            if (cellSizeMeters > MaxCellSizeMeters)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters,
                    $"坵塊邊長不得超過法定上限 {MaxCellSizeMeters} 公尺（第 261 條）。");
            }

            if (maxX <= minX || maxY <= minY)
            {
                throw new ArgumentException("網格範圍無效：max 必須大於 min。", nameof(maxX));
            }

            // 旋轉後仍要蓋滿原範圍，故先把範圍沿對角線外擴，寧可多算邊緣空格。
            var cx = (minX + maxX) / 2.0;
            var cy = (minY + maxY) / 2.0;
            var halfDiag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) / 2.0;

            var cols = (int)Math.Ceiling(2.0 * halfDiag / cellSizeMeters);
            if (cols < 1)
            {
                cols = 1;
            }

            // 平移 L 的整數倍等於沒平移（同一組格線陣列），先對 L 取正餘數 → 0 ≤ offset < L，
            // 避免超大平移量讓補格數暴增。
            originOffsetXMeters = NormalizeOffset(originOffsetXMeters, cellSizeMeters);
            originOffsetYMeters = NormalizeOffset(originOffsetYMeters, cellSizeMeters);

            // 平移後要補格：每側需多出 |offset|。無平移時 pad=0，與舊版一致。
            var maxOffset = Math.Max(Math.Abs(originOffsetXMeters), Math.Abs(originOffsetYMeters));
            if (maxOffset > 0)
            {
                // 一律補「偶數」格（每側 ceil(offset/L) 格）：奇偶性不變，平移才是相對同一格線陣列的真平移。
                cols += 2 * (int)Math.Ceiling(maxOffset / cellSizeMeters);
            }

            var rows = cols;

            var originX = cx - cols * cellSizeMeters / 2.0 + originOffsetXMeters;
            var originY = cy - rows * cellSizeMeters / 2.0 + originOffsetYMeters;

            var rad = rotationDegrees * Math.PI / 180.0; // 幾何旋轉用真 π，與法定公式的 3.14 無關
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);

            var cells = new List<ParcelCell>(cols * rows);
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var x0 = originX + col * cellSizeMeters;
                    var y0 = originY + row * cellSizeMeters;
                    var x1 = x0 + cellSizeMeters;
                    var y1 = y0 + cellSizeMeters;

                    var corners = new[]
                    {
                        RotateAbout(x0, y0, cx, cy, cos, sin),
                        RotateAbout(x1, y0, cx, cy, cos, sin),
                        RotateAbout(x1, y1, cx, cy, cos, sin),
                        RotateAbout(x0, y1, cx, cy, cos, sin),
                    };

                    var center = RotateAbout((x0 + x1) / 2.0, (y0 + y1) / 2.0, cx, cy, cos, sin);
                    cells.Add(new ParcelCell(col, row, center, corners));
                }
            }

            return cells;
        }

        // ────────────────────────────────────────────────
        // 基地範圍：多邊形裁切與面積加權
        // ────────────────────────────────────────────────

        /// <summary>面積小於此值（㎡）視為「未與基地相交」，該坵塊不保留。</summary>
        public const double MinOverlapAreaSquareMeters = 1e-6;

        /// <summary>多邊形絕對面積（鞋帶公式，與繞向無關）。少於 3 點回 0。</summary>
        public static double PolygonArea(IReadOnlyList<ParcelPoint> polygon)
        {
            return Math.Abs(SignedPolygonArea(polygon));
        }

        private static double SignedPolygonArea(IReadOnlyList<ParcelPoint> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return 0.0;
            }

            var sum = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }

            return sum / 2.0;
        }

        /// <summary>
        /// Sutherland–Hodgman：把任意多邊形 <paramref name="subject"/> 裁切到<b>凸</b>多邊形 <paramref name="convexClip"/> 內。
        /// 凸的一方必須當裁切窗——SH 只對凸裁切窗正確；基地範圍可能是凹多邊形，所以由呼叫端
        /// 把「坵塊（凸）」當窗、「基地（任意簡單多邊形）」當被裁物。
        /// 被裁物為凹形且裁出多塊時，結果會帶有沿窗邊來回的退化邊，這些邊對鞋帶面積的貢獻互相抵銷，
        /// 故<b>面積正確</b>，但回傳的點列不保證是單一簡單多邊形——只應拿來算面積。
        /// </summary>
        public static IReadOnlyList<ParcelPoint> ClipToConvex(
            IReadOnlyList<ParcelPoint> subject, IReadOnlyList<ParcelPoint> convexClip)
        {
            if (subject == null || convexClip == null || subject.Count < 3 || convexClip.Count < 3)
            {
                return new List<ParcelPoint>();
            }

            // 窗統一成逆時針，內側 = 邊向量左側（cross >= 0）。
            var clip = SignedPolygonArea(convexClip) >= 0
                ? convexClip
                : new List<ParcelPoint>(convexClip).AsEnumerable().Reverse().ToList();

            IReadOnlyList<ParcelPoint> output = subject;
            for (var i = 0; i < clip.Count && output.Count > 0; i++)
            {
                var a = clip[i];
                var b = clip[(i + 1) % clip.Count];
                var input = output;
                var next = new List<ParcelPoint>();

                for (var j = 0; j < input.Count; j++)
                {
                    var cur = input[j];
                    var prev = input[(j + input.Count - 1) % input.Count];
                    var curIn = Side(a, b, cur) >= 0;
                    var prevIn = Side(a, b, prev) >= 0;

                    if (curIn)
                    {
                        if (!prevIn)
                        {
                            next.Add(LineIntersection(prev, cur, a, b));
                        }

                        next.Add(cur);
                    }
                    else if (prevIn)
                    {
                        next.Add(LineIntersection(prev, cur, a, b));
                    }
                }

                output = next;
            }

            return output;
        }

        /// <summary>
        /// 坵塊（凸四邊形）落在基地範圍（任意簡單多邊形）內的面積（㎡）。完全在外回 0，完全在內回坵塊面積。
        /// </summary>
        public static double AreaInsideBoundary(
            IReadOnlyList<ParcelPoint> cellCorners, IReadOnlyList<ParcelPoint> boundary)
        {
            var clipped = ClipToConvex(boundary, cellCorners);
            var area = PolygonArea(clipped);
            return area < MinOverlapAreaSquareMeters ? 0.0 : area;
        }

        /// <summary>
        /// 面積加權平均坡度：Σ(S_i × A_i) / ΣA_i。基地範圍切到坵塊時採用（參考 BlueCAD 輸出之明細表格式；使用者提供樣本，未查證官方說明）。
        /// 無基地範圍時每格 A_i 皆為 L²，結果等同簡單平均。ΣA_i 必須 &gt; 0。
        /// </summary>
        public static double WeightedAverageSlope(IReadOnlyList<double> slopes, IReadOnlyList<double> areas)
        {
            if (slopes == null || areas == null || slopes.Count != areas.Count)
            {
                throw new ArgumentException("坡度與面積數量必須相同。");
            }

            var sumArea = 0.0;
            var sumSa = 0.0;
            for (var i = 0; i < slopes.Count; i++)
            {
                if (areas[i] < 0 || double.IsNaN(areas[i]) || double.IsNaN(slopes[i]))
                {
                    throw new ArgumentException("面積不得為負、坡度與面積不得為 NaN。");
                }

                sumArea += areas[i];
                sumSa += slopes[i] * areas[i];
            }

            if (sumArea <= 0)
            {
                throw new ArgumentException("面積總和必須大於 0，無法計算面積加權平均坡度。");
            }

            return sumSa / sumArea;
        }

        /// <summary>
        /// 把一堆線段／折線串成封閉輪廓。端點在 <paramref name="toleranceMeters"/> 內視為相接；
        /// 完全重複（同點列或反向同點列）的鏈只留一條（PropertyLine 父元件與子線段常回傳同一曲線）。
        /// 串不回起點的鏈計入 <see cref="ParcelLoopResult.UnclosedChainCount"/> 並丟棄。
        /// 回傳的封閉輪廓不重複最後一點。
        /// </summary>
        public static ParcelLoopResult JoinSegmentsToLoops(
            IReadOnlyList<IReadOnlyList<ParcelPoint>> chains, double toleranceMeters)
        {
            var result = new ParcelLoopResult();
            if (chains == null)
            {
                return result;
            }

            var tol = toleranceMeters > 0 ? toleranceMeters : 0.01;

            // 去除重複鏈 + 無效鏈
            var pool = new List<List<ParcelPoint>>();
            foreach (var chain in chains)
            {
                if (chain == null || chain.Count < 2)
                {
                    continue;
                }

                var pts = new List<ParcelPoint>(chain);
                var isDup = false;
                foreach (var existing in pool)
                {
                    if (SameChain(existing, pts, tol))
                    {
                        isDup = true;
                        break;
                    }
                }

                if (!isDup)
                {
                    pool.Add(pts);
                }
            }

            // 反覆剔除懸線：任一端點不與其他鏈端點相接（度數 1）的鏈不可能屬於封閉輪廓
            // （例如 U 形角點多出的一條短線）。剔除後原本被它占住的角點度數會降回 2，輪廓才串得起來。
            bool removed;
            do
            {
                removed = false;
                for (var i = pool.Count - 1; i >= 0; i--)
                {
                    if (!HasDanglingEnd(pool, i, tol))
                    {
                        continue;
                    }

                    pool.RemoveAt(i);
                    result.DanglingChainCount++;
                    removed = true;
                }
            }
            while (removed);

            while (pool.Count > 0)
            {
                var current = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);

                var usedChains = 1;
                var closed = false;
                while (true)
                {
                    if (Near(current[0], current[current.Count - 1], tol))
                    {
                        closed = current.Count >= 4;
                        break;
                    }

                    var matched = false;
                    for (var i = 0; i < pool.Count; i++)
                    {
                        var other = pool[i];
                        var tail = current[current.Count - 1];
                        if (Near(tail, other[0], tol))
                        {
                            current.AddRange(other.GetRange(1, other.Count - 1));
                        }
                        else if (Near(tail, other[other.Count - 1], tol))
                        {
                            for (var k = other.Count - 2; k >= 0; k--)
                            {
                                current.Add(other[k]);
                            }
                        }
                        else
                        {
                            continue;
                        }

                        pool.RemoveAt(i);
                        usedChains++;
                        matched = true;
                        break;
                    }

                    if (!matched)
                    {
                        break;
                    }
                }

                if (closed)
                {
                    current.RemoveAt(current.Count - 1); // 去掉重複的收尾點
                    result.ClosedLoops.Add(current);
                }
                else
                {
                    result.UnclosedChainCount += usedChains;
                }
            }

            return result;
        }

        /// <summary>
        /// §262 但書概略坡度：基地內等高線最高程、最低程的高差 ÷ 兩組點之間<b>最短</b>水平距離 × 100。
        /// 這只是<b>提示用的概略值</b>（取樣點的極值，非逐線精算），不得拿來自動豁免任何坵塊。
        /// 高差為 0（等高線 Z 全相同，常見於平面 CAD）或兩點水平重合時回 null。
        /// </summary>
        public static ParcelRoughSlope RoughSlopeBetweenExtremes(IEnumerable<ParcelPoint3> points)
        {
            if (points == null)
            {
                return null;
            }

            var list = points.ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var maxZ = list.Max(p => p.Z);
            var minZ = list.Min(p => p.Z);
            var dz = maxZ - minZ;
            if (dz <= 1e-9)
            {
                return null;
            }

            // 最高、最低高程往往是整條等高線（許多取樣點，Z 相同）。取「所有最高程點」與「所有最低程點」
            // 之間的最短水平距離——坡度最大、偏保守（不會因恰好選到兩條線上相距遠的點而低估坡度）。
            const double zTol = 1e-6;
            var highs = list.Where(p => maxZ - p.Z <= zTol).ToList();
            var lows = list.Where(p => p.Z - minZ <= zTol).ToList();

            var bestSq = double.MaxValue;
            var bestHi = highs[0];
            var bestLo = lows[0];
            foreach (var h in highs)
            {
                foreach (var l in lows)
                {
                    var dx = h.X - l.X;
                    var dy = h.Y - l.Y;
                    var sq = dx * dx + dy * dy;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        bestHi = h;
                        bestLo = l;
                    }
                }
            }

            var dist = Math.Sqrt(bestSq);
            if (dist <= 1e-9)
            {
                return null;
            }

            return new ParcelRoughSlope(dz / dist * 100.0, bestHi, bestLo, dist);
        }

        /// <summary>點在簡單多邊形內（射線法；邊界上的點結果不定，呼叫端不依賴邊界）。</summary>
        public static bool PointInPolygon(IReadOnlyList<ParcelPoint> polygon, double x, double y)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }

            var inside = false;
            for (var i = 0; i < polygon.Count; i++)
            {
                var s = polygon[i];
                var e = polygon[(i + 1) % polygon.Count];
                if ((s.Y > y) != (e.Y > y))
                {
                    var ix = s.X + (y - s.Y) * (e.X - s.X) / (e.Y - s.Y);
                    if (x < ix)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        private static double Side(ParcelPoint a, ParcelPoint b, ParcelPoint p)
        {
            return (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        }

        /// <summary>線段 p→q 與直線 a→b 的交點（呼叫端保證 p、q 在直線兩側）。</summary>
        private static ParcelPoint LineIntersection(ParcelPoint p, ParcelPoint q, ParcelPoint a, ParcelPoint b)
        {
            var sp = Side(a, b, p);
            var sq = Side(a, b, q);
            var denom = sp - sq;
            if (Math.Abs(denom) < 1e-300)
            {
                return p;
            }

            var t = sp / denom;
            return new ParcelPoint(p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y));
        }

        private static bool Near(ParcelPoint a, ParcelPoint b, double tol)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy <= tol * tol;
        }

        /// <summary>
        /// 鏈 <paramref name="index"/> 是否有端點孤立。自身首尾相接（閉合折線）不算孤立；
        /// 端點與「其他鏈」的任一端點（或自身另一端）在容差內才算相接。
        /// </summary>
        private static bool HasDanglingEnd(List<List<ParcelPoint>> pool, int index, double tol)
        {
            var chain = pool[index];
            var first = chain[0];
            var last = chain[chain.Count - 1];
            if (Near(first, last, tol))
            {
                return false;
            }

            return !EndTouches(pool, index, first, tol) || !EndTouches(pool, index, last, tol);
        }

        private static bool EndTouches(List<List<ParcelPoint>> pool, int self, ParcelPoint end, double tol)
        {
            for (var j = 0; j < pool.Count; j++)
            {
                if (j == self)
                {
                    continue;
                }

                var other = pool[j];
                if (Near(end, other[0], tol) || Near(end, other[other.Count - 1], tol))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameChain(List<ParcelPoint> a, List<ParcelPoint> b, double tol)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            var forward = true;
            var backward = true;
            for (var i = 0; i < a.Count; i++)
            {
                if (!Near(a[i], b[i], tol))
                {
                    forward = false;
                }

                if (!Near(a[i], b[a.Count - 1 - i], tol))
                {
                    backward = false;
                }
            }

            return forward || backward;
        }

        /// <summary>
        /// 平移量對格邊長取正餘數，結果落在 [0, L)。例：L=25，offset=-12.5 → 12.5；offset=60 → 10。
        /// 平移 L 的整數倍會落在同一組格線上，結果與未平移完全相同。
        /// </summary>
        public static double NormalizeOffset(double offsetMeters, double cellSizeMeters)
        {
            if (!IsPositiveFinite(cellSizeMeters))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters, "坵塊邊長必須是大於 0 的有限數值。");
            }

            if (double.IsNaN(offsetMeters) || double.IsInfinity(offsetMeters))
            {
                throw new ArgumentException("原點平移量必須是有限數值。", nameof(offsetMeters));
            }

            var r = offsetMeters % cellSizeMeters;
            if (r < 0)
            {
                r += cellSizeMeters;
            }

            // 浮點誤差可能讓 r 恰等於 L（例如 -1e-20 + 25）
            return r >= cellSizeMeters ? 0.0 : r;
        }

        private static ParcelPoint RotateAbout(double x, double y, double cx, double cy, double cos, double sin)
        {
            var dx = x - cx;
            var dy = y - cy;
            return new ParcelPoint(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        private static bool IsPositiveFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        }
    }

    /// <summary>§262 的三段法律效果。</summary>
    public enum ParcelRestriction
    {
        /// <summary>≤30%：無本條限制。</summary>
        None = 0,

        /// <summary>&gt;30% 且 ≤55%：得作法定空地或開放空間，不得配置建築物（§262 第 2 項）。</summary>
        OpenSpaceOnly = 1,

        /// <summary>&gt;55%：不得計入法定空地面積（§262 第 2 項）。</summary>
        ExcludedFromOpenSpace = 2,
    }

    /// <summary>平面座標點（公尺）。</summary>
    public struct ParcelPoint
    {
        public ParcelPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }
    }

    /// <summary>一個坵塊（正方格）。</summary>
    public sealed class ParcelCell
    {
        public ParcelCell(int col, int row, ParcelPoint center, IReadOnlyList<ParcelPoint> corners)
        {
            if (corners == null || corners.Count != 4)
            {
                throw new ArgumentException("坵塊必須是正方格，需正好 4 個角點。", nameof(corners));
            }

            Col = col;
            Row = row;
            Center = center;
            Corners = corners;
        }

        public int Col { get; }

        public int Row { get; }

        public ParcelPoint Center { get; }

        /// <summary>四角，逆時針序。</summary>
        public IReadOnlyList<ParcelPoint> Corners { get; }
    }

    /// <summary>帶高程的點（公尺），用於 §262 但書概略坡度。</summary>
    public struct ParcelPoint3
    {
        public ParcelPoint3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }
    }

    /// <summary>極值點間的概略坡度（提示用，非法定計算）。</summary>
    public sealed class ParcelRoughSlope
    {
        public ParcelRoughSlope(double slopePercent, ParcelPoint3 highest, ParcelPoint3 lowest, double horizontalDistanceMeters)
        {
            SlopePercent = slopePercent;
            Highest = highest;
            Lowest = lowest;
            HorizontalDistanceMeters = horizontalDistanceMeters;
        }

        public double SlopePercent { get; }

        public ParcelPoint3 Highest { get; }

        public ParcelPoint3 Lowest { get; }

        public double HorizontalDistanceMeters { get; }

        public double ElevationDifferenceMeters => Highest.Z - Lowest.Z;
    }

    /// <summary><see cref="ParcelSlopeMath.JoinSegmentsToLoops"/> 的結果。</summary>
    public sealed class ParcelLoopResult
    {
        /// <summary>封閉輪廓（不重複收尾點）。</summary>
        public List<List<ParcelPoint>> ClosedLoops { get; } = new List<List<ParcelPoint>>();

        /// <summary>被剔除的懸線數（端點不與其他鏈相接，例如角點多出的短線）。</summary>
        public int DanglingChainCount { get; set; }

        /// <summary>剔除懸線後仍串不成封閉輪廓而被丟棄的原始鏈數。</summary>
        public int UnclosedChainCount { get; set; }
    }
}
