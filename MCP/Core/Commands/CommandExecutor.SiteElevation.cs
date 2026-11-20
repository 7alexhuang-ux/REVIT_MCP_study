using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

// Revit 2025+ ElementId: int → long
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 場地高程基準診斷（唯讀）：座標點、Toposolid 等高線設定、Toposolid 表面點高程。
    ///
    /// 存在理由：排查「等高線標籤不是整數（例如 X.048）」時，需要比對內部原點、
    /// 專案基準點、測量點、主體樓層與等高線 Start 各自的高程基準。先前沒有任何工具
    /// 讀得到這些值——Elev 參數字串會被顯示精度捨入，Contour Display 也不在參數清單裡，
    /// 只能請使用者手動開對話框或放高程點（放高程點本身就是改模型）。
    ///
    /// 高程換算一律用測量點：測量點定義共用座標原點，共用座標對內部座標只有繞 Z 軸的旋轉，
    /// 所以 Z 方向是純位移：sharedZ = internalZ + (Survey.SharedPosition.Z - Survey.Position.Z)。
    /// </summary>
    public partial class CommandExecutor
    {
        private const double SiteMmPerFoot = 304.8;

        private static double SiteFtToMm(double feet) => Math.Round(feet * SiteMmPerFoot, 3);

        /// <summary>內部座標 Z（ft）換算成共用座標高程（ft）的位移量。</summary>
        private static double SharedZOffsetFeet(Document doc)
        {
            BasePoint survey = BasePoint.GetSurveyPoint(doc);
            return survey.SharedPosition.Z - survey.Position.Z;
        }

        private static object DescribeSitePoint(XYZ position, XYZ sharedPosition)
        {
            return new
            {
                InternalMm = new { X = SiteFtToMm(position.X), Y = SiteFtToMm(position.Y), Z = SiteFtToMm(position.Z) },
                SharedMm = new { X = SiteFtToMm(sharedPosition.X), Y = SiteFtToMm(sharedPosition.Y), Z = SiteFtToMm(sharedPosition.Z) }
            };
        }

        /// <summary>某個共用高程（mm）對指定間距（mm）的小數尾數，用來比對 X.048 這類偏差。</summary>
        private static double FractionOfStepMm(double valueMm, double stepMm)
        {
            if (stepMm <= 0) return 0;
            double r = valueMm % stepMm;
            if (r < 0) r += stepMm;
            if (stepMm - r < 0.0005) r = 0;
            return Math.Round(r, 3);
        }

        private object GetSiteCoordinates(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            double zOffset = SharedZOffsetFeet(doc);

            InternalOrigin origin = InternalOrigin.Get(doc);
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            BasePoint survey = BasePoint.GetSurveyPoint(doc);

            ProjectLocation location = doc.ActiveProjectLocation;
            ProjectPosition position = location.GetProjectPosition(XYZ.Zero);

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l =>
                {
                    var levelType = doc.GetElement(l.GetTypeId()) as LevelType;
                    Parameter baseParam = levelType?.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE);
                    return (object)new
                    {
                        ElementId = l.Id.GetIdValue(),
                        l.Name,
                        TypeName = levelType?.Name,
                        ElevationBase = baseParam?.AsValueString(),
                        InternalElevationMm = SiteFtToMm(l.Elevation),
                        ProjectElevationMm = SiteFtToMm(l.ProjectElevation),
                        SharedElevationMm = SiteFtToMm(l.Elevation + zOffset)
                    };
                })
                .ToList();

            return new
            {
                Success = true,
                DocumentTitle = doc.Title,
                Note = "InternalMm 是內部座標；SharedMm 是共用座標。SharedElevationMm = 內部 Z + 測量點的共用/內部 Z 差。數值未經顯示精度捨入。",
                SharedZOffsetMm = SiteFtToMm(zOffset),
                InternalOrigin = DescribeSitePoint(origin.Position, origin.SharedPosition),
                ProjectBasePoint = new
                {
                    Point = DescribeSitePoint(pbp.Position, pbp.SharedPosition),
                    ElevParam = pbp.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM)?.AsValueString()
                },
                SurveyPoint = new
                {
                    Point = DescribeSitePoint(survey.Position, survey.SharedPosition),
                    survey.Clipped,
                    ElevParam = survey.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM)?.AsValueString()
                },
                ActiveLocation = new
                {
                    location.Name,
                    EastWestMm = SiteFtToMm(position.EastWest),
                    NorthSouthMm = SiteFtToMm(position.NorthSouth),
                    ElevationMm = SiteFtToMm(position.Elevation),
                    AngleToTrueNorthDeg = Math.Round(position.Angle * 180.0 / Math.PI, 4)
                },
                Levels = levels
            };
        }

