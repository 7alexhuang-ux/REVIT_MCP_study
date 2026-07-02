#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitMCP.Core.Grading
{
    internal interface IToposolidGradingAdapter
    {
        Toposolid ValidateToposolid(Document doc, long id);
        IReadOnlyList<Floor> ValidateFloors(Document doc, IReadOnlyList<long> ids);
        IReadOnlyList<FloorFootprint> ExtractBottomFootprints(IReadOnlyList<Floor> floors);
        Toposolid CreateDesignCopy(Document doc, Toposolid original, bool allowPhaseSetup);
        string WriteAssociation(Document doc, Toposolid design, long originalId, IReadOnlyList<long> floorIds);
        int ApplyFootprintOnly(Document doc, Toposolid design, IReadOnlyList<FloorFootprint> footprints);
        (double cutCubicMeters, double fillCubicMeters) ReadCutFill(Toposolid design);
    }

    internal sealed class RevitToposolidGradingAdapter : IToposolidGradingAdapter
    {
        private const string AbsoluteFinishUnavailableMessage =
            "Revit 2024 公開 API 無法可靠設定此 Toposolid 的絕對完成面";
        private const int MaximumCurveSubdivisionDepth = 32;
        private const int MaximumDiscretizedPointCount = 100000;
        private const double ChordLengthToleranceFactor = 1e-9;

        private static readonly Guid AssociationSchemaGuid =
            new Guid("9B4B16C7-4C9C-4B73-9D13-B44F88650D29");

        public Toposolid ValidateToposolid(Document doc, long id)
        {
            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            var element = doc.GetElement(new ElementId(CheckedElementId(id)));
            if (!(element is Toposolid toposolid))
            {
                throw new InvalidOperationException($"元素 ID {id} 不是 Toposolid。");
            }

            return toposolid;
        }

        public IReadOnlyList<Floor> ValidateFloors(Document doc, IReadOnlyList<long> ids)
        {
            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            if (ids == null || ids.Count == 0)
            {
                throw new ArgumentException("至少需要一個樓板 ID。", nameof(ids));
            }

            var floors = new List<Floor>(ids.Count);
            foreach (var id in ids)
            {
                var element = doc.GetElement(new ElementId(CheckedElementId(id)));
                if (!(element is Floor floor))
                {
                    throw new InvalidOperationException($"元素 ID {id} 不是 Floor。");
                }

                floors.Add(floor);
            }

            return floors;
        }

        public IReadOnlyList<FloorFootprint> ExtractBottomFootprints(IReadOnlyList<Floor> floors)
        {
            if (floors == null)
            {
                throw new ArgumentNullException(nameof(floors));
            }

            var footprints = new List<FloorFootprint>(floors.Count);
            foreach (var floor in floors)
            {
                if (floor == null)
                {
                    throw new ArgumentException("樓板集合不可包含 null。", nameof(floors));
                }

                footprints.Add(ExtractBottomFootprint(floor));
            }

            return footprints;
        }

        public Toposolid CreateDesignCopy(Document doc, Toposolid original, bool allowPhaseSetup)
        {
            EnsureModifiable(doc);
            if (original == null || original.Document != doc)
            {
                throw new ArgumentException("原始 Toposolid 必須屬於指定文件。", nameof(original));
            }

            var currentPhase = GetCurrentPhase(doc);
            var phases = doc.Phases;
            var currentPhaseIndex = FindPhaseIndex(phases, currentPhase.Id);
            if (currentPhaseIndex < 0)
            {
                throw new InvalidOperationException("目前視圖階段不在文件的階段清單中。");
            }

            var originalCreated = RequirePhaseParameter(original, BuiltInParameter.PHASE_CREATED, "建立階段");
            var originalDemolished = RequirePhaseParameter(original, BuiltInParameter.PHASE_DEMOLISHED, "拆除階段");
            var originalCreatedIndex = FindPhaseIndex(phases, originalCreated.AsElementId());
            var needsEarlierCreatedPhase = originalCreatedIndex < 0 || originalCreatedIndex >= currentPhaseIndex;
            var needsCurrentDemolishedPhase = originalDemolished.AsElementId() != currentPhase.Id;

            if ((needsEarlierCreatedPhase || needsCurrentDemolishedPhase) && !allowPhaseSetup)
            {
                throw new InvalidOperationException(
                    "原始 Toposolid 必須在較早階段建立並於目前階段拆除；請允許階段設定後再執行。");
            }

            if (needsEarlierCreatedPhase)
            {
                if (currentPhaseIndex == 0)
                {
                    throw new InvalidOperationException("目前階段之前沒有可供原始 Toposolid 使用的較早階段。");
                }

                SetPhaseParameter(originalCreated, phases.get_Item(currentPhaseIndex - 1).Id, "原始 Toposolid 建立階段");
            }

            if (needsCurrentDemolishedPhase)
            {
                SetPhaseParameter(originalDemolished, currentPhase.Id, "原始 Toposolid 拆除階段");
            }

            var copiedIds = ElementTransformUtils.CopyElement(doc, original.Id, XYZ.Zero);
            var copiedToposolids = copiedIds
                .Select(id => doc.GetElement(id))
                .OfType<Toposolid>()
                .ToList();
            if (copiedToposolids.Count != 1)
            {
                throw new InvalidOperationException("複製原始 Toposolid 後未取得唯一的設計 Toposolid。");
            }

            var design = copiedToposolids[0];
            var designCreated = RequirePhaseParameter(design, BuiltInParameter.PHASE_CREATED, "建立階段");
            var designDemolished = RequirePhaseParameter(design, BuiltInParameter.PHASE_DEMOLISHED, "拆除階段");
            SetPhaseParameter(designDemolished, ElementId.InvalidElementId, "設計 Toposolid 拆除階段");
            SetPhaseParameter(designCreated, currentPhase.Id, "設計 Toposolid 建立階段");

            doc.Regenerate();
            if (design.SketchId == ElementId.InvalidElementId)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有有效的 SketchId。");
            }

            var editor = design.GetSlabShapeEditor();
            if (editor == null || !editor.IsValidObject)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有有效的 SlabShapeEditor。");
            }

            return design;
        }

        public string WriteAssociation(
            Document doc,
            Toposolid design,
            long originalId,
            IReadOnlyList<long> floorIds)
        {
            EnsureModifiable(doc);
            if (design == null || design.Document != doc)
            {
                throw new ArgumentException("設計 Toposolid 必須屬於指定文件。", nameof(design));
            }

            if (floorIds == null)
            {
                throw new ArgumentNullException(nameof(floorIds));
            }

            var schema = GetOrCreateAssociationSchema();
            EnsureAssociationSchema(schema);
            var associationId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            var entity = new Entity(schema);
            entity.Set("AssociationId", associationId);
            entity.Set("OriginalToposolidId", originalId);
            entity.Set(
                "FloorIds",
                string.Join(",", floorIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
            design.SetEntity(entity);
            return associationId;
        }

        public int ApplyFootprintOnly(
            Document doc,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints)
        {
            EnsureModifiable(doc);
            if (design == null || design.Document != doc)
            {
                throw new ArgumentException("設計 Toposolid 必須屬於指定文件。", nameof(design));
            }

            if (footprints == null || footprints.Count == 0)
            {
                throw new ArgumentException("至少需要一個樓板投影。", nameof(footprints));
            }

            var editor = design.GetSlabShapeEditor();
            if (editor == null || !editor.IsValidObject)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有有效的 SlabShapeEditor。");
            }

            // Revit 2024 公開 API 可讀頂點 Position，卻未公開頂點目前的 offset。
            // 缺少目前 offset 就無法由絕對 Z 校準參考平面，因此在任何形狀寫入前安全中止。
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                _ = vertex.Position;
            }

            throw new InvalidOperationException(AbsoluteFinishUnavailableMessage);
        }

        public (double cutCubicMeters, double fillCubicMeters) ReadCutFill(Toposolid design)
        {
            if (design == null)
            {
                throw new ArgumentNullException(nameof(design));
            }

            var cut = ReadVolumeParameter(
                design,
                BuiltInParameter.VOLUME_CUT,
                "CUT");
            var fill = ReadVolumeParameter(
                design,
                BuiltInParameter.VOLUME_FILL,
                "FILL");

            if (Math.Abs(cut) <= 1e-12 && Math.Abs(fill) <= 1e-12)
            {
                throw new InvalidOperationException("Toposolid 幾何變更後 CUT 與 FILL 皆為零，無法通過驗收。");
            }

            return (
                UnitUtils.ConvertFromInternalUnits(cut, UnitTypeId.CubicMeters),
                UnitUtils.ConvertFromInternalUnits(fill, UnitTypeId.CubicMeters));
        }

        private static FloorFootprint ExtractBottomFootprint(Floor floor)
        {
            var geometry = floor.get_Geometry(new Options());
            var bottomFaces = new List<PlanarFace>();
            if (geometry != null)
            {
                foreach (var geometryObject in geometry)
                {
                    if (!(geometryObject is Solid solid) || solid.Volume <= 0)
                    {
                        continue;
                    }

                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace && planarFace.FaceNormal.Z < -0.999)
                        {
                            bottomFaces.Add(planarFace);
                        }
                    }
                }
            }

            if (bottomFaces.Count != 1)
            {
                throw new InvalidOperationException(
                    $"樓板 ID {floor.Id.Value} 沒有可用的單一平面底面");
            }

            var bottomFace = bottomFaces[0];
            var loops = bottomFace.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0)
            {
                throw new InvalidOperationException(
                    $"樓板 ID {floor.Id.Value} 沒有可用的單一平面底面");
            }

            var outerLoop = loops
                .Select(DiscretizeLoop)
                .Where(points => points.Count >= 3)
                .OrderByDescending(points => Math.Abs(SignedArea(points)))
                .FirstOrDefault();
            if (outerLoop == null)
            {
                throw new InvalidOperationException(
                    $"樓板 ID {floor.Id.Value} 沒有可用的單一平面底面");
            }

            var origin = bottomFace.Origin;
            var normal = bottomFace.FaceNormal;
            return new FloorFootprint
            {
                FloorId = floor.Id.Value,
                OuterLoop = outerLoop,
                BottomElevationAt = (x, y) => origin.Z
                    - ((normal.X * (x - origin.X) + normal.Y * (y - origin.Y)) / normal.Z)
            };
        }

        private static IReadOnlyList<Point2D> DiscretizeLoop(CurveLoop loop)
        {
            var maximumChordLength = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
            var points = new List<Point2D>();
            foreach (var curve in loop)
            {
                if (curve is Line)
                {
                    AddDistinctPoint(points, ToPoint2D(curve.GetEndPoint(0)));
                    AddDistinctPoint(points, ToPoint2D(curve.GetEndPoint(1)));
                    EnsurePointLimit(points.Count);
                    continue;
                }

                var curvePoints = new List<XYZ> { curve.Evaluate(0, true) };
                AppendAdaptiveCurvePoints(
                    curve,
                    0,
                    curvePoints[0],
                    1,
                    curve.Evaluate(1, true),
                    0,
                    maximumChordLength,
                    curvePoints);
                ValidateChordLengths(curvePoints, maximumChordLength);

                foreach (var curvePoint in curvePoints)
                {
                    AddDistinctPoint(points, ToPoint2D(curvePoint));
                    EnsurePointLimit(points.Count);
                }
            }

            if (points.Count > 1 && DistanceSquared(points[0], points[points.Count - 1]) <= 1e-18)
            {
                points.RemoveAt(points.Count - 1);
            }

            return points;
        }

        private static void AppendAdaptiveCurvePoints(
            Curve curve,
            double startParameter,
            XYZ startPoint,
            double endParameter,
            XYZ endPoint,
            int depth,
            double maximumChordLength,
            ICollection<XYZ> points)
        {
            var tolerance = maximumChordLength * ChordLengthToleranceFactor;
            var subcurveLength = GetSubcurveLength(curve, startParameter, endParameter);
            var chordLength = startPoint.DistanceTo(endPoint);
            if (subcurveLength <= maximumChordLength + tolerance
                && chordLength <= maximumChordLength + tolerance)
            {
                EnsurePointLimit(points.Count + 1);
                points.Add(endPoint);
                return;
            }

            if (depth >= MaximumCurveSubdivisionDepth)
            {
                throw new InvalidOperationException(
                    $"曲線離散化超過最大遞迴深度 {MaximumCurveSubdivisionDepth}，"
                    + "無法保證 300 mm 最大弦長。");
            }

            var middleParameter = (startParameter + endParameter) / 2.0;
            if (middleParameter <= startParameter || middleParameter >= endParameter)
            {
                throw new InvalidOperationException("曲線參數無法繼續細分，無法保證 300 mm 最大弦長。");
            }

            var middlePoint = curve.Evaluate(middleParameter, true);
            AppendAdaptiveCurvePoints(
                curve,
                startParameter,
                startPoint,
                middleParameter,
                middlePoint,
                depth + 1,
                maximumChordLength,
                points);
            AppendAdaptiveCurvePoints(
                curve,
                middleParameter,
                middlePoint,
                endParameter,
                endPoint,
                depth + 1,
                maximumChordLength,
                points);
        }

        private static double GetSubcurveLength(
            Curve curve,
            double startParameter,
            double endParameter)
        {
            using (var subcurve = curve.Clone())
            {
                subcurve.MakeBound(
                    curve.ComputeRawParameter(startParameter),
                    curve.ComputeRawParameter(endParameter));
                return subcurve.Length;
            }
        }

        private static void ValidateChordLengths(
            IReadOnlyList<XYZ> points,
            double maximumChordLength)
        {
            var maximumWithTolerance = maximumChordLength
                * (1 + ChordLengthToleranceFactor);
            for (var index = 1; index < points.Count; index++)
            {
                if (points[index - 1].DistanceTo(points[index]) > maximumWithTolerance)
                {
                    throw new InvalidOperationException("曲線離散化產生超過 300 mm 的弦長。");
                }
            }
        }

        private static Point2D ToPoint2D(XYZ point)
        {
            return new Point2D(point.X, point.Y);
        }

        private static void EnsurePointLimit(int pointCount)
        {
            if (pointCount > MaximumDiscretizedPointCount)
            {
                throw new InvalidOperationException(
                    $"曲線離散化超過最大點數 {MaximumDiscretizedPointCount}，已中止處理。");
            }
        }

        private static void AddDistinctPoint(ICollection<Point2D> points, Point2D candidate)
        {
            var last = points.LastOrDefault();
            if (points.Count == 0 || DistanceSquared(last, candidate) > 1e-18)
            {
                points.Add(candidate);
            }
        }

        private static double SignedArea(IReadOnlyList<Point2D> polygon)
        {
            var doubledArea = 0.0;
            for (var index = 0; index < polygon.Count; index++)
            {
                var current = polygon[index];
                var next = polygon[(index + 1) % polygon.Count];
                doubledArea += current.X * next.Y - next.X * current.Y;
            }

            return doubledArea / 2.0;
        }

        private static double DistanceSquared(Point2D first, Point2D second)
        {
            var deltaX = second.X - first.X;
            var deltaY = second.Y - first.Y;
            return deltaX * deltaX + deltaY * deltaY;
        }

        private static int CheckedElementId(long id)
        {
            if (id <= 0 || id > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(id), "元素 ID 必須介於 1 與 Int32.MaxValue 之間。");
            }

            return checked((int)id);
        }

        private static void EnsureModifiable(Document doc)
        {
            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            if (!doc.IsModifiable)
            {
                throw new InvalidOperationException("呼叫端必須先開啟 Revit 交易。");
            }
        }

        private static Phase GetCurrentPhase(Document doc)
        {
            var activeView = doc.ActiveView;
            var phaseParameter = activeView?.get_Parameter(BuiltInParameter.VIEW_PHASE);
            var phase = phaseParameter == null ? null : doc.GetElement(phaseParameter.AsElementId()) as Phase;
            if (phase == null)
            {
                throw new InvalidOperationException("目前視圖沒有有效的階段。");
            }

            return phase;
        }

        private static int FindPhaseIndex(PhaseArray phases, ElementId phaseId)
        {
            for (var index = 0; index < phases.Size; index++)
            {
                if (phases.get_Item(index).Id == phaseId)
                {
                    return index;
                }
            }

            return -1;
        }

        private static Parameter RequirePhaseParameter(
            Element element,
            BuiltInParameter builtInParameter,
            string displayName)
        {
            var parameter = element.get_Parameter(builtInParameter);
            if (parameter == null || parameter.StorageType != StorageType.ElementId)
            {
                throw new InvalidOperationException($"元素 ID {element.Id.Value} 缺少有效的{displayName}參數。");
            }

            return parameter;
        }

        private static void SetPhaseParameter(Parameter parameter, ElementId value, string displayName)
        {
            if (parameter.IsReadOnly || !parameter.Set(value))
            {
                throw new InvalidOperationException($"無法設定{displayName}。");
            }
        }

        private static Schema GetOrCreateAssociationSchema()
        {
            var schema = Schema.Lookup(AssociationSchemaGuid);
            if (schema != null)
            {
                return schema;
            }

            var builder = new SchemaBuilder(AssociationSchemaGuid);
            builder.SetSchemaName("RevitMCP_ToposolidGrading");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("AssociationId", typeof(string));
            builder.AddSimpleField("OriginalToposolidId", typeof(long));
            builder.AddSimpleField("FloorIds", typeof(string));
            return builder.Finish();
        }

        private static void EnsureAssociationSchema(Schema schema)
        {
            if (schema.GetField("AssociationId") == null
                || schema.GetField("OriginalToposolidId") == null
                || schema.GetField("FloorIds") == null)
            {
                throw new InvalidOperationException("既有 RevitMCP_ToposolidGrading schema 欄位不相容。");
            }
        }

        private static double ReadVolumeParameter(
            Toposolid design,
            BuiltInParameter builtInParameter,
            string displayName)
        {
            var parameter = design.get_Parameter(builtInParameter);
            if (parameter == null
                || !parameter.HasValue
                || parameter.StorageType != StorageType.Double)
            {
                throw new InvalidOperationException(
                    $"設計 Toposolid 缺少有值且可讀取的內建 {displayName} 體積參數。");
            }

            var value = parameter.AsDouble();
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException($"設計 Toposolid 的 {displayName} 體積不是有效數值。");
            }

            return value;
        }
    }
}
#endif
