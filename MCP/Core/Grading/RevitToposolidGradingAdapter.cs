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
        GradingOutcome ApplyGrading(
            Document doc,
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            ICollection<string> warnings);
        void WriteSchemeRecord(Document doc, Toposolid design, string json, string associationId);
        int CountSchemeRecords(Document doc);
        void ShiftFloorHeights(Document doc, IReadOnlyList<Floor> floors, double deltaFeet);
        (double cutCubicMeters, double fillCubicMeters) ReadCutFill(Toposolid design);
    }

    internal sealed class RevitToposolidGradingAdapter : IToposolidGradingAdapter
    {
        private const string AbsoluteFinishUnavailableMessage =
            "Revit 2024 公開 API 無法可靠設定此 Toposolid 的絕對完成面";
        private const int MaximumCurveSubdivisionDepth = 32;
        private const int MaximumDiscretizedPointCount = 100000;
        private const double ChordLengthToleranceFactor = 1e-9;

        // 底面法向量 Z 分量上限：-cos(15°)。允許至約 15 度（約 1:3.7）的斜板底面，仍排除側面與頂面。
        private const double BottomFaceNormalZMaximum = -0.965925826289068;

        private static readonly Guid AssociationSchemaGuid =
            new Guid("9B4B16C7-4C9C-4B73-9D13-B44F88650D29");

        private readonly GradingTimeline _timeline;

        public RevitToposolidGradingAdapter()
            : this(null)
        {
        }

        public RevitToposolidGradingAdapter(GradingTimeline timeline)
        {
            _timeline = timeline ?? new GradingTimeline();
        }

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
            if (original == null || !doc.Equals(original.Document))
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
            if (design == null || !doc.Equals(design.Document))
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

        public void WriteSchemeRecord(Document doc, Toposolid design, string json, string associationId)
        {
            EnsureModifiable(doc);
            if (design == null || !doc.Equals(design.Document))
            {
                throw new ArgumentException("設計 Toposolid 必須屬於指定文件。", nameof(design));
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("方案記錄 JSON 不可空白。", nameof(json));
            }

            var schema = GetOrCreateSchemeSchema();
            var entity = new Entity(schema);
            entity.Set("AssociationId", associationId ?? string.Empty);
            entity.Set("SchemeJson", json);
            design.SetEntity(entity);
        }

        public int CountSchemeRecords(Document doc)
        {
            return ReadSchemeRecords(doc).Count;
        }

        /// <summary>把全部控制樓板的「自標高偏移」統一加上 deltaFeet（保留樓板間相對高差）。</summary>
        public void ShiftFloorHeights(Document doc, IReadOnlyList<Floor> floors, double deltaFeet)
        {
            EnsureModifiable(doc);
            if (floors == null || floors.Count == 0)
            {
                throw new ArgumentException("至少需要一片樓板。", nameof(floors));
            }

            foreach (var floor in floors)
            {
                var offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParameter == null
                    || offsetParameter.IsReadOnly
                    || offsetParameter.StorageType != StorageType.Double)
                {
                    throw new InvalidOperationException(
                        $"樓板 ID {floor.Id.Value} 缺少可寫入的「自標高偏移」參數，無法試算升降。");
                }

                if (!offsetParameter.Set(offsetParameter.AsDouble() + deltaFeet))
                {
                    throw new InvalidOperationException($"樓板 ID {floor.Id.Value} 的自標高偏移寫入失敗。");
                }
            }

            doc.Regenerate();
        }

        /// <summary>讀取模型內全部方案記錄 JSON（依設計地形逐一掃描）。</summary>
        public static IReadOnlyList<string> ReadSchemeRecords(Document doc)
        {
            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            var records = new List<string>();
            var schema = Schema.Lookup(SchemeSchemaGuid);
            if (schema == null)
            {
                return records;
            }

            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Toposolid)))
            {
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid())
                {
                    continue;
                }

                var json = entity.Get<string>("SchemeJson");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    records.Add(json);
                }
            }

            return records;
        }

        private static readonly Guid SchemeSchemaGuid =
            new Guid("3F8A9D2C-71B4-4E5A-9C86-2D4E5F6A7B80");

        private static Schema GetOrCreateSchemeSchema()
        {
            var schema = Schema.Lookup(SchemeSchemaGuid);
            if (schema != null)
            {
                if (schema.GetField("AssociationId") == null || schema.GetField("SchemeJson") == null)
                {
                    throw new InvalidOperationException("既有 RevitMCP_GradingScheme schema 欄位不相容。");
                }

                return schema;
            }

            var builder = new SchemaBuilder(SchemeSchemaGuid);
            builder.SetSchemaName("RevitMCP_GradingScheme");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("AssociationId", typeof(string));
            builder.AddSimpleField("SchemeJson", typeof(string));
            return builder.Finish();
        }

        public GradingOutcome ApplyGrading(
            Document doc,
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            ICollection<string> warnings)
        {
            EnsureModifiable(doc);
            if (original == null || !doc.Equals(original.Document))
            {
                throw new ArgumentException("原始 Toposolid 必須屬於指定文件。", nameof(original));
            }

            if (design == null || !doc.Equals(design.Document))
            {
                throw new ArgumentException("設計 Toposolid 必須屬於指定文件。", nameof(design));
            }

            if (footprints == null || footprints.Count == 0)
            {
                throw new ArgumentException("至少需要一個樓板投影。", nameof(footprints));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (warnings == null)
            {
                throw new ArgumentNullException(nameof(warnings));
            }

            var editor = design.GetSlabShapeEditor();
            if (editor == null || !editor.IsValidObject)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有有效的 SlabShapeEditor。");
            }

            var xyTolerance = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var elevationTolerance = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
            using (_timeline.Measure("重疊衝突檢查"))
            {
                EnsureNoConflictingOverlaps(footprints, xyTolerance, elevationTolerance);
            }

            if (!editor.IsEnabled)
            {
                using (_timeline.Measure("形狀編輯器啟用"))
                {
                    editor.Enable();
                    doc.Regenerate();
                }
            }

            // 現況地形頂面 Z 以垂直射線與設計副本實體求交；必須在任何形狀修改前取樣。
            IReadOnlyList<Solid> solids;
            BoundingBoxXYZ boundingBox;
            List<XYZ> existingPositions;
            using (_timeline.Measure("現況幾何擷取"))
            {
                solids = CollectSolids(design);
                boundingBox = design.get_BoundingBox(null);
                existingPositions = CollectVertexPositions(editor);
            }

            if (solids.Count == 0)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有可用的實體幾何。");
            }

            if (boundingBox == null)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有有效的範圍盒。");
            }

            var rayBottomZ = boundingBox.Min.Z - 10.0;
            var rayTopZ = boundingBox.Max.Z + 10.0;
            var pointsToAdd = new List<XYZ>();
            var boundarySampleScope = _timeline.Measure("邊界射線取樣");
            foreach (var footprint in footprints)
            {
                var terrainHitCount = 0;
                foreach (var boundaryPoint in footprint.OuterLoop)
                {
                    var terrainZ = IntersectTerrainTopZ(solids, boundaryPoint, rayBottomZ, rayTopZ);
                    if (!terrainZ.HasValue)
                    {
                        // 規格規則 7：投影超出地形的部分僅處理相交區域。
                        continue;
                    }

                    terrainHitCount++;
                    var candidate = new XYZ(boundaryPoint.X, boundaryPoint.Y, terrainZ.Value);
                    if (HasNearbyXY(existingPositions, candidate, xyTolerance)
                        || HasNearbyXY(pointsToAdd, candidate, xyTolerance))
                    {
                        continue;
                    }

                    pointsToAdd.Add(candidate);
                }

                if (terrainHitCount == 0
                    && !existingPositions.Any(position => Polygon2D.Contains(
                        footprint.OuterLoop,
                        ToPoint2D(position),
                        xyTolerance)))
                {
                    throw new InvalidOperationException(
                        $"樓板 ID {footprint.FloorId} 的投影範圍完全不與地形相交。");
                }
            }

            boundarySampleScope.Dispose();

            if (pointsToAdd.Count > 0)
            {
                using (_timeline.Measure("邊界點加入與重生"))
                {
                    EnsurePointLimit(existingPositions.Count + pointsToAdd.Count);
                    editor.AddPoints(pointsToAdd);
                    doc.Regenerate();
                }
            }

            // 邊界摺線：以分割線把樓板邊界刻成網格稜線，邊界內外的三角形都止於邊界，
            // 杜絕外部高處地形的三角形跨越樓板上空。
            using (_timeline.Measure("邊界摺線"))
            {
                DrawBoundarySplitLines(editor, footprints, xyTolerance);
                doc.Regenerate();
            }

            // 銜接帶：投影外一圈依模式外推 daylight 圈點並刻摺線，讓銜接帶成為網格硬邊。
            IReadOnlyList<IReadOnlyList<XYZ>> transitionRings = null;
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                using (_timeline.Measure("銜接帶外圈取樣"))
                {
                    transitionRings = BuildTransitionRings(
                        solids, footprints, settings, xyTolerance, elevationTolerance,
                        rayBottomZ, rayTopZ, warnings);
                }

                var positionsAfterBoundary = CollectVertexPositions(editor);
                var ringPoints = new List<XYZ>();
                foreach (var candidate in transitionRings.SelectMany(ring => ring))
                {
                    if (HasNearbyXY(positionsAfterBoundary, candidate, xyTolerance)
                        || HasNearbyXY(ringPoints, candidate, xyTolerance))
                    {
                        continue;
                    }

                    ringPoints.Add(candidate);
                }

                if (ringPoints.Count > 0)
                {
                    using (_timeline.Measure("銜接帶外圈加入與重生"))
                    {
                        EnsurePointLimit(positionsAfterBoundary.Count + ringPoints.Count);
                        editor.AddPoints(ringPoints);
                        doc.Regenerate();
                    }
                }

                using (_timeline.Measure("銜接帶摺線"))
                {
                    DrawRingSplitLines(editor, transitionRings, xyTolerance);
                    doc.Regenerate();
                }
            }

            // 分類全部頂點：XY 位於樓板投影內或邊界上者，目標高程為該處樓板底面。
            var targets = new List<VertexTarget>();
            var classifyScope = _timeline.Measure("頂點分類");
            var allPositions = CollectVertexPositions(editor);
            var assignedIndices = new HashSet<int>();
            for (var positionIndex = 0; positionIndex < allPositions.Count; positionIndex++)
            {
                var position = allPositions[positionIndex];
                double? targetZ = null;
                foreach (var footprint in footprints)
                {
                    if (!Polygon2D.Contains(footprint.OuterLoop, ToPoint2D(position), xyTolerance))
                    {
                        continue;
                    }

                    var bottomZ = footprint.BottomElevationAt(position.X, position.Y);
                    if (targetZ.HasValue && Math.Abs(targetZ.Value - bottomZ) > elevationTolerance)
                    {
                        throw new InvalidOperationException("樓板投影重疊且目標高程衝突，已中止整地。");
                    }

                    targetZ = targetZ ?? bottomZ;
                }

                if (targetZ.HasValue)
                {
                    targets.Add(new VertexTarget(position, targetZ.Value));
                    assignedIndices.Add(positionIndex);
                }
            }

            classifyScope.Dispose();

            // 銜接帶頂點分類：投影外、距最近樓板邊界在帶寬內者，依距離場計算銜接目標高程。
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                var bandCandidateWidth = ToInternalMeters(
                    settings.Mode == GradingMode.OffsetTransition
                        ? settings.OffsetDistanceMeters
                        : settings.MaxExtensionMeters);
                var offsetWidthFeet = settings.Mode == GradingMode.OffsetTransition
                    ? ToInternalMeters(settings.OffsetDistanceMeters)
                    : 0.0;
                var bandScope = _timeline.Measure("銜接帶頂點分類");
                for (var positionIndex = 0; positionIndex < allPositions.Count; positionIndex++)
                {
                    if (assignedIndices.Contains(positionIndex))
                    {
                        continue;
                    }

                    var position = allPositions[positionIndex];
                    var sample = ToPoint2D(position);
                    FloorFootprint nearestFootprint = null;
                    var nearestDistance = double.MaxValue;
                    var nearestBoundary = default(Point2D);
                    foreach (var footprint in footprints)
                    {
                        var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(footprint.OuterLoop, sample);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestFootprint = footprint;
                            nearestBoundary = boundaryPoint;
                        }
                    }

                    if (nearestFootprint == null || nearestDistance > bandCandidateWidth + xyTolerance)
                    {
                        continue;
                    }

                    var boundaryBottomZ = nearestFootprint.BottomElevationAt(nearestBoundary.X, nearestBoundary.Y);
                    var localTerrainZ = position.Z;
                    var targetZ = settings.Mode == GradingMode.OffsetTransition
                        ? TransitionGeometry.OffsetTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance, offsetWidthFeet)
                        : TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance, settings.RunPerRise);
                    if (Math.Abs(targetZ - localTerrainZ) <= elevationTolerance)
                    {
                        continue;
                    }

                    targets.Add(new VertexTarget(position, targetZ));
                }

                bandScope.Dispose();
            }

            if (targets.Count == 0)
            {
                throw new InvalidOperationException("樓板投影範圍內沒有可修改的地形控制點。");
            }

            // 兩段式校準：先寫入 offset 0 讀回基準 Z，再寫入目標差值。
            // 無論 ModifySubElement 的 offset 是絕對或增量語意，最終高程都收斂到目標值，
            // 不猜測參考平面高程；偏差由下方 2 mm 驗收閘門把關。
            using (_timeline.Measure("校準寫入第一輪"))
            {
                ApplyVertexOffsets(doc, editor, targets.Select(target => (target.Position, 0.0)).ToList());
            }

            double[] baselineZ;
            using (_timeline.Measure("基準高程讀取"))
            {
                baselineZ = ReadVertexElevations(editor, targets);
            }

            using (_timeline.Measure("校準寫入第二輪"))
            {
                ApplyVertexOffsets(
                    doc,
                    editor,
                    targets.Select((target, index) => (target.Position, target.TargetZ - baselineZ[index])).ToList());
            }

            using (_timeline.Measure("驗收檢查"))
            {
                var finalZ = ReadVertexElevations(editor, targets);
                for (var index = 0; index < targets.Count; index++)
                {
                    if (Math.Abs(finalZ[index] - targets[index].TargetZ) > elevationTolerance)
                    {
                        throw new InvalidOperationException(AbsoluteFinishUnavailableMessage);
                    }
                }
            }

            // 表面抽樣驗收：控制點正確不代表點間表面正確，
            // 以網格射線抽樣投影內實際表面，偏離板底超過 2 mm 即回滾。
            using (_timeline.Measure("表面抽樣驗收"))
            {
                VerifySurfaceAgainstFootprints(
                    design,
                    footprints,
                    elevationTolerance,
                    xyTolerance,
                    rayBottomZ,
                    rayTopZ);
            }

            if (settings.Mode != GradingMode.FootprintOnly)
            {
                using (_timeline.Measure("銜接帶包絡驗收"))
                {
                    VerifyTransitionBand(
                        original, design, footprints, settings,
                        elevationTolerance, xyTolerance, rayBottomZ, rayTopZ);
                }
            }

            // 方案指標：最大挖深/填高取自控制點原高程與目標高程差；
            // 擾動面積 = 樓板投影面積（精確）＋銜接帶外圈面積（近似，圈點缺段時取樣不全）。
            var disturbedArea = footprints.Sum(fp => Polygon2D.Area(fp.OuterLoop));
            var disturbedAreaIsApproximate = false;
            if (settings.Mode != GradingMode.FootprintOnly && transitionRings != null)
            {
                disturbedAreaIsApproximate = true;
                for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
                {
                    var ring = transitionRings[footprintIndex];
                    if (ring.Count < 3)
                    {
                        continue;
                    }

                    var ringLoop = ring.Select(ToPoint2D).ToList();
                    disturbedArea += Math.Max(
                        Polygon2D.Area(ringLoop) - Polygon2D.Area(footprints[footprintIndex].OuterLoop),
                        0);
                }
            }

            return new GradingOutcome
            {
                ModifiedPointCount = targets.Count,
                MaxCutDepthFeet = SchemeMetrics.MaxCutDepth(
                    targets.Select(target => (target.Position.Z, target.TargetZ))),
                MaxFillHeightFeet = SchemeMetrics.MaxFillHeight(
                    targets.Select(target => (target.Position.Z, target.TargetZ))),
                DisturbedAreaSquareFeet = disturbedArea,
                DisturbedAreaIsApproximate = disturbedAreaIsApproximate
            };
        }

        private readonly struct VertexTarget
        {
            public VertexTarget(XYZ position, double targetZ)
            {
                Position = position;
                TargetZ = targetZ;
            }

            public XYZ Position { get; }
            public double TargetZ { get; }
        }

        // 頂點在 AddPoints 或重新生成後可能有極小的 XY 位移，比對採 10 mm 容差。
        private static double VertexMatchTolerance =>
            UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);

        private static void EnsureNoConflictingOverlaps(
            IReadOnlyList<FloorFootprint> footprints,
            double xyTolerance,
            double elevationTolerance)
        {
            for (var firstIndex = 0; firstIndex < footprints.Count; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < footprints.Count; secondIndex++)
                {
                    var first = footprints[firstIndex];
                    var second = footprints[secondIndex];
                    if (!Polygon2D.Overlaps(first.OuterLoop, second.OuterLoop, xyTolerance))
                    {
                        continue;
                    }

                    if (HasConflictingElevation(first, second, xyTolerance, elevationTolerance)
                        || HasConflictingElevation(second, first, xyTolerance, elevationTolerance))
                    {
                        throw new InvalidOperationException(
                            $"樓板 ID {first.FloorId} 與 {second.FloorId} 的投影重疊且底面高程不同，請先處理衝突。");
                    }
                }
            }
        }

        private static bool HasConflictingElevation(
            FloorFootprint sampled,
            FloorFootprint other,
            double xyTolerance,
            double elevationTolerance)
        {
            foreach (var point in sampled.OuterLoop)
            {
                if (!Polygon2D.Contains(other.OuterLoop, point, xyTolerance))
                {
                    continue;
                }

                var sampledZ = sampled.BottomElevationAt(point.X, point.Y);
                var otherZ = other.BottomElevationAt(point.X, point.Y);
                if (Math.Abs(sampledZ - otherZ) > elevationTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<Solid> CollectSolids(Toposolid toposolid)
        {
            var solids = new List<Solid>();
            var geometry = toposolid.get_Geometry(new Options());
            if (geometry != null)
            {
                foreach (var geometryObject in geometry)
                {
                    if (geometryObject is Solid solid && solid.Volume > 0)
                    {
                        solids.Add(solid);
                    }
                }
            }

            return solids;
        }

        private static double? IntersectTerrainTopZ(
            IReadOnlyList<Solid> solids,
            Point2D point,
            double rayBottomZ,
            double rayTopZ)
        {
            var ray = Line.CreateBound(
                new XYZ(point.X, point.Y, rayBottomZ),
                new XYZ(point.X, point.Y, rayTopZ));
            var options = new SolidCurveIntersectionOptions
            {
                ResultType = SolidCurveIntersectionMode.CurveSegmentsInside
            };

            double? topZ = null;
            foreach (var solid in solids)
            {
                var intersection = solid.IntersectWithCurve(ray, options);
                if (intersection == null)
                {
                    continue;
                }

                for (var index = 0; index < intersection.SegmentCount; index++)
                {
                    var segment = intersection.GetCurveSegment(index);
                    var segmentTopZ = Math.Max(segment.GetEndPoint(0).Z, segment.GetEndPoint(1).Z);
                    topZ = topZ.HasValue ? Math.Max(topZ.Value, segmentTopZ) : segmentTopZ;
                }
            }

            return topZ;
        }

        private static List<XYZ> CollectVertexPositions(SlabShapeEditor editor)
        {
            var positions = new List<XYZ>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                if (vertex != null && vertex.IsValidObject)
                {
                    positions.Add(vertex.Position);
                }
            }

            return positions;
        }

        private static bool HasNearbyXY(IReadOnlyList<XYZ> points, XYZ candidate, double tolerance)
        {
            var toleranceSquared = tolerance * tolerance;
            foreach (var point in points)
            {
                if (XYDistanceSquared(point, candidate) <= toleranceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static double XYDistanceSquared(XYZ first, XYZ second)
        {
            var deltaX = second.X - first.X;
            var deltaY = second.Y - first.Y;
            return deltaX * deltaX + deltaY * deltaY;
        }

        private static void ApplyVertexOffsets(
            Document doc,
            SlabShapeEditor editor,
            IReadOnlyList<(XYZ Position, double Offset)> entries)
        {
            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                if (vertex != null && vertex.IsValidObject)
                {
                    vertices.Add(vertex);
                }
            }

            foreach (var entry in entries)
            {
                SlabShapeVertex nearest = null;
                var nearestDistanceSquared = double.MaxValue;
                foreach (var vertex in vertices)
                {
                    var distanceSquared = XYDistanceSquared(vertex.Position, entry.Position);
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                        nearest = vertex;
                    }
                }

                var matchTolerance = VertexMatchTolerance;
                if (nearest == null || nearestDistanceSquared > matchTolerance * matchTolerance)
                {
                    throw new InvalidOperationException(AbsoluteFinishUnavailableMessage);
                }

                editor.ModifySubElement(nearest, entry.Offset);
            }

            doc.Regenerate();
        }

        private static double[] ReadVertexElevations(
            SlabShapeEditor editor,
            IReadOnlyList<VertexTarget> targets)
        {
            var positions = CollectVertexPositions(editor);
            var matchTolerance = VertexMatchTolerance;
            var matchToleranceSquared = matchTolerance * matchTolerance;
            var elevations = new double[targets.Count];
            for (var index = 0; index < targets.Count; index++)
            {
                XYZ nearest = null;
                var nearestDistanceSquared = double.MaxValue;
                foreach (var position in positions)
                {
                    var distanceSquared = XYDistanceSquared(position, targets[index].Position);
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                        nearest = position;
                    }
                }

                if (nearest == null || nearestDistanceSquared > matchToleranceSquared)
                {
                    throw new InvalidOperationException(AbsoluteFinishUnavailableMessage);
                }

                elevations[index] = nearest.Z;
            }

            return elevations;
        }

        private static void DrawBoundarySplitLines(
            SlabShapeEditor editor,
            IReadOnlyList<FloorFootprint> footprints,
            double xyTolerance)
        {
            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                if (vertex != null && vertex.IsValidObject)
                {
                    vertices.Add(vertex);
                }
            }

            var matchTolerance = VertexMatchTolerance;
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                for (var index = 0; index < loop.Count; index++)
                {
                    var startVertex = FindNearestVertex(vertices, loop[index], matchTolerance);
                    var endVertex = FindNearestVertex(vertices, loop[(index + 1) % loop.Count], matchTolerance);
                    if (startVertex == null || endVertex == null)
                    {
                        // 邊界點可能因超出地形未建立（規則 7），跳過該段；完整性由表面抽樣驗收把關。
                        continue;
                    }

                    if (XYDistanceSquared(startVertex.Position, endVertex.Position) <= xyTolerance * xyTolerance)
                    {
                        continue;
                    }

                    try
                    {
                        editor.DrawSplitLine(startVertex, endVertex);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 既有摺線或退化線段會被 Revit 拒絕；缺段風險由表面抽樣驗收把關。
                        continue;
                    }
                }
            }
        }

        private static SlabShapeVertex FindNearestVertex(
            IReadOnlyList<SlabShapeVertex> vertices,
            Point2D point,
            double matchTolerance)
        {
            SlabShapeVertex nearest = null;
            var nearestDistanceSquared = double.MaxValue;
            foreach (var vertex in vertices)
            {
                var position = vertex.Position;
                var deltaX = position.X - point.X;
                var deltaY = position.Y - point.Y;
                var distanceSquared = deltaX * deltaX + deltaY * deltaY;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearest = vertex;
                }
            }

            return nearestDistanceSquared <= matchTolerance * matchTolerance ? nearest : null;
        }

        private static double ToInternalMeters(double meters)
        {
            return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        }

        private IReadOnlyList<IReadOnlyList<XYZ>> BuildTransitionRings(
            IReadOnlyList<Solid> solids,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double xyTolerance,
            double elevationTolerance,
            double rayBottomZ,
            double rayTopZ,
            ICollection<string> warnings)
        {
            var offsetFeet = settings.Mode == GradingMode.OffsetTransition
                ? ToInternalMeters(settings.OffsetDistanceMeters)
                : 0.0;
            var maxExtensionFeet = settings.Mode == GradingMode.SlopeTransition
                ? ToInternalMeters(settings.MaxExtensionMeters)
                : 0.0;
            var rings = new List<IReadOnlyList<XYZ>>(footprints.Count);
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                var directions = Polygon2D.OutwardDirections(loop);
                var ring = new List<XYZ>(loop.Count);
                var cappedCount = 0;
                var maxResidualFeet = 0.0;
                for (var index = 0; index < loop.Count; index++)
                {
                    var boundaryPoint = loop[index];
                    var direction = directions[index];
                    var boundaryBottomZ = footprint.BottomElevationAt(boundaryPoint.X, boundaryPoint.Y);
                    double extension;
                    if (settings.Mode == GradingMode.OffsetTransition)
                    {
                        extension = offsetFeet;
                    }
                    else
                    {
                        // 定點迭代：放坡與地形相交距離依地形起伏而變，迭代 8 輪足夠收斂；
                        // 未收斂不報錯——最終高程由距離場函式決定，圈點只是網格密度控制。
                        extension = maxExtensionFeet;
                        for (var iteration = 0; iteration < 8; iteration++)
                        {
                            var probe = new Point2D(
                                boundaryPoint.X + (direction.X * extension),
                                boundaryPoint.Y + (direction.Y * extension));
                            var probeTerrainZ = IntersectTerrainTopZ(solids, probe, rayBottomZ, rayTopZ);
                            if (!probeTerrainZ.HasValue)
                            {
                                break;
                            }

                            var needed = TransitionGeometry.SlopeExtensionNeeded(
                                boundaryBottomZ, probeTerrainZ.Value, settings.RunPerRise);
                            extension = Math.Min(needed, maxExtensionFeet);
                        }

                        if (extension < xyTolerance)
                        {
                            continue; // 地形已在板底高程，無帶可放。
                        }
                    }

                    var ringXY = new Point2D(
                        boundaryPoint.X + (direction.X * extension),
                        boundaryPoint.Y + (direction.Y * extension));
                    if (footprints.Any(other => Polygon2D.Contains(other.OuterLoop, ringXY, xyTolerance)))
                    {
                        continue; // 凹角或鄰板：圈點落回投影內時跳過，缺段由摺線 try-catch 與包絡驗收把關。
                    }

                    var terrainZ = IntersectTerrainTopZ(solids, ringXY, rayBottomZ, rayTopZ);
                    if (!terrainZ.HasValue)
                    {
                        continue; // 規則 7：超出地形不處理。
                    }

                    if (settings.Mode == GradingMode.SlopeTransition)
                    {
                        var targetAtRing = TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, terrainZ.Value, extension, settings.RunPerRise);
                        var residual = Math.Abs(targetAtRing - terrainZ.Value);
                        if (extension >= maxExtensionFeet - xyTolerance && residual > elevationTolerance)
                        {
                            cappedCount++;
                            maxResidualFeet = Math.Max(maxResidualFeet, residual);
                        }
                    }

                    ring.Add(new XYZ(ringXY.X, ringXY.Y, terrainZ.Value));
                }

                if (cappedCount > 0)
                {
                    var residualMeters = UnitUtils.ConvertFromInternalUnits(maxResidualFeet, UnitTypeId.Meters);
                    warnings.Add(
                        $"樓板 ID {footprint.FloorId} 放坡有 {cappedCount} 個邊界點在 maxExtension 上限截止，"
                        + $"殘留高差最大 {residualMeters:F2} m。");
                }

                rings.Add(ring);
            }

            return rings;
        }

        private static void DrawRingSplitLines(
            SlabShapeEditor editor,
            IReadOnlyList<IReadOnlyList<XYZ>> rings,
            double xyTolerance)
        {
            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                if (vertex != null && vertex.IsValidObject)
                {
                    vertices.Add(vertex);
                }
            }

            var matchTolerance = VertexMatchTolerance;
            foreach (var ring in rings)
            {
                for (var index = 0; index < ring.Count; index++)
                {
                    var startVertex = FindNearestVertex(vertices, ToPoint2D(ring[index]), matchTolerance);
                    var endVertex = FindNearestVertex(
                        vertices, ToPoint2D(ring[(index + 1) % ring.Count]), matchTolerance);
                    if (startVertex == null || endVertex == null)
                    {
                        continue;
                    }

                    if (XYDistanceSquared(startVertex.Position, endVertex.Position) <= xyTolerance * xyTolerance)
                    {
                        continue;
                    }

                    try
                    {
                        editor.DrawSplitLine(startVertex, endVertex);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue; // 既有摺線或退化線段；缺段風險由包絡驗收把關。
                    }
                }
            }
        }

        // 銜接帶粗閘門：帶內為近似銜接面（非 2 mm 工程保證），此閘門抓「跨越樓板上空／
        // 漏摺線」級錯誤；土方量以實際網格計算，數字仍精確。
        private const double TransitionBandToleranceMillimeters = 500.0;

        private static void VerifyTransitionBand(
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double elevationTolerance,
            double xyTolerance,
            double rayBottomZ,
            double rayTopZ)
        {
            var originalSolids = CollectSolids(original);
            var designSolids = CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                throw new InvalidOperationException("包絡驗收無法取得原地形或設計地形的實體幾何。");
            }

            var bandTolerance = UnitUtils.ConvertToInternalUnits(
                TransitionBandToleranceMillimeters, UnitTypeId.Millimeters);
            var boundaryMargin = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
            var sampleStep = UnitUtils.ConvertToInternalUnits(2000, UnitTypeId.Millimeters);
            var bandWidth = ToInternalMeters(settings.Mode == GradingMode.OffsetTransition
                ? settings.OffsetDistanceMeters
                : settings.MaxExtensionMeters);
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;
                foreach (var point in loop)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }

                for (var x = minX - bandWidth + (sampleStep / 2); x <= maxX + bandWidth; x += sampleStep)
                {
                    for (var y = minY - bandWidth + (sampleStep / 2); y <= maxY + bandWidth; y += sampleStep)
                    {
                        var sample = new Point2D(x, y);
                        if (footprints.Any(any => Polygon2D.Contains(any.OuterLoop, sample, xyTolerance)))
                        {
                            continue; // 投影內由既有 2 mm 抽樣把關。
                        }

                        var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(loop, sample);
                        if (distance < boundaryMargin || distance > bandWidth - boundaryMargin)
                        {
                            continue;
                        }

                        var terrainZ = IntersectTerrainTopZ(originalSolids, sample, rayBottomZ, rayTopZ);
                        var designZ = IntersectTerrainTopZ(designSolids, sample, rayBottomZ, rayTopZ);
                        if (!terrainZ.HasValue || !designZ.HasValue)
                        {
                            continue; // 規則 7：超出地形不檢查。
                        }

                        var boundaryBottomZ = footprint.BottomElevationAt(boundaryPoint.X, boundaryPoint.Y);
                        if (settings.Mode == GradingMode.SlopeTransition)
                        {
                            var targetZ = TransitionGeometry.SlopeTargetZ(
                                boundaryBottomZ, terrainZ.Value, distance, settings.RunPerRise);
                            if (Math.Abs(targetZ - terrainZ.Value) <= elevationTolerance)
                            {
                                continue; // 帶外：放坡已在此距離前貼合地形。
                            }
                        }

                        var lower = Math.Min(boundaryBottomZ, terrainZ.Value) - bandTolerance;
                        var upper = Math.Max(boundaryBottomZ, terrainZ.Value) + bandTolerance;
                        if (designZ.Value < lower || designZ.Value > upper)
                        {
                            var deviationMeters = UnitUtils.ConvertFromInternalUnits(
                                designZ.Value > upper ? designZ.Value - upper : lower - designZ.Value,
                                UnitTypeId.Meters);
                            throw new InvalidOperationException(
                                $"樓板 ID {footprint.FloorId} 銜接帶抽樣超出包絡 {deviationMeters:F2} m"
                                + "（容許 0.5 m），疑似網格跨越或摺線缺漏，已回滾整地。");
                        }
                    }
                }
            }
        }

        private static void VerifySurfaceAgainstFootprints(
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            double elevationTolerance,
            double xyTolerance,
            double rayBottomZ,
            double rayTopZ)
        {
            var solids = CollectSolids(design);
            if (solids.Count == 0)
            {
                throw new InvalidOperationException("整地後設計 Toposolid 沒有可用的實體幾何。");
            }

            var boundaryMargin = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
            var sampleStep = UnitUtils.ConvertToInternalUnits(2000, UnitTypeId.Millimeters);
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;
                foreach (var point in loop)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }

                for (var x = minX + sampleStep / 2; x <= maxX; x += sampleStep)
                {
                    for (var y = minY + sampleStep / 2; y <= maxY; y += sampleStep)
                    {
                        var sample = new Point2D(x, y);
                        if (!Polygon2D.Contains(loop, sample, xyTolerance))
                        {
                            continue;
                        }

                        if (Polygon2D.DistanceToBoundary(loop, sample) < boundaryMargin)
                        {
                            continue;
                        }

                        var surfaceZ = IntersectTerrainTopZ(solids, sample, rayBottomZ, rayTopZ);
                        if (!surfaceZ.HasValue)
                        {
                            // 超出地形的投影部分不檢查（規則 7）。
                            continue;
                        }

                        var bottomZ = footprint.BottomElevationAt(x, y);
                        if (Math.Abs(surfaceZ.Value - bottomZ) > elevationTolerance)
                        {
                            var deviationMillimeters = UnitUtils.ConvertFromInternalUnits(
                                Math.Abs(surfaceZ.Value - bottomZ),
                                UnitTypeId.Millimeters);
                            throw new InvalidOperationException(
                                $"樓板 ID {footprint.FloorId} 投影內表面抽樣偏離板底 "
                                + $"{deviationMillimeters:F1} mm，超過 2 mm 容許，已回滾整地。");
                        }
                    }
                }
            }
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
                        if (face is PlanarFace planarFace && planarFace.FaceNormal.Z < BottomFaceNormalZMaximum)
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
