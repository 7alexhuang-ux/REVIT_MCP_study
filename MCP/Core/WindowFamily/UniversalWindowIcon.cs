using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>
    /// ribbon 按鈕圖示：以 WPF 向量繪製一扇窗（深色外框、淺藍玻璃、中框），避免另外維護 PNG 檔。
    /// 專案既有按鈕沒有圖示資源，本類別只供「萬用窗」面板使用；繪製失敗時回傳 null（按鈕退回純文字）。
    /// </summary>
    public static class UniversalWindowIcon
    {
        /// <summary>建立 size × size 的圖示（Revit 慣例：大圖 32、小圖 16）。</summary>
        public static BitmapSource Create(int size)
        {
            try
            {
                double s = size;
                double pad = s * 0.1;
                double thick = Math.Max(1.5, s * 0.08);

                var frameBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x4A, 0x62));
                var glassBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xD3, 0xF0));
                var pen = new Pen(frameBrush, thick);

                var dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    var outer = new Rect(pad, pad, s - 2 * pad, s - 2 * pad);
                    dc.DrawRoundedRectangle(glassBrush, pen, outer, s * 0.04, s * 0.04);
                    // 中框：十字
                    dc.DrawLine(pen, new Point(s / 2, pad), new Point(s / 2, s - pad));
                    dc.DrawLine(pen, new Point(pad, s * 0.42), new Point(s - pad, s * 0.42));
                }

                var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(dv);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
