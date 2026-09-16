using System;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 視圖 CropBox 操作

        /// <summary>
        /// 將指定 2D 視圖的 CropBox 對齊到目標元素的 BoundingBox。
        /// </summary>
        private object AlignViewCropBoxToElement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            IdType elementId = parameters["elementId"]?.Value<IdType>() ?? 0;
            if (elementId == 0)
                throw new Exception("必須指定 elementId");

            IdType viewIdParam = parameters["viewId"]?.Value<IdType>() ?? 0;
            double paddingMm = parameters["padding_mm"]?.Value<double>() ?? 0;
            double paddingFeet = paddingMm / 304.8;

            View view = viewIdParam == 0
                ? doc.ActiveView
                : doc.GetElement(viewIdParam.ToElementId()) as View;
            if (view == null)
                throw new Exception($"找不到視圖 ID={viewIdParam}");

            Element element = doc.GetElement(elementId.ToElementId());
            if (element == null)
                throw new Exception($"找不到元素 ID={elementId}");

            // Backward-compatible bridge for MCP clients whose tool registry was
            // initialized before set_3d_section_box existed. The legacy command
            // name must still use the real View3D Section Box path in 3D; it must
            // never fall through to the CropBox implementation below.
            if (view is View3D)
                return Set3DSectionBox(parameters);

            BoundingBoxXYZ elemBbox = element.get_BoundingBox(view);
            if (elemBbox == null)
                throw new Exception($"元素 ID={elementId} 在視圖 '{view.Name}' 中沒有有效的 BoundingBox");

            BoundingBoxXYZ oldCropBox = view.CropBox;
            Transform cropTransform = oldCropBox.Transform;

            XYZ elemMinLocal = cropTransform.Inverse.OfPoint(elemBbox.Min);
            XYZ elemMaxLocal = cropTransform.Inverse.OfPoint(elemBbox.Max);

            double minX = Math.Min(elemMinLocal.X, elemMaxLocal.X) - paddingFeet;
            double maxX = Math.Max(elemMinLocal.X, elemMaxLocal.X) + paddingFeet;
            double minY = Math.Min(elemMinLocal.Y, elemMaxLocal.Y) - paddingFeet;
            double maxY = Math.Max(elemMinLocal.Y, elemMaxLocal.Y) + paddingFeet;

            XYZ newMin = new XYZ(minX, minY, oldCropBox.Min.Z);
            XYZ newMax = new XYZ(maxX, maxY, oldCropBox.Max.Z);

            BoundingBoxXYZ newCropBox = new BoundingBoxXYZ
            {
                Min = newMin,
                Max = newMax,
                Transform = cropTransform
            };

            using (Transaction trans = TransactionHelper.Begin(doc, "對齊 CropBox 到元素"))
            {
                trans.Start();
                view.CropBoxActive = true;
                view.CropBoxVisible = true;
                view.CropBox = newCropBox;
                trans.Commit();
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                ViewType = view.ViewType.ToString(),
                Mode = "CropBox",
                ElementId = elementId,
                ElementCategory = element.Category?.Name ?? "Unknown",
                Padding_mm = paddingMm,
                OldCropBox_mm = new
                {
                    Min = new { x = oldCropBox.Min.X * 304.8, y = oldCropBox.Min.Y * 304.8, z = oldCropBox.Min.Z * 304.8 },
                    Max = new { x = oldCropBox.Max.X * 304.8, y = oldCropBox.Max.Y * 304.8, z = oldCropBox.Max.Z * 304.8 },
                    Width = (oldCropBox.Max.X - oldCropBox.Min.X) * 304.8,
                    Height = (oldCropBox.Max.Y - oldCropBox.Min.Y) * 304.8
                },
                NewCropBox_mm = new
                {
                    Min = new { x = newMin.X * 304.8, y = newMin.Y * 304.8, z = newMin.Z * 304.8 },
                    Max = new { x = newMax.X * 304.8, y = newMax.Y * 304.8, z = newMax.Z * 304.8 },
                    Width = (newMax.X - newMin.X) * 304.8,
                    Height = (newMax.Y - newMin.Y) * 304.8
                }
            };
        }

        /// <summary>
        /// 將 3D 視圖的 Section Box 對齊到元素的完整模型座標 BoundingBox。
        /// 這是 3D 定位的唯一合法路徑；同時關閉 CropBox，避免雙重裁切。
        /// </summary>
        private object Set3DSectionBox(JObject parameters)
        {
            var uiDoc = _uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            IdType viewIdParam = parameters["viewId"]?.Value<IdType>() ?? 0;
            View3D view3D = (viewIdParam == 0 ? doc.ActiveView : doc.GetElement(viewIdParam.ToElementId())) as View3D;
            if (view3D == null || view3D.IsTemplate)
                throw new Exception("目標必須是非樣板 3D 視圖");

            bool clearCropOnly = parameters["clearCropOnly"]?.Value<bool>() ?? false;
            bool setActive = parameters["setActive"]?.Value<bool>() ?? true;
            bool selectElement = parameters["selectElement"]?.Value<bool>() ?? true;
            bool includeSupportingBeams = parameters["includeSupportingBeams"]?.Value<bool>() ?? false;
            double supportingBeamToleranceMm = parameters["supportingBeamTolerance_mm"]?.Value<double>() ?? 300;
            // padding_mm is retained as the backward-compatible all-axis fallback.
            // Smoke-review views normally need asymmetric Z extents: enough depth
            // below the room to show the full supporting beams, but no extra height
            // above the room that would reveal the next floor structure.
            double paddingMm = parameters["padding_mm"]?.Value<double>() ?? 1000;
            double paddingXYMm = parameters["padding_xy_mm"]?.Value<double>() ?? paddingMm;
            double paddingBottomMm = parameters["padding_bottom_mm"]?.Value<double>() ?? paddingMm;
            double paddingTopMm = parameters["padding_top_mm"]?.Value<double>() ?? paddingMm;
            if (paddingMm < 0 || paddingXYMm < 0 || paddingBottomMm < 0 || paddingTopMm < 0 || supportingBeamToleranceMm < 0)
                throw new Exception("Section Box padding 不可小於 0");

            if (clearCropOnly)
            {
                using (Transaction trans = TransactionHelper.Begin(doc, "關閉 3D CropBox"))
                {
                    trans.Start();
                    view3D.CropBoxActive = false;
                    view3D.CropBoxVisible = false;
                    trans.Commit();
                }

                if (setActive)
                    uiDoc.ActiveView = view3D;

                return new
                {
                    Success = true,
                    Mode = "ClearCropOnly",
                    ViewId = view3D.Id.GetIdValue(),
                    ViewName = view3D.Name,
                    CropBoxActive = view3D.CropBoxActive,
                    SectionBoxActive = view3D.IsSectionBoxActive
                };
            }

            IdType elementId = parameters["elementId"]?.Value<IdType>() ?? 0;
            if (elementId == 0)
                throw new Exception("必須指定 elementId；若只要清除錯誤的 CropBox，請設 clearCropOnly=true");

            Element element = doc.GetElement(elementId.ToElementId());
            if (element == null)
                throw new Exception($"找不到元素 ID={elementId}");

            BoundingBoxXYZ elementBox = element.get_BoundingBox(null);
            if (elementBox == null)
                throw new Exception($"元素 ID={elementId} 沒有有效的模型 BoundingBox");

            Transform transform = elementBox.Transform ?? Transform.Identity;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;

            for (int ix = 0; ix <= 1; ix++)
            for (int iy = 0; iy <= 1; iy++)
            for (int iz = 0; iz <= 1; iz++)
            {
                XYZ localPoint = new XYZ(
                    ix == 0 ? elementBox.Min.X : elementBox.Max.X,
                    iy == 0 ? elementBox.Min.Y : elementBox.Max.Y,
                    iz == 0 ? elementBox.Min.Z : elementBox.Max.Z);
                XYZ worldPoint = transform.OfPoint(localPoint);
                minX = Math.Min(minX, worldPoint.X);
                minY = Math.Min(minY, worldPoint.Y);
                minZ = Math.Min(minZ, worldPoint.Z);
                maxX = Math.Max(maxX, worldPoint.X);
                maxY = Math.Max(maxY, worldPoint.Y);
                maxZ = Math.Max(maxZ, worldPoint.Z);
            }

            int supportingBeamCount = 0;
            if (includeSupportingBeams)
            {
                // Capture the target element bounds before unioning, otherwise each
                // newly included beam could recursively pull in the next beam bay.
                double targetMinX = minX;
                double targetMinY = minY;
                double targetMinZ = minZ;
                double targetMaxX = maxX;
                double targetMaxY = maxY;
                double toleranceFeet = supportingBeamToleranceMm / 304.8;

                var framing = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType();

                foreach (Element beam in framing)
                {
                    BoundingBoxXYZ beamBox = beam.get_BoundingBox(null);
                    if (beamBox == null) continue;

                    Transform beamTransform = beamBox.Transform ?? Transform.Identity;
                    double beamMinX = double.PositiveInfinity;
                    double beamMinY = double.PositiveInfinity;
                    double beamMinZ = double.PositiveInfinity;
                    double beamMaxX = double.NegativeInfinity;
                    double beamMaxY = double.NegativeInfinity;
                    double beamMaxZ = double.NegativeInfinity;

                    for (int ix = 0; ix <= 1; ix++)
                    for (int iy = 0; iy <= 1; iy++)
                    for (int iz = 0; iz <= 1; iz++)
                    {
                        XYZ beamPoint = beamTransform.OfPoint(new XYZ(
                            ix == 0 ? beamBox.Min.X : beamBox.Max.X,
                            iy == 0 ? beamBox.Min.Y : beamBox.Max.Y,
                            iz == 0 ? beamBox.Min.Z : beamBox.Max.Z));
                        beamMinX = Math.Min(beamMinX, beamPoint.X);
                        beamMinY = Math.Min(beamMinY, beamPoint.Y);
                        beamMinZ = Math.Min(beamMinZ, beamPoint.Z);
                        beamMaxX = Math.Max(beamMaxX, beamPoint.X);
                        beamMaxY = Math.Max(beamMaxY, beamPoint.Y);
                        beamMaxZ = Math.Max(beamMaxZ, beamPoint.Z);
                    }

                    bool overlapsTargetXY =
                        beamMaxX >= targetMinX - toleranceFeet && beamMinX <= targetMaxX + toleranceFeet &&
                        beamMaxY >= targetMinY - toleranceFeet && beamMinY <= targetMaxY + toleranceFeet;
                    bool supportsTargetBase = Math.Abs(beamMaxZ - targetMinZ) <= toleranceFeet;
                    if (!overlapsTargetXY || !supportsTargetBase) continue;

                    minX = Math.Min(minX, beamMinX);
                    minY = Math.Min(minY, beamMinY);
                    minZ = Math.Min(minZ, beamMinZ);
                    maxX = Math.Max(maxX, beamMaxX);
                    maxY = Math.Max(maxY, beamMaxY);
                    // Deliberately do not raise maxZ above the target element top.
                    supportingBeamCount++;
                }
            }

            double paddingXYFeet = paddingXYMm / 304.8;
            double paddingBottomFeet = paddingBottomMm / 304.8;
            double paddingTopFeet = paddingTopMm / 304.8;
            XYZ sectionMin = new XYZ(minX - paddingXYFeet, minY - paddingXYFeet, minZ - paddingBottomFeet);
            XYZ sectionMax = new XYZ(maxX + paddingXYFeet, maxY + paddingXYFeet, maxZ + paddingTopFeet);
            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ
            {
                Min = sectionMin,
                Max = sectionMax,
                Transform = Transform.Identity,
                Enabled = true
            };

            using (Transaction trans = TransactionHelper.Begin(doc, "設定 3D Section Box"))
            {
                trans.Start();
                view3D.SetSectionBox(sectionBox);
                view3D.IsSectionBoxActive = true;
                view3D.CropBoxActive = false;
                view3D.CropBoxVisible = false;
                trans.Commit();
            }

            if (setActive)
                uiDoc.ActiveView = view3D;
            if (selectElement)
                uiDoc.Selection.SetElementIds(new[] { element.Id });

            return new
            {
                Success = true,
                Mode = "SectionBox",
                ViewId = view3D.Id.GetIdValue(),
                ViewName = view3D.Name,
                ElementId = elementId,
                ElementName = element.Name,
                ElementCategory = element.Category?.Name ?? "Unknown",
                Padding_mm = paddingMm,
                PaddingXY_mm = paddingXYMm,
                PaddingBottom_mm = paddingBottomMm,
                PaddingTop_mm = paddingTopMm,
                IncludeSupportingBeams = includeSupportingBeams,
                SupportingBeamTolerance_mm = supportingBeamToleranceMm,
                SupportingBeamCount = supportingBeamCount,
                CropBoxActive = view3D.CropBoxActive,
                SectionBoxActive = view3D.IsSectionBoxActive,
                SectionBox_mm = new
                {
                    Min = new { x = sectionMin.X * 304.8, y = sectionMin.Y * 304.8, z = sectionMin.Z * 304.8 },
                    Max = new { x = sectionMax.X * 304.8, y = sectionMax.Y * 304.8, z = sectionMax.Z * 304.8 },
                    Width = (sectionMax.X - sectionMin.X) * 304.8,
                    Depth = (sectionMax.Y - sectionMin.Y) * 304.8,
                    Height = (sectionMax.Z - sectionMin.Z) * 304.8
                }
            };
        }

        /// <summary>
        /// 平移指定視圖的 CropBox（在 CropBox 自身座標系中位移 dx, dy）
        /// </summary>
        private object ShiftViewCropBox(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            IdType viewIdParam = parameters["viewId"]?.Value<IdType>() ?? 0;
            double dxMm = parameters["dx_mm"]?.Value<double>() ?? 0;
            double dyMm = parameters["dy_mm"]?.Value<double>() ?? 0;

            View view = viewIdParam == 0
                ? doc.ActiveView
                : doc.GetElement(viewIdParam.ToElementId()) as View;
            if (view == null)
                throw new Exception($"找不到視圖 ID={viewIdParam}");

            double dxFeet = dxMm / 304.8;
            double dyFeet = dyMm / 304.8;

            BoundingBoxXYZ oldCropBox = view.CropBox;
            XYZ newMin = new XYZ(oldCropBox.Min.X + dxFeet, oldCropBox.Min.Y + dyFeet, oldCropBox.Min.Z);
            XYZ newMax = new XYZ(oldCropBox.Max.X + dxFeet, oldCropBox.Max.Y + dyFeet, oldCropBox.Max.Z);

            BoundingBoxXYZ newCropBox = new BoundingBoxXYZ
            {
                Min = newMin,
                Max = newMax,
                Transform = oldCropBox.Transform
            };

            using (Transaction trans = TransactionHelper.Begin(doc, "平移 CropBox"))
            {
                trans.Start();
                view.CropBoxActive = true;
                view.CropBoxVisible = true;
                view.CropBox = newCropBox;
                trans.Commit();
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                Dx_mm = dxMm,
                Dy_mm = dyMm,
                NewCropBox_mm = new
                {
                    Min = new { x = newMin.X * 304.8, y = newMin.Y * 304.8 },
                    Max = new { x = newMax.X * 304.8, y = newMax.Y * 304.8 }
                }
            };
        }

        #endregion
    }
}
