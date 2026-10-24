using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 標高標註排布純數學（零 Revit 相依，NUnit 覆蓋）。
    /// 2026-07-10 D2/D11：使用者回報標註互疊——兩片樓板共享角點重複標、
    /// 無 leader 文字全擠在原點。合併過近點＋依方位放射錯開。
    /// </summary>
    public static class AnnotationLayoutMath
    {
        /// <summary>
        /// 貪婪選點：依輸入順序保留彼此 XY 距離 ≥ minSpacing 的點（先到先贏），
        /// 回傳保留點的原始索引。用於合併相鄰樓板共享角點等重複標註點。
        /// </summary>
        public static IReadOnlyList<int> SelectSpacedIndices(
            IReadOnlyList<Point2D> points, double minSpacing)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            var kept = new List<int>();
            var minSpacingSquared = minSpacing * minSpacing;
            for (var index = 0; index < points.Count; index++)
            {
                var candidate = points[index];
                var tooClose = false;
                foreach (var keptIndex in kept)
                {
                    var dx = candidate.X - points[keptIndex].X;
                    var dy = candidate.Y - points[keptIndex].Y;
                    if ((dx * dx) + (dy * dy) < minSpacingSquared)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    kept.Add(index);
                }
            }

            return kept;
        }

        /// <summary>
        /// 兩側欄位排槽（2026-07-10 使用者裁決：標籤拉線到圖外，不壓圖）：
        /// 每個目標點依螢幕橫座標 R 分到左（-1）／右（+1）欄，同欄依螢幕縱座標 U 由上而下排序。
        /// itemHeights 為 null 時：均分 [minU, maxU] 槽位（舊行為，項目少時夠用）。
        /// itemHeights 有值時（2026-07-10 六輪修正——強制單側時項目全擠一邊，均分格位可能小於
        /// 文字實際高度而疊字）：改由上而下依「各項實際高度＋間距」逐一堆疊，保證不重疊；
        /// 疊到超出 maxU..minU 範圍時仍繼續往下延伸（寧可跨出裁切框邊緣，不可疊字）。
        /// 回傳與輸入同序的 (側別, 槽位U＝該項中心)。
        /// </summary>
        public static IReadOnlyList<(int Side, double SlotU)> AssignSideColumnSlots(
            IReadOnlyList<Point2D> screenPoints, double splitR, double minU, double maxU,
            IReadOnlyList<double> itemHeights = null, double spacing = 0.0)
        {
            if (screenPoints == null)
            {
                throw new ArgumentNullException(nameof(screenPoints));
            }

            if (itemHeights != null && itemHeights.Count != screenPoints.Count)
            {
                throw new ArgumentException("itemHeights 數量須與 screenPoints 一致。", nameof(itemHeights));
            }

            var result = new (int Side, double SlotU)[screenPoints.Count];
            foreach (var side in new[] { -1, +1 })
            {
                var members = new List<(int Index, double U)>();
                for (var index = 0; index < screenPoints.Count; index++)
                {
                    var pointSide = screenPoints[index].X >= splitR ? +1 : -1;
                    if (pointSide == side)
                    {
                        members.Add((index, screenPoints[index].Y));
                    }
                }

                members.Sort((a, b) => b.U.CompareTo(a.U)); // 上到下

                if (itemHeights == null)
                {
                    for (var rank = 0; rank < members.Count; rank++)
                    {
                        var slotU = members.Count == 1
                            ? (minU + maxU) / 2
                            : maxU - (rank * (maxU - minU) / (members.Count - 1));
                        result[members[rank].Index] = (side, slotU);
                    }
                }
                else
                {
                    var cursor = maxU;
                    foreach (var member in members)
                    {
                        var h = itemHeights[member.Index];
                        var slotU = cursor - (h / 2);
                        cursor -= h + spacing;
                        result[member.Index] = (side, slotU);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 放射錯開偏移量：每點依「相對 center 的方位角」分入 sectorCount 個扇區，
        /// 同扇區內依角度序給遞增偏移（base、base+step、base+2*step…），
        /// 避免同方向的標註文字疊在同一半徑上。回傳每點的外推距離（與輸入同序）。
        /// </summary>
        public static IReadOnlyList<double> ComputeRadialStaggerOffsets(
            IReadOnlyList<Point2D> points, Point2D center,
            int sectorCount, double baseOffset, double stepOffset)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (sectorCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorCount));
            }

            var sectorMembers = new Dictionary<int, List<(int Index, double Angle)>>();
            for (var index = 0; index < points.Count; index++)
            {
                var angle = Math.Atan2(points[index].Y - center.Y, points[index].X - center.X);
                var sector = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * sectorCount);
                sector = Math.Min(Math.Max(sector, 0), sectorCount - 1);
                if (!sectorMembers.TryGetValue(sector, out var members))
                {
                    members = new List<(int, double)>();
                    sectorMembers[sector] = members;
                }

                members.Add((index, angle));
            }

            // 2026-07-10 三輪：相鄰扇區同半徑的標籤仍會輕碰——奇數扇區整體多推半步，
            // 讓相鄰扇區的文字框在半徑方向錯開（棋盤式）。
            var offsets = new double[points.Count];
            foreach (var entry in sectorMembers)
            {
                var members = entry.Value;
                var parityExtra = (entry.Key % 2 != 0) ? stepOffset * 0.5 : 0.0;
                members.Sort((a, b) => a.Angle.CompareTo(b.Angle));
                for (var rank = 0; rank < members.Count; rank++)
                {
                    offsets[members[rank].Index] = baseOffset + parityExtra + (rank * stepOffset);
                }
            }

            return offsets;
        }
    }
}