#if REVIT2024_OR_GREATER
        private static Toposolid ResolveToposolid(Document doc, JObject parameters)
        {
            IdType? id = parameters["toposolidId"]?.Value<IdType>();
            if (!id.HasValue)
                throw new Exception("請提供 toposolidId。");
            if (!(doc.GetElement(id.Value.ToElementId()) is Toposolid topo))
                throw new Exception($"找不到 Toposolid：{id.Value}（作用中文件：{doc.Title}）。");
            return topo;
        }

        private object GetToposolidContourSettings(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            double zOffset = SharedZOffsetFeet(doc);

            IEnumerable<Toposolid> targets = parameters["toposolidId"] != null
                ? new[] { ResolveToposolid(doc, parameters) }
                : new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).Cast<Toposolid>();

            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            BasePoint survey = BasePoint.GetSurveyPoint(doc);

            // 等高線 Start 相對哪個基準 Revit 沒有在 API 公開，這裡把每個候選基準都算一次，
            // 讓呼叫者拿畫面上的標籤尾數（例如 .048）去比對哪一個對得上。
            var references = new List<Tuple<string, double>>
            {
                Tuple.Create("InternalOrigin", zOffset),
                Tuple.Create("ProjectBasePoint", pbp.Position.Z + zOffset),
                Tuple.Create("SurveyPoint", survey.Position.Z + zOffset)
            };

            var results = new List<object>();
            foreach (Toposolid topo in targets)
            {
                var type = doc.GetElement(topo.GetTypeId()) as ToposolidType;
                var level = doc.GetElement(topo.LevelId) as Level;
                double heightOffset = topo.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;

                var candidateRefs = new List<Tuple<string, double>>(references);
                if (level != null)
                {
                    candidateRefs.Add(Tuple.Create("HostLevel", level.Elevation + zOffset));
                    candidateRefs.Add(Tuple.Create("HostLevelPlusOffset", level.Elevation + heightOffset + zOffset));
                }

                var items = new List<object>();
                ContourSetting setting = type?.GetContourSetting();
                if (setting != null)
                {
                    foreach (ContourSettingItem item in setting.GetContourSettingItems())
                    {
                        double startMm = SiteFtToMm(item.Start);
                        double stepMm = SiteFtToMm(item.Step);
                        items.Add(new
                        {
                            Index = setting.GetItemIndex(item),
                            StartMm = startMm,
                            StopMm = SiteFtToMm(item.Stop),
                            StepMm = stepMm,
                            SubCategory = doc.GetElement(item.SubCategoryId)?.Name
                                ?? (Category.GetCategory(doc, item.SubCategoryId)?.Name),
                            ContourFractionIfRelativeTo = candidateRefs.Select(r => new
                            {
                                Reference = r.Item1,
                                ReferenceSharedElevationMm = SiteFtToMm(r.Item2),
                                FirstContourSharedMm = Math.Round(SiteFtToMm(r.Item2) + startMm, 3),
                                FractionOfStepMm = FractionOfStepMm(SiteFtToMm(r.Item2) + startMm, stepMm)
                            }).ToList()
                        });
                    }
                }

                results.Add(new
                {
                    ElementId = topo.Id.GetIdValue(),
                    topo.Name,
                    TypeId = type?.Id.GetIdValue(),
                    TypeName = type?.Name,
                    IsSubDivision = topo.HostTopoId != ElementId.InvalidElementId,
                    HostLevel = level == null ? null : new
                    {
                        ElementId = level.Id.GetIdValue(),
                        level.Name,
                        InternalElevationMm = SiteFtToMm(level.Elevation),
                        SharedElevationMm = SiteFtToMm(level.Elevation + zOffset)
                    },
                    HeightOffsetFromLevelMm = SiteFtToMm(heightOffset),
                    ContourItems = items
                });
            }

            return new
            {
                Success = true,
                DocumentTitle = doc.Title,
                Note = "ContourFractionIfRelativeTo 列出：若等高線從該基準起算，第一條線的共用高程與它對間距的尾數(mm)。拿畫面標籤的尾數（例如 48 mm）比對，即可看出 Revit 實際用哪個基準。",
                Count = results.Count,
                Toposolids = results
            };
        }

        private object GetToposolidPoints(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            Toposolid topo = ResolveToposolid(doc, parameters);
            int maxCount = parameters["maxCount"]?.Value<int>() ?? 30;
            double stepMm = parameters["stepMm"]?.Value<double>() ?? 1000.0;
            double zOffset = SharedZOffsetFeet(doc);

            SlabShapeEditor editor = topo.GetSlabShapeEditor();
            if (editor == null)
                throw new Exception($"Toposolid {topo.Id.GetIdValue()} 沒有可讀的形狀點（可能是 Sub-Division）。");

            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex v in editor.SlabShapeVertices) vertices.Add(v);

            var shared = vertices.Select(v => SiteFtToMm(v.Position.Z + zOffset)).ToList();
            var fractionHistogram = shared
                .GroupBy(z => FractionOfStepMm(z, stepMm))
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new { FractionOfStepMm = g.Key, Count = g.Count() })
                .ToList();

            // 均勻抽樣，避免只看到同一區塊
            int stride = Math.Max(1, vertices.Count / Math.Max(1, maxCount));
            var samples = vertices
                .Where((v, i) => i % stride == 0)
                .Take(maxCount)
                .Select(v => new
                {
                    InternalMm = new { X = SiteFtToMm(v.Position.X), Y = SiteFtToMm(v.Position.Y), Z = SiteFtToMm(v.Position.Z) },
                    SharedElevationMm = SiteFtToMm(v.Position.Z + zOffset),
                    VertexType = v.VertexType.ToString()
                })
                .ToList();

            return new
            {
                Success = true,
                DocumentTitle = doc.Title,
                ElementId = topo.Id.GetIdValue(),
                topo.Name,
                VertexCount = vertices.Count,
                SharedElevationMinMm = shared.Count > 0 ? shared.Min() : (double?)null,
                SharedElevationMaxMm = shared.Count > 0 ? shared.Max() : (double?)null,
                StepMm = stepMm,
                Note = "FractionHistogram 統計所有形狀點共用高程對 stepMm 的尾數。測量點多為整數或固定小數位；若大多數點都帶同一個尾數，代表整個地形面有系統性位移。",
                FractionHistogram = fractionHistogram,
                Samples = samples
            };
        }
#endif
    }
}
