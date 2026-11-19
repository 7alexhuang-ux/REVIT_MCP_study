#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core.IssueManager
{
    // imsheetmarks（Issue Manager D-910）：圖紙版面資料（視口外框、比例、旋轉、模型↔圖紙轉換參數、剖面／立面標記）。
    // 這個檔只有純計算與 JSON 組裝，不依賴 Revit API，可以單元測試；讀 Revit 的部分在 RevitSheetLayoutReader（只在 Revit 內編譯）。
    //
    // 座標約定（與 view_positions.json 相同）：模型座標一律 glTF 公尺（Y 向上、已扣 originOffsetMeters）；
    // 紙張座標一律公釐，原點＝圖紙外框的左下角、X 向右、Y 向上（Revit 圖紙座標方向），網站端再換成 PDF 頁面的正規化座標。

    /// <summary>圖紙上的一個視口：外框、比例、旋轉，以及「模型 ↔ 紙張」轉換所需的視圖座標系。</summary>
    public sealed class ViewportPlacement
    {
        public string ViewUniqueId { get; set; }
        /// <summary>plan／ceiling／section／elevation／3d／detail（沿用 manifest 的視圖種類）。</summary>
        public string ViewKind { get; set; }
        /// <summary>視口外框（公釐，紙張座標）。</summary>
        public double BoxMinX { get; set; }
        public double BoxMinY { get; set; }
        public double BoxMaxX { get; set; }
        public double BoxMaxY { get; set; }
        /// <summary>視口中心（公釐，紙張座標）：視圖輪廓中心對應到這一點。</summary>
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        /// <summary>視圖比例的分母（1:100 → 100）。</summary>
        public double Scale { get; set; }
        /// <summary>視口在圖紙上的旋轉，逆時針為正（Revit 的「順時針 90°」＝ −90）。</summary>
        public double RotationDeg { get; set; }
        /// <summary>視圖座標系（glTF 公尺）：原點、向右、向上、看向模型的方向（三個單位向量互相垂直）。</summary>
        public Vec3 Origin { get; set; }
        public Vec3 Right { get; set; }
        public Vec3 Up { get; set; }
        public Vec3 Direction { get; set; }
        /// <summary>視圖輪廓（Revit 的 View.Outline）中心在「視圖座標、已縮放到紙張公釐」下的位置。</summary>
        public double OutlineCenterU { get; set; }
        public double OutlineCenterV { get; set; }
        /// <summary>平面視圖的切高（glTF Y 公尺）；沒有／不是平面為 null。用來判斷剖面標記是否在這張平面上看得到。</summary>
        public double? CutPlaneMeters { get; set; }
        /// <summary>裁剪框（選填）：是否啟用，與裁剪框在視圖座標系下的 min／max（公尺）。</summary>
        public bool CropActive { get; set; }
        public Vec3? CropLocalMin { get; set; }
        public Vec3? CropLocalMax { get; set; }
        /// <summary>裁剪框的 Transform（glTF 公尺；局部 → 模型）。</summary>
        public Vec3? CropOrigin { get; set; }
        public Vec3? CropBasisX { get; set; }
        public Vec3? CropBasisY { get; set; }
        public Vec3? CropBasisZ { get; set; }

        public bool ContainsPaperPoint(double x, double y) { return x >= BoxMinX && x <= BoxMaxX && y >= BoxMinY && y <= BoxMaxY; }
    }

    /// <summary>圖紙：紙張尺寸（公釐）、圖紙外框原點（Revit 圖紙座標，僅供診斷）與視口。</summary>
    public sealed class SheetPlacement
    {
        public string SheetUniqueId { get; set; }
        public string SheetNumber { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        /// <summary>Revit 圖紙座標下外框左下角（公釐）；紙張座標已扣除它。</summary>
        public double OutlineMinXMm { get; set; }
        public double OutlineMinYMm { get; set; }
        public List<ViewportPlacement> Viewports { get; set; } = new List<ViewportPlacement>();
    }

    /// <summary>一條剖面／立面標記線（glTF 公尺）：指向 <see cref="ViewUniqueId"/> 這個視圖。</summary>
    public sealed class SectionLine
    {
        public string ViewUniqueId { get; set; }
        /// <summary>section 或 elevation。</summary>
        public string Kind { get; set; }
        public Vec3 P0 { get; set; }
        public Vec3 P1 { get; set; }
        /// <summary>這個剖面／立面的垂直範圍（glTF Y 公尺）。</summary>
        public double MinY { get; set; }
        public double MaxY { get; set; }
    }

    /// <summary>紙張 ↔ 模型 座標轉換與線段裁切。全是純函式。</summary>
    public static class SheetMarkMath
    {
        public const int FormatVersion = 1;
        private const double MmPerM = 1000.0;

        /// <summary>模型點（glTF 公尺）→ 紙張座標（公釐）。</summary>
        public static void ModelToPaper(ViewportPlacement vp, Vec3 point, out double x, out double y)
        {
            if (vp == null) throw new ArgumentNullException("vp");
            if (!(vp.Scale > 0)) throw new ArgumentException("視圖比例必須為正數。");
            var d = point - vp.Origin;
            var du = d.Dot(vp.Right) * MmPerM / vp.Scale - vp.OutlineCenterU;
            var dv = d.Dot(vp.Up) * MmPerM / vp.Scale - vp.OutlineCenterV;
            var t = vp.RotationDeg * Math.PI / 180.0;
            var c = Math.Cos(t);
            var s = Math.Sin(t);
            x = vp.CenterX + du * c - dv * s;
            y = vp.CenterY + du * s + dv * c;
        }

        /// <summary>
        /// 紙張座標（公釐）→ 模型點（glTF 公尺）。視圖平面上的點沿「看向模型的方向」還有一個未知深度：
        /// 給 <paramref name="heightMeters"/>（glTF Y，例如平面的樓板高度＋切高）就沿方向解到那個高度（方向沒有垂直分量時退回深度 0）；
        /// 沒給就取視圖原點所在的平面（深度 0）。
        /// </summary>
        public static Vec3 PaperToModel(ViewportPlacement vp, double x, double y, double? heightMeters = null)
        {
            if (vp == null) throw new ArgumentNullException("vp");
            if (!(vp.Scale > 0)) throw new ArgumentException("視圖比例必須為正數。");
            var t = vp.RotationDeg * Math.PI / 180.0;
            var c = Math.Cos(t);
            var s = Math.Sin(t);
            var dx = x - vp.CenterX;
            var dy = y - vp.CenterY;
            var du = dx * c + dy * s;
            var dv = -dx * s + dy * c;
            var a = (du + vp.OutlineCenterU) * vp.Scale / MmPerM;
            var b = (dv + vp.OutlineCenterV) * vp.Scale / MmPerM;
            var onPlane = vp.Origin + vp.Right * a + vp.Up * b;
            if (!heightMeters.HasValue || Math.Abs(vp.Direction.Y) < 1e-9) return onPlane;
            var depth = (heightMeters.Value - onPlane.Y) / vp.Direction.Y;
            return onPlane + vp.Direction * depth;
        }

        /// <summary>線段與矩形有沒有交集（含線段完全在矩形內；Liang–Barsky）。</summary>
        public static bool SegmentIntersectsBox(double x0, double y0, double x1, double y1, double minX, double minY, double maxX, double maxY)
        {
            double t0 = 0, t1 = 1;
            var dx = x1 - x0;
            var dy = y1 - y0;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - minX, maxX - x0, y0 - minY, maxY - y0 };
            for (var i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0) return false;
                    continue;
                }
                var r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return true;
        }

        /// <summary>一條剖面／立面標記線在這個視口（要是平面或天花平面）裡的紙張線段；看不到回 null。</summary>
        public static JObject MarkerOnViewport(ViewportPlacement host, SectionLine line)
        {
            if (host == null || line == null) return null;
            if (host.ViewKind != ViewKinds.Plan && host.ViewKind != ViewKinds.Ceiling) return null;
            if (string.Equals(host.ViewUniqueId, line.ViewUniqueId, StringComparison.Ordinal)) return null;
            if (host.CutPlaneMeters.HasValue && (host.CutPlaneMeters.Value < line.MinY - 1e-3 || host.CutPlaneMeters.Value > line.MaxY + 1e-3)) return null;
            double x0, y0, x1, y1;
            ModelToPaper(host, line.P0, out x0, out y0);
            ModelToPaper(host, line.P1, out x1, out y1);
            if (!IsFinite(x0) || !IsFinite(y0) || !IsFinite(x1) || !IsFinite(y1)) return null;
            if (!SegmentIntersectsBox(x0, y0, x1, y1, host.BoxMinX, host.BoxMinY, host.BoxMaxX, host.BoxMaxY)) return null;
            return new JObject
            {
                ["viewUniqueId"] = line.ViewUniqueId,
                ["kind"] = line.Kind,
                ["hostViewUniqueId"] = host.ViewUniqueId,
                ["p0Mm"] = Pair(x0, y0),
                ["p1Mm"] = Pair(x1, y1),
                ["p0"] = Arr(line.P0),
                ["p1"] = Arr(line.P1),
            };
        }

        private static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        private static JArray Pair(double x, double y) { return new JArray(Math.Round(x, 2), Math.Round(y, 2)); }
        private static JArray Arr(Vec3 v) { return new JArray(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6)); }

        private static JObject ViewportJson(ViewportPlacement vp)
        {
            var o = new JObject
            {
                ["viewUniqueId"] = vp.ViewUniqueId,
                ["viewKind"] = vp.ViewKind,
                ["boxMm"] = new JObject { ["min"] = Pair(vp.BoxMinX, vp.BoxMinY), ["max"] = Pair(vp.BoxMaxX, vp.BoxMaxY) },
                ["centerMm"] = Pair(vp.CenterX, vp.CenterY),
                ["scale"] = Math.Round(vp.Scale, 6),
                ["rotationDeg"] = Math.Round(vp.RotationDeg, 6),
                ["frame"] = new JObject { ["origin"] = Arr(vp.Origin), ["right"] = Arr(vp.Right), ["up"] = Arr(vp.Up), ["direction"] = Arr(vp.Direction) },
                ["outlineCenterMm"] = new JArray(Math.Round(vp.OutlineCenterU, 4), Math.Round(vp.OutlineCenterV, 4)),
                ["cutPlaneMeters"] = vp.CutPlaneMeters.HasValue ? (JToken)Math.Round(vp.CutPlaneMeters.Value, 6) : JValue.CreateNull(),
            };
            if (vp.CropLocalMin.HasValue && vp.CropLocalMax.HasValue && vp.CropOrigin.HasValue && vp.CropBasisX.HasValue && vp.CropBasisY.HasValue && vp.CropBasisZ.HasValue)
            {
                o["crop"] = new JObject
                {
                    ["active"] = vp.CropActive,
                    ["localMin"] = Arr(vp.CropLocalMin.Value),
                    ["localMax"] = Arr(vp.CropLocalMax.Value),
                    ["transform"] = new JObject
                    {
                        ["origin"] = Arr(vp.CropOrigin.Value), ["basisX"] = Arr(vp.CropBasisX.Value),
                        ["basisY"] = Arr(vp.CropBasisY.Value), ["basisZ"] = Arr(vp.CropBasisZ.Value),
                    },
                };
            }
            return o;
        }

        /// <summary>
        /// 組出 view_positions.json 裡的 <c>sheetLayout</c> 區塊（formatVersion 1）。沒有任何圖紙就回 null（呼叫端不要寫入）。
        /// 剖面／立面標記只算在平面／天花平面的視口上；一個標記在多張圖紙的多個視口都看得到時，各列一筆。
        /// </summary>
        public static JObject BuildLayout(IEnumerable<SheetPlacement> sheets, IEnumerable<SectionLine> sectionLines)
        {
            var sheetList = (sheets ?? Enumerable.Empty<SheetPlacement>()).Where(s => s != null && s.Viewports.Count > 0).ToList();
            if (sheetList.Count == 0) return null;
            var lines = (sectionLines ?? Enumerable.Empty<SectionLine>()).Where(l => l != null).ToList();
            var sheetArray = new JArray();
            foreach (var sheet in sheetList)
            {
                var markers = new JArray();
                foreach (var host in sheet.Viewports)
                    foreach (var line in lines)
                    {
                        var marker = MarkerOnViewport(host, line);
                        if (marker != null) markers.Add(marker);
                    }
                sheetArray.Add(new JObject
                {
                    ["sheetUniqueId"] = sheet.SheetUniqueId,
                    ["sheetNumber"] = sheet.SheetNumber,
                    ["sizeMm"] = Pair(sheet.WidthMm, sheet.HeightMm),
                    ["outlineMinMm"] = Pair(sheet.OutlineMinXMm, sheet.OutlineMinYMm),
                    ["viewports"] = new JArray(sheet.Viewports.Select(ViewportJson)),
                    ["markers"] = markers,
                });
            }
            return new JObject
            {
                ["formatVersion"] = FormatVersion,
                ["units"] = new JObject { ["paper"] = "mm", ["model"] = "m" },
                ["paperOrigin"] = "sheet-outline-lower-left",
                ["sheets"] = sheetArray,
            };
        }

        /// <summary>把 sheetLayout 併進已寫好的 view_positions.json 內容（其他欄位原封不動）。</summary>
        public static string MergeIntoViewPositions(string viewPositionsJson, JObject layout)
        {
            JObject root;
            using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(viewPositionsJson)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                root = JObject.Load(reader); // 不把像日期的字串改寫成別的格式
            root["sheetLayout"] = layout;
            return root.ToString(Newtonsoft.Json.Formatting.None); // 緊湊輸出：位置檔有 4 MiB 上限
        }
    }
}
