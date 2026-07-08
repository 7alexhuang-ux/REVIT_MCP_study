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

        // 銜接帶控制環密度：直邊最大弦長 1 m、凸角圓弧最大角步距 15°（貼合距離場坡腳、消除三角網過衝）。
        private static readonly double RingMaxChordFeet =
            UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
        private static readonly double RingArcMaxStepRadians = Math.PI / 12.0;
        // 銜接帶徑向填充步距：於放坡帶寬度中間每 1.5 m 補一圈控制點，砍三角網徑向跨距（陡坡地包絡殘留）。
        private static readonly double RadialFillStepFeet =
            UnitUtils.ConvertToInternalUnits(1500.0, UnitTypeId.Millimeters);

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

                var footprint = ExtractBottomFootprint(floor);
                // V3-S2：板上的邊坡率政策一併帶出，放坡時逐邊套用。
                footprint.EdgePolicies = FloorEdgePolicyStore.ToGeometryPolicies(
                    FloorEdgePolicyStore.Read(floor));
                footprints.Add(footprint);
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

            // 複製 Toposolid 時 Revit 會連帶複製其子分割（Sub-Division），且複製品不在 CopyElement
            // 回傳清單中（隱式相依複製）；留在設計副本上會每放坡一次多一份、污染模型與整地檢視，
            // 故取得副本後一律刪除。原地形自身的子分割不受影響。
            var copiedSubDivisionIds = design.GetSubDivisionIds();
            if (copiedSubDivisionIds != null && copiedSubDivisionIds.Count > 0)
            {
                doc.Delete(copiedSubDivisionIds);
            }

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

        /// <summary>讀取指定設計地形上的方案記錄 JSON；無記錄回 null。</summary>
        public static string TryReadSchemeRecord(Document doc, long designToposolidId)
        {
            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            var schema = Schema.Lookup(SchemeSchemaGuid);
            if (schema == null)
            {
                return null;
            }

            var element = doc.GetElement(new ElementId(CheckedElementId(designToposolidId)));
            if (!(element is Toposolid design))
            {
                return null;
            }

            var entity = design.GetEntity(schema);
            if (entity == null || !entity.IsValid())
            {
                return null;
            }

            var json = entity.Get<string>("SchemeJson");
            return string.IsNullOrWhiteSpace(json) ? null : json;
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

            // 連續放坡（使用者裁決 2026-07-08：壓蓋板緣不要高差）：放坡帶內「會被覆蓋的外露非控制樓板」
            // 自動納入控制樓板——地形沿板底通過、於板緣接續放坡；投影重疊衝突或底面抽取失敗者
            // 退回帶頂點壓蓋保護（見銜接帶頂點分類）。
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                using (_timeline.Measure("鄰接樓板自動納入"))
                {
                    footprints = AutoIncludeCoveredFloors(
                        doc, design, footprints, settings, xyTolerance, elevationTolerance, warnings);
                }
            }

            // V3-S2 逐邊坡率：每片板的政策匹配到當前輪廓 → 邊界坡率場（無政策＝null，行為與單一坡率一致）。
            var rateFields = BuildBoundaryRateFields(footprints, settings, warnings);

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
                // 邊界點加入與邊界摺線已各觸發一次 doc.Regenerate()，line 429 擷取的 solids
                // 快照對應的實體幾何在重生後已失效；footprint 模式在重生前就用完 solids（line 453），
                // 故不受影響，但銜接帶取樣在重生後才使用，必須重新擷取設計副本的當前幾何。
                // 此時尚未校準頂點（投影外仍為原地形高程），重新取樣到的地形 Z 與原設計一致。
                IReadOnlyList<Solid> transitionSolids;
                using (_timeline.Measure("銜接帶幾何重取"))
                {
                    transitionSolids = CollectSolids(design);
                }

                if (transitionSolids.Count == 0)
                {
                    throw new InvalidOperationException("重生後設計 Toposolid 沒有可用的實體幾何，無法建立銜接帶。");
                }

                IReadOnlyList<XYZ> bandFill;
                IReadOnlyList<IReadOnlyCollection<int>> transitionRingBreaks;
                using (_timeline.Measure("銜接帶外圈取樣"))
                {
                    var built = BuildTransitionRings(
                        transitionSolids, footprints, settings, rateFields, xyTolerance, elevationTolerance,
                        rayBottomZ, rayTopZ, warnings);
                    transitionRings = built.Rings;
                    transitionRingBreaks = built.RingBreaks;
                    bandFill = built.BandFill;
                }

                var positionsAfterBoundary = CollectVertexPositions(editor);
                var ringPoints = new List<XYZ>();
                foreach (var candidate in transitionRings.SelectMany(ring => ring).Concat(bandFill))
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
                    DrawRingSplitLines(editor, transitionRings, transitionRingBreaks, xyTolerance);
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
                // 不覆蓋政策（使用者裁決 2026-07-08）：放坡帶可能觸及的非控制樓板，
                // 帶頂點落在其投影內時目標壓至板底以下（原本就埋在原地形下者不動）。
                var capFloorFootprints = CollectCapFloorFootprints(doc, footprints, bandCandidateWidth);
                var capTolerance = UnitUtils.ConvertToInternalUnits(
                    BurialToleranceMillimeters, UnitTypeId.Millimeters);
                var cappedFloorIds = new SortedSet<long>();
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
                    var nearestFootprintIndex = -1;
                    var nearestDistance = double.MaxValue;
                    var nearestBoundary = default(Point2D);
                    for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
                    {
                        var footprint = footprints[footprintIndex];
                        var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(footprint.OuterLoop, sample);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestFootprint = footprint;
                            nearestFootprintIndex = footprintIndex;
                            nearestBoundary = boundaryPoint;
                        }
                    }

                    if (nearestFootprint == null || nearestDistance > bandCandidateWidth + xyTolerance)
                    {
                        continue;
                    }

                    var boundaryBottomZ = nearestFootprint.BottomElevationAt(nearestBoundary.X, nearestBoundary.Y);
                    var localTerrainZ = position.Z;
                    // V3-S2：距離場坡率取「最近邊界位置」生效值（逐邊政策＋角點混坡）。
                    var fieldRate = rateFields?[nearestFootprintIndex]?.RateAt(nearestBoundary)
                        ?? settings.RunPerRise;
                    var targetZ = settings.Mode == GradingMode.OffsetTransition
                        ? TransitionGeometry.OffsetTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance, offsetWidthFeet)
                        : TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance, fieldRate);

                    // 不覆蓋政策：頂點落在外露非控制樓板投影內 → 目標壓至板底以下。
                    foreach (var capFloor in capFloorFootprints)
                    {
                        if (!Polygon2D.Contains(capFloor.OuterLoop, sample, xyTolerance))
                        {
                            continue;
                        }

                        var floorBottomZ = capFloor.BottomElevationAt(sample.X, sample.Y);
                        var capped = FloorBurialCheck.CapTargetZ(
                            targetZ, localTerrainZ, floorBottomZ, capTolerance);
                        if (capped < targetZ)
                        {
                            targetZ = capped;
                            cappedFloorIds.Add(capFloor.FloorId);
                        }
                    }

                    if (Math.Abs(targetZ - localTerrainZ) <= elevationTolerance)
                    {
                        continue;
                    }

                    targets.Add(new VertexTarget(position, targetZ));
                }

                bandScope.Dispose();

                foreach (var cappedFloorId in cappedFloorIds)
                {
                    warnings.Add(
                        $"非控制樓板 ID {cappedFloorId} 位於放坡帶內：設計地形已壓至其底面以下、不覆蓋該板；"
                        + "如需連續放坡請將該板納入 floorIds 一併整地。");
                }
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
                        original, design, footprints, settings, rateFields,
                        elevationTolerance, xyTolerance, rayBottomZ, rayTopZ);
                }
            }

            // 非控制樓板覆蓋告警：Revit 的 overlap 警告會被 FailuresPreprocessor 自動消化、
            // 不夠醒目（實機教訓 2026-07-07：坡道板未納入 floorIds 被 1:12 填坡整片蓋掉）。
            // 只告警、不回滾——蓋掉可能是刻意的（如回填既有平台）。
            using (_timeline.Measure("非控制樓板覆蓋檢查"))
            {
                WarnNonControlFloorBurial(
                    doc, original, design, footprints, settings, rayBottomZ, rayTopZ, warnings);
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

        internal static IReadOnlyList<Solid> CollectSolids(Element element)
        {
            var solids = new List<Solid>();
            var geometry = element.get_Geometry(new Options());
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

        internal static double? IntersectTerrainTopZ(
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

        /// <summary>V3-S2：逐板建邊界坡率場；僅放坡模式＋板帶政策時生效，其餘為 null（單一坡率行為不變）。</summary>
        private static IReadOnlyList<BoundaryRateField> BuildBoundaryRateFields(
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            ICollection<string> warnings)
        {
            var fields = new BoundaryRateField[footprints.Count];
            for (var index = 0; index < footprints.Count; index++)
            {
                var footprint = footprints[index];
                var policies = footprint.EdgePolicies;
                if (policies == null || policies.Count == 0)
                {
                    continue;
                }

                if (settings.Mode != GradingMode.SlopeTransition)
                {
                    warnings.Add(
                        $"樓板 ID {footprint.FloorId} 帶 {policies.Count} 條邊坡率政策，"
                        + "但僅 slope_transition（放坡）模式生效，本次已忽略。");
                    continue;
                }

                var (edgeRates, unmatched) = EdgeSlopePolicies.MatchEdgeRates(
                    footprint.OuterLoop, policies, settings.RunPerRise);
                if (unmatched.Count > 0)
                {
                    warnings.Add(
                        $"樓板 ID {footprint.FloorId} 有 {unmatched.Count} 條邊坡率政策找不到對應邊"
                        + "（板輪廓改過？），已忽略——請以「邊坡率」重新指定。");
                }

                if (edgeRates.Any(rate => Math.Abs(rate - settings.RunPerRise) > 1e-12))
                {
                    fields[index] = new BoundaryRateField(
                        footprint.OuterLoop, edgeRates, ToInternalMeters(BoundaryRateBlendMeters));
                }
            }

            return fields;
        }

        /// <summary>逐邊坡率的角點混坡長度（公尺）。</summary>
        private const double BoundaryRateBlendMeters = 2.0;

        private (IReadOnlyList<IReadOnlyList<XYZ>> Rings, IReadOnlyList<IReadOnlyCollection<int>> RingBreaks, IReadOnlyList<XYZ> BandFill) BuildTransitionRings(
            IReadOnlyList<Solid> solids,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            IReadOnlyList<BoundaryRateField> rateFields,
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
            var ringBreaks = new List<IReadOnlyCollection<int>>(footprints.Count);
            var bandFill = new List<XYZ>();
            for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
            {
                var footprint = footprints[footprintIndex];
                var rateField = rateFields?[footprintIndex];
                var loop = footprint.OuterLoop;
                // 2026-07-05：改用「直邊細分＋凸角圓弧」密錨點取代每頂點單一角平分點，
                // 讓控制環貼合距離場真實坡腳（凸角圓弧、凹角收斂），消除稀疏圈點造成的三角網過衝（offset 包絡失敗根因）。
                var anchors = Polygon2D.DensifiedBoundaryWithCornerArcs(
                    loop, RingMaxChordFeet, RingArcMaxStepRadians);
                var ring = new List<XYZ>(anchors.Count);
                // 2026-07-08 實機教訓：錨點被跳過（鄰板投影/超出地形/無帶可放）時摺線必須斷段，
                // 封圈連弦會從高處斜切樓板上空、SlabShape 沿線抬升成垂直細片鰭。
                var breakTracker = new RingBreakTracker();
                var cappedCount = 0;
                var maxResidualFeet = 0.0;
                foreach (var (boundaryPoint, direction) in anchors)
                {
                    var boundaryBottomZ = footprint.BottomElevationAt(boundaryPoint.X, boundaryPoint.Y);
                    // V3-S2：此錨點生效坡率（逐邊政策＋角點混坡）；無政策＝整圈預設坡率。
                    var anchorRate = rateField?.RateAt(boundaryPoint) ?? settings.RunPerRise;
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
                                boundaryBottomZ, probeTerrainZ.Value, anchorRate);
                            extension = Math.Min(needed, maxExtensionFeet);
                        }

                        if (extension < xyTolerance)
                        {
                            breakTracker.MarkSkipped();
                            continue; // 地形已在板底高程，無帶可放。
                        }
                    }

                    var ringXY = new Point2D(
                        boundaryPoint.X + (direction.X * extension),
                        boundaryPoint.Y + (direction.Y * extension));
                    if (footprints.Any(other => Polygon2D.Contains(other.OuterLoop, ringXY, xyTolerance)))
                    {
                        breakTracker.MarkSkipped();
                        continue; // 凹角或鄰板：圈點落回投影內時跳過，缺段由鄰板邊界摺線把關。
                    }

                    var terrainZ = IntersectTerrainTopZ(solids, ringXY, rayBottomZ, rayTopZ);
                    if (!terrainZ.HasValue)
                    {
                        breakTracker.MarkSkipped();
                        continue; // 規則 7：超出地形不處理。
                    }

                    if (settings.Mode == GradingMode.SlopeTransition)
                    {
                        var targetAtRing = TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, terrainZ.Value, extension, anchorRate);
                        var residual = Math.Abs(targetAtRing - terrainZ.Value);
                        if (extension >= maxExtensionFeet - xyTolerance && residual > elevationTolerance)
                        {
                            cappedCount++;
                            maxResidualFeet = Math.Max(maxResidualFeet, residual);
                        }
                    }

                    ring.Add(new XYZ(ringXY.X, ringXY.Y, terrainZ.Value));
                    breakTracker.MarkAdded();

                    // 徑向填充：在此錨點的放坡帶寬度中間每 RadialFillStepFeet 補控制點，
                    // 供距離場分類賦予漸變目標高程，砍除跨帶寬三角網鼓起（offset 陡坡地包絡殘留）。
                    for (var fillDistance = RadialFillStepFeet;
                        fillDistance < extension - (RadialFillStepFeet / 2.0);
                        fillDistance += RadialFillStepFeet)
                    {
                        var fillXY = new Point2D(
                            boundaryPoint.X + (direction.X * fillDistance),
                            boundaryPoint.Y + (direction.Y * fillDistance));
                        if (footprints.Any(other => Polygon2D.Contains(other.OuterLoop, fillXY, xyTolerance)))
                        {
                            continue;
                        }

                        var fillTerrainZ = IntersectTerrainTopZ(solids, fillXY, rayBottomZ, rayTopZ);
                        if (fillTerrainZ.HasValue)
                        {
                            bandFill.Add(new XYZ(fillXY.X, fillXY.Y, fillTerrainZ.Value));
                        }
                    }
                }

                if (cappedCount > 0)
                {
                    var residualMeters = UnitUtils.ConvertFromInternalUnits(maxResidualFeet, UnitTypeId.Meters);
                    warnings.Add(
                        $"樓板 ID {footprint.FloorId} 放坡有 {cappedCount} 個邊界點在 maxExtension 上限截止，"
                        + $"殘留高差最大 {residualMeters:F2} m。");
                }

                rings.Add(ring);
                ringBreaks.Add(breakTracker.Finalize());
            }

            return (rings, ringBreaks, bandFill);
        }

        private static void DrawRingSplitLines(
            SlabShapeEditor editor,
            IReadOnlyList<IReadOnlyList<XYZ>> rings,
            IReadOnlyList<IReadOnlyCollection<int>> ringBreaks,
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
            for (var ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var ring = rings[ringIndex];
                var breaks = ringBreaks != null && ringIndex < ringBreaks.Count
                    ? ringBreaks[ringIndex]
                    : null;
                for (var index = 0; index < ring.Count; index++)
                {
                    if (breaks != null && breaks.Contains(index))
                    {
                        // 斷段（錨點缺口）：不連弦，缺口段地形由鄰板邊界摺線或原地形自身把關。
                        continue;
                    }

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

        // 非控制樓板覆蓋告警：取樣步距 2 m、覆蓋門檻 50 mm（告警用途，非工程閘門）。
        private const double BurialSampleStepMillimeters = 2000.0;
        private const double BurialToleranceMillimeters = 50.0;

        /// <summary>擾動範圍外框：全部控制樓板投影包圍盒外擴帶寬。</summary>
        private static (double MinX, double MinY, double MaxX, double MaxY) ComputeDisturbedExtent(
            IReadOnlyList<FloorFootprint> footprints,
            double bandWidth)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var footprint in footprints)
            {
                foreach (var point in footprint.OuterLoop)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
            }

            return (minX - bandWidth, minY - bandWidth, maxX + bandWidth, maxY + bandWidth);
        }

        /// <summary>
        /// 連續放坡：迭代發現「會被距離場覆蓋的外露非控制樓板」並納入控制樓板清單。
        /// 每輪以當前清單重建坡率場與候選集（新納入樓板的放坡帶可能再覆蓋更外圍的樓板），
        /// 至無新增或達輪數上限為止；與既有控制板投影重疊衝突者跳過（退回帶頂點壓蓋保護）。
        /// </summary>
        private static IReadOnlyList<FloorFootprint> AutoIncludeCoveredFloors(
            Document doc,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double xyTolerance,
            double elevationTolerance,
            ICollection<string> warnings)
        {
            var terrainSolids = CollectSolids(design);
            var boundingBox = design.get_BoundingBox(null);
            if (terrainSolids.Count == 0 || boundingBox == null)
            {
                return footprints; // 拿不到幾何就不擴充；覆蓋風險由壓蓋與告警把關。
            }

            var rayBottomZ = boundingBox.Min.Z - 10.0;
            var rayTopZ = boundingBox.Max.Z + 10.0;
            var bandWidth = ToInternalMeters(settings.Mode == GradingMode.OffsetTransition
                ? settings.OffsetDistanceMeters
                : settings.MaxExtensionMeters);
            var offsetWidthFeet = settings.Mode == GradingMode.OffsetTransition
                ? ToInternalMeters(settings.OffsetDistanceMeters)
                : 0.0;
            var sampleStep = UnitUtils.ConvertToInternalUnits(
                BurialSampleStepMillimeters, UnitTypeId.Millimeters);
            var tolerance = UnitUtils.ConvertToInternalUnits(
                BurialToleranceMillimeters, UnitTypeId.Millimeters);

            var working = footprints.ToList();
            const int maxRounds = 5;
            for (var round = 0; round < maxRounds; round++)
            {
                // 政策匹配警告只在正式建場（ApplyGrading 本體）時記；發現階段用暫存清單避免重複。
                var probeWarnings = new List<string>();
                var rateFields = BuildBoundaryRateFields(working, settings, probeWarnings);
                var candidates = CollectCapFloorFootprints(doc, working, bandWidth);
                var added = false;
                foreach (var candidate in candidates)
                {
                    var samples = new List<(double FloorBottomZ, double OriginalZ, double FieldTargetZ)>();
                    var extent = ComputeDisturbedExtent(new[] { candidate }, 0.0);
                    for (var x = extent.MinX + (sampleStep / 2); x <= extent.MaxX; x += sampleStep)
                    {
                        for (var y = extent.MinY + (sampleStep / 2); y <= extent.MaxY; y += sampleStep)
                        {
                            var sample = new Point2D(x, y);
                            if (!Polygon2D.Contains(candidate.OuterLoop, sample, xyTolerance))
                            {
                                continue;
                            }

                            var originalZ = IntersectTerrainTopZ(
                                terrainSolids, sample, rayBottomZ, rayTopZ);
                            if (!originalZ.HasValue)
                            {
                                continue; // 規則 7：超出地形不處理。
                            }

                            var nearestDistance = double.MaxValue;
                            var nearestIndex = -1;
                            var nearestBoundary = default(Point2D);
                            for (var index = 0; index < working.Count; index++)
                            {
                                var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(
                                    working[index].OuterLoop, sample);
                                if (distance < nearestDistance)
                                {
                                    nearestDistance = distance;
                                    nearestIndex = index;
                                    nearestBoundary = boundaryPoint;
                                }
                            }

                            if (nearestIndex < 0 || nearestDistance > bandWidth + xyTolerance)
                            {
                                continue; // 帶外：距離場不會動到這裡。
                            }

                            var boundaryBottomZ = working[nearestIndex]
                                .BottomElevationAt(nearestBoundary.X, nearestBoundary.Y);
                            var fieldRate = rateFields?[nearestIndex]?.RateAt(nearestBoundary)
                                ?? settings.RunPerRise;
                            var fieldTarget = settings.Mode == GradingMode.OffsetTransition
                                ? TransitionGeometry.OffsetTargetZ(
                                    boundaryBottomZ, originalZ.Value, nearestDistance, offsetWidthFeet)
                                : TransitionGeometry.SlopeTargetZ(
                                    boundaryBottomZ, originalZ.Value, nearestDistance, fieldRate);
                            samples.Add((
                                candidate.BottomElevationAt(sample.X, sample.Y),
                                originalZ.Value,
                                fieldTarget));
                        }
                    }

                    if (!FloorBurialCheck.ShouldAutoInclude(samples, tolerance))
                    {
                        continue;
                    }

                    // 與既有控制板投影重疊且板底高程衝突 → 無法納入，退回壓蓋保護（共邊等高不算衝突）。
                    var trial = new List<FloorFootprint>(working) { candidate };
                    try
                    {
                        EnsureNoConflictingOverlaps(trial, xyTolerance, elevationTolerance);
                    }
                    catch (InvalidOperationException)
                    {
                        warnings.Add(
                            $"非控制樓板 ID {candidate.FloorId} 會被放坡覆蓋，但與控制樓板投影重疊且底面高程衝突，"
                            + "無法自動納入連續放坡；退回不覆蓋壓蓋保護（板緣將出現高差）。");
                        continue;
                    }

                    working.Add(candidate);
                    added = true;
                    warnings.Add(
                        $"非控制樓板 ID {candidate.FloorId} 會被放坡覆蓋，已自動納入控制樓板連續放坡"
                        + "（地形沿板底通過、於板緣接續放坡）。");
                }

                if (!added)
                {
                    break;
                }
            }

            return working;
        }

        /// <summary>
        /// 不覆蓋政策：蒐集放坡帶可能觸及的非控制樓板 footprint（帶頂點壓蓋用）。
        /// 底面抽取失敗的特殊樓板跳過——留給覆蓋告警把關。
        /// </summary>
        private static IReadOnlyList<FloorFootprint> CollectCapFloorFootprints(
            Document doc,
            IReadOnlyList<FloorFootprint> footprints,
            double bandWidth)
        {
            var extent = ComputeDisturbedExtent(footprints, bandWidth);
            var controlFloorIds = new HashSet<long>(footprints.Select(fp => fp.FloorId));
            var capFloors = new List<FloorFootprint>();
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                long floorId = floor.Id.GetIdValue();
                if (controlFloorIds.Contains(floorId))
                {
                    continue;
                }

                var bounds = floor.get_BoundingBox(null);
                if (bounds == null
                    || bounds.Max.X < extent.MinX || bounds.Min.X > extent.MaxX
                    || bounds.Max.Y < extent.MinY || bounds.Min.Y > extent.MaxY)
                {
                    continue; // 樓板不在擾動範圍內。
                }

                try
                {
                    capFloors.Add(ExtractBottomFootprint(floor));
                }
                catch (InvalidOperationException)
                {
                    // 非單一平面底面等特殊樓板：無法壓蓋，由覆蓋告警把關。
                }
            }

            return capFloors;
        }

        /// <summary>
        /// 掃描擾動範圍（控制樓板投影外擴帶寬）內的非控制樓板，
        /// 以 FloorBurialCheck 判定整地是否「新增」蓋過其頂面；有則加告警（不回滾）。
        /// </summary>
        private static void WarnNonControlFloorBurial(
            Document doc,
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double rayBottomZ,
            double rayTopZ,
            ICollection<string> warnings)
        {
            var originalSolids = CollectSolids(original);
            var designSolids = CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                return; // 告警檢查拿不到幾何就靜默跳過，不阻斷整地。
            }

            var bandWidth = settings.Mode == GradingMode.FootprintOnly
                ? 0.0
                : ToInternalMeters(settings.Mode == GradingMode.OffsetTransition
                    ? settings.OffsetDistanceMeters
                    : settings.MaxExtensionMeters);

            var extent = ComputeDisturbedExtent(footprints, bandWidth);
            var controlFloorIds = new HashSet<long>(footprints.Select(fp => fp.FloorId));
            var sampleStep = UnitUtils.ConvertToInternalUnits(
                BurialSampleStepMillimeters, UnitTypeId.Millimeters);
            var tolerance = UnitUtils.ConvertToInternalUnits(
                BurialToleranceMillimeters, UnitTypeId.Millimeters);

            var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>();
            foreach (var floor in floors)
            {
                long floorId = floor.Id.GetIdValue();
                if (controlFloorIds.Contains(floorId))
                {
                    continue;
                }

                var bounds = floor.get_BoundingBox(null);
                if (bounds == null)
                {
                    continue;
                }

                var minX = Math.Max(bounds.Min.X, extent.MinX);
                var maxX = Math.Min(bounds.Max.X, extent.MaxX);
                var minY = Math.Max(bounds.Min.Y, extent.MinY);
                var maxY = Math.Min(bounds.Max.Y, extent.MaxY);
                if (minX > maxX || minY > maxY)
                {
                    continue; // 樓板不在擾動範圍內。
                }

                var floorSolids = CollectSolids(floor);
                if (floorSolids.Count == 0)
                {
                    continue;
                }

                var samples = new List<(double FloorTopZ, double OriginalZ, double DesignZ)>();
                void AddSample(double x, double y)
                {
                    var point = new Point2D(x, y);
                    var floorTopZ = IntersectTerrainTopZ(floorSolids, point, rayBottomZ, rayTopZ);
                    if (!floorTopZ.HasValue)
                    {
                        return; // 射線沒打到樓板（重疊框的空角）。
                    }

                    var originalZ = IntersectTerrainTopZ(originalSolids, point, rayBottomZ, rayTopZ);
                    var designZ = IntersectTerrainTopZ(designSolids, point, rayBottomZ, rayTopZ);
                    if (originalZ.HasValue && designZ.HasValue)
                    {
                        samples.Add((floorTopZ.Value, originalZ.Value, designZ.Value));
                    }
                }

                for (var x = minX + (sampleStep / 2); x <= maxX; x += sampleStep)
                {
                    for (var y = minY + (sampleStep / 2); y <= maxY; y += sampleStep)
                    {
                        AddSample(x, y);
                    }
                }

                if (samples.Count == 0)
                {
                    // 細長薄板可能整片落在步距之間，至少取重疊框中心一點。
                    AddSample((minX + maxX) / 2, (minY + maxY) / 2);
                }

                var (newlyBuried, maxNewDepth) = FloorBurialCheck.Evaluate(samples, tolerance);
                if (newlyBuried)
                {
                    var depthMeters = UnitUtils.ConvertFromInternalUnits(maxNewDepth, UnitTypeId.Meters);
                    warnings.Add(
                        $"非控制樓板 ID {floorId} 整地後被設計地形覆蓋（新增埋深最大 {depthMeters:F2} m）；"
                        + "若需保留外露，請將該板納入 floorIds 一併整地，或調整坡率／邊坡率政策。");
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
            IReadOnlyList<BoundaryRateField> rateFields,
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
            for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
            {
                var footprint = footprints[footprintIndex];
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
                            // V3-S2：貼合判定用該取樣點最近邊界位置的生效坡率（逐邊政策＋角點混坡），
                            // 與放坡寫入端同一個坡率場；用預設坡率會在例外邊誤判帶內外。
                            var fieldRate = rateFields?[footprintIndex]?.RateAt(boundaryPoint)
                                ?? settings.RunPerRise;
                            var targetZ = TransitionGeometry.SlopeTargetZ(
                                boundaryBottomZ, terrainZ.Value, distance, fieldRate);
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
