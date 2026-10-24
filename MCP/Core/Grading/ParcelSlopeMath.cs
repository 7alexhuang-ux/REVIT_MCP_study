using System;
using System.Collections.Generic;

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
            var rows = cols;
            if (cols < 1)
            {
                cols = 1;
                rows = 1;
            }

            var originX = cx - cols * cellSizeMeters / 2.0;
            var originY = cy - rows * cellSizeMeters / 2.0;

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
}
