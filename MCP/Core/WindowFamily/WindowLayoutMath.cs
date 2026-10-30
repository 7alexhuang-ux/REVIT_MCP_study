using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>
    /// 單一扇的佈局結果（cm）。
    /// 座標沿用 configurator-demo.html：X 自框外緣左端往右，Y 自窗頂（Head）往下。
    /// 另提供 Revit 族座標換算（X 以寬度中心為 0、Z 自窗台往上）。
    /// </summary>
    public class SashLayout
    {
        public int Row { get; set; }
        public int Col { get; set; }
        /// <summary>格內扇索引，0 = 最左。</summary>
        public int Index { get; set; }
        /// <summary>格內扇數。</summary>
        public int Count { get; set; }
        public SashType Type { get; set; }
        public HingeSide Hinge { get; set; }
        /// <summary>橫拉格：偶數索引為外軌（ADR：偶數索引外軌）。</summary>
        public bool IsOuterTrack { get; set; }
        /// <summary>所屬格是否整格橫拉。</summary>
        public bool IsSlidingCell { get; set; }

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>該扇的扇框寬；固定扇用 sash × 0.5（壓條）。</summary>
        public double SashFrameWidth { get; set; }

        public double GlassX { get { return X + SashFrameWidth; } }
        public double GlassY { get { return Y + SashFrameWidth; } }
        public double GlassWidth { get { return Width - 2 * SashFrameWidth; } }
        public double GlassHeight { get { return Height - 2 * SashFrameWidth; } }

        /// <summary>玻璃面積 m²。</summary>
        public double GlassAreaM2 { get; set; }
        /// <summary>有效採光面積 m²（扣 FL+75 cm 以下）。</summary>
        public double DaylightAreaM2 { get; set; }
        /// <summary>可開口面積 m²（橫拉只算半扇）。</summary>
        public double OpenableAreaM2 { get; set; }

        public bool IsOpenable { get { return Type != SashType.Fixed; } }

        public string CellId
        {
            get { return string.Format(CultureInfo.InvariantCulture, "R{0}C{1}", Row + 1, Col + 1); }
        }
    }

    /// <summary>一格（R#C#）的佈局結果（cm）。</summary>
    public class CellLayout
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsSlidingCell { get; set; }
        public List<SashLayout> Sashes { get; set; } = new List<SashLayout>();
    }

    /// <summary>一樘（col）的佈局結果（cm）。</summary>
    public class BayLayout
    {
        public int Index { get; set; }
        public double X { get; set; }
        public double Width { get; set; }
        public List<CellLayout> Cells { get; set; } = new List<CellLayout>();
    }

    /// <summary>SPEC §8 輸出 JSON 的 derived 區塊。</summary>
    public class WindowDerived
    {
        public int SashCount { get; set; }
        public int OpenableCount { get; set; }
        public double GlassAreaM2 { get; set; }
        public double DaylightAreaM2 { get; set; }
        public double OpenableAreaM2 { get; set; }
        public double MoWidth { get; set; }
        public double MoHeight { get; set; }
        public double[] RowHeights { get; set; }
        public double[] ColWidths { get; set; }
    }

    /// <summary>整窗的佈局結果。</summary>
    public class WindowLayout
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double SillHeight { get; set; }
        public double AvailWidth { get; set; }
        public double AvailHeight { get; set; }
        /// <summary>橫拉交疊量（cm）。</summary>
        public double Overlap { get; set; }
        public double[] RowHeights { get; set; }
        public double[] ColWidths { get; set; }
        public List<BayLayout> Bays { get; set; } = new List<BayLayout>();
        public WindowDerived Derived { get; set; }

        /// <summary>所有扇（依樘 → 排 → 扇索引）。</summary>
        public IEnumerable<SashLayout> AllSashes
        {
            get { return Bays.SelectMany(b => b.Cells).SelectMany(c => c.Sashes); }
        }

        /// <summary>所有格。</summary>
        public IEnumerable<CellLayout> AllCells
        {
            get { return Bays.SelectMany(b => b.Cells); }
        }
    }

    /// <summary>
    /// 萬用窗佈局數學（cm，不依賴 Revit API）。
    /// 規則正本：docs/universal-window/SPEC.md §5，參考實作：configurator-demo.html 的 layout()／summary()。
    /// </summary>
    public static class WindowLayoutMath
    {
        /// <summary>採光有效高度下限（FL 以上 cm）。SPEC §7：採光扣 FL+75 cm 以下。</summary>
        public const double DaylightDatumCm = 75.0;

        /// <summary>橫拉扇的可開口面積係數（半扇）。</summary>
        public const double SlidingOpenableFactor = 0.5;

        /// <summary>固定扇的壓條係數：扇框寬 × 0.5。</summary>
        public const double FixedSashFrameFactor = 0.5;

        /// <summary>依規格算出完整佈局；算出負值尺寸即丟 <see cref="WindowSpecException"/>。</summary>
        public static WindowLayout Compute(WindowFamilySpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            spec.Validate();

            double fw = spec.TypeParams.FrameOuter;
            double mw = spec.TypeParams.Mullion;
            double sw = spec.TypeParams.Sash;
            double ov = spec.TypeParams.OverlapCm;
            double W = spec.InstanceParams.Width;
            double H = spec.InstanceParams.Height;
            int R = spec.Rows;
            int C = spec.Cols;

            double availW = W - 2 * fw - (C - 1) * mw;
            double availH = H - 2 * fw - (R - 1) * mw;
            if (availW <= 0)
                throw new WindowSpecException(
                    $"淨寬算出非正值：總寬 {Fmt(W)} cm 扣掉外框 2×{Fmt(fw)} 與 {C - 1} 支中框 {Fmt(mw)} 後剩 {Fmt(availW)} cm。請加大寬度或縮小框料。");
            if (availH <= 0)
                throw new WindowSpecException(
                    $"淨高算出非正值：總高 {Fmt(H)} cm 扣掉外框 2×{Fmt(fw)} 與 {R - 1} 支中框 {Fmt(mw)} 後剩 {Fmt(availH)} cm。請加大高度或縮小框料。");

            var rowHeights = new double[R];
            for (int r = 0; r < R; r++) rowHeights[r] = availH * spec.InstanceParams.RowRatios[r] / 100.0;
            var colWidths = new double[C];
            for (int c = 0; c < C; c++) colWidths[c] = availW * spec.InstanceParams.ColRatios[c] / 100.0;

            var layout = new WindowLayout
            {
                Width = W,
                Height = H,
                SillHeight = spec.InstanceParams.SillHeight,
                AvailWidth = availW,
                AvailHeight = availH,
                Overlap = ov,
                RowHeights = rowHeights,
                ColWidths = colWidths
            };

            double x = fw;
            for (int c = 0; c < C; c++)
            {
                var bay = new BayLayout { Index = c, X = x, Width = colWidths[c] };
                double bayX = x;
                x += colWidths[c] + mw;

                double y = fw;
                for (int r = 0; r < R; r++)
                {
                    double cellY = y;
                    double cellH = rowHeights[r];
                    y += cellH + mw;

                    var cellSpec = spec.Cells[r][c];
                    bool sliding = cellSpec.AllSliding;
                    int m = cellSpec.Sashes.Count;
                    double sashW = sliding ? (bay.Width + (m - 1) * ov) / m : bay.Width / m;

                    var cell = new CellLayout
                    {
                        Row = r,
                        Col = c,
                        X = bayX,
                        Y = cellY,
                        Width = bay.Width,
                        Height = cellH,
                        IsSlidingCell = sliding
                    };

                    for (int k = 0; k < m; k++)
                    {
                        var sashSpec = cellSpec.Sashes[k];
                        double sashFrame = sashSpec.Type == SashType.Fixed ? sw * FixedSashFrameFactor : sw;
                        var sash = new SashLayout
                        {
                            Row = r,
                            Col = c,
                            Index = k,
                            Count = m,
                            Type = sashSpec.Type,
                            Hinge = sashSpec.Hinge,
                            IsSlidingCell = sliding,
                            IsOuterTrack = sliding && (k % 2 == 0),
                            X = bayX + k * (sliding ? sashW - ov : sashW),
                            Y = cellY,
                            Width = sashW,
                            Height = cellH,
                            SashFrameWidth = sashFrame
                        };

                        if (sash.GlassWidth <= 0 || sash.GlassHeight <= 0)
                            throw new WindowSpecException(
                                $"格 {sash.CellId} 第 {k + 1} 扇的玻璃尺寸算出非正值（{Fmt(sash.GlassWidth)} × {Fmt(sash.GlassHeight)} cm）。扇框寬 {Fmt(sashFrame)} cm 相對扇尺寸過大。");

                        // 面積（沿用 demo summary()）
                        double areaM2 = sash.GlassWidth * sash.GlassHeight / 1e4;
                        sash.GlassAreaM2 = areaM2;

                        double glassTopAboveFl = layout.SillHeight + H - (sash.Y + sashFrame);
                        double glassBottomAboveFl = glassTopAboveFl - sash.GlassHeight;
                        double effectiveH = Math.Max(0.0, glassTopAboveFl - Math.Max(glassBottomAboveFl, DaylightDatumCm));
                        sash.DaylightAreaM2 = sash.GlassWidth * effectiveH / 1e4;

                        sash.OpenableAreaM2 = sashSpec.Type == SashType.Fixed
                            ? 0.0
                            : (sliding ? areaM2 * SlidingOpenableFactor : areaM2);

                        cell.Sashes.Add(sash);
                    }

                    bay.Cells.Add(cell);
                }

                layout.Bays.Add(bay);
            }

            var all = layout.AllSashes.ToList();
            layout.Derived = new WindowDerived
            {
                SashCount = all.Count,
                OpenableCount = all.Count(s => s.IsOpenable),
                GlassAreaM2 = all.Sum(s => s.GlassAreaM2),
                DaylightAreaM2 = all.Sum(s => s.DaylightAreaM2),
                OpenableAreaM2 = all.Sum(s => s.OpenableAreaM2),
                MoWidth = W + 2 * spec.InstanceParams.Mo.GapPerSide,
                MoHeight = H + 2 * spec.InstanceParams.Mo.GapPerSide,
                RowHeights = rowHeights,
                ColWidths = colWidths
            };

            return layout;
        }

        // ────────────────────────────────────────────────
        // 族座標換算（供 Revit 端使用；純算術，可單元測試）
        // ────────────────────────────────────────────────

        /// <summary>demo 的 X（自左外緣）→ 族 X（寬度中心為 0）。</summary>
        public static double ToFamilyX(double xFromLeft, double totalWidth)
        {
            return xFromLeft - totalWidth / 2.0;
        }

        /// <summary>demo 的 Y（自窗頂往下）→ 族 Z（自窗台往上）。</summary>
        public static double ToFamilyZ(double yFromTop, double totalHeight)
        {
            return totalHeight - yFromTop;
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
