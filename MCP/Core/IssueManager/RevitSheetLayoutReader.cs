#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCP.Core.Walkthrough;
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 讀 Revit 的圖紙版面（imsheetmarks）：圖紙尺寸、視口外框、比例、旋轉、視圖座標系與剖面／立面剖切線。唯讀、不開 Transaction。
    /// 必須在 Revit API 執行緒呼叫。換算全交給 <see cref="SheetMarkMath"/>（純計算、有單元測試）；
    /// 這個檔案本身只有 Revit 讀值，沒有在實機核對過——規則見 docs/reports/2026-11-02_sheetmarks.md「還沒驗證的」。
    /// 視口 ↔ 視圖座標的對應沿用 CommandExecutor.ViewportPosition 實測過的做法（2026-05-01）：
    /// 視口中心（GetBoxCenter）對應 View.Outline 的中心，View.Outline 以「視圖原點、紙張英呎」為單位。
    /// </summary>
    internal static class RevitSheetLayoutReader
    {
        private const double FeetToMm = 304.8;
        private const double FeetToMeters = 0.3048;

        private static Vec3 Meters(XYZ p) { return new Vec3(p.X * FeetToMeters, p.Y * FeetToMeters, p.Z * FeetToMeters); }
        private static Vec3 Dir(XYZ d) { return new Vec3(d.X, d.Y, d.Z); }

        private static string KindOf(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    return view is ViewPlan ? ViewKinds.Plan : null;
                case ViewType.CeilingPlan: return view is ViewPlan ? ViewKinds.Ceiling : null;
                case ViewType.Section: return view is ViewSection ? ViewKinds.Section : null;
                case ViewType.Elevation: return view is ViewSection ? ViewKinds.Elevation : null;
                default: return null;
            }
        }

        public static SheetPlacementBatch ReadSheets(Document doc, IReadOnlyList<SheetItem> sheets, double[] originMeters)
        {
            var batch = new SheetPlacementBatch();
            var offset = new Vec3(originMeters[0], originMeters[1], originMeters[2]);
            var failedViewports = 0;
            foreach (var item in sheets)
            {
                try
                {
                    var sheet = doc.GetElement(checked((IdType)item.ElementId).ToElementId()) as ViewSheet;
                    if (sheet == null) { batch.Warnings.Add("圖紙 " + item.Number + " 找不到，略過版面。"); continue; }
                    var outline = sheet.Outline; // BoundingBoxUV，英呎，圖紙座標
                    if (outline == null) { batch.Warnings.Add("圖紙 " + item.Number + " 讀不到外框，略過版面。"); continue; }
                    var minXMm = outline.Min.U * FeetToMm;
                    var minYMm = outline.Min.V * FeetToMm;
                    var placement = new SheetPlacement
                    {
                        SheetUniqueId = item.UniqueId,
                        SheetNumber = item.Number,
                        WidthMm = (outline.Max.U - outline.Min.U) * FeetToMm,
                        HeightMm = (outline.Max.V - outline.Min.V) * FeetToMm,
                        OutlineMinXMm = minXMm,
                        OutlineMinYMm = minYMm,
                    };
                    foreach (var vi in item.Viewports)
                    {
                        try
                        {
                            var viewport = vi.ViewportElementId == 0 ? null : doc.GetElement(checked((IdType)vi.ViewportElementId).ToElementId()) as Viewport;
                            if (viewport == null) continue;
                            var view = doc.GetElement(viewport.ViewId) as View;
                            var kind = view == null ? null : KindOf(view);
                            if (kind == null) continue; // 3D、詳圖等不對應圖紙上的點，不輸出
                            var vp = ReadViewport(doc, viewport, view, kind, vi, offset, minXMm, minYMm);
                            if (vp != null) placement.Viewports.Add(vp);
                            else failedViewports++;
                        }
                        catch (Exception)
                        {
                            failedViewports++;
                        }
                    }
                    batch.Sheets.Add(placement);
                }
                catch (Exception)
                {
                    batch.Warnings.Add("圖紙 " + item.Number + " 的版面讀取失敗，略過。");
                }
            }
            if (failedViewports > 0) batch.Warnings.Add(failedViewports + " 個視口的版面讀不到（比例或外框無效），略過。");
            return batch;
        }

        private static ViewportPlacement ReadViewport(Document doc, Viewport viewport, View view, string kind, ViewportItem known, Vec3 offset, double sheetMinXMm, double sheetMinYMm)
        {
            if (view.Scale <= 0) return null;
            double bMinX, bMinY, bMaxX, bMaxY;
            if (known != null && known.HasBox) { bMinX = known.BoxMinXFt; bMinY = known.BoxMinYFt; bMaxX = known.BoxMaxXFt; bMaxY = known.BoxMaxYFt; }
            else
            {
                var box = viewport.GetBoxOutline(); // 昂貴；通常已在量測步驟讀過
                bMinX = box.MinimumPoint.X; bMinY = box.MinimumPoint.Y; bMaxX = box.MaximumPoint.X; bMaxY = box.MaximumPoint.Y;
            }
            var center = viewport.GetBoxCenter();
            var viewOutline = view.Outline; // BoundingBoxUV：視圖範圍（紙張英呎、以視圖原點為準）
            if (viewOutline == null) return null;

            var right = ViewPositionsMath.ToGltfDirection(Dir(view.RightDirection)).Normalized();
            var up = ViewPositionsMath.ToGltfDirection(Dir(view.UpDirection)).Normalized();
            var look = ViewPositionsMath.ToGltfDirection(ViewPositionsMath.LookDirection(Dir(view.RightDirection), Dir(view.UpDirection))).Normalized();
            var vp = new ViewportPlacement
            {
                ViewUniqueId = view.UniqueId,
                ViewKind = kind,
                BoxMinX = bMinX * FeetToMm - sheetMinXMm,
                BoxMinY = bMinY * FeetToMm - sheetMinYMm,
                BoxMaxX = bMaxX * FeetToMm - sheetMinXMm,
                BoxMaxY = bMaxY * FeetToMm - sheetMinYMm,
                CenterX = center.X * FeetToMm - sheetMinXMm,
                CenterY = center.Y * FeetToMm - sheetMinYMm,
                Scale = view.Scale,
                RotationDeg = RotationDegrees(viewport),
                Origin = ViewPositionsMath.ToGltfPoint(Meters(view.Origin), offset),
                Right = right,
                Up = up,
                Direction = look,
                OutlineCenterU = (viewOutline.Min.U + viewOutline.Max.U) / 2.0 * FeetToMm,
                OutlineCenterV = (viewOutline.Min.V + viewOutline.Max.V) / 2.0 * FeetToMm,
            };

            if (kind == ViewKinds.Plan || kind == ViewKinds.Ceiling) vp.CutPlaneMeters = CutPlaneGltf(doc, (ViewPlan)view, offset);

            var cb = view.CropBox;
            if (cb != null && cb.Transform != null)
            {
                var t = cb.Transform;
                vp.CropActive = view.CropBoxActive;
                vp.CropLocalMin = Meters(cb.Min);
                vp.CropLocalMax = Meters(cb.Max);
                vp.CropOrigin = ViewPositionsMath.ToGltfPoint(Meters(t.Origin), offset);
                vp.CropBasisX = ViewPositionsMath.ToGltfDirection(Dir(t.BasisX)).Normalized();
                vp.CropBasisY = ViewPositionsMath.ToGltfDirection(Dir(t.BasisY)).Normalized();
                vp.CropBasisZ = ViewPositionsMath.ToGltfDirection(Dir(t.BasisZ)).Normalized();
            }
            return vp;
        }

        // Revit 的視口旋轉：順時針 90°＝−90（逆時針為正），逆時針 90°＝+90。
        private static double RotationDegrees(Viewport viewport)
        {
            switch (viewport.Rotation)
            {
                case ViewportRotation.Clockwise: return -90;
                case ViewportRotation.Counterclockwise: return 90;
                default: return 0;
            }
        }

        private static double? CutPlaneGltf(Document doc, ViewPlan plan, Vec3 offset)
        {
            try
            {
                var range = plan.GetViewRange();
                var level = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level;
                if (level == null) return null;
                var z = (level.ProjectElevation + range.GetOffset(PlanViewPlane.CutPlane)) * FeetToMeters;
                return ViewPositionsMath.ToGltfHeight(z, offset);
            }
            catch (Exception) { return null; }
        }

        public static SectionLineBatch ReadSectionLines(Document doc, IReadOnlyList<ViewItem> views, double[] originMeters)
        {
            var batch = new SectionLineBatch();
            var offset = new Vec3(originMeters[0], originMeters[1], originMeters[2]);
            var failed = 0;
            foreach (var item in views)
            {
                try
                {
                    var view = doc.GetElement(checked((IdType)item.ElementId).ToElementId()) as ViewSection;
                    if (view == null || view.IsTemplate) continue;
                    var box = view.CropBox;
                    if (box == null || box.Transform == null) { failed++; continue; }
                    var t = box.Transform;
                    var farClip = true;
                    try { farClip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.AsInteger() != 0; } catch (Exception) { }
                    string problem, note;
                    var frame = ViewPositionsMath.BuildFrame(Meters(t.Origin), Dir(t.BasisX), Dir(t.BasisY), Meters(box.Min), Meters(box.Max), offset, farClip, out problem, out note);
                    if (frame == null) { failed++; continue; }
                    var half = frame.Right * (frame.WidthMeters / 2);
                    var spanY = Math.Abs(frame.Up.Y) * frame.HeightMeters / 2 + Math.Abs(frame.Right.Y) * frame.WidthMeters / 2;
                    batch.Lines.Add(new SectionLine
                    {
                        ViewUniqueId = item.UniqueId,
                        Kind = item.Kind,
                        P0 = frame.Origin - half,
                        P1 = frame.Origin + half,
                        MinY = frame.Origin.Y - spanY,
                        MaxY = frame.Origin.Y + spanY,
                    });
                }
                catch (Exception) { failed++; }
            }
            if (failed > 0) batch.Warnings.Add(failed + " 個剖面／立面讀不到剖切線，它們在圖面上不會有可點的標記。");
            return batch;
        }
    }
}
