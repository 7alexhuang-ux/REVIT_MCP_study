using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Models;

namespace RevitMCP.Core
{
    /// <summary>
    /// 命令執行器 - 執行各種 Revit 操作
    /// </summary>
    public class CommandExecutor
    {
        private readonly UIApplication _uiApp;

        public CommandExecutor(UIApplication uiApp)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
        }

        /// <summary>
        /// 共用方法：查找樓層
        /// </summary>
        private Level FindLevel(Document doc, string levelName, bool useFirstIfNotFound = true)
        {
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName || l.Name.Contains(levelName) || levelName.Contains(l.Name));

            if (level == null && useFirstIfNotFound)
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();
            }

            if (level == null)
            {
                throw new Exception($"找不到樓層: {levelName}");
            }

            return level;
        }

        /// <summary>
        /// 執行命令
        /// </summary>
        public RevitCommandResponse ExecuteCommand(RevitCommandRequest request)
        {
            try
            {
                var parameters = request.Parameters as JObject ?? new JObject();
                object result = null;

                switch (request.CommandName.ToLower())
                {
                    case "create_wall":
                        result = CreateWall(parameters);
                        break;
                    
                    case "get_project_info":
                        result = GetProjectInfo();
                        break;

                    
                    case "create_floor":
                        result = CreateFloor(parameters);
                        break;
                    
                    case "get_all_levels":
                        result = GetAllLevels();
                        break;
                    
                    case "get_element_info":
                        result = GetElementInfo(parameters);
                        break;
                    
                    case "delete_element":
                        result = DeleteElement(parameters);
                        break;
                    
                    case "modify_element_parameter":
                        result = ModifyElementParameter(parameters);
                        break;
                    
                    case "create_door":
                        result = CreateDoor(parameters);
                        break;
                    
                    case "create_window":
                        result = CreateWindow(parameters);
                        break;
                    
                    case "get_all_grids":
                        result = GetAllGrids();
                        break;
                    
                    case "get_column_types":
                        result = GetColumnTypes(parameters);
                        break;
                    
                    case "create_column":
                        result = CreateColumn(parameters);
                        break;
                    
                    case "get_furniture_types":
                        result = GetFurnitureTypes(parameters);
                        break;
                    
                    case "place_furniture":
                        result = PlaceFurniture(parameters);
                        break;
                    
                    case "get_room_info":
                        result = GetRoomInfo(parameters);
                        break;
                    
                    case "get_rooms_by_level":
                        result = GetRoomsByLevel(parameters);
                        break;
                    
                    case "get_all_views":
                        result = GetAllViews(parameters);
                        break;
                    
                    case "get_active_view":
                        result = GetActiveView();
                        break;
                    
                    case "set_active_view":
                        result = SetActiveView(parameters);
                        break;
                    
                    case "select_element":
                        result = SelectElement(parameters);
                        break;
                    
                    case "zoom_to_element":
                        result = ZoomToElement(parameters);
                        break;
                    
                    case "measure_distance":
                        result = MeasureDistance(parameters);
                        break;
                    
                    case "get_wall_info":
                        result = GetWallInfo(parameters);
                        break;
                    
                    case "create_dimension":
                        result = CreateDimension(parameters);
                        break;
                    
                    case "query_walls_by_location":
                        result = QueryWallsByLocation(parameters);
                        break;
                    
                                        case "query_elements":
                    
                                            result = QueryElements(parameters);
                    
                                            break;
                    
                                        case "get_active_schema":
                    
                                            result = GetActiveSchema(parameters);
                    
                                            break;
                    
                                        case "get_category_fields":
                    
                                            result = GetCategoryFields(parameters);
                    
                                            break;
                    
                                        case "get_field_values":
                    
                                            result = GetFieldValues(parameters);
                    
                                            break;
                    
                                        case "override_element_graphics":
                        result = OverrideElementGraphics(parameters);
                        break;
                    
                    case "clear_element_override":
                        result = ClearElementOverride(parameters);
                        break;
                    
                    case "unjoin_wall_joins":
                        result = UnjoinWallJoins(parameters);
                        break;
                    
                    case "rejoin_wall_joins":
                        result = RejoinWallJoins(parameters);
                        break;
                    
                    case "check_exterior_wall_openings":
                        result = CheckExteriorWallOpenings(parameters);
                        break;

                    case "get_room_daylight_info":
                        result = GetRoomDaylightInfo(parameters);
                        break;

                    case "get_view_templates":
                        result = GetViewTemplates(parameters);
                        break;

                    // 帷幕牆相關命令
                    case "get_curtain_wall_info":
                        result = GetCurtainWallInfo(parameters);
                        break;

                    case "get_curtain_panel_types":
                        result = GetCurtainPanelTypes(parameters);
                        break;

                    case "create_curtain_panel_type":
                        result = CreateCurtainPanelType(parameters);
                        break;

                    case "apply_panel_pattern":
                        result = ApplyPanelPattern(parameters);
                        break;

                    // 立面面板相關命令
                    case "create_facade_panel":
                        result = CreateFacadePanel(parameters);
                        break;

                    case "create_facade_from_analysis":
                        result = CreateFacadeFromAnalysis(parameters);
                        break;

                    case "auto_place_rooms":
                        result = AutoPlaceRooms(parameters);
                        break;

                    // 排煙窗檢討相關命令
                    case "check_smoke_exhaust_windows":
                        result = CheckSmokeExhaustWindows(parameters);
                        break;

                    case "check_floor_effective_openings":
                        result = CheckFloorEffectiveOpenings(parameters);
                        break;

                    // 視覺化工具
                    case "create_section_view":
                        result = CreateSectionView(parameters);
                        break;

                    case "create_detail_lines":
                        result = CreateDetailLines(parameters);
                        break;

                    case "create_filled_region":
                        result = CreateFilledRegion(parameters);
                        break;

                    case "create_text_note":
                        result = CreateTextNote(parameters);
                        break;

                    case "export_smoke_review_excel":
                        result = ExportSmokeReviewExcel(parameters);
                        break;

                    default:
                        throw new NotImplementedException($"未實作的命令: {request.CommandName}");
                }

                return new RevitCommandResponse
                {
                    Success = true,
                    Data = result,
                    RequestId = request.RequestId
                };
            }
            catch (Exception ex)
            {
                return new RevitCommandResponse
                {
                    Success = false,
                    Error = ex.Message,
                    RequestId = request.RequestId
                };
            }
        }

        #region 命令實作

        /// <summary>
        /// 建立牆
        /// </summary>
        private object CreateWall(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            double startX = parameters["startX"]?.Value<double>() ?? 0;
            double startY = parameters["startY"]?.Value<double>() ?? 0;
            double endX = parameters["endX"]?.Value<double>() ?? 0;
            double endY = parameters["endY"]?.Value<double>() ?? 0;
            double height = parameters["height"]?.Value<double>() ?? 3000;

            // 轉換為英尺 (Revit 內部單位)
            XYZ start = new XYZ(startX / 304.8, startY / 304.8, 0);
            XYZ end = new XYZ(endX / 304.8, endY / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "建立牆"))
            {
                trans.Start();

                // 建立線
                Line line = Line.CreateBound(start, end);

                // 取得預設樓層
                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault();

                if (level == null)
                {
                    throw new Exception("找不到樓層");
                }

                // 建立牆
                Wall wall = Wall.Create(doc, line, level.Id, false);
                
                // 設定高度
                Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null && !heightParam.IsReadOnly)
                {
                    heightParam.Set(height / 304.8);
                }

                trans.Commit();

                return new
                {
                    ElementId = wall.Id.IntegerValue,
                    Message = $"成功建立牆，ID: {wall.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得專案資訊
        /// </summary>
        private object GetProjectInfo()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            ProjectInfo projInfo = doc.ProjectInformation;

            return new
            {
                ProjectName = doc.Title,
                BuildingName = projInfo.BuildingName,
                OrganizationName = projInfo.OrganizationName,
                Author = projInfo.Author,
                Address = projInfo.Address,
                ClientName = projInfo.ClientName,
                ProjectNumber = projInfo.Number,
                ProjectStatus = projInfo.Status
            };
        }

        /// <summary>
        /// 取得所有樓層
        /// </summary>
        private object GetAllLevels()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    ElementId = l.Id.IntegerValue,
                    Name = l.Name,
                    Elevation = Math.Round(l.Elevation * 304.8, 2) // 轉換為公釐
                })
                .ToList();

            return new
            {
                Count = levels.Count,
                Levels = levels
            };
        }

        /// <summary>
        /// 取得元素資訊
        /// </summary>
        private object GetElementInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            var parameterList = new List<object>();
            foreach (Parameter param in element.Parameters)
            {
                if (param.HasValue)
                {
                    parameterList.Add(new
                    {
                        Name = param.Definition.Name,
                        Value = param.AsValueString() ?? param.AsString(),
                        Type = param.StorageType.ToString()
                    });
                }
            }

            return new
            {
                ElementId = element.Id.IntegerValue,
                Name = element.Name,
                Category = element.Category?.Name,
                Type = doc.GetElement(element.GetTypeId())?.Name,
                Level = doc.GetElement(element.LevelId)?.Name,
                Parameters = parameterList
            };
        }

        /// <summary>
        /// 刪除元素
        /// </summary>
        private object DeleteElement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;

            using (Transaction trans = new Transaction(doc, "刪除元素"))
            {
                trans.Start();

                Element element = doc.GetElement(new ElementId(elementId));
                if (element == null)
                {
                    throw new Exception($"找不到元素 ID: {elementId}");
                }

                doc.Delete(new ElementId(elementId));
                trans.Commit();

                return new
                {
                    Message = $"成功刪除元素 ID: {elementId}"
                };
            }
        }

        /// <summary>
        /// 建立樓板
        /// </summary>
        private object CreateFloor(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            
            var pointsArray = parameters["points"] as JArray;
            string levelName = parameters["levelName"]?.Value<string>() ?? "Level 1";
            
            if (pointsArray == null || pointsArray.Count < 3)
            {
                throw new Exception("需要至少 3 個點來建立樓板");
            }

            using (Transaction trans = new Transaction(doc, "建立樓板"))
            {
                trans.Start();

                // 取得樓層
                Level level = FindLevel(doc, levelName, true);

                // 建立邊界曲線
                var points = pointsArray.Select(p => new XYZ(
                    p["x"]?.Value<double>() / 304.8 ?? 0,
                    p["y"]?.Value<double>() / 304.8 ?? 0,
                    0
                )).ToList();

                // 取得預設樓板類型
                FloorType floorType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault();

                if (floorType == null)
                {
                    throw new Exception("找不到樓板類型");
                }

                // 建立 CurveLoop (Revit 2022+ 使用)
                CurveLoop curveLoop = new CurveLoop();
                for (int i = 0; i < points.Count; i++)
                {
                    XYZ start = points[i];
                    XYZ end = points[(i + 1) % points.Count];
                    curveLoop.Append(Line.CreateBound(start, end));
                }

                // 使用 Floor.Create (適用於 Revit 2022+)
                Floor floor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorType.Id, level.Id);

                trans.Commit();

                return new
                {
                    ElementId = floor.Id.IntegerValue,
                    Level = level.Name,
                    Message = $"成功建立樓板，ID: {floor.Id.IntegerValue}"
                };
            }
        }


        /// <summary>
        /// 修改元素參數
        /// </summary>
        private object ModifyElementParameter(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;
            string parameterName = parameters["parameterName"]?.Value<string>();
            string value = parameters["value"]?.Value<string>();

            if (string.IsNullOrEmpty(parameterName))
            {
                throw new Exception("請指定參數名稱");
            }

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            using (Transaction trans = new Transaction(doc, "修改參數"))
            {
                trans.Start();

                Parameter param = element.LookupParameter(parameterName);
                if (param == null)
                {
                    throw new Exception($"找不到參數: {parameterName}");
                }

                if (param.IsReadOnly)
                {
                    throw new Exception($"參數 {parameterName} 是唯讀的");
                }

                bool success = false;
                switch (param.StorageType)
                {
                    case StorageType.String:
                        success = param.Set(value);
                        break;
                    case StorageType.Double:
                        if (double.TryParse(value, out double dVal))
                            success = param.Set(dVal);
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int iVal))
                            success = param.Set(iVal);
                        break;
                    default:
                        throw new Exception($"不支援的參數類型: {param.StorageType}");
                }

                if (!success)
                {
                    throw new Exception($"設定參數失敗");
                }

                trans.Commit();

                return new
                {
                    ElementId = elementId,
                    ParameterName = parameterName,
                    NewValue = value,
                    Message = $"成功修改參數 {parameterName}"
                };
            }
        }

        /// <summary>
        /// 建立門
        /// </summary>
        private object CreateDoor(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int wallId = parameters["wallId"]?.Value<int>() ?? 0;
            double locationX = parameters["locationX"]?.Value<double>() ?? 0;
            double locationY = parameters["locationY"]?.Value<double>() ?? 0;

            Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
            if (wall == null)
            {
                throw new Exception($"找不到牆 ID: {wallId}");
            }

            using (Transaction trans = new Transaction(doc, "建立門"))
            {
                trans.Start();

                // 取得門類型
                FamilySymbol doorSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (doorSymbol == null)
                {
                    throw new Exception("找不到門類型");
                }

                if (!doorSymbol.IsActive)
                {
                    doorSymbol.Activate();
                    doc.Regenerate();
                }

                // 取得牆的樓層
                Level level = doc.GetElement(wall.LevelId) as Level;
                XYZ location = new XYZ(locationX / 304.8, locationY / 304.8, level?.Elevation ?? 0);

                FamilyInstance door = doc.Create.NewFamilyInstance(
                    location, doorSymbol, wall, level, 
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                trans.Commit();

                return new
                {
                    ElementId = door.Id.IntegerValue,
                    DoorType = doorSymbol.Name,
                    WallId = wallId,
                    Message = $"成功建立門，ID: {door.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 建立窗
        /// </summary>
        private object CreateWindow(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int wallId = parameters["wallId"]?.Value<int>() ?? 0;
            double locationX = parameters["locationX"]?.Value<double>() ?? 0;
            double locationY = parameters["locationY"]?.Value<double>() ?? 0;

            Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
            if (wall == null)
            {
                throw new Exception($"找不到牆 ID: {wallId}");
            }

            using (Transaction trans = new Transaction(doc, "建立窗"))
            {
                trans.Start();

                // 取得窗類型
                FamilySymbol windowSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (windowSymbol == null)
                {
                    throw new Exception("找不到窗類型");
                }

                if (!windowSymbol.IsActive)
                {
                    windowSymbol.Activate();
                    doc.Regenerate();
                }

                // 取得牆的樓層
                Level level = doc.GetElement(wall.LevelId) as Level;
                XYZ location = new XYZ(locationX / 304.8, locationY / 304.8, (level?.Elevation ?? 0) + 3); // 窗戶高度 3 英尺

                FamilyInstance window = doc.Create.NewFamilyInstance(
                    location, windowSymbol, wall, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                trans.Commit();

                return new
                {
                    ElementId = window.Id.IntegerValue,
                    WindowType = windowSymbol.Name,
                    WallId = wallId,
                    Message = $"成功建立窗，ID: {window.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得所有網格線
        /// </summary>
        private object GetAllGrids()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Select(g =>
                {
                    // 取得 Grid 的曲線（通常是直線）
                    Curve curve = g.Curve;
                    XYZ startPoint = curve.GetEndPoint(0);
                    XYZ endPoint = curve.GetEndPoint(1);

                    // 判斷方向（水平或垂直）
                    double dx = Math.Abs(endPoint.X - startPoint.X);
                    double dy = Math.Abs(endPoint.Y - startPoint.Y);
                    string direction = dx > dy ? "水平" : "垂直";

                    return new
                    {
                        ElementId = g.Id.IntegerValue,
                        Name = g.Name,
                        Direction = direction,
                        StartX = Math.Round(startPoint.X * 304.8, 2),  // 英尺 → 公釐
                        StartY = Math.Round(startPoint.Y * 304.8, 2),
                        EndX = Math.Round(endPoint.X * 304.8, 2),
                        EndY = Math.Round(endPoint.Y * 304.8, 2)
                    };
                })
                .OrderBy(g => g.Name)
                .ToList();

            return new
            {
                Count = grids.Count,
                Grids = grids
            };
        }

        /// <summary>
        /// 取得柱類型
        /// </summary>
        private object GetColumnTypes(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string materialFilter = parameters["material"]?.Value<string>();

            // 查詢結構柱和建築柱的 FamilySymbol
            var columnTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => fs.Category != null && 
                    (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Columns ||
                     fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralColumns))
                .Select(fs =>
                {
                    // 嘗試取得尺寸參數
                    double width = 0, depth = 0;
                    
                    // 常見的柱尺寸參數名稱
                    Parameter widthParam = fs.LookupParameter("寬度") ?? 
                                          fs.LookupParameter("Width") ?? 
                                          fs.LookupParameter("b");
                    Parameter depthParam = fs.LookupParameter("深度") ?? 
                                          fs.LookupParameter("Depth") ?? 
                                          fs.LookupParameter("h");
                    
                    if (widthParam != null && widthParam.HasValue)
                        width = Math.Round(widthParam.AsDouble() * 304.8, 0);  // 轉公釐
                    if (depthParam != null && depthParam.HasValue)
                        depth = Math.Round(depthParam.AsDouble() * 304.8, 0);

                    return new
                    {
                        ElementId = fs.Id.IntegerValue,
                        TypeName = fs.Name,
                        FamilyName = fs.FamilyName,
                        Category = fs.Category?.Name,
                        Width = width,
                        Depth = depth,
                        SizeDescription = width > 0 && depth > 0 ? $"{width}x{depth}" : "未知尺寸"
                    };
                })
                .Where(ct => string.IsNullOrEmpty(materialFilter) || 
                             ct.FamilyName.Contains(materialFilter) || 
                             ct.TypeName.Contains(materialFilter))
                .OrderBy(ct => ct.FamilyName)
                .ThenBy(ct => ct.TypeName)
                .ToList();

            return new
            {
                Count = columnTypes.Count,
                ColumnTypes = columnTypes
            };
        }

        /// <summary>
        /// 建立柱子
        /// </summary>
        private object CreateColumn(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            // 解析參數
            double x = parameters["x"]?.Value<double>() ?? 0;
            double y = parameters["y"]?.Value<double>() ?? 0;
            string bottomLevelName = parameters["bottomLevel"]?.Value<string>() ?? "Level 1";
            string topLevelName = parameters["topLevel"]?.Value<string>();
            string columnTypeName = parameters["columnType"]?.Value<string>();

            // 轉換座標（公釐 → 英尺）
            XYZ location = new XYZ(x / 304.8, y / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "建立柱子"))
            {
                trans.Start();

                // 取得底部樓層
                Level bottomLevel = FindLevel(doc, bottomLevelName, true);

                // 取得柱類型（FamilySymbol）
                FamilySymbol columnSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Category != null &&
                        (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Columns ||
                         fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralColumns))
                    .FirstOrDefault(fs => string.IsNullOrEmpty(columnTypeName) || 
                                          fs.Name == columnTypeName ||
                                          fs.FamilyName.Contains(columnTypeName));

                if (columnSymbol == null)
                {
                    throw new Exception(string.IsNullOrEmpty(columnTypeName) 
                        ? "專案中沒有可用的柱類型" 
                        : $"找不到柱類型: {columnTypeName}");
                }

                // 確保 FamilySymbol 已啟用
                if (!columnSymbol.IsActive)
                {
                    columnSymbol.Activate();
                    doc.Regenerate();
                }

                // 建立柱子
                FamilyInstance column = doc.Create.NewFamilyInstance(
                    location,
                    columnSymbol,
                    bottomLevel,
                    Autodesk.Revit.DB.Structure.StructuralType.Column
                );

                // 設定頂部樓層（如果有指定）
                if (!string.IsNullOrEmpty(topLevelName))
                {
                    Level topLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name == topLevelName);

                    if (topLevel != null)
                    {
                        Parameter topLevelParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                        if (topLevelParam != null && !topLevelParam.IsReadOnly)
                        {
                            topLevelParam.Set(topLevel.Id);
                        }
                    }
                }

                trans.Commit();

                return new
                {
                    ElementId = column.Id.IntegerValue,
                    ColumnType = columnSymbol.Name,
                    FamilyName = columnSymbol.FamilyName,
                    Level = bottomLevel.Name,
                    LocationX = x,
                    LocationY = y,
                    Message = $"成功建立柱子，ID: {column.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得家具類型
        /// </summary>
        private object GetFurnitureTypes(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string categoryFilter = parameters["category"]?.Value<string>();

            var furnitureTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Furniture)
                .Cast<FamilySymbol>()
                .Select(fs => new
                {
                    ElementId = fs.Id.IntegerValue,
                    TypeName = fs.Name,
                    FamilyName = fs.FamilyName,
                    IsActive = fs.IsActive
                })
                .Where(ft => string.IsNullOrEmpty(categoryFilter) ||
                             ft.FamilyName.Contains(categoryFilter) ||
                             ft.TypeName.Contains(categoryFilter))
                .OrderBy(ft => ft.FamilyName)
                .ThenBy(ft => ft.TypeName)
                .ToList();

            return new
            {
                Count = furnitureTypes.Count,
                FurnitureTypes = furnitureTypes
            };
        }

        /// <summary>
        /// 放置家具
        /// </summary>
        private object PlaceFurniture(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            double x = parameters["x"]?.Value<double>() ?? 0;
            double y = parameters["y"]?.Value<double>() ?? 0;
            string furnitureTypeName = parameters["furnitureType"]?.Value<string>();
            string levelName = parameters["level"]?.Value<string>() ?? "Level 1";
            double rotation = parameters["rotation"]?.Value<double>() ?? 0;

            // 轉換座標（公釐 → 英尺）
            XYZ location = new XYZ(x / 304.8, y / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "放置家具"))
            {
                trans.Start();

                // 取得樓層
                Level level = FindLevel(doc, levelName, true);

                // 取得家具類型
                FamilySymbol furnitureSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Furniture)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Name == furnitureTypeName ||
                                          fs.FamilyName.Contains(furnitureTypeName));

                if (furnitureSymbol == null)
                {
                    throw new Exception($"找不到家具類型: {furnitureTypeName}");
                }

                // 確保 FamilySymbol 已啟用
                if (!furnitureSymbol.IsActive)
                {
                    furnitureSymbol.Activate();
                    doc.Regenerate();
                }

                // 放置家具
                FamilyInstance furniture = doc.Create.NewFamilyInstance(
                    location,
                    furnitureSymbol,
                    level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                );

                // 旋轉
                if (Math.Abs(rotation) > 0.001)
                {
                    Line axis = Line.CreateBound(location, location + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, furniture.Id, axis, rotation * Math.PI / 180);
                }

                trans.Commit();

                return new
                {
                    ElementId = furniture.Id.IntegerValue,
                    FurnitureType = furnitureSymbol.Name,
                    FamilyName = furnitureSymbol.FamilyName,
                    Level = level.Name,
                    LocationX = x,
                    LocationY = y,
                    Rotation = rotation,
                    Message = $"成功放置家具，ID: {furniture.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得房間資訊
        /// </summary>
        private object GetRoomInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int? roomId = parameters["roomId"]?.Value<int>();
            string roomName = parameters["roomName"]?.Value<string>();

            Room room = null;

            if (roomId.HasValue)
            {
                room = doc.GetElement(new ElementId(roomId.Value)) as Room;
            }
            else if (!string.IsNullOrEmpty(roomName))
            {
                room = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .FirstOrDefault(r => r.Name.Contains(roomName) || 
                                         r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()?.Contains(roomName) == true);
            }

            if (room == null)
            {
                throw new Exception(roomId.HasValue 
                    ? $"找不到房間 ID: {roomId}" 
                    : $"找不到房間名稱包含: {roomName}");
            }

            // 取得房間位置點
            LocationPoint locPoint = room.Location as LocationPoint;
            XYZ center = locPoint?.Point ?? XYZ.Zero;

            // 取得 BoundingBox
            BoundingBoxXYZ bbox = room.get_BoundingBox(null);
            
            // 取得面積
            double area = room.Area * 0.092903; // 平方英尺 → 平方公尺

            return new
            {
                ElementId = room.Id.IntegerValue,
                Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                Number = room.Number,
                Level = doc.GetElement(room.LevelId)?.Name,
                Area = Math.Round(area, 2),
                CenterX = Math.Round(center.X * 304.8, 2),
                CenterY = Math.Round(center.Y * 304.8, 2),
                BoundingBox = bbox != null ? new
                {
                    MinX = Math.Round(bbox.Min.X * 304.8, 2),
                    MinY = Math.Round(bbox.Min.Y * 304.8, 2),
                    MaxX = Math.Round(bbox.Max.X * 304.8, 2),
                    MaxY = Math.Round(bbox.Max.Y * 304.8, 2)
                } : null
            };
        }

        /// <summary>
        /// 取得樓層房間清單
        /// </summary>
        private object GetRoomsByLevel(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["level"]?.Value<string>();
            bool includeUnnamed = parameters["includeUnnamed"]?.Value<bool>() ?? true;

            if (string.IsNullOrEmpty(levelName))
            {
                throw new Exception("請指定樓層名稱");
            }

            // 取得指定樓層
            Level targetLevel = FindLevel(doc, levelName, false);

            // 取得該樓層的所有房間
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == targetLevel.Id)
                .Where(r => r.Area > 0) // 排除面積為 0 的房間（未封閉）
                .Select(r => 
                {
                    string roomName = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    bool hasName = !string.IsNullOrEmpty(roomName) && roomName != "房間";
                    
                    // 取得房間中心點
                    LocationPoint locPoint = r.Location as LocationPoint;
                    XYZ center = locPoint?.Point ?? XYZ.Zero;
                    
                    // 取得面積（平方英尺 → 平方公尺）
                    double areaM2 = r.Area * 0.092903;
                    
                    return new
                    {
                        ElementId = r.Id.IntegerValue,
                        Name = roomName ?? "未命名",
                        Number = r.Number,
                        Area = Math.Round(areaM2, 2),
                        HasName = hasName,
                        CenterX = Math.Round(center.X * 304.8, 2),
                        CenterY = Math.Round(center.Y * 304.8, 2)
                    };
                })
                .Where(r => includeUnnamed || r.HasName)
                .OrderBy(r => r.Number)
                .ToList();

            // 計算統計
            double totalArea = rooms.Sum(r => r.Area);
            int roomsWithName = rooms.Count(r => r.HasName);
            int roomsWithoutName = rooms.Count(r => !r.HasName);

            return new
            {
                Level = targetLevel.Name,
                LevelId = targetLevel.Id.IntegerValue,
                TotalRooms = rooms.Count,
                TotalArea = Math.Round(totalArea, 2),
                RoomsWithName = roomsWithName,
                RoomsWithoutName = roomsWithoutName,
                DataCompleteness = rooms.Count > 0 
                    ? $"{Math.Round((double)roomsWithName / rooms.Count * 100, 1)}%" 
                    : "N/A",
                Rooms = rooms
            };
        }

        /// <summary>
        /// 取得房間採光資訊
        /// </summary>
        private object GetRoomDaylightInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["level"]?.Value<string>();

            IEnumerable<Room> rooms;
            if (!string.IsNullOrEmpty(levelName))
            {
                Level level = FindLevel(doc, levelName, false);
                rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LevelId == level.Id && r.Area > 0);
            }
            else
            {
                rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0);
            }


            // 預先取得所有 Room Tags 並建立對照表 (RoomId -> List<TagId>)
            // 預先取得所有 Room Tags 並建立對照表 (RoomId -> List<TagId>)
            // 預先取得所有 Room Tags 並建立對照表 (RoomId -> List<TagId>)
            var roomTagCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElementTag))
                .WhereElementIsNotElementType()
                .Where(e => e is RoomTag)
                .Cast<RoomTag>();

            var roomTagMap = new Dictionary<int, List<int>>();
            foreach (var tag in roomTagCollector)
            {
                // 注意：Tag 可能沒有關聯的 Room (Orphaned)
                try {
                    // Tag.Room 屬性在某些視圖可能無效，或需用 Tag.IsOrphaned
                    if (tag.Room != null) 
                    {
                        int roomId = tag.Room.Id.IntegerValue;
                        if (!roomTagMap.ContainsKey(roomId))
                        {
                            roomTagMap[roomId] = new List<int>();
                        }
                        roomTagMap[roomId].Add(tag.Id.IntegerValue);
                    }
                } catch {}
            }

            var roomData = new List<object>();
            SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions();
            var globalProcessedIds = new HashSet<int>();

            foreach (Room room in rooms)
            {
                var openings = new List<object>();

                IList<IList<BoundarySegment>> segments = room.GetBoundarySegments(options);
                if (segments != null)
                {
                    foreach (IList<BoundarySegment> segmentList in segments)
                    {
                        foreach (BoundarySegment segment in segmentList)
                        {
                            Element element = doc.GetElement(segment.ElementId);
                            if (element is Wall wall)
                            {
                                IList<ElementId> insertIds = wall.FindInserts(true, false, false, false);
                                foreach (ElementId insertId in insertIds)
                                {
                                    if (globalProcessedIds.Contains(insertId.IntegerValue)) continue;

                                    Element insert = doc.GetElement(insertId);
                                    if (insert is FamilyInstance fi &&
                                        (fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Windows))
                                    {
                                        bool belongsToRoom = false;

                                        // Geometric check: is the window within this boundary segment's range?
                                        if (wall.Location is LocationCurve wallLocCurve && insert.Location is LocationPoint insertLoc)
                                        {
                                            Curve wallCurve = wallLocCurve.Curve;
                                            Curve segmentCurve = segment.GetCurve();

                                            IntersectionResult resStart = wallCurve.Project(segmentCurve.GetEndPoint(0));
                                            IntersectionResult resEnd = wallCurve.Project(segmentCurve.GetEndPoint(1));

                                            if (resStart != null && resEnd != null)
                                            {
                                                double tMin = Math.Min(resStart.Parameter, resEnd.Parameter);
                                                double tMax = Math.Max(resStart.Parameter, resEnd.Parameter);

                                                IntersectionResult resWindow = wallCurve.Project(insertLoc.Point);
                                                if (resWindow != null)
                                                {
                                                    double tWindow = resWindow.Parameter;
                                                    // 500mm tolerance to catch windows near segment boundaries
                                                    double tol = 500.0 / 304.8;
                                                    if (tWindow >= tMin - tol && tWindow <= tMax + tol)
                                                    {
                                                        belongsToRoom = true;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                // Fallback: projection failed, use Room API
                                                if (fi.FromRoom != null && fi.FromRoom.Id == room.Id) belongsToRoom = true;
                                                else if (fi.ToRoom != null && fi.ToRoom.Id == room.Id) belongsToRoom = true;
                                            }
                                        }
                                        else
                                        {
                                            // Non-curve wall fallback
                                            if (fi.FromRoom != null && fi.FromRoom.Id == room.Id) belongsToRoom = true;
                                            else if (fi.ToRoom != null && fi.ToRoom.Id == room.Id) belongsToRoom = true;
                                        }

                                        if (!belongsToRoom) continue;
                                        globalProcessedIds.Add(insertId.IntegerValue);

                                        bool isExterior = wall.WallType.Function == WallFunction.Exterior;

                                        const double FEET_TO_MM = 304.8;
                                        Element symbol = fi.Symbol;

                                        BuiltInParameter[] widthBips = new BuiltInParameter[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH };
                                        string[] widthNames = new string[] { "粗略寬度", "寬度", "Width", "寬" };

                                        BuiltInParameter[] heightBips = new BuiltInParameter[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT };
                                        string[] heightNames = new string[] { "粗略高度", "高度", "Height", "高" };

                                        BuiltInParameter[] sillBips = new BuiltInParameter[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM };
                                        string[] sillNames = new string[] { "窗台高度", "Sill Height", "底高度", "窗臺高度" };

                                        BuiltInParameter[] headBips = new BuiltInParameter[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM };
                                        string[] headNames = new string[] { "窗頂高度", "Head Height", "頂高度" };

                                        double? wVal = GetParamValue(fi, widthBips, widthNames);
                                        if (wVal == null || wVal == 0)
                                        {
                                            wVal = GetParamValue(symbol, widthBips, widthNames);
                                        }
                                        double widthRaw = wVal ?? 0;
                                        double width = widthRaw * FEET_TO_MM;

                                        double? hVal = GetParamValue(fi, heightBips, heightNames);
                                        if (hVal == null || hVal == 0)
                                        {
                                            hVal = GetParamValue(symbol, heightBips, heightNames);
                                        }
                                        double heightRaw = hVal ?? 0;
                                        double height = heightRaw * FEET_TO_MM;

                                        double sillHeightRaw = GetParamValue(fi, sillBips, sillNames) ?? 0;
                                        double sillHeight = sillHeightRaw * FEET_TO_MM;

                                        double headHeightRaw = GetParamValue(fi, headBips, headNames) ?? (sillHeightRaw + heightRaw);
                                        double headHeight = headHeightRaw * FEET_TO_MM;

                                        openings.Add(new
                                        {
                                            Id = insert.Id.IntegerValue,
                                            Name = insert.Name,
                                            FamilyName = fi.Symbol.FamilyName,
                                            Category = insert.Category.Name,
                                            Width = Math.Round(width, 2),
                                            Height = Math.Round(height, 2),
                                            SillHeight = Math.Round(sillHeight, 2),
                                            HeadHeight = Math.Round(headHeight, 2),
                                            IsExterior = isExterior,
                                            HostWallId = wall.Id.IntegerValue
                                        });
                                    }
                                }
                            }
                        }
                    }
                }

                // 取得房間標籤 ID
                List<int> tagIds = new List<int>();
                if (roomTagMap.ContainsKey(room.Id.IntegerValue))
                {
                    tagIds = roomTagMap[room.Id.IntegerValue];
                }

                roomData.Add(new
                {
                    ElementId = room.Id.IntegerValue,
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "未命名",
                    Number = room.Number,
                    Level = doc.GetElement(room.LevelId)?.Name,
                    Area = Math.Round(room.Area * 0.092903, 2),
                    Openings = openings,
                    TagIds = tagIds
                });
            }

            return new
            {
                Count = roomData.Count,
                Rooms = roomData
            };
        }

        private double? GetParamDouble(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            if (p != null && (p.StorageType == StorageType.Double)) return p.AsDouble();
            return null;
        }

        private double? GetParamValue(Element e, BuiltInParameter[] bips, string[] names)
        {
            if (e == null) return null;
            
            foreach (BuiltInParameter bip in bips)
            {
                var val = GetParamDouble(e, bip);
                if (val.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"Found via BIP: {bip} = {val}");
                    return val;
                }
            }
            
            foreach (var name in names)
            {
                Parameter p = e.LookupParameter(name);
                if (p != null && p.StorageType == StorageType.Double)
                {
                    System.Diagnostics.Debug.WriteLine($"Found via Name: {name} = {p.AsDouble()}");
                    return p.AsDouble();
                }
            }
            
            // Fallback: iterate all parameters to find by name match
            foreach (Parameter param in e.Parameters)
            {
                if (param.StorageType != StorageType.Double) continue;
                
                string paramName = param.Definition.Name;
                foreach (var name in names)
                {
                    if (paramName == name)
                    {
                        System.Diagnostics.Debug.WriteLine($"Found via iteration: {paramName} = {param.AsDouble()}");
                        return param.AsDouble();
                    }
                }
            }
            
            return null;
        }

        /// <summary>
        /// 取得所有視圖
        /// </summary>
        private object GetAllViews(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string viewTypeFilter = parameters["viewType"]?.Value<string>();
            string levelNameFilter = parameters["levelName"]?.Value<string>();

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .Select(v =>
                {
                    string levelName = "";
                    if (v.GenLevel != null)
                    {
                        levelName = v.GenLevel.Name;
                    }

                    return new
                    {
                        ElementId = v.Id.IntegerValue,
                        Name = v.Name,
                        ViewType = v.ViewType.ToString(),
                        LevelName = levelName,
                        Scale = v.Scale
                    };
                })
                .Where(v => string.IsNullOrEmpty(viewTypeFilter) || 
                            v.ViewType.ToLower().Contains(viewTypeFilter.ToLower()))
                .Where(v => string.IsNullOrEmpty(levelNameFilter) || 
                            v.LevelName.Contains(levelNameFilter))
                .OrderBy(v => v.ViewType)
                .ThenBy(v => v.Name)
                .ToList();

            return new
            {
                Count = views.Count,
                Views = views
            };
        }

        /// <summary>
        /// 取得目前視圖
        /// </summary>
        private object GetActiveView()
        {
            View activeView = _uiApp.ActiveUIDocument.ActiveView;
            Document doc = _uiApp.ActiveUIDocument.Document;

            string levelName = "";
            if (activeView.GenLevel != null)
            {
                levelName = activeView.GenLevel.Name;
            }

            return new
            {
                ElementId = activeView.Id.IntegerValue,
                Name = activeView.Name,
                ViewType = activeView.ViewType.ToString(),
                LevelName = levelName,
                Scale = activeView.Scale
            };
        }

        /// <summary>
        /// 切換視圖
        /// </summary>
        private object SetActiveView(JObject parameters)
        {
            int viewId = parameters["viewId"]?.Value<int>() ?? 0;
            Document doc = _uiApp.ActiveUIDocument.Document;

            View view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw new Exception($"找不到視圖 ID: {viewId}");
            }

            _uiApp.ActiveUIDocument.ActiveView = view;

            return new
            {
                Success = true,
                ViewId = viewId,
                ViewName = view.Name,
                Message = $"已切換至視圖: {view.Name}"
            };
        }

        /// <summary>
        /// 選取元素
        /// </summary>
        private object SelectElement(JObject parameters)
        {
            var elementIds = new List<ElementId>();
            
            // 支援單一 ID
            if (parameters.ContainsKey("elementId"))
            {
                int id = parameters["elementId"].Value<int>();
                if (id > 0) elementIds.Add(new ElementId(id));
            }

            // 支援多個 ID
            if (parameters.ContainsKey("elementIds"))
            {
                var ids = parameters["elementIds"].Values<int>();
                foreach (var id in ids)
                {
                    if (id > 0) elementIds.Add(new ElementId(id));
                }
            }

            if (elementIds.Count == 0)
            {
                throw new Exception("未提供有效的 elementId 或 elementIds");
            }

            Document doc = _uiApp.ActiveUIDocument.Document;
            
            // 選取元素
            _uiApp.ActiveUIDocument.Selection.SetElementIds(elementIds);

            return new
            {
                Success = true,
                Count = elementIds.Count,
                Message = $"已選取 {elementIds.Count} 個元素"
            };
        }

        /// <summary>
        /// 縮放至元素
        /// </summary>
        private object ZoomToElement(JObject parameters)
        {
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;
            Document doc = _uiApp.ActiveUIDocument.Document;

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            // 顯示元素（會自動縮放）
            var elementIds = new List<ElementId> { new ElementId(elementId) };
            _uiApp.ActiveUIDocument.ShowElements(elementIds);

            return new
            {
                Success = true,
                ElementId = elementId,
                ElementName = element.Name,
                Message = $"已縮放至元素: {element.Name}"
            };
        }

        /// <summary>
        /// 測量距離
        /// </summary>
        private object MeasureDistance(JObject parameters)
        {
            double p1x = parameters["point1X"]?.Value<double>() ?? 0;
            double p1y = parameters["point1Y"]?.Value<double>() ?? 0;
            double p1z = parameters["point1Z"]?.Value<double>() ?? 0;
            double p2x = parameters["point2X"]?.Value<double>() ?? 0;
            double p2y = parameters["point2Y"]?.Value<double>() ?? 0;
            double p2z = parameters["point2Z"]?.Value<double>() ?? 0;

            // 轉換為英尺
            XYZ point1 = new XYZ(p1x / 304.8, p1y / 304.8, p1z / 304.8);
            XYZ point2 = new XYZ(p2x / 304.8, p2y / 304.8, p2z / 304.8);

            double distanceFeet = point1.DistanceTo(point2);
            double distanceMm = distanceFeet * 304.8;

            return new
            {
                Distance = Math.Round(distanceMm, 2),
                Unit = "mm",
                Point1 = new { X = p1x, Y = p1y, Z = p1z },
                Point2 = new { X = p2x, Y = p2y, Z = p2z }
            };
        }

        /// <summary>
        /// 取得牆資訊
        /// </summary>
        private object GetWallInfo(JObject parameters)
        {
            int? wallId = parameters["wallId"]?.Value<int>();
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            Wall wall = null;
            if (wallId.HasValue && wallId.Value != 0)
            {
                wall = doc.GetElement(new ElementId(wallId.Value)) as Wall;
            }
            else
            {
                var selection = uidoc.Selection.GetElementIds();
                if (selection.Count > 0)
                    wall = doc.GetElement(selection.First()) as Wall;
            }

            if (wall == null)
            {
                throw new Exception("找不到牆體，請指定 wallId 或在 Revit 中選取一面牆");
            }

            wallId = wall.Id.IntegerValue;

            // 取得牆的位置曲線
            LocationCurve locCurve = wall.Location as LocationCurve;
            Curve curve = locCurve?.Curve;

            XYZ startPoint = curve?.GetEndPoint(0) ?? XYZ.Zero;
            XYZ endPoint = curve?.GetEndPoint(1) ?? XYZ.Zero;

            // 取得牆厚度
            double thickness = wall.Width * 304.8; // 英尺 → 公釐

            // 取得牆長度
            double length = curve != null ? curve.Length * 304.8 : 0;

            // 取得牆高度
            Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            double height = heightParam != null ? heightParam.AsDouble() * 304.8 : 0;

            return new
            {
                ElementId = wallId.Value,
                Name = wall.Name,
                WallType = wall.WallType.Name,
                Thickness = Math.Round(thickness, 2),
                Length = Math.Round(length, 2),
                Height = Math.Round(height, 2),
                StartX = Math.Round(startPoint.X * 304.8, 2),
                StartY = Math.Round(startPoint.Y * 304.8, 2),
                EndX = Math.Round(endPoint.X * 304.8, 2),
                EndY = Math.Round(endPoint.Y * 304.8, 2),
                Level = doc.GetElement(wall.LevelId)?.Name
            };
        }

        /// <summary>
        /// 建立尺寸標註
        /// </summary>
        private object CreateDimension(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            
            int viewId = parameters["viewId"]?.Value<int>() ?? 0;
            double startX = parameters["startX"]?.Value<double>() ?? 0;
            double startY = parameters["startY"]?.Value<double>() ?? 0;
            double endX = parameters["endX"]?.Value<double>() ?? 0;
            double endY = parameters["endY"]?.Value<double>() ?? 0;
            double offset = parameters["offset"]?.Value<double>() ?? 500;

            View view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw new Exception($"找不到視圖 ID: {viewId}");
            }

            using (Transaction trans = new Transaction(doc, "建立尺寸標註"))
            {
                trans.Start();

                // 轉換座標
                XYZ start = new XYZ(startX / 304.8, startY / 304.8, 0);
                XYZ end = new XYZ(endX / 304.8, endY / 304.8, 0);

                // 建立參考線
                Line line = Line.CreateBound(start, end);

                // 建立尺寸標註用的參考陣列
                ReferenceArray refArray = new ReferenceArray();

                // 使用 DetailCurve 作為參考
                // 先建立兩個詳圖線作為參考點
                XYZ perpDir = new XYZ(-(end.Y - start.Y), end.X - start.X, 0).Normalize();
                double offsetFeet = offset / 304.8;

                // 偏移後的標註線位置
                XYZ dimLinePoint = start.Add(perpDir.Multiply(offsetFeet));
                Line dimLine = Line.CreateBound(
                    start.Add(perpDir.Multiply(offsetFeet)),
                    end.Add(perpDir.Multiply(offsetFeet))
                );

                // 使用 NewDetailCurve 建立參考（建立足夠長的線段）
                // 詳圖線應垂直於標註方向，作為標註的參考點
                double lineLength = 1.0; // 1 英尺 = 約 305mm

                // 使用 perpDir（垂直方向）來建立詳圖線
                DetailCurve dc1 = doc.Create.NewDetailCurve(view, Line.CreateBound(
                    start.Subtract(perpDir.Multiply(lineLength / 2)), 
                    start.Add(perpDir.Multiply(lineLength / 2))));
                DetailCurve dc2 = doc.Create.NewDetailCurve(view, Line.CreateBound(
                    end.Subtract(perpDir.Multiply(lineLength / 2)), 
                    end.Add(perpDir.Multiply(lineLength / 2))));

                refArray.Append(dc1.GeometryCurve.Reference);
                refArray.Append(dc2.GeometryCurve.Reference);

                // 建立尺寸標註
                Dimension dim = doc.Create.NewDimension(view, dimLine, refArray);

                // 注意：保留詳圖線作為標註參考點（如需刪除請手動處理）

                trans.Commit();

                double dimValue = dim.Value.HasValue ? dim.Value.Value * 304.8 : 0;

                return new
                {
                    DimensionId = dim.Id.IntegerValue,
                    Value = Math.Round(dimValue, 2),
                    Unit = "mm",
                    ViewId = viewId,
                    ViewName = view.Name,
                    Message = $"成功建立尺寸標註: {Math.Round(dimValue, 0)} mm"
                };
            }
        }

        /// <summary>
        /// 查詢指定位置附近的牆體
        /// </summary>
        private object QueryWallsByLocation(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            
            double centerX = parameters["x"]?.Value<double>() ?? 0;
            double centerY = parameters["y"]?.Value<double>() ?? 0;
            double searchRadius = parameters["searchRadius"]?.Value<double>() ?? 5000;
            string levelName = parameters["level"]?.Value<string>();

            // 轉換為英尺
            XYZ center = new XYZ(centerX / 304.8, centerY / 304.8, 0);
            double radiusFeet = searchRadius / 304.8;

            // 取得所有牆
            var wallCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>();

            // 如果指定樓層，過濾樓層
            if (!string.IsNullOrEmpty(levelName))
            {
                var level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Contains(levelName));

                if (level != null)
                {
                    wallCollector = wallCollector.Where(w => w.LevelId == level.Id);
                }
            }

            var nearbyWalls = new List<object>();

            foreach (var wall in wallCollector)
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve == null) continue;

                Curve curve = locCurve.Curve;
                XYZ startPoint = curve.GetEndPoint(0);
                XYZ endPoint = curve.GetEndPoint(1);
                
                // 計算點到線段的最近距離
                XYZ wallDir = (endPoint - startPoint).Normalize();
                XYZ toCenter = center - startPoint;
                double proj = toCenter.DotProduct(wallDir);
                double wallLength = curve.Length;
                
                XYZ closestPoint;
                if (proj < 0)
                    closestPoint = startPoint;
                else if (proj > wallLength)
                    closestPoint = endPoint;
                else
                    closestPoint = startPoint + wallDir * proj;
                
                double distToWall = center.DistanceTo(closestPoint) * 304.8;

                if (distToWall <= searchRadius)
                {
                    // 取得牆厚度
                    double thickness = wall.Width * 304.8;
                    
                    // 計算牆的方向向量（垂直於位置線）
                    XYZ perpendicular = new XYZ(-wallDir.Y, wallDir.X, 0);
                    double halfThickness = wall.Width / 2;
                    
                    // 牆的兩個面
                    XYZ face1Point = closestPoint + perpendicular * halfThickness;
                    XYZ face2Point = closestPoint - perpendicular * halfThickness;

                    nearbyWalls.Add(new
                    {
                        ElementId = wall.Id.IntegerValue,
                        Name = wall.Name,
                        WallType = wall.WallType.Name,
                        Thickness = Math.Round(thickness, 2),
                        Length = Math.Round(curve.Length * 304.8, 2),
                        DistanceToCenter = Math.Round(distToWall, 2),
                        // 位置線座標
                        LocationLine = new
                        {
                            StartX = Math.Round(startPoint.X * 304.8, 2),
                            StartY = Math.Round(startPoint.Y * 304.8, 2),
                            EndX = Math.Round(endPoint.X * 304.8, 2),
                            EndY = Math.Round(endPoint.Y * 304.8, 2)
                        },
                        // 最近點位置
                        ClosestPoint = new
                        {
                            X = Math.Round(closestPoint.X * 304.8, 2),
                            Y = Math.Round(closestPoint.Y * 304.8, 2)
                        },
                        // 兩側面座標（在最近點處）
                        Face1 = new
                        {
                            X = Math.Round(face1Point.X * 304.8, 2),
                            Y = Math.Round(face1Point.Y * 304.8, 2)
                        },
                        Face2 = new
                        {
                            X = Math.Round(face2Point.X * 304.8, 2),
                            Y = Math.Round(face2Point.Y * 304.8, 2)
                        },
                        // 判斷牆是水平還是垂直
                        Orientation = Math.Abs(wallDir.X) > Math.Abs(wallDir.Y) ? "Horizontal" : "Vertical"
                    });
                }
            }

            // 直接返回列表（已在搜尋時過濾距離）

            return new
            {
                Count = nearbyWalls.Count,
                SearchCenter = new { X = centerX, Y = centerY },
                SearchRadius = searchRadius,
                Walls = nearbyWalls
            };
        }


        /// <summary>
        /// 查詢視圖中的元素 (增強版)
        /// </summary>
        private object QueryElements(JObject parameters)
        {
            try
            {
                string categoryName = parameters["category"]?.Value<string>();
                int? viewId = parameters["viewId"]?.Value<int>();
                int maxCount = parameters["maxCount"]?.Value<int>() ?? 100;
                JArray filters = parameters["filters"] as JArray;
                JArray returnFields = parameters["returnFields"] as JArray;
                
                Document doc = _uiApp.ActiveUIDocument.Document;
                ElementId targetViewId = viewId.HasValue ? new ElementId(viewId.Value) : doc.ActiveView.Id;
                
                FilteredElementCollector collector = new FilteredElementCollector(doc, targetViewId);
                
                // 1. 品類過濾
                ElementId catId = ResolveCategoryId(doc, categoryName);
                if (catId != ElementId.InvalidElementId)
                {
                    collector.OfCategoryId(catId);
                }
                else
                {
                    // 備用方案: 根據常用名稱
                    if (categoryName.Equals("Walls", StringComparison.OrdinalIgnoreCase)) collector.OfClass(typeof(Wall));
                    else if (categoryName.Equals("Rooms", StringComparison.OrdinalIgnoreCase)) collector.OfCategory(BuiltInCategory.OST_Rooms);
                    else throw new Exception($"無法辨識品類: {categoryName}");
                }

                var elements = collector.WhereElementIsNotElementType().ToElements();
                var filteredList = new List<Element>();

                // 2. 執行過濾邏輯
                foreach (var elem in elements)
                {
                    bool match = true;
                    if (filters != null)
                    {
                        foreach (var filter in filters)
                        {
                            string field = filter["field"]?.Value<string>();
                            string op = filter["operator"]?.Value<string>();
                            string targetValue = filter["value"]?.Value<string>();
                            
                            if (!CheckFilterMatch(elem, field, op, targetValue))
                            {
                                match = false;
                                break;
                            }
                        }
                    }
                    if (match) filteredList.Add(elem);
                    if (filteredList.Count >= maxCount) break;
                }

                // 3. 準備回傳欄位
                var resultList = filteredList.Select(elem =>
                {
                    var item = new Dictionary<string, object>
                    {
                        { "ElementId", elem.Id.Value },
                        { "Name", elem.Name ?? "" }
                    };

                    if (returnFields != null)
                    {
                        foreach (var f in returnFields)
                        {
                            string fieldName = f.Value<string>();
                            if (string.IsNullOrEmpty(fieldName) || item.ContainsKey(fieldName)) continue;
                            
                            Parameter p = FindParameter(elem, fieldName);
                            if (p != null) 
                            {
                                string val = p.AsValueString() ?? p.AsString() ?? "";
                                item[fieldName] = val;
                            }
                            else
                            {
                                item[fieldName] = "N/A";
                            }
                        }
                    }
                    return item;
                }).ToList();

                return new { Success = true, Count = resultList.Count, Elements = resultList };
            }
            catch (Exception ex)
            {
                throw new Exception($"QueryElements 錯誤: {ex.Message}");
            }
        }

        private Parameter FindParameter(Element elem, string name)
        {
            // 1. 優先找實例參數
            foreach (Parameter p in elem.Parameters)
            {
                if (p.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
            }

            // 2. 找類型參數
            Element typeElem = elem.Document.GetElement(elem.GetTypeId());
            if (typeElem != null)
            {
                foreach (Parameter p in typeElem.Parameters)
                {
                    if (p.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }

            return null;
        }

        private bool CheckFilterMatch(Element elem, string field, string op, string targetValue)
        {
            Parameter p = FindParameter(elem, field);
            if (p == null) return false;

            string val = p.AsValueString() ?? p.AsString() ?? "";
            
            switch (op)
            {
                case "equals": return val.Equals(targetValue, StringComparison.OrdinalIgnoreCase);
                case "contains": return val.Contains(targetValue);
                case "not_equals": return !val.Equals(targetValue, StringComparison.OrdinalIgnoreCase);
                case "less_than":
                case "greater_than":
                    // 移除單位字串並嘗試解析
                    string cleanVal = System.Text.RegularExpressions.Regex.Replace(val, @"[^\d.-]", "");
                    if (double.TryParse(cleanVal, out double v1) && 
                        double.TryParse(targetValue, out double v2))
                    {
                        return op == "less_than" ? v1 < v2 : v1 > v2;
                    }
                    return false;
                default: return false;
            }
        }

        private ElementId ResolveCategoryId(Document doc, string name)
        {
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || 
                    cat.BuiltInCategory.ToString().Equals("OST_" + name, StringComparison.OrdinalIgnoreCase) ||
                    cat.BuiltInCategory.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return cat.Id;
            }
            return ElementId.InvalidElementId;
        }

        /// <summary>
        /// 取得視圖架構 (第一階段)
        /// </summary>
        private object GetActiveSchema(JObject parameters)
        {
            try
            {
                Document doc = _uiApp.ActiveUIDocument.Document;
                int? viewId = parameters["viewId"]?.Value<int>();
                ElementId targetViewId = viewId.HasValue ? new ElementId(viewId.Value) : doc.ActiveView.Id;

                var collector = new FilteredElementCollector(doc, targetViewId);
                var categories = collector.WhereElementIsNotElementType()
                    .Where(e => e.Category != null)
                    .GroupBy(e => e.Category.Id.Value)
                    .Select(g => {
                        ElementId catId = new ElementId(g.Key);
                        Category cat = Category.GetCategory(doc, catId);
                        return new { 
                            Name = cat?.Name ?? "未知品類",
                            InternalName = cat?.BuiltInCategory.ToString().Replace("OST_", "") ?? "Unknown",
                            Count = g.Count() 
                        };
                    })
                    .OrderByDescending(c => c.Count)
                    .ToList();

                return new { Success = true, ViewId = targetViewId.IntegerValue, Categories = categories };
            }
            catch (Exception ex)
            {
                throw new Exception($"GetActiveSchema 錯誤: {ex.Message}");
            }
        }

        /// <summary>
        /// 取得品類參數欄位 (第二階段 - A)
        /// </summary>
        private object GetCategoryFields(JObject parameters)
        {
            try
            {
                string categoryName = parameters["category"]?.Value<string>();
                Document doc = _uiApp.ActiveUIDocument.Document;
                ElementId catId = ResolveCategoryId(doc, categoryName);
                
                if (catId == ElementId.InvalidElementId)
                    throw new Exception($"找不到品類: {categoryName}");

                Element sample = new FilteredElementCollector(doc)
                    .OfCategoryId(catId)
                    .WhereElementIsNotElementType()
                    .FirstElement();
                
                if (sample == null) 
                    return new { Success = false, Message = $"專案中沒有任何 {categoryName} 元素可供分析" };

                var instanceFields = sample.GetOrderedParameters()
                    .Where(p => {
                        InternalDefinition def = p.Definition as InternalDefinition;
                        return def == null || def.Visible;
                    })
                    .Select(p => p.Definition.Name)
                    .Distinct()
                    .ToList();

                var typeFields = new List<string>();
                ElementId typeId = sample.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    Element typeElem = doc.GetElement(typeId);
                    if (typeElem != null)
                    {
                        typeFields = typeElem.GetOrderedParameters()
                            .Where(p => {
                                InternalDefinition def = p.Definition as InternalDefinition;
                                return def == null || def.Visible;
                            })
                            .Select(p => p.Definition.Name)
                            .Distinct()
                            .ToList();
                    }
                }

                return new { Success = true, Category = categoryName, InstanceFields = instanceFields, TypeFields = typeFields };
            }
            catch (Exception ex)
            {
                throw new Exception($"GetCategoryFields 錯誤: {ex.Message}");
            }
        }

        /// <summary>
        /// 取得參數值分布 (第二階段 - B)
        /// </summary>
        private object GetFieldValues(JObject parameters)
        {
            string categoryName = parameters["category"]?.Value<string>();
            string fieldName = parameters["fieldName"]?.Value<string>();
            int maxSamples = parameters["maxSamples"]?.Value<int>() ?? 500;
            
            Document doc = _uiApp.ActiveUIDocument.Document;
            ElementId catId = ResolveCategoryId(doc, categoryName);
            var elements = new FilteredElementCollector(doc).OfCategoryId(catId).WhereElementIsNotElementType().Take(maxSamples);

            var values = new HashSet<string>();
            bool isNumeric = false;
            double min = double.MaxValue;
            double max = double.MinValue;

            foreach (var elem in elements)
            {
                Parameter p = elem.LookupParameter(fieldName);
                if (p == null)
                {
                    Element typeElem = doc.GetElement(elem.GetTypeId());
                    if (typeElem != null) p = typeElem.LookupParameter(fieldName);
                }
                
                if (p != null && p.HasValue)
                {
                    string valString = p.AsValueString() ?? p.AsString();
                    if (valString != null) values.Add(valString);

                    if (p.StorageType == StorageType.Double || p.StorageType == StorageType.Integer)
                    {
                        isNumeric = true;
                        double val = (p.StorageType == StorageType.Double) ? p.AsDouble() : p.AsInteger();
                        
                        // 轉換為 mm (如果適用，Revit 2024 寫法)
                        if (p.Definition.GetDataType() == SpecTypeId.Length) val *= 304.8;
                        
                        if (val < min) min = val;
                        if (val > max) max = val;
                    }
                }
            }

            return new { 
                Success = true, 
                Category = categoryName, 
                Field = fieldName, 
                UniqueValues = values.Take(20).ToList(),
                IsNumeric = isNumeric,
                Range = isNumeric ? new { Min = Math.Round(min, 2), Max = Math.Round(max, 2) } : null
            };
        }

        /// <summary>
        /// 覆寫元素圖形顯示
        /// 支援平面圖（切割樣式）和立面圖/剖面圖（表面樣式）
        /// </summary>
        private object OverrideElementGraphics(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"].Value<int>();
            int? viewId = parameters["viewId"]?.Value<int>();

            // 取得視圖
            View view;
            if (viewId.HasValue)
            {
                view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                    throw new Exception($"找不到視圖 ID: {viewId}");
            }
            else
            {
                view = _uiApp.ActiveUIDocument.ActiveView;
            }

            // 取得元素
            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
                throw new Exception($"找不到元素 ID: {elementId}");

            // 判斷使用切割樣式或表面樣式
            // patternMode: "auto" (自動根據視圖類型), "cut" (切割), "surface" (表面)
            string patternMode = parameters["patternMode"]?.Value<string>() ?? "auto";
            
            bool useCutPattern = false;
            if (patternMode == "cut")
            {
                useCutPattern = true;
            }
            else if (patternMode == "surface")
            {
                useCutPattern = false;
            }
            else // auto
            {
                // 平面圖、天花板平面圖使用切割樣式
                // 立面圖、剖面圖、3D 視圖使用表面樣式
                useCutPattern = (view.ViewType == ViewType.FloorPlan || 
                                 view.ViewType == ViewType.CeilingPlan ||
                                 view.ViewType == ViewType.AreaPlan ||
                                 view.ViewType == ViewType.EngineeringPlan);
            }

            using (Transaction trans = new Transaction(doc, "Override Element Graphics"))
            {
                trans.Start();

                // 建立覆寫設定
                OverrideGraphicSettings overrideSettings = new OverrideGraphicSettings();

                // 取得實心填滿圖樣 ID
                ElementId solidPatternId = GetSolidFillPatternId(doc);

                // 設定填滿顏色
                if (parameters["surfaceFillColor"] != null)
                {
                    var colorObj = parameters["surfaceFillColor"];
                    byte r = (byte)colorObj["r"].Value<int>();
                    byte g = (byte)colorObj["g"].Value<int>();
                    byte b = (byte)colorObj["b"].Value<int>();
                    Color fillColor = new Color(r, g, b);

                    if (useCutPattern)
                    {
                        // 平面圖：使用切割樣式（前景）
                        overrideSettings.SetCutForegroundPatternColor(fillColor);
                        if (solidPatternId != null && solidPatternId != ElementId.InvalidElementId)
                        {
                            overrideSettings.SetCutForegroundPatternId(solidPatternId);
                            overrideSettings.SetCutForegroundPatternVisible(true);
                        }
                    }
                    else
                    {
                        // 立面圖/剖面圖：使用表面樣式
                        overrideSettings.SetSurfaceForegroundPatternColor(fillColor);
                        if (solidPatternId != null && solidPatternId != ElementId.InvalidElementId)
                        {
                            overrideSettings.SetSurfaceForegroundPatternId(solidPatternId);
                            overrideSettings.SetSurfaceForegroundPatternVisible(true);
                        }
                    }
                }

                // 設定線條顏色（可選）
                if (parameters["lineColor"] != null)
                {
                    var lineColorObj = parameters["lineColor"];
                    byte r = (byte)lineColorObj["r"].Value<int>();
                    byte g = (byte)lineColorObj["g"].Value<int>();
                    byte b = (byte)lineColorObj["b"].Value<int>();
                    Color lineColor = new Color(r, g, b);
                    
                    if (useCutPattern)
                    {
                        overrideSettings.SetCutLineColor(lineColor);
                    }
                    else
                    {
                        overrideSettings.SetProjectionLineColor(lineColor);
                    }
                }

                // 設定透明度
                int transparency = parameters["transparency"]?.Value<int>() ?? 0;
                if (transparency > 0)
                {
                    overrideSettings.SetSurfaceTransparency(transparency);
                }

                // 應用覆寫
                view.SetElementOverrides(new ElementId(elementId), overrideSettings);

                trans.Commit();

                return new
                {
                    Success = true,
                    ElementId = elementId,
                    ViewId = view.Id.IntegerValue,
                    ViewType = view.ViewType.ToString(),
                    PatternMode = useCutPattern ? "Cut" : "Surface",
                    ViewName = view.Name,
                    Message = $"已成功覆寫元素 {elementId} 在視圖 '{view.Name}' 的圖形顯示"
                };
            }
        }

        /// <summary>
        /// 清除元素圖形覆寫
        /// </summary>
        private object ClearElementOverride(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int? singleElementId = parameters["elementId"]?.Value<int>();
            var elementIdsArray = parameters["elementIds"] as JArray;
            int? viewId = parameters["viewId"]?.Value<int>();

            // 取得視圖
            View view;
            if (viewId.HasValue)
            {
                view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                    throw new Exception($"找不到視圖 ID: {viewId}");
            }
            else
            {
                view = _uiApp.ActiveUIDocument.ActiveView;
            }

            // 收集要清除的元素 ID
            List<int> elementIds = new List<int>();
            if (singleElementId.HasValue)
            {
                elementIds.Add(singleElementId.Value);
            }
            if (elementIdsArray != null)
            {
                elementIds.AddRange(elementIdsArray.Select(id => id.Value<int>()));
            }

            if (elementIds.Count == 0)
            {
                throw new Exception("請提供至少一個元素 ID");
            }

            using (Transaction trans = new Transaction(doc, "Clear Element Override"))
            {
                trans.Start();

                int successCount = 0;
                foreach (int elemId in elementIds)
                {
                    Element element = doc.GetElement(new ElementId(elemId));
                    if (element != null)
                    {
                        // 設定空的覆寫設定 = 重置為預設
                        view.SetElementOverrides(new ElementId(elemId), new OverrideGraphicSettings());
                        successCount++;
                    }
                }

                trans.Commit();

                return new
                {
                    Success = true,
                    ClearedCount = successCount,
                    ViewId = view.Id.IntegerValue,
                    ViewName = view.Name,
                    Message = $"已清除 {successCount} 個元素在視圖 '{view.Name}' 的圖形覆寫"
                };
            }
        }

        /// <summary>
        /// 取得實心填滿圖樣 ID
        /// </summary>
        private ElementId GetSolidFillPatternId(Document doc)
        {
            // 嘗試找到實心填滿圖樣
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            var fillPatterns = collector
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .Where(fp => fp.GetFillPattern().IsSolidFill)
                .ToList();

            if (fillPatterns.Any())
            {
                return fillPatterns.First().Id;
            }

            return ElementId.InvalidElementId;
        }

        // 靜態變數：儲存取消接合的元素對
        private static List<Tuple<ElementId, ElementId>> _unjoinedPairs = new List<Tuple<ElementId, ElementId>>();

        /// <summary>
        /// 取消牆體與其他元素（柱子等）的接合關係
        /// </summary>
        private object UnjoinWallJoins(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            
            // 取得牆體 ID 列表
            var wallIdsArray = parameters["wallIds"] as JArray;
            int? viewId = parameters["viewId"]?.Value<int>();
            
            List<int> wallIds = new List<int>();
            if (wallIdsArray != null)
            {
                wallIds.AddRange(wallIdsArray.Select(id => id.Value<int>()));
            }
            
            // 如果沒有提供 wallIds，則查詢視圖中所有牆體
            if (wallIds.Count == 0 && viewId.HasValue)
            {
                var collector = new FilteredElementCollector(doc, new ElementId(viewId.Value));
                var walls = collector.OfClass(typeof(Wall)).ToElements();
                wallIds = walls.Select(w => w.Id.IntegerValue).ToList();
            }
            
            if (wallIds.Count == 0)
            {
                throw new Exception("請提供 wallIds 或 viewId 參數");
            }

            int unjoinedCount = 0;
            _unjoinedPairs.Clear();

            using (Transaction trans = new Transaction(doc, "Unjoin Wall Geometry"))
            {
                trans.Start();

                foreach (int wallId in wallIds)
                {
                    Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
                    if (wall == null) continue;

                    // 取得牆體的 BoundingBox 來找附近的柱子
                    BoundingBoxXYZ bbox = wall.get_BoundingBox(null);
                    if (bbox == null) continue;

                    // 擴大搜尋範圍
                    XYZ min = bbox.Min - new XYZ(1, 1, 1);
                    XYZ max = bbox.Max + new XYZ(1, 1, 1);
                    Outline outline = new Outline(min, max);

                    // 查詢附近的柱子
                    var columnCollector = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Columns)
                        .WherePasses(new BoundingBoxIntersectsFilter(outline));
                    
                    var structColumnCollector = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralColumns)
                        .WherePasses(new BoundingBoxIntersectsFilter(outline));

                    var columns = columnCollector.ToElements().Concat(structColumnCollector.ToElements());

                    foreach (Element column in columns)
                    {
                        try
                        {
                            if (JoinGeometryUtils.AreElementsJoined(doc, wall, column))
                            {
                                JoinGeometryUtils.UnjoinGeometry(doc, wall, column);
                                _unjoinedPairs.Add(new Tuple<ElementId, ElementId>(wall.Id, column.Id));
                                unjoinedCount++;
                            }
                        }
                        catch
                        {
                            // 忽略無法取消接合的元素
                        }
                    }
                }

                trans.Commit();
            }

            return new
            {
                Success = true,
                UnjoinedCount = unjoinedCount,
                WallCount = wallIds.Count,
                StoredPairs = _unjoinedPairs.Count,
                Message = $"已取消 {unjoinedCount} 個接合關係"
            };
        }

        /// <summary>
        /// 恢復之前取消的接合關係
        /// </summary>
        private object RejoinWallJoins(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            
            if (_unjoinedPairs.Count == 0)
            {
                return new
                {
                    Success = true,
                    RejoinedCount = 0,
                    Message = "沒有需要恢復的接合關係"
                };
            }

            int rejoinedCount = 0;

            using (Transaction trans = new Transaction(doc, "Rejoin Wall Geometry"))
            {
                trans.Start();

                foreach (var pair in _unjoinedPairs)
                {
                    try
                    {
                        Element elem1 = doc.GetElement(pair.Item1);
                        Element elem2 = doc.GetElement(pair.Item2);
                        
                        if (elem1 != null && elem2 != null)
                        {
                            if (!JoinGeometryUtils.AreElementsJoined(doc, elem1, elem2))
                            {
                                JoinGeometryUtils.JoinGeometry(doc, elem1, elem2);
                                rejoinedCount++;
                            }
                        }
                    }
                    catch
                    {
                        // 忽略無法恢復接合的元素
                    }
                }

                trans.Commit();
            }

            int storedCount = _unjoinedPairs.Count;
            _unjoinedPairs.Clear();

            return new
            {
                Success = true,
                RejoinedCount = rejoinedCount,
                TotalPairs = storedCount,
                Message = $"已恢復 {rejoinedCount} 個接合關係"
            };
        }

        /// <summary>
        /// 取得所有視圖樣版及其設定
        /// </summary>
        private object GetViewTemplates(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            bool includeDetails = parameters["includeDetails"]?.Value<bool>() ?? true;

            // 取得所有視圖樣版 (IsTemplate = true)
            var viewTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .ToList();

            var templateList = new List<object>();

            foreach (var template in viewTemplates)
            {
                var templateInfo = new Dictionary<string, object>
                {
                    ["ElementId"] = template.Id.IntegerValue,
                    ["Name"] = template.Name,
                    ["ViewType"] = template.ViewType.ToString(),
                    ["ViewFamily"] = template.ViewType.ToString()
                };

                if (includeDetails)
                {
                    // 取得詳細等級
                    try
                    {
                        templateInfo["DetailLevel"] = template.DetailLevel.ToString();
                    }
                    catch { templateInfo["DetailLevel"] = "N/A"; }

                    // 取得視覺樣式
                    try
                    {
                        templateInfo["DisplayStyle"] = template.DisplayStyle.ToString();
                    }
                    catch { templateInfo["DisplayStyle"] = "N/A"; }

                    // 取得比例尺
                    try
                    {
                        templateInfo["Scale"] = template.Scale > 0 ? $"1:{template.Scale}" : "N/A";
                    }
                    catch { templateInfo["Scale"] = "N/A"; }

                    // 取得視圖樣版控制的參數
                    try
                    {
                        var nonControlledParams = template.GetNonControlledTemplateParameterIds();
                        var allParams = template.GetTemplateParameterIds();
                        templateInfo["ControlledParameterCount"] = allParams.Count - nonControlledParams.Count;
                        templateInfo["TotalParameterCount"] = allParams.Count;
                    }
                    catch 
                    { 
                        templateInfo["ControlledParameterCount"] = "N/A";
                        templateInfo["TotalParameterCount"] = "N/A";
                    }

                    // 取得類別可見性設定（僅列出主要隱藏的類別）
                    try
                    {
                        var hiddenCategories = new List<string>();
                        var categories = doc.Settings.Categories;
                        foreach (Category cat in categories)
                        {
                            try
                            {
                                if (cat.CategoryType == CategoryType.Model || cat.CategoryType == CategoryType.Annotation)
                                {
                                    if (!template.GetCategoryHidden(cat.Id))
                                        continue;
                                    hiddenCategories.Add(cat.Name);
                                }
                            }
                            catch { }
                        }
                        templateInfo["HiddenCategoryCount"] = hiddenCategories.Count;
                        // 只列出前 10 個隱藏類別
                        templateInfo["HiddenCategories"] = hiddenCategories.Take(10).ToList();
                    }
                    catch { templateInfo["HiddenCategories"] = new List<string>(); }

                    // 取得視圖專屬覆寫（篩選器）
                    try
                    {
                        var filterIds = template.GetFilters();
                        var filterNames = filterIds
                            .Select(id => doc.GetElement(id)?.Name ?? "Unknown")
                            .ToList();
                        templateInfo["FilterCount"] = filterIds.Count;
                        templateInfo["Filters"] = filterNames;
                    }
                    catch 
                    { 
                        templateInfo["FilterCount"] = 0;
                        templateInfo["Filters"] = new List<string>(); 
                    }

                    // 取得裁剪設定
                    try
                    {
                        templateInfo["CropBoxActive"] = template.CropBoxActive;
                        templateInfo["CropBoxVisible"] = template.CropBoxVisible;
                    }
                    catch 
                    { 
                        templateInfo["CropBoxActive"] = "N/A";
                        templateInfo["CropBoxVisible"] = "N/A";
                    }

                    // 取得底層設定（底層通常在平面圖視圖中有效）
                    try
                    {
                        // ViewPlan 有 Underlay 屬性，但 View 基類沒有
                        // 這裡只標記是否支援底層
                        templateInfo["SupportsUnderlay"] = (template.ViewType == ViewType.FloorPlan || 
                                                            template.ViewType == ViewType.CeilingPlan ||
                                                            template.ViewType == ViewType.AreaPlan);
                    }
                    catch { templateInfo["SupportsUnderlay"] = false; }
                }

                templateList.Add(templateInfo);
            }

            return new
            {
                ProjectName = doc.Title,
                Count = templateList.Count,
                ViewTemplates = templateList
            };
        }

        #endregion

        #region 外牆開口檢討

        /// <summary>
        /// 執行外牆開口檢討（第45條 + 第110條）
        /// </summary>
        private object CheckExteriorWallOpenings(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            bool checkArticle45 = parameters["checkArticle45"]?.Value<bool>() ?? true;
            bool checkArticle110 = parameters["checkArticle110"]?.Value<bool>() ?? true;
            bool colorizeViolations = parameters["colorizeViolations"]?.Value<bool>() ?? true;
            bool checkBuildingDistance = parameters["checkBuildingDistance"]?.Value<bool>() ?? false;
            bool exportReport = parameters["exportReport"]?.Value<bool>() ?? false;
            string reportPath = parameters["reportPath"]?.Value<string>();

            var checker = new ExteriorWallOpeningChecker(doc);
            var allResults = new List<object>();

            using (Transaction trans = new Transaction(doc, "外牆開口檢討"))
            {
                // 使用防禦性交易處理
                bool isTransactionStarted = false;

                // 2. 取得所有外牆
                int totalWalls = 0;
                int totalOpenings = 0;
                int violations = 0;
                int warnings = 0;
                int passed = 0;
                
                // 1. 取得基地邊界線
                // Note: GetPropertyLines doesn't require transaction status to run, assuming it just reads.
                // However, to be safe and consistent with previous flow, we'll keep logic similar but ensure variables are scoped correctly.
                List<Curve> propertyLines = null;

                try
                {
                    if (trans.Start() == TransactionStatus.Started)
                    {
                        isTransactionStarted = true;

                        // DEBUG VERSION LOG
                        System.Diagnostics.Debug.WriteLine("DLL Version: 2026.01.14.02 - Transaction Started");

                        propertyLines = checker.GetPropertyLines();
                        if (propertyLines.Count == 0)
                        {
                            throw new InvalidOperationException("找不到基地邊界線（PropertyLine）。請確認專案中已建立地界線，且您已結束編輯模式（打勾）。");
                        }

                        var exteriorWalls = checker.GetExteriorWalls();

                        // 3. 遍歷每面外牆
                        foreach (var wall in exteriorWalls)
                        {
                            totalWalls++;
                            var openings = checker.GetWallOpenings(wall);

                            foreach (var opening in openings)
                            {
                                totalOpenings++;
                                var openingInfo = checker.GetOpeningInfo(opening);
                                if (openingInfo == null) continue;

                                // 計算距離
                                var boundaryResult = checker.CalculateDistanceToBoundary(openingInfo, propertyLines);
                                var distanceToBoundary = boundaryResult.MinDistance;
                                var distanceToBuilding = checkBuildingDistance
                                    ? checker.CalculateDistanceToAdjacentBuildings(openingInfo, wall)
                                    : double.MaxValue;

                                // 執行檢查
                                ExteriorWallOpeningChecker.Article45Result article45Result = null;
                                ExteriorWallOpeningChecker.Article110Result article110Result = null;

                                if (checkArticle45)
                                {
                                    article45Result = checker.CheckArticle45(openingInfo, distanceToBoundary, distanceToBuilding);
                                }

                                if (checkArticle110)
                                {
                                    article110Result = checker.CheckArticle110(openingInfo, distanceToBoundary, distanceToBuilding);
                                }

                                // 視覺化
                                if (colorizeViolations)
                                {
                                    var overallStatus = DetermineOverallStatus(article45Result, article110Result);
                                    ColorizeOpening(doc, uidoc.ActiveView, opening.Id, overallStatus);

                                    if (overallStatus == ExteriorWallOpeningChecker.CheckStatus.Fail) violations++;
                                    else if (overallStatus == ExteriorWallOpeningChecker.CheckStatus.Warning) warnings++;
                                    else passed++;

                                    // 如果違規或有警告，建立標註 (Dimension)
                                    if ((overallStatus == ExteriorWallOpeningChecker.CheckStatus.Fail || overallStatus == ExteriorWallOpeningChecker.CheckStatus.Warning) && boundaryResult.ClosestPoint != null)
                                    {
                                        try
                                        {
                                            // 1. 定義標註線 (Opening Center -> Boundary Point)
                                            // 確保 Z 軸一致 (在開口高度)
                                            XYZ start = openingInfo.Location;
                                            XYZ end = new XYZ(boundaryResult.ClosestPoint.X, boundaryResult.ClosestPoint.Y, start.Z);
                                            
                                            // 避免極短線段
                                            if (start.DistanceTo(end) > 0.01)
                                            {
                                                Line line = Line.CreateBound(start, end);

                                                // 2. 建立參考平面 (SketchPlane)
                                                // 需要一個包含該線的平面。水平線通常位於 XY 平面。
                                                XYZ norm = XYZ.BasisZ;
                                                Plane plane = Plane.CreateByNormalAndOrigin(norm, start);
                                                SketchPlane sketchPlane = SketchPlane.Create(doc, plane);

                                                // 3. 建立模型線 (Model Line)
                                                ModelCurve modelCurve = doc.Create.NewModelCurve(line, sketchPlane);
                                                
                                                // 嘗試設定線樣式為紅色 (若有)
                                                // (省略樣式設定以保持簡單)

                                                // 4. 建立尺寸標註 (Dimension)
                                                // 尺寸標註必須依附於 View。如果 View 是 3D View，必須設定 WorkPoint。
                                                // 簡單起見，嘗試建立基於模型線端點的尺寸。
                                                
                                                ReferenceArray refArray = new ReferenceArray();
                                                refArray.Append(modelCurve.GeometryCurve.GetEndPointReference(0));
                                                refArray.Append(modelCurve.GeometryCurve.GetEndPointReference(1));

                                                Dimension dim = doc.Create.NewDimension(uidoc.ActiveView, line, refArray);

                                                // 5. 將標註設為紅色
                                                OverrideGraphicSettings redOverride = new OverrideGraphicSettings();
                                                redOverride.SetProjectionLineColor(new Color(255, 0, 0)); // 紅色
                                                uidoc.ActiveView.SetElementOverrides(dim.Id, redOverride);
                                            }
                                        }
                                        catch (Exception dimEx)
                                        {
                                            // 標註建立失敗不應中斷檢討流程
                                            System.Diagnostics.Debug.WriteLine($"無法建立標註: {dimEx.Message}");
                                        }
                                    }
                                }

                                // 記錄結果
                                allResults.Add(new
                                {
                                    openingId = openingInfo.OpeningId.IntegerValue,
                                    wallId = openingInfo.WallId?.IntegerValue,
                                    openingType = openingInfo.OpeningType,
                                    location = new
                                    {
                                        x = Math.Round(openingInfo.Location.X * 304.8, 2),
                                        y = Math.Round(openingInfo.Location.Y * 304.8, 2),
                                        z = Math.Round(openingInfo.Location.Z * 304.8, 2)
                                    },
                                    area = Math.Round(openingInfo.Area * 0.0929, 2), // 平方英尺 → 平方公尺
                                    article45 = article45Result,
                                    article110 = article110Result
                                });
                            }
                        }

                        trans.Commit();
                    }
                    else
                    {
                        throw new InvalidOperationException("無法啟動 Revit 交易，可能目前正處於其他命令或編輯模式中。");
                    }

                    var summary = new
                    {
                        totalWalls,
                        totalOpenings,
                        violations,
                        warnings,
                        passed,
                        propertyLineCount = propertyLines.Count
                    };

                    var response = new
                    {
                        success = true,
                        summary,
                        details = allResults,
                        message = $"檢討完成：共檢查 {totalWalls} 面外牆、{totalOpenings} 個開口"
                    };

                    // 匯出報表（可選）
                    if (exportReport && !string.IsNullOrEmpty(reportPath))
                    {
                        System.IO.File.WriteAllText(reportPath,
                            Newtonsoft.Json.JsonConvert.SerializeObject(response, Newtonsoft.Json.Formatting.Indented));
                    }

                    return response;
                }
                catch (Exception ex)
                {
                    if (isTransactionStarted && trans.GetStatus() == TransactionStatus.Started)
                    {
                        trans.RollBack();
                    }
                    throw new Exception($"外牆開口檢討失敗：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 判定總體狀態
        /// </summary>
        private ExteriorWallOpeningChecker.CheckStatus DetermineOverallStatus(
            ExteriorWallOpeningChecker.Article45Result article45Result,
            ExteriorWallOpeningChecker.Article110Result article110Result)
        {
            var statuses = new List<ExteriorWallOpeningChecker.CheckStatus>();

            if (article45Result != null) statuses.Add(article45Result.OverallStatus);
            if (article110Result != null) statuses.Add(article110Result.OverallStatus);

            if (statuses.Contains(ExteriorWallOpeningChecker.CheckStatus.Fail)) 
                return ExteriorWallOpeningChecker.CheckStatus.Fail;
            if (statuses.Contains(ExteriorWallOpeningChecker.CheckStatus.Warning)) 
                return ExteriorWallOpeningChecker.CheckStatus.Warning;
            return ExteriorWallOpeningChecker.CheckStatus.Pass;
        }

        /// <summary>
        /// 為開口元素設定顏色
        /// 同時設定 Cut（平面圖）和 Surface（立面圖）樣式，確保所有視圖類型都能顯示
        /// </summary>
        private void ColorizeOpening(Document doc, View view, ElementId openingId, ExteriorWallOpeningChecker.CheckStatus status)
        {
            var overrideSettings = new OverrideGraphicSettings();
            ElementId solidPatternId = GetSolidFillPatternId(doc);
            Color color;

            switch (status)
            {
                case ExteriorWallOpeningChecker.CheckStatus.Fail:
                    color = new Color(255, 0, 0); // 紅色
                    break;
                case ExteriorWallOpeningChecker.CheckStatus.Warning:
                    color = new Color(255, 165, 0); // 橘色
                    break;
                case ExteriorWallOpeningChecker.CheckStatus.Pass:
                    color = new Color(0, 255, 0); // 綠色
                    break;
                default:
                    return;
            }

            // 投影線顏色（所有視圖通用）
            overrideSettings.SetProjectionLineColor(color);

            // Surface pattern（立面/剖面/3D）
            overrideSettings.SetSurfaceForegroundPatternColor(color);
            if (solidPatternId != null && solidPatternId != ElementId.InvalidElementId)
            {
                overrideSettings.SetSurfaceForegroundPatternId(solidPatternId);
                overrideSettings.SetSurfaceForegroundPatternVisible(true);
            }

            // Cut pattern（平面圖中門窗被牆切割時顯示）
            overrideSettings.SetCutForegroundPatternColor(color);
            if (solidPatternId != null && solidPatternId != ElementId.InvalidElementId)
            {
                overrideSettings.SetCutForegroundPatternId(solidPatternId);
                overrideSettings.SetCutForegroundPatternVisible(true);
            }

            // Cut line 顏色
            overrideSettings.SetCutLineColor(color);

            view.SetElementOverrides(openingId, overrideSettings);
        }

        #endregion

        #region 帷幕牆操作

        /// <summary>
        /// 取得帷幕牆資訊（Grid 結構與 Panel 資訊）
        /// </summary>
        private object GetCurtainWallInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            int? elementId = parameters["elementId"]?.Value<int>();
            Wall wall = null;

            // 如果沒有指定 elementId，使用目前選取的元素
            if (elementId.HasValue)
            {
                Element elem = doc.GetElement(new ElementId(elementId.Value));
                wall = elem as Wall;
            }
            else
            {
                var selection = uidoc.Selection.GetElementIds();
                if (selection.Count == 0)
                    throw new Exception("請先選取一個帷幕牆，或指定 elementId");

                Element elem = doc.GetElement(selection.First());
                wall = elem as Wall;
            }

            if (wall == null)
                throw new Exception("選取的元素不是牆");

            // 檢查是否為帷幕牆
            CurtainGrid grid = wall.CurtainGrid;
            if (grid == null)
                throw new Exception("此牆不是帷幕牆（沒有 CurtainGrid）");

            // 取得 Grid 資訊
            var uGridIds = grid.GetUGridLineIds();
            var vGridIds = grid.GetVGridLineIds();
            var panelIds = grid.GetPanelIds();

            // 計算 rows 和 columns
            int rows = uGridIds.Count + 1;    // U Grid = 水平線 = 定義 Row
            int columns = vGridIds.Count + 1; // V Grid = 垂直線 = 定義 Column

            // 收集面板資訊
            var panelTypeDict = new Dictionary<int, (string TypeName, string MaterialName, string MaterialColor, int Count)>();
            var panelMatrix = new List<List<int>>(); // [row][col] = typeId

            // 取得牆的位置線來計算方向
            LocationCurve locCurve = wall.Location as LocationCurve;
            Curve curve = locCurve?.Curve;

            // 收集面板並分析
            foreach (ElementId panelId in panelIds)
            {
                Element panel = doc.GetElement(panelId);
                if (panel == null) continue;

                ElementId typeId = panel.GetTypeId();
                int typeIdInt = typeId.IntegerValue;

                if (!panelTypeDict.ContainsKey(typeIdInt))
                {
                    ElementType panelType = doc.GetElement(typeId) as ElementType;
                    string typeName = panelType?.Name ?? "Unknown";

                    // 嘗試取得材料資訊
                    string materialName = "";
                    string materialColor = "#808080";

                    try
                    {
                        Parameter matParam = panelType?.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (matParam != null && matParam.HasValue)
                        {
                            ElementId matId = matParam.AsElementId();
                            Material mat = doc.GetElement(matId) as Material;
                            if (mat != null)
                            {
                                materialName = mat.Name;
                                Color color = mat.Color;
                                if (color != null && color.IsValid)
                                {
                                    materialColor = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
                                }
                            }
                        }
                    }
                    catch { }

                    panelTypeDict[typeIdInt] = (typeName, materialName, materialColor, 0);
                }

                var current = panelTypeDict[typeIdInt];
                panelTypeDict[typeIdInt] = (current.TypeName, current.MaterialName, current.MaterialColor, current.Count + 1);
            }

            // 取得面板尺寸（從第一個面板估算）
            double panelWidth = 0;
            double panelHeight = 0;

            if (panelIds.Count > 0)
            {
                Element firstPanel = doc.GetElement(panelIds.First());
                BoundingBoxXYZ bb = firstPanel?.get_BoundingBox(null);
                if (bb != null)
                {
                    panelWidth = Math.Round((bb.Max.X - bb.Min.X) * 304.8, 2);
                    panelHeight = Math.Round((bb.Max.Z - bb.Min.Z) * 304.8, 2);
                }
            }

            // 組織回傳資料
            var panelTypes = panelTypeDict.Select(kvp => new
            {
                TypeId = kvp.Key,
                TypeName = kvp.Value.TypeName,
                MaterialName = kvp.Value.MaterialName,
                MaterialColor = kvp.Value.MaterialColor,
                Count = kvp.Value.Count
            }).ToList();

            return new
            {
                ElementId = wall.Id.IntegerValue,
                WallType = wall.WallType.Name,
                IsCurtainWall = true,
                Rows = rows,
                Columns = columns,
                TotalPanels = panelIds.Count,
                PanelWidth = panelWidth,
                PanelHeight = panelHeight,
                UGridCount = uGridIds.Count,
                VGridCount = vGridIds.Count,
                PanelTypes = panelTypes,
                PanelIds = panelIds.Select(id => id.IntegerValue).ToList()
            };
        }

        /// <summary>
        /// 取得專案中所有可用的帷幕面板類型
        /// </summary>
        private object GetCurtainPanelTypes(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            // 取得所有 Curtain Panel 類型
            var panelTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .Select(pt =>
                {
                    string materialName = "";
                    string materialColor = "#808080";
                    int transparency = 0;

                    try
                    {
                        Parameter matParam = pt.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (matParam != null && matParam.HasValue)
                        {
                            ElementId matId = matParam.AsElementId();
                            Material mat = doc.GetElement(matId) as Material;
                            if (mat != null)
                            {
                                materialName = mat.Name;
                                Color color = mat.Color;
                                if (color != null && color.IsValid)
                                {
                                    materialColor = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
                                }
                                transparency = mat.Transparency;
                            }
                        }
                    }
                    catch { }

                    return new
                    {
                        TypeId = pt.Id.IntegerValue,
                        TypeName = pt.Name,
                        Family = (pt as FamilySymbol)?.FamilyName ?? "System Panel",
                        MaterialName = materialName,
                        MaterialColor = materialColor,
                        Transparency = transparency
                    };
                })
                .OrderBy(pt => pt.Family)
                .ThenBy(pt => pt.TypeName)
                .ToList();

            return new
            {
                Count = panelTypes.Count,
                PanelTypes = panelTypes
            };
        }

        /// <summary>
        /// 建立新的帷幕面板類型（含材料）
        /// </summary>
        private object CreateCurtainPanelType(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            string typeName = parameters["typeName"]?.Value<string>();
            string colorHex = parameters["color"]?.Value<string>() ?? "#808080";
            int transparency = parameters["transparency"]?.Value<int>() ?? 0;
            string basePanelTypeName = parameters["basePanelType"]?.Value<string>();

            if (string.IsNullOrEmpty(typeName))
                throw new Exception("請指定新類型名稱 (typeName)");

            // 解析顏色
            colorHex = colorHex.TrimStart('#');
            byte r = Convert.ToByte(colorHex.Substring(0, 2), 16);
            byte g = Convert.ToByte(colorHex.Substring(2, 2), 16);
            byte b = Convert.ToByte(colorHex.Substring(4, 2), 16);
            Color revitColor = new Color(r, g, b);

            using (Transaction trans = new Transaction(doc, "建立帷幕面板類型"))
            {
                trans.Start();

                // 1. 找到基礎面板類型來複製
                ElementType basePanelType = null;

                if (!string.IsNullOrEmpty(basePanelTypeName))
                {
                    basePanelType = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                        .WhereElementIsElementType()
                        .Cast<ElementType>()
                        .FirstOrDefault(pt => pt.Name == basePanelTypeName);
                }

                if (basePanelType == null)
                {
                    // 使用預設的 System Panel
                    basePanelType = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                        .WhereElementIsElementType()
                        .Cast<ElementType>()
                        .FirstOrDefault();
                }

                if (basePanelType == null)
                    throw new Exception("找不到可用的帷幕面板類型作為基礎");

                // 2. 檢查是否已存在同名類型
                ElementType existingType = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                    .WhereElementIsElementType()
                    .Cast<ElementType>()
                    .FirstOrDefault(pt => pt.Name == typeName);

                ElementType newPanelType;
                bool isNewType = false;

                if (existingType != null)
                {
                    newPanelType = existingType;
                }
                else
                {
                    // 3. 複製類型
                    newPanelType = basePanelType.Duplicate(typeName) as ElementType;
                    isNewType = true;
                }

                // 4. 建立或更新材料
                string materialName = $"CW_PNL_{typeName}";
                Material material = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(m => m.Name == materialName);

                if (material == null)
                {
                    // 建立新材料
                    ElementId newMatId = Material.Create(doc, materialName);
                    material = doc.GetElement(newMatId) as Material;
                }

                // 設定材料屬性
                material.Color = revitColor;
                material.Transparency = transparency;

                // 5. 將材料指派給面板類型
                Parameter matParam = newPanelType.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (matParam != null && !matParam.IsReadOnly)
                {
                    matParam.Set(material.Id);
                }

                trans.Commit();

                return new
                {
                    Success = true,
                    TypeId = newPanelType.Id.IntegerValue,
                    TypeName = typeName,
                    IsNewType = isNewType,
                    MaterialId = material.Id.IntegerValue,
                    MaterialName = materialName,
                    Color = $"#{r:X2}{g:X2}{b:X2}",
                    Transparency = transparency,
                    Message = isNewType
                        ? $"成功建立新面板類型: {typeName}"
                        : $"已更新既有面板類型: {typeName}"
                };
            }
        }

        /// <summary>
        /// 批次套用面板排列模式
        /// 支援兩種模式：
        /// 1. typeMapping + matrix: 使用字母矩陣配合類型映射
        /// 2. pattern: 直接使用 TypeId 矩陣
        /// </summary>
        private object ApplyPanelPattern(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            int? wallElementId = parameters["elementId"]?.Value<int>() ?? parameters["wallId"]?.Value<int>();
            JObject typeMapping = parameters["typeMapping"] as JObject;
            JArray matrix = parameters["matrix"] as JArray;
            JArray patternArray = parameters["pattern"] as JArray;

            // 取得帷幕牆
            Wall wall = null;
            if (wallElementId.HasValue)
            {
                wall = doc.GetElement(new ElementId(wallElementId.Value)) as Wall;
            }
            else
            {
                var selection = uidoc.Selection.GetElementIds();
                if (selection.Count > 0)
                {
                    wall = doc.GetElement(selection.First()) as Wall;
                }
            }

            if (wall == null)
                throw new Exception("找不到帷幕牆，請指定 elementId 或選取帷幕牆");

            CurtainGrid grid = wall.CurtainGrid;
            if (grid == null)
                throw new Exception("此牆不是帷幕牆");

            // 建立類型映射字典
            var typeMappingDict = new Dictionary<string, int>();
            if (typeMapping != null)
            {
                foreach (var prop in typeMapping.Properties())
                {
                    typeMappingDict[prop.Name] = prop.Value.Value<int>();
                }
            }

            // 決定使用哪種模式
            JArray sourceMatrix = matrix ?? patternArray;
            if (sourceMatrix == null)
                throw new Exception("請提供 matrix（字母矩陣 + typeMapping）或 pattern（TypeId 矩陣）");

            // 取得所有面板
            var panelIds = grid.GetPanelIds().ToList();

            // 建立面板位置映射 (依據幾何位置排序)
            var panelPositions = new List<(ElementId Id, XYZ Center)>();

            foreach (ElementId panelId in panelIds)
            {
                Element panel = doc.GetElement(panelId);
                if (panel == null) continue;

                BoundingBoxXYZ bb = panel.get_BoundingBox(null);
                if (bb == null) continue;

                XYZ center = (bb.Min + bb.Max) / 2;
                panelPositions.Add((panelId, center));
            }

            // 依照位置排序並分配 Row/Col
            // 先依 Z (高度) 分組（由上到下），再依 X 或 Y 排序（由左到右）
            var sortedByZ = panelPositions.OrderByDescending(p => p.Center.Z).ToList();

            // 分組 by Z
            var rowGroups = new List<List<(ElementId Id, XYZ Center)>>();
            double zTolerance = 0.5; // 0.5 feet

            foreach (var panel in sortedByZ)
            {
                bool added = false;
                foreach (var group in rowGroups)
                {
                    if (Math.Abs(group[0].Center.Z - panel.Center.Z) < zTolerance)
                    {
                        group.Add((panel.Id, panel.Center));
                        added = true;
                        break;
                    }
                }
                if (!added)
                {
                    rowGroups.Add(new List<(ElementId, XYZ)> { (panel.Id, panel.Center) });
                }
            }

            // 建立 Row/Col 到 PanelId 的映射
            var panelGrid = new Dictionary<(int row, int col), ElementId>();
            int rowIndex = 0;
            foreach (var rowGroup in rowGroups)
            {
                var sortedRow = rowGroup.OrderBy(p => p.Center.X).ThenBy(p => p.Center.Y).ToList();
                int colIndex = 0;
                foreach (var panel in sortedRow)
                {
                    panelGrid[(rowIndex, colIndex)] = panel.Id;
                    colIndex++;
                }
                rowIndex++;
            }

            // 套用模式
            int successCount = 0;
            int failCount = 0;
            var failedPanels = new List<object>();

            using (Transaction trans = new Transaction(doc, "套用帷幕面板排列"))
            {
                trans.Start();

                for (int r = 0; r < sourceMatrix.Count && r < rowGroups.Count; r++)
                {
                    JArray rowData = sourceMatrix[r] as JArray;
                    if (rowData == null) continue;

                    for (int c = 0; c < rowData.Count; c++)
                    {
                        if (!panelGrid.ContainsKey((r, c))) continue;

                        // 取得目標類型 ID
                        int targetTypeId = 0;
                        var cellValue = rowData[c];

                        if (cellValue.Type == JTokenType.String)
                        {
                            // 字母模式，從 typeMapping 查找
                            string key = cellValue.Value<string>();
                            if (string.IsNullOrEmpty(key)) continue;
                            if (!typeMappingDict.TryGetValue(key, out targetTypeId))
                            {
                                failedPanels.Add(new { Row = r, Col = c, Reason = $"找不到映射: {key}" });
                                failCount++;
                                continue;
                            }
                        }
                        else if (cellValue.Type == JTokenType.Integer)
                        {
                            // 直接 TypeId 模式
                            targetTypeId = cellValue.Value<int>();
                        }

                        if (targetTypeId == 0) continue;

                        ElementId panelId = panelGrid[(r, c)];
                        Element panel = doc.GetElement(panelId);

                        if (panel == null)
                        {
                            failCount++;
                            continue;
                        }

                        try
                        {
                            // 取得目標類型
                            ElementType targetType = doc.GetElement(new ElementId(targetTypeId)) as ElementType;
                            if (targetType == null)
                            {
                                failedPanels.Add(new { PanelId = panelId.IntegerValue, Row = r, Col = c, Reason = $"找不到 TypeId: {targetTypeId}" });
                                failCount++;
                                continue;
                            }

                            // 變更面板類型
                            panel.ChangeTypeId(new ElementId(targetTypeId));
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            failedPanels.Add(new { PanelId = panelId.IntegerValue, Row = r, Col = c, Reason = ex.Message });
                            failCount++;
                        }
                    }
                }

                trans.Commit();
            }

            return new
            {
                Success = true,
                WallId = wall.Id.IntegerValue,
                TotalPanels = panelIds.Count,
                SuccessCount = successCount,
                FailCount = failCount,
                FailedPanels = failedPanels,
                GridSize = new { Rows = rowGroups.Count, Columns = rowGroups.FirstOrDefault()?.Count ?? 0 },
                Message = $"成功套用 {successCount} 個面板，失敗 {failCount} 個"
            };
        }

        // ============================
        // 立面面板 (Facade Panel) 相關
        // ============================

        /// <summary>
        /// 建立單片立面面板 (DirectShape)
        /// 支援多種幾何類型：curved_panel（弧形面板）、beveled_opening（斜切凹窗框）、flat_panel（平面面板）
        /// </summary>
        private object CreateFacadePanel(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            // 解析共用參數
            int? wallId = parameters["wallId"]?.Value<int>();
            double positionAlongWall = parameters["positionAlongWall"]?.Value<double>() ?? 0;
            double positionZ = parameters["positionZ"]?.Value<double>() ?? 0;
            double width = parameters["width"]?.Value<double>() ?? 800;
            double height = parameters["height"]?.Value<double>() ?? 3400;
            double depth = parameters["depth"]?.Value<double>() ?? 150;
            double thickness = parameters["thickness"]?.Value<double>() ?? 30;
            double offset = parameters["offset"]?.Value<double>() ?? 200;
            string colorHex = parameters["color"]?.Value<string>() ?? "#B85C3A";
            string panelName = parameters["name"]?.Value<string>() ?? "FacadePanel";
            string geometryType = parameters["geometryType"]?.Value<string>() ?? "curved_panel";

            // curved_panel 專用
            string curveType = parameters["curveType"]?.Value<string>() ?? "concave";

            // beveled_opening 專用
            string bevelDirection = parameters["bevelDirection"]?.Value<string>() ?? "center";
            double openingWidth = parameters["openingWidth"]?.Value<double>() ?? 600;
            double openingHeight = parameters["openingHeight"]?.Value<double>() ?? 800;
            double bevelDepth = parameters["bevelDepth"]?.Value<double>() ?? 300;

            // angled_panel 專用
            double tiltAngle = parameters["tiltAngle"]?.Value<double>() ?? 15;
            string tiltAxis = parameters["tiltAxis"]?.Value<string>() ?? "horizontal";

            // rounded_opening 專用
            double cornerRadius = parameters["cornerRadius"]?.Value<double>() ?? 100;
            string openingShape = parameters["openingShape"]?.Value<string>() ?? "rounded_rect";

            // 取得牆體
            Wall wall = null;
            if (wallId.HasValue)
            {
                wall = doc.GetElement(new ElementId(wallId.Value)) as Wall;
            }
            else
            {
                var selection = uidoc.Selection.GetElementIds();
                if (selection.Count > 0)
                    wall = doc.GetElement(selection.First()) as Wall;
            }

            if (wall == null)
                throw new Exception("找不到牆體，請指定 wallId 或選取牆體");

            LocationCurve wallLoc = wall.Location as LocationCurve;
            if (wallLoc == null)
                throw new Exception("無法取得牆體位置線");

            Line wallLine = wallLoc.Curve as Line;
            if (wallLine == null)
                throw new Exception("目前僅支援直線牆");

            XYZ wallDir = wallLine.Direction.Normalize();
            // 使用 wall.Orientation 取得外牆面法線（永遠指向室外）
            XYZ wallNormal = wall.Orientation.Normalize();
            // 將起始點從牆中心線偏移到外牆面（半個牆厚度）
            double halfWallThickness = wall.Width / 2.0; // 已經是 feet
            XYZ wallExteriorStart = wallLine.GetEndPoint(0) + wallNormal * halfWallThickness;

            using (Transaction trans = new Transaction(doc, $"建立立面面板: {panelName}"))
            {
                trans.Start();

                try
                {
                    Solid solid;

                    switch (geometryType)
                    {
                        case "beveled_opening":
                            solid = CreateBeveledOpeningSolid(
                                wallExteriorStart, wallDir, wallNormal,
                                positionAlongWall, positionZ, width, height,
                                openingWidth, openingHeight, bevelDepth, thickness,
                                bevelDirection, offset);
                            break;

                        case "angled_panel":
                            solid = CreateAngledPanelSolid(
                                wallExteriorStart, wallDir, wallNormal,
                                positionAlongWall, positionZ, width, height,
                                thickness, offset, tiltAngle, tiltAxis);
                            break;

                        case "rounded_opening":
                            solid = CreateRoundedOpeningSolid(
                                wallExteriorStart, wallDir, wallNormal,
                                positionAlongWall, positionZ, width, height,
                                openingWidth, openingHeight, depth, thickness,
                                cornerRadius, openingShape, offset);
                            break;

                        case "flat_panel":
                            solid = CreateFlatPanelSolid(
                                wallExteriorStart, wallDir, wallNormal,
                                positionAlongWall, positionZ, width, height,
                                thickness, offset);
                            break;

                        case "curved_panel":
                        default:
                            solid = CreateCurvedPanelSolid(
                                wallExteriorStart, wallDir, wallNormal,
                                positionAlongWall, positionZ, width, height,
                                depth, thickness, curveType, offset);
                            break;
                    }

                    // 建立 DirectShape
                    DirectShape ds = DirectShape.CreateElement(
                        doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.ApplicationId = "RevitMCP_FacadePanel";
                    ds.ApplicationDataId = panelName;
                    ds.SetShape(new GeometryObject[] { solid });

                    // 材料覆寫
                    Material mat = FindOrCreateFacadeMaterial(doc, colorHex, panelName);
                    ApplyMaterialOverride(doc, ds.Id, mat);

                    trans.Commit();

                    return new
                    {
                        Success = true,
                        ElementId = ds.Id.IntegerValue,
                        Name = panelName,
                        GeometryType = geometryType,
                        Width = width,
                        Height = height,
                        Depth = depth,
                        Color = colorHex,
                        Message = $"成功建立立面面板: {panelName} ({geometryType}), ID: {ds.Id.IntegerValue}"
                    };
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started)
                        trans.RollBack();
                    throw new Exception($"建立立面面板失敗: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 建立弧形面板 Solid（弧形截面沿 Z 軸擠出）
        /// </summary>
        private Solid CreateCurvedPanelSolid(
            XYZ wallStart, XYZ wallDir, XYZ wallNormal,
            double posAlongMm, double posZMm, double widthMm, double heightMm,
            double depthMm, double thicknessMm, string curveType, double offsetMm)
        {
            double w = widthMm / 304.8;
            double h = heightMm / 304.8;
            double d = depthMm / 304.8;
            double t = thicknessMm / 304.8;
            double off = offsetMm / 304.8;
            double posA = posAlongMm / 304.8;
            double posZ = posZMm / 304.8;

            XYZ center = wallStart + wallDir * posA + wallNormal * off;
            XYZ p1 = center - wallDir * (w / 2);
            XYZ p2 = center + wallDir * (w / 2);

            double arcSign = curveType == "concave" ? 1.0 : -1.0;
            XYZ midPt = center + wallNormal * (d * arcSign);

            p1 = new XYZ(p1.X, p1.Y, posZ);
            p2 = new XYZ(p2.X, p2.Y, posZ);
            midPt = new XYZ(midPt.X, midPt.Y, posZ);

            Arc innerArc = Arc.Create(p1, p2, midPt);
            XYZ p1o = p1 + wallNormal * (t * arcSign);
            XYZ p2o = p2 + wallNormal * (t * arcSign);
            XYZ midO = midPt + wallNormal * (t * arcSign);
            Arc outerArc = Arc.Create(p1o, p2o, midO);

            CurveLoop profile = new CurveLoop();
            profile.Append(innerArc);
            profile.Append(Line.CreateBound(p2, p2o));
            profile.Append(outerArc.CreateReversed());
            profile.Append(Line.CreateBound(p1o, p1));

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { profile }, XYZ.BasisZ, h);
        }

        /// <summary>
        /// 建立斜切凹窗框 Solid（外框 + 斜切面，中心開口）
        /// bevelDirection: "center"(均勻), "up"(上深), "down"(下深), "left"(左深), "right"(右深)
        /// </summary>
        private Solid CreateBeveledOpeningSolid(
            XYZ wallStart, XYZ wallDir, XYZ wallNormal,
            double posAlongMm, double posZMm, double framWidthMm, double frameHeightMm,
            double openWidthMm, double openHeightMm, double bevelDepthMm, double frameThickMm,
            string bevelDirection, double offsetMm)
        {
            double fw = framWidthMm / 304.8;
            double fh = frameHeightMm / 304.8;
            double ow = openWidthMm / 304.8;
            double oh = openHeightMm / 304.8;
            double bd = bevelDepthMm / 304.8;
            double ft = frameThickMm / 304.8;
            double off = offsetMm / 304.8;
            double posA = posAlongMm / 304.8;
            double posZ = posZMm / 304.8;

            // 外框位置
            XYZ center = wallStart + wallDir * posA + wallNormal * off;

            // 外框四角（牆面上，Z = posZ）
            XYZ oA = center - wallDir * (fw / 2) + new XYZ(0, 0, posZ);           // 左下
            XYZ oB = center + wallDir * (fw / 2) + new XYZ(0, 0, posZ);           // 右下
            XYZ oC = center + wallDir * (fw / 2) + new XYZ(0, 0, posZ + fh);      // 右上
            XYZ oD = center - wallDir * (fw / 2) + new XYZ(0, 0, posZ + fh);      // 左上

            // 內開口四角（深入 bevelDepth 的位置）
            // 根據 bevelDirection 調整各邊的深度
            double dTop = bd, dBottom = bd, dLeft = bd, dRight = bd;

            switch (bevelDirection)
            {
                case "up":    dTop = bd * 0.3; dBottom = bd * 1.5; break;
                case "down":  dTop = bd * 1.5; dBottom = bd * 0.3; break;
                case "left":  dLeft = bd * 0.3; dRight = bd * 1.5; break;
                case "right": dLeft = bd * 1.5; dRight = bd * 0.3; break;
                // center: 均勻深度
            }

            double innerCenterX_offset = 0;
            double innerCenterZ_offset = 0;

            XYZ innerCenter = center + wallNormal * bd;

            XYZ iA = innerCenter - wallDir * (ow / 2) + new XYZ(0, 0, posZ + (fh - oh) / 2);
            XYZ iB = innerCenter + wallDir * (ow / 2) + new XYZ(0, 0, posZ + (fh - oh) / 2);
            XYZ iC = innerCenter + wallDir * (ow / 2) + new XYZ(0, 0, posZ + (fh + oh) / 2);
            XYZ iD = innerCenter - wallDir * (ow / 2) + new XYZ(0, 0, posZ + (fh + oh) / 2);

            // 對斜切方向做微調：偏移內開口位置
            XYZ dirShift = XYZ.Zero;
            switch (bevelDirection)
            {
                case "up":    dirShift = new XYZ(0, 0, (fh - oh) * 0.15); break;
                case "down":  dirShift = new XYZ(0, 0, -(fh - oh) * 0.15); break;
                case "left":  dirShift = -wallDir * (fw - ow) * 0.15; break;
                case "right": dirShift = wallDir * (fw - ow) * 0.15; break;
            }
            iA = iA + dirShift;
            iB = iB + dirShift;
            iC = iC + dirShift;
            iD = iD + dirShift;

            // 建立幾何：用 4 個梯形面 + 外框背面組成的實體
            // 使用 BooleanOperationsUtils：外框實體 - 內開口金字塔形空間
            // 方法：建立外框 box，建立內部的金字塔形 void，做布林減法

            // 外框 solid：矩形截面沿法線擠出
            CurveLoop outerProfile = new CurveLoop();
            XYZ oA2 = new XYZ(oA.X, oA.Y, oA.Z);
            XYZ oB2 = new XYZ(oB.X, oB.Y, oB.Z);
            XYZ oC2 = new XYZ(oC.X, oC.Y, oC.Z);
            XYZ oD2 = new XYZ(oD.X, oD.Y, oD.Z);

            outerProfile.Append(Line.CreateBound(oA2, oB2));
            outerProfile.Append(Line.CreateBound(oB2, oC2));
            outerProfile.Append(Line.CreateBound(oC2, oD2));
            outerProfile.Append(Line.CreateBound(oD2, oA2));

            Solid outerBox = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { outerProfile },
                wallNormal,
                bd + ft
            );

            // 內部金字塔形切割：用 CreateBlendGeometry 或逐面構建
            // 簡化：用較小的矩形在 bevelDepth 位置建立，做布林減法
            CurveLoop innerProfile = new CurveLoop();
            innerProfile.Append(Line.CreateBound(iA, iB));
            innerProfile.Append(Line.CreateBound(iB, iC));
            innerProfile.Append(Line.CreateBound(iC, iD));
            innerProfile.Append(Line.CreateBound(iD, iA));

            Solid innerVoid = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { innerProfile },
                wallNormal,
                ft + 0.01 // 穿透整個厚度
            );

            // 布林減法：外框 - 內開口
            Solid result = BooleanOperationsUtils.ExecuteBooleanOperation(
                outerBox, innerVoid, BooleanOperationsType.Difference);

            return result;
        }

        /// <summary>
        /// 建立平面面板 Solid（簡單矩形截面沿法線擠出）
        /// </summary>
        private Solid CreateFlatPanelSolid(
            XYZ wallStart, XYZ wallDir, XYZ wallNormal,
            double posAlongMm, double posZMm, double widthMm, double heightMm,
            double thicknessMm, double offsetMm)
        {
            double w = widthMm / 304.8;
            double h = heightMm / 304.8;
            double t = thicknessMm / 304.8;
            double off = offsetMm / 304.8;
            double posA = posAlongMm / 304.8;
            double posZ = posZMm / 304.8;

            XYZ center = wallStart + wallDir * posA + wallNormal * off;
            XYZ p1 = center - wallDir * (w / 2) + new XYZ(0, 0, posZ);
            XYZ p2 = center + wallDir * (w / 2) + new XYZ(0, 0, posZ);
            XYZ p3 = center + wallDir * (w / 2) + new XYZ(0, 0, posZ + h);
            XYZ p4 = center - wallDir * (w / 2) + new XYZ(0, 0, posZ + h);

            CurveLoop profile = new CurveLoop();
            profile.Append(Line.CreateBound(p1, p2));
            profile.Append(Line.CreateBound(p2, p3));
            profile.Append(Line.CreateBound(p3, p4));
            profile.Append(Line.CreateBound(p4, p1));

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { profile }, wallNormal, t);
        }

        /// <summary>
        /// 建立傾斜平板 Solid（平面面板繞軸旋轉一定角度）
        /// tiltAxis: "horizontal"（繞水平軸前後傾斜）, "vertical"（繞垂直軸左右傾斜）
        /// </summary>
        private Solid CreateAngledPanelSolid(
            XYZ wallStart, XYZ wallDir, XYZ wallNormal,
            double posAlongMm, double posZMm, double widthMm, double heightMm,
            double thicknessMm, double offsetMm, double tiltAngleDeg, string tiltAxis)
        {
            double w = widthMm / 304.8;
            double h = heightMm / 304.8;
            double t = thicknessMm / 304.8;
            double off = offsetMm / 304.8;
            double posA = posAlongMm / 304.8;
            double posZ = posZMm / 304.8;
            double angleRad = tiltAngleDeg * Math.PI / 180.0;

            XYZ center = wallStart + wallDir * posA + wallNormal * off;

            // 面板四角（未傾斜時）
            XYZ p1 = new XYZ(-w / 2, 0, 0);       // 左下
            XYZ p2 = new XYZ(w / 2, 0, 0);         // 右下
            XYZ p3 = new XYZ(w / 2, 0, h);          // 右上
            XYZ p4 = new XYZ(-w / 2, 0, h);         // 左上

            // 套用傾斜
            if (tiltAxis == "horizontal")
            {
                // 繞水平軸（wallDir）旋轉：上邊前傾或後傾
                double dz = Math.Sin(angleRad) * h / 2;
                double dy = (1 - Math.Cos(angleRad)) * h / 2;
                p1 = new XYZ(p1.X, p1.Y + dy - Math.Sin(angleRad) * 0, p1.Z - dz);
                p2 = new XYZ(p2.X, p2.Y + dy - Math.Sin(angleRad) * 0, p2.Z - dz);
                p3 = new XYZ(p3.X, p3.Y - dy + Math.Sin(angleRad) * h, p3.Z + dz - h + h * Math.Cos(angleRad));
                p4 = new XYZ(p4.X, p4.Y - dy + Math.Sin(angleRad) * h, p4.Z + dz - h + h * Math.Cos(angleRad));

                // 簡化：直接偏移上下邊的 normal 方向
                double topOffset = Math.Tan(angleRad) * h / 2;
                p1 = new XYZ(-w / 2, -topOffset, 0);
                p2 = new XYZ(w / 2, -topOffset, 0);
                p3 = new XYZ(w / 2, topOffset, h);
                p4 = new XYZ(-w / 2, topOffset, h);
            }
            else // vertical
            {
                // 繞垂直軸旋轉：左右邊前後偏移
                double sideOffset = Math.Tan(angleRad) * w / 2;
                p1 = new XYZ(-w / 2, -sideOffset, 0);
                p2 = new XYZ(w / 2, sideOffset, 0);
                p3 = new XYZ(w / 2, sideOffset, h);
                p4 = new XYZ(-w / 2, -sideOffset, h);
            }

            // 轉換到世界座標
            Transform localToWorld = Transform.Identity;
            localToWorld.BasisX = wallDir;
            localToWorld.BasisY = wallNormal;
            localToWorld.BasisZ = XYZ.BasisZ;
            localToWorld.Origin = center + new XYZ(0, 0, posZ);

            XYZ wp1 = localToWorld.OfPoint(p1);
            XYZ wp2 = localToWorld.OfPoint(p2);
            XYZ wp3 = localToWorld.OfPoint(p3);
            XYZ wp4 = localToWorld.OfPoint(p4);

            // 建立前面
            CurveLoop frontProfile = new CurveLoop();
            frontProfile.Append(Line.CreateBound(wp1, wp2));
            frontProfile.Append(Line.CreateBound(wp2, wp3));
            frontProfile.Append(Line.CreateBound(wp3, wp4));
            frontProfile.Append(Line.CreateBound(wp4, wp1));

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { frontProfile }, wallNormal, t);
        }

        /// <summary>
        /// 建立圓角開口 Solid（厚牆上的圓角矩形開口）
        /// openingShape: "rounded_rect"（圓角矩形）, "arch"（上方圓拱）, "stadium"（上下半圓）
        /// </summary>
        private Solid CreateRoundedOpeningSolid(
            XYZ wallStart, XYZ wallDir, XYZ wallNormal,
            double posAlongMm, double posZMm, double frameWidthMm, double frameHeightMm,
            double openWidthMm, double openHeightMm, double depthMm, double thicknessMm,
            double cornerRadiusMm, string openingShape, double offsetMm)
        {
            double fw = frameWidthMm / 304.8;
            double fh = frameHeightMm / 304.8;
            double ow = openWidthMm / 304.8;
            double oh = openHeightMm / 304.8;
            double dep = depthMm / 304.8;
            double ft = thicknessMm / 304.8;
            double cr = cornerRadiusMm / 304.8;
            double off = offsetMm / 304.8;
            double posA = posAlongMm / 304.8;
            double posZ = posZMm / 304.8;

            // 確保圓角半徑不超過開口尺寸的一半
            cr = Math.Min(cr, Math.Min(ow / 2, oh / 2));

            XYZ center = wallStart + wallDir * posA + wallNormal * off;

            // 外框 solid（矩形截面，沿法線擠出）
            XYZ oA = center - wallDir * (fw / 2) + new XYZ(0, 0, posZ);
            XYZ oB = center + wallDir * (fw / 2) + new XYZ(0, 0, posZ);
            XYZ oC = center + wallDir * (fw / 2) + new XYZ(0, 0, posZ + fh);
            XYZ oD = center - wallDir * (fw / 2) + new XYZ(0, 0, posZ + fh);

            CurveLoop outerProfile = new CurveLoop();
            outerProfile.Append(Line.CreateBound(oA, oB));
            outerProfile.Append(Line.CreateBound(oB, oC));
            outerProfile.Append(Line.CreateBound(oC, oD));
            outerProfile.Append(Line.CreateBound(oD, oA));

            Solid outerBox = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { outerProfile }, wallNormal, dep + ft);

            // 內開口 solid（圓角矩形，用於布林減法）
            XYZ iCenter = center + wallNormal * ft; // 從表面厚度之後開始
            double iLeft = -ow / 2;
            double iRight = ow / 2;
            double iBottom = posZ + (fh - oh) / 2;
            double iTop = posZ + (fh + oh) / 2;

            CurveLoop innerProfile = CreateRoundedRectProfile(
                iCenter, wallDir, iLeft, iRight, iBottom, iTop, cr, openingShape);

            Solid innerVoid = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { innerProfile }, wallNormal, dep + 0.01);

            return BooleanOperationsUtils.ExecuteBooleanOperation(
                outerBox, innerVoid, BooleanOperationsType.Difference);
        }

        /// <summary>
        /// 建立圓角矩形 CurveLoop 輪廓
        /// </summary>
        private CurveLoop CreateRoundedRectProfile(
            XYZ center, XYZ wallDir,
            double left, double right, double bottom, double top,
            double radius, string shape)
        {
            CurveLoop loop = new CurveLoop();

            XYZ pBL = center + wallDir * left + new XYZ(0, 0, bottom);   // 左下
            XYZ pBR = center + wallDir * right + new XYZ(0, 0, bottom);  // 右下
            XYZ pTR = center + wallDir * right + new XYZ(0, 0, top);     // 右上
            XYZ pTL = center + wallDir * left + new XYZ(0, 0, top);      // 左上

            if (radius <= 0.001 || shape == "rect")
            {
                // 無圓角
                loop.Append(Line.CreateBound(pBL, pBR));
                loop.Append(Line.CreateBound(pBR, pTR));
                loop.Append(Line.CreateBound(pTR, pTL));
                loop.Append(Line.CreateBound(pTL, pBL));
                return loop;
            }

            double r = radius;

            if (shape == "arch")
            {
                // 上方圓拱：下方直角，上方半圓弧
                double archRadius = (right - left) / 2;
                double archCenterZ = top - archRadius;

                // 下左 → 下右
                loop.Append(Line.CreateBound(pBL, pBR));
                // 下右 → 右側拱起點
                XYZ archStartR = center + wallDir * right + new XYZ(0, 0, archCenterZ);
                loop.Append(Line.CreateBound(pBR, archStartR));
                // 右側 → 圓拱頂 → 左側
                XYZ archTop = center + new XYZ(0, 0, top);
                XYZ archStartL = center + wallDir * left + new XYZ(0, 0, archCenterZ);
                Arc arch = Arc.Create(archStartR, archStartL, archTop);
                loop.Append(arch);
                // 左側拱終點 → 下左
                loop.Append(Line.CreateBound(archStartL, pBL));
                return loop;
            }

            // rounded_rect / stadium：四角帶圓弧
            // 各角的圓弧中心
            XYZ cBL = center + wallDir * (left + r) + new XYZ(0, 0, bottom + r);
            XYZ cBR = center + wallDir * (right - r) + new XYZ(0, 0, bottom + r);
            XYZ cTR = center + wallDir * (right - r) + new XYZ(0, 0, top - r);
            XYZ cTL = center + wallDir * (left + r) + new XYZ(0, 0, top - r);

            // 底邊（左下角結束 → 右下角開始）
            XYZ bl_end = center + wallDir * (left + r) + new XYZ(0, 0, bottom);
            XYZ br_start = center + wallDir * (right - r) + new XYZ(0, 0, bottom);
            if (bl_end.DistanceTo(br_start) > 0.001)
                loop.Append(Line.CreateBound(bl_end, br_start));

            // 右下角圓弧
            XYZ br_end = center + wallDir * right + new XYZ(0, 0, bottom + r);
            XYZ br_mid = cBR + (wallDir * r + new XYZ(0, 0, -r)).Normalize() * r;
            Arc arcBR = Arc.Create(br_start, br_end, br_mid);
            loop.Append(arcBR);

            // 右邊
            XYZ tr_start = center + wallDir * right + new XYZ(0, 0, top - r);
            if (br_end.DistanceTo(tr_start) > 0.001)
                loop.Append(Line.CreateBound(br_end, tr_start));

            // 右上角圓弧
            XYZ tr_end = center + wallDir * (right - r) + new XYZ(0, 0, top);
            XYZ tr_mid = cTR + (wallDir * r + new XYZ(0, 0, r)).Normalize() * r;
            Arc arcTR = Arc.Create(tr_start, tr_end, tr_mid);
            loop.Append(arcTR);

            // 頂邊
            XYZ tl_start = center + wallDir * (left + r) + new XYZ(0, 0, top);
            if (tr_end.DistanceTo(tl_start) > 0.001)
                loop.Append(Line.CreateBound(tr_end, tl_start));

            // 左上角圓弧
            XYZ tl_end = center + wallDir * left + new XYZ(0, 0, top - r);
            XYZ tl_mid = cTL + (wallDir * (-r) + new XYZ(0, 0, r)).Normalize() * r;
            Arc arcTL = Arc.Create(tl_start, tl_end, tl_mid);
            loop.Append(arcTL);

            // 左邊
            XYZ bl_start = center + wallDir * left + new XYZ(0, 0, bottom + r);
            if (tl_end.DistanceTo(bl_start) > 0.001)
                loop.Append(Line.CreateBound(tl_end, bl_start));

            // 左下角圓弧
            XYZ bl_mid = cBL + (wallDir * (-r) + new XYZ(0, 0, -r)).Normalize() * r;
            Arc arcBL = Arc.Create(bl_start, bl_end, bl_mid);
            loop.Append(arcBL);

            return loop;
        }

        /// <summary>
        /// 為 DirectShape 套用材料覆寫
        /// </summary>
        private void ApplyMaterialOverride(Document doc, ElementId elementId, Material mat)
        {
            View activeView = doc.ActiveView;
            if (activeView == null) return;

            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternColor(mat.Color);

            FillPatternElement solidFill = FillPatternElement.GetFillPatternElementByName(
                doc, FillPatternTarget.Drafting, "<Solid fill>");
            if (solidFill == null)
            {
                solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
            }
            if (solidFill != null)
                ogs.SetSurfaceForegroundPatternId(solidFill.Id);

            activeView.SetElementOverrides(elementId, ogs);
        }

        /// <summary>
        /// 批次建立整面立面（根據 AI 分析結果）
        /// </summary>
        private object CreateFacadeFromAnalysis(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            // 解析參數
            int? wallId = parameters["wallId"]?.Value<int>();

            JObject facadeLayers = parameters["facadeLayers"] as JObject;
            if (facadeLayers == null)
                throw new Exception("請提供 facadeLayers 參數");

            JObject outerLayer = facadeLayers["outer"] as JObject;
            if (outerLayer == null)
                throw new Exception("請提供 facadeLayers.outer 參數");

            double globalOffset = outerLayer["offset"]?.Value<double>() ?? 200;
            double gap = outerLayer["gap"]?.Value<double>() ?? 20;
            double bandHeight = outerLayer["horizontalBandHeight"]?.Value<double>() ?? 0;
            double floorHeight = outerLayer["floorHeight"]?.Value<double>() ?? 3600;

            JArray panelTypesArray = outerLayer["panelTypes"] as JArray;
            JArray patternArray = outerLayer["pattern"] as JArray;

            if (panelTypesArray == null || panelTypesArray.Count == 0)
                throw new Exception("請提供至少一個 panelTypes");
            if (patternArray == null || patternArray.Count == 0)
                throw new Exception("請提供 pattern 排列矩陣");

            // 取得牆體
            Wall wall = null;
            if (wallId.HasValue)
            {
                wall = doc.GetElement(new ElementId(wallId.Value)) as Wall;
            }
            else
            {
                var selection = uidoc.Selection.GetElementIds();
                if (selection.Count > 0)
                    wall = doc.GetElement(selection.First()) as Wall;
            }

            if (wall == null)
                throw new Exception("找不到牆體，請指定 wallId 或選取牆體");

            // 取得牆的位置和方向
            LocationCurve wallLoc = wall.Location as LocationCurve;
            if (wallLoc == null)
                throw new Exception("無法取得牆體位置線");

            Line wallLine = wallLoc.Curve as Line;
            if (wallLine == null)
                throw new Exception("目前僅支援直線牆");

            XYZ wallDir = wallLine.Direction.Normalize();
            // 使用 wall.Orientation 取得外牆面法線（永遠指向室外）
            XYZ wallNormal = wall.Orientation.Normalize();
            // 將起始點從牆中心線偏移到外牆面（半個牆厚度）
            double halfWallThickness = wall.Width / 2.0; // 已經是 feet
            XYZ wallStart = wallLine.GetEndPoint(0) + wallNormal * halfWallThickness;
            double wallLength = wallLine.Length * 304.8; // ft → mm

            // 取得牆的基準高程（Level 高程 + Base Offset）
            Level baseLevel = doc.GetElement(wall.LevelId) as Level;
            double wallBaseZ = baseLevel != null ? baseLevel.Elevation : 0; // feet
            Parameter baseOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            double baseOffset = baseOffsetParam != null ? baseOffsetParam.AsDouble() : 0; // feet
            double wallBaseElevationMm = (wallBaseZ + baseOffset) * 304.8; // mm

            // 解析面板類型
            var typeDict = new Dictionary<string, JObject>();
            foreach (JObject ptObj in panelTypesArray)
            {
                string id = ptObj["id"]?.Value<string>();
                if (!string.IsNullOrEmpty(id))
                    typeDict[id] = ptObj;
            }

            // 開始建立
            int successCount = 0;
            int failCount = 0;
            var createdPanels = new List<object>();
            var failedPanels = new List<object>();

            using (Transaction trans = new Transaction(doc, "建立立面面板組"))
            {
                trans.Start();

                // 預先建立所有材料和 DirectShapeType
                var materialCache = new Dictionary<string, Material>();
                var dsTypeCache = new Dictionary<string, DirectShapeType>();
                foreach (var kvp in typeDict)
                {
                    string colorHex = kvp.Value["color"]?.Value<string>() ?? "#808080";
                    string userName = kvp.Value["name"]?.Value<string>() ?? $"FP_{kvp.Key}";
                    if (!materialCache.ContainsKey(kvp.Key))
                    {
                        materialCache[kvp.Key] = FindOrCreateFacadeMaterial(doc, colorHex, userName);
                    }

                    // 建立 DirectShapeType，命名規則: FP_{TypeId}_{名稱}
                    string dsTypeName = $"FP_{kvp.Key}_{userName}";
                    DirectShapeType existingType = new FilteredElementCollector(doc)
                        .OfClass(typeof(DirectShapeType))
                        .Cast<DirectShapeType>()
                        .FirstOrDefault(t => t.Name == dsTypeName);

                    if (existingType != null)
                    {
                        dsTypeCache[kvp.Key] = existingType;
                    }
                    else
                    {
                        DirectShapeType dsType = DirectShapeType.Create(
                            doc, dsTypeName,
                            new ElementId(BuiltInCategory.OST_GenericModel));
                        dsTypeCache[kvp.Key] = dsType;
                    }
                }

                // 取得實心填滿圖案
                FillPatternElement solidFill = FillPatternElement.GetFillPatternElementByName(
                    doc, FillPatternTarget.Drafting, "<Solid fill>");
                if (solidFill == null)
                {
                    solidFill = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
                }

                View activeView = doc.ActiveView;

                // 遍歷每一層
                for (int floor = 0; floor < patternArray.Count; floor++)
                {
                    string rowPattern = patternArray[floor]?.Value<string>() ?? "";
                    if (string.IsNullOrEmpty(rowPattern)) continue;

                    double panelH = floorHeight - bandHeight; // 面板高度
                    double zBase = wallBaseElevationMm + floor * floorHeight; // 此層底部 Z (mm)，加上牆基準高程

                    // 計算此列所有面板的總寬度（用於對齊）
                    double totalRowWidth = 0;
                    for (int c = 0; c < rowPattern.Length; c++)
                    {
                        string typeId = rowPattern[c].ToString();
                        if (typeDict.ContainsKey(typeId))
                        {
                            totalRowWidth += typeDict[typeId]["width"]?.Value<double>() ?? 800;
                            if (c < rowPattern.Length - 1) totalRowWidth += gap;
                        }
                    }

                    // 起始 X 位置（置中對齊）
                    double startX = (wallLength - totalRowWidth) / 2;
                    double x = startX;

                    for (int col = 0; col < rowPattern.Length; col++)
                    {
                        string typeId = rowPattern[col].ToString();
                        if (!typeDict.ContainsKey(typeId)) continue;

                        JObject pt = typeDict[typeId];
                        double pw = pt["width"]?.Value<double>() ?? 800;
                        double pd = pt["depth"]?.Value<double>() ?? 150;
                        double pThick = pt["thickness"]?.Value<double>() ?? 30;
                        string pCurve = pt["curveType"]?.Value<string>() ?? "concave";
                        string pColor = pt["color"]?.Value<string>() ?? "#808080";
                        string pName = pt["name"]?.Value<string>() ?? $"FP_{typeId}";
                        string pGeomType = pt["geometryType"]?.Value<string>() ?? "curved_panel";

                        // 各幾何類型專用參數
                        double pTiltAngle = pt["tiltAngle"]?.Value<double>() ?? 15;
                        string pTiltAxis = pt["tiltAxis"]?.Value<string>() ?? "horizontal";
                        double pCornerRadius = pt["cornerRadius"]?.Value<double>() ?? 100;
                        string pOpeningShape = pt["openingShape"]?.Value<string>() ?? "rounded_rect";
                        string pBevelDir = pt["bevelDirection"]?.Value<string>() ?? "center";
                        double pOpenW = pt["openingWidth"]?.Value<double>() ?? (pw * 0.7);
                        double pOpenH = pt["openingHeight"]?.Value<double>() ?? (panelH * 0.7);

                        try
                        {
                            double posAlongMm = x + pw / 2;

                            // 根據 geometryType 呼叫對應方法
                            Solid solid;
                            switch (pGeomType)
                            {
                                case "beveled_opening":
                                    solid = CreateBeveledOpeningSolid(
                                        wallStart, wallDir, wallNormal,
                                        posAlongMm, zBase, pw, panelH,
                                        pOpenW, pOpenH, pd, pThick,
                                        pBevelDir, globalOffset);
                                    break;

                                case "angled_panel":
                                    solid = CreateAngledPanelSolid(
                                        wallStart, wallDir, wallNormal,
                                        posAlongMm, zBase, pw, panelH,
                                        pThick, globalOffset, pTiltAngle, pTiltAxis);
                                    break;

                                case "rounded_opening":
                                    solid = CreateRoundedOpeningSolid(
                                        wallStart, wallDir, wallNormal,
                                        posAlongMm, zBase, pw, panelH,
                                        pOpenW, pOpenH, pd, pThick,
                                        pCornerRadius, pOpeningShape, globalOffset);
                                    break;

                                case "flat_panel":
                                    solid = CreateFlatPanelSolid(
                                        wallStart, wallDir, wallNormal,
                                        posAlongMm, zBase, pw, panelH,
                                        pThick, globalOffset);
                                    break;

                                case "curved_panel":
                                default:
                                    solid = CreateCurvedPanelSolid(
                                        wallStart, wallDir, wallNormal,
                                        posAlongMm, zBase, pw, panelH,
                                        pd, pThick, pCurve, globalOffset);
                                    break;
                            }

                            // DirectShape — 命名規則: FP_{TypeId}_F{樓層}_C{欄位}
                            string dsName = $"FP_{typeId}_F{floor + 1}_C{col + 1}";
                            DirectShape ds = DirectShape.CreateElement(
                                doc,
                                new ElementId(BuiltInCategory.OST_GenericModel)
                            );
                            ds.ApplicationId = "RevitMCP_FacadePanel";
                            ds.ApplicationDataId = dsName;
                            ds.SetShape(new GeometryObject[] { solid });

                            // 指定 DirectShapeType
                            if (dsTypeCache.ContainsKey(typeId))
                            {
                                ds.SetTypeId(dsTypeCache[typeId].Id);
                            }

                            // 材料覆寫
                            if (materialCache.ContainsKey(typeId))
                            {
                                ApplyMaterialOverride(doc, ds.Id, materialCache[typeId]);
                            }

                            createdPanels.Add(new
                            {
                                ElementId = ds.Id.IntegerValue,
                                Name = dsName,
                                Floor = floor + 1,
                                Column = col + 1,
                                TypeId = typeId
                            });

                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            failedPanels.Add(new
                            {
                                Floor = floor + 1,
                                Column = col + 1,
                                TypeId = typeId,
                                Reason = ex.Message
                            });
                            failCount++;
                        }

                        x += pw + gap;
                    }
                }

                // 建立水平分隔帶（如果有）
                if (bandHeight > 0)
                {
                    // 建立分隔帶 DirectShapeType
                    string bandTypeName = $"FP_Band_H{bandHeight}";
                    DirectShapeType bandType = new FilteredElementCollector(doc)
                        .OfClass(typeof(DirectShapeType))
                        .Cast<DirectShapeType>()
                        .FirstOrDefault(t => t.Name == bandTypeName);
                    if (bandType == null)
                    {
                        bandType = DirectShapeType.Create(
                            doc, bandTypeName,
                            new ElementId(BuiltInCategory.OST_GenericModel));
                    }

                    for (int floor = 0; floor < patternArray.Count; floor++)
                    {
                        double panelH = floorHeight - bandHeight;
                        double bandZ = (wallBaseElevationMm + floor * floorHeight + panelH) / 304.8;
                        double bh_ft = bandHeight / 304.8;
                        double bandThick = 50 / 304.8; // 分隔帶厚度 50mm

                        try
                        {
                            // 分隔帶為簡單矩形擠出
                            XYZ b1 = wallStart + wallNormal * (globalOffset / 304.8);
                            XYZ b2 = b1 + wallDir * (wallLength / 304.8);
                            XYZ b3 = b2 + wallNormal * bandThick;
                            XYZ b4 = b1 + wallNormal * bandThick;

                            b1 = new XYZ(b1.X, b1.Y, bandZ);
                            b2 = new XYZ(b2.X, b2.Y, bandZ);
                            b3 = new XYZ(b3.X, b3.Y, bandZ);
                            b4 = new XYZ(b4.X, b4.Y, bandZ);

                            CurveLoop bandProfile = new CurveLoop();
                            bandProfile.Append(Line.CreateBound(b1, b2));
                            bandProfile.Append(Line.CreateBound(b2, b3));
                            bandProfile.Append(Line.CreateBound(b3, b4));
                            bandProfile.Append(Line.CreateBound(b4, b1));

                            Solid bandSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                                new List<CurveLoop> { bandProfile },
                                XYZ.BasisZ,
                                bh_ft
                            );

                            DirectShape bandDs = DirectShape.CreateElement(
                                doc,
                                new ElementId(BuiltInCategory.OST_GenericModel)
                            );
                            bandDs.ApplicationId = "RevitMCP_FacadeBand";
                            bandDs.ApplicationDataId = $"FP_Band_F{floor + 1}";
                            bandDs.SetShape(new GeometryObject[] { bandSolid });
                            bandDs.SetTypeId(bandType.Id);
                        }
                        catch
                        {
                            // 分隔帶建立失敗不影響主流程
                        }
                    }
                }

                trans.Commit();
            }

            return new
            {
                Success = true,
                WallId = wall.Id.IntegerValue,
                TotalPanels = successCount + failCount,
                SuccessCount = successCount,
                FailCount = failCount,
                CreatedPanels = createdPanels,
                FailedPanels = failedPanels,
                Message = $"成功建立 {successCount} 片立面面板，失敗 {failCount} 片"
            };
        }

        /// <summary>
        /// 建立或取得立面面板材料
        /// </summary>
        private Material FindOrCreateFacadeMaterial(Document doc, string colorHex, string baseName)
        {
            colorHex = colorHex.TrimStart('#');
            byte r = Convert.ToByte(colorHex.Substring(0, 2), 16);
            byte g = Convert.ToByte(colorHex.Substring(2, 2), 16);
            byte b = Convert.ToByte(colorHex.Substring(4, 2), 16);

            string materialName = $"FP_MAT_{baseName}";

            Material material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name == materialName);

            if (material == null)
            {
                ElementId newMatId = Material.Create(doc, materialName);
                material = doc.GetElement(newMatId) as Material;
            }

            material.Color = new Color(r, g, b);
            material.Transparency = 0;

            return material;
        }

        /// <summary>
        /// 自動放置房間 - 使用 PlanTopology 偵測封閉空間並放置 Room
        /// </summary>
        private object AutoPlaceRooms(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            // 取得參數
            string levelName = parameters["levelName"]?.Value<string>();
            bool skipExisting = parameters["skipExisting"]?.Value<bool>() ?? true;
            double minAreaM2 = parameters["minArea"]?.Value<double>() ?? 0.5; // 最小面積（平方公尺），過濾極小空間

            // 決定目標樓層：優先用參數指定，否則從 active view 取
            Level targetLevel = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                targetLevel = FindLevel(doc, levelName, false);
            }
            else
            {
                // 從目前視圖取得樓層
                View activeView = uidoc.ActiveView;
                if (activeView is ViewPlan viewPlan)
                {
                    targetLevel = viewPlan.GenLevel;
                }

                if (targetLevel == null)
                {
                    throw new Exception("目前視圖不是平面圖，無法自動偵測樓層。請指定 levelName 參數或切換到平面圖視圖。");
                }
            }

            // 取得該樓層目前已有的 Room 數量
            var existingRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == targetLevel.Id && r.Area > 0)
                .ToList();

            int existingCount = existingRooms.Count;

            // 取得 PlanTopology
            PlanTopology planTopo;
            try
            {
                planTopo = doc.get_PlanTopology(targetLevel);
            }
            catch (Exception ex)
            {
                throw new Exception($"無法取得樓層 '{targetLevel.Name}' 的 PlanTopology: {ex.Message}");
            }

            // 收集所有封閉迴路資訊
            var circuits = new List<object>();
            var placedRooms = new List<object>();
            int skippedExisting = 0;
            int skippedTooSmall = 0;

            // 最小面積轉換：平方公尺 → 平方英尺
            double minAreaSqFt = minAreaM2 / 0.092903;

            using (Transaction trans = new Transaction(doc, "自動放置房間"))
            {
                trans.Start();

                try
                {
                    foreach (PlanCircuit circuit in planTopo.Circuits)
                    {
                        double areaSqFt = circuit.Area;
                        double areaM2 = areaSqFt * 0.092903;

                        // 檢查是否已有 Room
                        if (circuit.IsRoomLocated)
                        {
                            skippedExisting++;
                            if (skipExisting)
                            {
                                circuits.Add(new
                                {
                                    Area = Math.Round(areaM2, 2),
                                    Status = "已有房間",
                                    Skipped = true
                                });
                                continue;
                            }
                        }

                        // 過濾太小的空間
                        if (areaM2 < minAreaM2)
                        {
                            skippedTooSmall++;
                            circuits.Add(new
                            {
                                Area = Math.Round(areaM2, 2),
                                Status = "面積過小",
                                Skipped = true
                            });
                            continue;
                        }

                        // 放置 Room
                        Room newRoom = doc.Create.NewRoom(null, circuit);

                        if (newRoom != null)
                        {
                            // 取得房間位置
                            LocationPoint locPt = newRoom.Location as LocationPoint;
                            XYZ center = locPt?.Point ?? XYZ.Zero;

                            placedRooms.Add(new
                            {
                                ElementId = newRoom.Id.IntegerValue,
                                Number = newRoom.Number,
                                Area = Math.Round(areaM2, 2),
                                CenterX = Math.Round(center.X * 304.8, 2),
                                CenterY = Math.Round(center.Y * 304.8, 2)
                            });
                        }
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    throw new Exception($"放置房間時發生錯誤: {ex.Message}");
                }
            }

            return new
            {
                Level = targetLevel.Name,
                LevelId = targetLevel.Id.IntegerValue,
                ExistingRoomsBefore = existingCount,
                TotalCircuitsFound = planTopo.Circuits.Size,
                SkippedAlreadyHasRoom = skippedExisting,
                SkippedTooSmall = skippedTooSmall,
                RoomsPlaced = placedRooms.Count,
                PlacedRooms = placedRooms,
                MinAreaThreshold = minAreaM2
            };
        }

        #endregion

        #region 排煙窗檢討

        /// <summary>
        /// 從族群名稱推斷窗戶開啟方式與有效面積折減係數
        /// </summary>
        private (string operationType, double openingRatio, bool needsConfirm, string note) GetWindowOperationType(string familyName, string typeName)
        {
            string name = (familyName + " " + typeName).ToLower();

            // 固定窗 → 排煙無效
            if (ContainsAny(name, new[] { "fixed", "固定", "picture", "景觀", "fix" }))
                return ("fixed", 0, false, "固定窗：排煙有效面積為 0");

            // 全開型 → 1.0
            if (ContainsAny(name, new[] { "casement", "平開", "側開", "pivot", "樞軸", "中懸", "tilt", "內倒內開", "tiltturn" }))
                return ("casement", 1.0, false, null);

            // 半開型 → 0.5
            if (ContainsAny(name, new[] { "sliding", "橫拉", "推拉", "hung", "上下拉", "單拉", "double hung", "single hung", "doublehung", "singlehung" }))
                return ("sliding", 0.5, false, "橫拉/拉窗：有效面積折減 50%");

            // 外推型 → 0.5（保守）
            if (ContainsAny(name, new[] { "awning", "上懸", "外推", "hopper", "下懸", "projected" }))
                return ("projected", 0.5, false, "外推/懸窗：有效面積折減 50%（保守估計）");

            // 百葉 → 0.5
            if (ContainsAny(name, new[] { "louver", "百葉" }))
                return ("louver", 0.5, false, "百葉窗：有效面積折減 50%");

            // 無法判定
            return ("unknown", 0, true, "無法從族群名稱判定開啟方式，需人工確認");
        }

        private bool ContainsAny(string source, string[] keywords)
        {
            foreach (var kw in keywords)
            {
                if (source.Contains(kw)) return true;
            }
            return false;
        }

        /// <summary>
        /// 取得房間天花板高度（兩種方式）
        /// </summary>
        private double GetCeilingHeight(Document doc, Room room, string source)
        {
            const double FEET_TO_MM = 304.8;

            if (source == "ceiling_element")
            {
                // 方式 B：搜尋 Ceiling 元素
                var ceilings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType()
                    .ToList();

                Level roomLevel = doc.GetElement(room.LevelId) as Level;
                if (roomLevel == null) goto fallback;

                XYZ roomCenter = null;
                try
                {
                    // 取得房間中心點
                    BoundingBoxXYZ bb = room.get_BoundingBox(null);
                    if (bb != null)
                    {
                        roomCenter = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, bb.Min.Z);
                    }
                }
                catch { }

                foreach (var ceilingElem in ceilings)
                {
                    // 檢查天花板是否在同一樓層
                    Parameter levelParam = ceilingElem.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (levelParam == null) continue;
                    ElementId ceilingLevelId = levelParam.AsElementId();
                    if (ceilingLevelId != room.LevelId) continue;

                    // 取得天花板高度偏移
                    Parameter offsetParam = ceilingElem.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (offsetParam != null)
                    {
                        double offsetFeet = offsetParam.AsDouble();
                        // 如果有房間中心點，檢查天花板是否在房間範圍內
                        if (roomCenter != null)
                        {
                            BoundingBoxXYZ ceilingBB = ceilingElem.get_BoundingBox(null);
                            if (ceilingBB != null)
                            {
                                if (roomCenter.X >= ceilingBB.Min.X && roomCenter.X <= ceilingBB.Max.X &&
                                    roomCenter.Y >= ceilingBB.Min.Y && roomCenter.Y <= ceilingBB.Max.Y)
                                {
                                    return offsetFeet * FEET_TO_MM;
                                }
                            }
                        }
                        else
                        {
                            return offsetFeet * FEET_TO_MM;
                        }
                    }
                }
            }

            fallback:
            // 方式 A（預設）：讀 Room 的 Upper Limit + Limit Offset
            {
                Parameter upperLimitParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);
                Parameter upperOffsetParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);

                Level roomLevel = doc.GetElement(room.LevelId) as Level;
                double roomLevelElevation = roomLevel?.Elevation ?? 0;

                if (upperLimitParam != null && upperOffsetParam != null)
                {
                    ElementId upperLevelId = upperLimitParam.AsElementId();
                    double upperOffset = upperOffsetParam.AsDouble();

                    if (upperLevelId != ElementId.InvalidElementId)
                    {
                        Level upperLevel = doc.GetElement(upperLevelId) as Level;
                        if (upperLevel != null)
                        {
                            double height = (upperLevel.Elevation - roomLevelElevation + upperOffset) * FEET_TO_MM;
                            return height;
                        }
                    }

                    // 如果 Upper Level 就是自身樓層
                    return upperOffset * FEET_TO_MM;
                }

                // 最終 fallback：用 BoundingBox
                BoundingBoxXYZ bb = room.get_BoundingBox(null);
                if (bb != null)
                {
                    return (bb.Max.Z - bb.Min.Z) * FEET_TO_MM;
                }

                return 3000; // 預設 3m
            }
        }

        /// <summary>
        /// 排煙窗檢討（Step 2+5 合併）
        /// 檢查天花板下 80cm 內可開啟窗面積是否 ≥ 區劃面積 2%
        /// 法源：建技規§101① + 消防§188③⑦
        /// </summary>
        private object CheckSmokeExhaustWindows(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["levelName"]?.Value<string>();
            string ceilingHeightSource = parameters["ceilingHeightSource"]?.Value<string>() ?? "room_parameter";
            bool colorize = parameters["colorize"]?.Value<bool>() ?? true;
            double smokeZoneHeight = parameters["smokeZoneHeight"]?.Value<double>() ?? 800; // 預設 80cm

            // 非居室排除關鍵字（走廊、樓梯等非居室空間不需檢討排煙）
            string[] defaultExcludeKeywords = { "走廊", "corridor", "hall", "樓梯", "stair", "電梯", "elevator", "lift", "管道", "shaft", "機房", "mechanical", "廁所", "toilet", "restroom", "浴室", "bath", "玄關", "vestibule", "lobby", "陽台", "balcony" };
            var excludeParam = parameters["excludeKeywords"] as JArray;
            string[] excludeKeywords = excludeParam != null
                ? excludeParam.Select(t => t.Value<string>()).ToArray()
                : defaultExcludeKeywords;

            const double FEET_TO_MM = 304.8;
            const double SQ_FEET_TO_SQ_M = 0.092903;

            // 取得樓層
            Level level = FindLevel(doc, levelName, false);
            double levelElevation = level.Elevation;

            // 判定是否為地下室
            bool isBasement = levelElevation < 0;

            // 取得該樓層所有有面積的房間
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == level.Id && r.Area > 0)
                .ToList();

            var roomResults = new List<object>();
            int totalRooms = 0;
            int roomsChecked = 0;
            int roomsCompliant = 0;
            int roomsFailed = 0;
            int roomsSkipped = 0;
            int roomsSkippedNonResidential = 0;
            int roomsNeedConfirm = 0;

            // 收集所有需要上色的窗戶
            var colorizeList = new List<(int elementId, string type)>();
            var roomCeilingHeights = new List<double>(); // 收集天花板高度用於畫線
            var allWindowDetails = new List<(int id, double areaInZone, bool inZone, double width, double heightInZone)>(); // 所有窗戶的標註資料

            SpatialElementBoundaryOptions boundaryOptions = new SpatialElementBoundaryOptions();

            foreach (Room room in rooms)
            {
                totalRooms++;
                double roomAreaSqM = room.Area * SQ_FEET_TO_SQ_M;

                // 非居室排除
                string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                string roomNameLower = roomName.ToLower();
                bool isNonResidential = excludeKeywords.Any(kw => roomNameLower.Contains(kw.ToLower()));
                if (isNonResidential)
                {
                    roomsSkippedNonResidential++;
                    continue;
                }

                // 面積 ≤ 50m² 的房間不需檢討（建技規§1第35款第三目）
                if (roomAreaSqM <= 50)
                {
                    roomsSkipped++;
                    continue;
                }

                roomsChecked++;

                // 取得天花板高度
                double ceilingHeight = GetCeilingHeight(doc, room, ceilingHeightSource);
                roomCeilingHeights.Add(ceilingHeight);

                // 計算有效帶範圍（相對於樓層地板）
                double smokeZoneTop = ceilingHeight;
                double smokeZoneBottom = ceilingHeight - smokeZoneHeight;

                // 找出房間邊界牆上的所有窗戶
                var windowResults = new List<object>();
                var processedWindowIds = new HashSet<int>();
                bool hasConfirmNeeded = false;

                // 平行追蹤變數（避免 Cast<dynamic>()）
                double sumEffectiveArea = 0;
                int countInSmokeZone = 0;
                int countEffective = 0;
                int countNeedsConfirm = 0;
                // 追蹤固定窗與接近有效帶的窗戶（用於改善建議）
                int fixedWindowsInZoneCount = 0;
                double fixedWindowsInZonePotentialGain = 0;
                int nearZoneWindowsCount = 0;
                // 用於建議的詳細窗戶資訊
                var windowDetailsList = new List<(int id, string familyName, string typeName, string opType, double areaInZone, double headHeight, bool inZone, double width, double heightInZone)>();

                IList<IList<BoundarySegment>> segments = room.GetBoundarySegments(boundaryOptions);
                if (segments != null)
                {
                    foreach (IList<BoundarySegment> segmentList in segments)
                    {
                        foreach (BoundarySegment segment in segmentList)
                        {
                            Element element = doc.GetElement(segment.ElementId);
                            if (element is Wall wall)
                            {
                                IList<ElementId> insertIds = wall.FindInserts(true, false, false, false);
                                foreach (ElementId insertId in insertIds)
                                {
                                    if (processedWindowIds.Contains(insertId.IntegerValue)) continue;

                                    Element insert = doc.GetElement(insertId);
                                    if (insert is FamilyInstance fi &&
                                        fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Windows)
                                    {
                                        // 確認窗戶屬於此房間（使用與 GetRoomDaylightInfo 相同的邏輯）
                                        bool belongsToRoom = false;
                                        if (wall.Location is LocationCurve wallLocCurve && insert.Location is LocationPoint insertLoc)
                                        {
                                            Curve wallCurve = wallLocCurve.Curve;
                                            Curve segmentCurve = segment.GetCurve();
                                            IntersectionResult resStart = wallCurve.Project(segmentCurve.GetEndPoint(0));
                                            IntersectionResult resEnd = wallCurve.Project(segmentCurve.GetEndPoint(1));
                                            if (resStart != null && resEnd != null)
                                            {
                                                double tMin = Math.Min(resStart.Parameter, resEnd.Parameter);
                                                double tMax = Math.Max(resStart.Parameter, resEnd.Parameter);
                                                IntersectionResult resWindow = wallCurve.Project(insertLoc.Point);
                                                if (resWindow != null)
                                                {
                                                    double tWindow = resWindow.Parameter;
                                                    double tol = 500.0 / 304.8;
                                                    if (tWindow >= tMin - tol && tWindow <= tMax + tol)
                                                        belongsToRoom = true;
                                                }
                                            }
                                            else
                                            {
                                                if (fi.FromRoom != null && fi.FromRoom.Id == room.Id) belongsToRoom = true;
                                                else if (fi.ToRoom != null && fi.ToRoom.Id == room.Id) belongsToRoom = true;
                                            }
                                        }
                                        else
                                        {
                                            if (fi.FromRoom != null && fi.FromRoom.Id == room.Id) belongsToRoom = true;
                                            else if (fi.ToRoom != null && fi.ToRoom.Id == room.Id) belongsToRoom = true;
                                        }

                                        if (!belongsToRoom) continue;
                                        processedWindowIds.Add(insertId.IntegerValue);

                                        // 取得窗戶尺寸
                                        BuiltInParameter[] widthBips = { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH };
                                        string[] widthNames = { "粗略寬度", "寬度", "Width", "寬" };
                                        BuiltInParameter[] heightBips = { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT };
                                        string[] heightNames = { "粗略高度", "高度", "Height", "高" };
                                        BuiltInParameter[] sillBips = { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM };
                                        string[] sillNames = { "窗台高度", "Sill Height", "底高度", "窗臺高度" };
                                        BuiltInParameter[] headBips = { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM };
                                        string[] headNames = { "窗頂高度", "Head Height", "頂高度" };

                                        Element symbol = fi.Symbol;
                                        double? wValRaw = GetParamValue(fi, widthBips, widthNames);
                                        if (wValRaw == null || wValRaw == 0)
                                            wValRaw = GetParamValue(symbol, widthBips, widthNames);
                                        double wVal = wValRaw ?? 0;
                                        double width = wVal * FEET_TO_MM;

                                        double? hValRaw = GetParamValue(fi, heightBips, heightNames);
                                        if (hValRaw == null || hValRaw == 0)
                                            hValRaw = GetParamValue(symbol, heightBips, heightNames);
                                        double hVal = hValRaw ?? 0;
                                        double height = hVal * FEET_TO_MM;

                                        double sillHeightRaw = GetParamValue(fi, sillBips, sillNames) ?? 0;
                                        double sillHeight = sillHeightRaw * FEET_TO_MM;

                                        double headHeightRaw = GetParamValue(fi, headBips, headNames) ?? (sillHeightRaw + hVal);
                                        double headHeight = headHeightRaw * FEET_TO_MM;

                                        // 計算窗頂是否進入有效帶
                                        // 窗頂若超過天花板則截斷
                                        double headHeightCapped = Math.Min(headHeight, smokeZoneTop);
                                        bool isInSmokeZone = headHeightCapped > smokeZoneBottom;

                                        double heightInZone = 0;
                                        double areaInZone = 0;
                                        if (isInSmokeZone)
                                        {
                                            double effectiveBottom = Math.Max(sillHeight, smokeZoneBottom);
                                            heightInZone = headHeightCapped - effectiveBottom;
                                            if (heightInZone < 0) heightInZone = 0;
                                            areaInZone = (width / 1000.0) * (heightInZone / 1000.0); // 轉 m²
                                        }

                                        // 從族群名稱判定開啟方式
                                        var (operationType, openingRatio, needsConfirm, note) =
                                            GetWindowOperationType(fi.Symbol.FamilyName, fi.Symbol.Name);

                                        if (needsConfirm) hasConfirmNeeded = true;

                                        double effectiveArea = areaInZone * openingRatio;

                                        // 收集上色資訊
                                        if (colorize)
                                        {
                                            colorizeList.Add((insertId.IntegerValue, operationType));
                                        }

                                        windowResults.Add(new
                                        {
                                            WindowId = insertId.IntegerValue,
                                            FamilyName = fi.Symbol.FamilyName,
                                            TypeName = fi.Symbol.Name,
                                            Width = Math.Round(width, 1),
                                            Height = Math.Round(height, 1),
                                            SillHeight = Math.Round(sillHeight, 1),
                                            HeadHeight = Math.Round(headHeight, 1),
                                            HeadHeightCapped = Math.Round(headHeightCapped, 1),
                                            IsInSmokeZone = isInSmokeZone,
                                            HeightInZone = Math.Round(heightInZone, 1),
                                            AreaInZone = Math.Round(areaInZone, 4),
                                            OperationType = operationType,
                                            OperationSource = "familyName",
                                            OpeningRatio = openingRatio,
                                            EffectiveArea = Math.Round(effectiveArea, 4),
                                            NeedsManualConfirm = needsConfirm,
                                            Note = note,
                                            HostWallId = wall.Id.IntegerValue
                                        });

                                        // 更新平行追蹤變數
                                        sumEffectiveArea += Math.Round(effectiveArea, 4);
                                        if (isInSmokeZone) countInSmokeZone++;
                                        if (Math.Round(effectiveArea, 4) > 0) countEffective++;
                                        if (needsConfirm) countNeedsConfirm++;
                                        // 固定窗（用於改善建議 A）
                                        if (operationType == "fixed" && Math.Round(areaInZone, 4) > 0)
                                        {
                                            fixedWindowsInZoneCount++;
                                            fixedWindowsInZonePotentialGain += Math.Round(areaInZone, 4);
                                        }
                                        // 接近有效帶的窗（用於改善建議 B）
                                        if (!isInSmokeZone && headHeight > smokeZoneBottom - 300)
                                        {
                                            nearZoneWindowsCount++;
                                        }
                                        // 記錄詳細資訊供建議使用
                                        windowDetailsList.Add((insertId.IntegerValue, fi.Symbol.FamilyName, fi.Symbol.Name, operationType, Math.Round(areaInZone, 4), headHeight, isInSmokeZone, width, heightInZone));
                                        allWindowDetails.Add((insertId.IntegerValue, Math.Round(areaInZone, 4), isInSmokeZone, width, heightInZone));
                                    }
                                }
                            }
                        }
                    }
                }

                // 計算合規性（使用平行追蹤變數，避免 Cast<dynamic>()）
                double totalEffectiveArea = sumEffectiveArea;
                double requiredArea = roomAreaSqM * 0.02;
                double ratio = roomAreaSqM > 0 ? totalEffectiveArea / roomAreaSqM : 0;
                bool isCompliant = totalEffectiveArea >= requiredArea;
                double deficit = isCompliant ? 0 : requiredArea - totalEffectiveArea;

                // 無窗居室判定（建技規§1第35款第三目）
                bool isWindowlessRoom = totalEffectiveArea < requiredArea;

                // 防煙區劃警告（§101① / §188①）
                bool exceedsCompartment = roomAreaSqM > 500;

                // 改善建議（具體到每扇窗）
                var recommendations = new List<object>();
                if (!isCompliant)
                {
                    double remainingDeficit = deficit;

                    // 建議 A：固定窗改為可開啟窗
                    if (fixedWindowsInZoneCount > 0)
                    {
                        // 查詢專案中可用的 Casement 窗型
                        var availableCasementTypes = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Windows)
                            .WhereElementIsElementType()
                            .Cast<FamilySymbol>()
                            .Where(fs => ContainsAny((fs.FamilyName + " " + fs.Name).ToLower(),
                                new[] { "casement", "平開", "側開", "pivot", "樞軸" }))
                            .Select(fs => new { TypeName = fs.FamilyName + ": " + fs.Name, TypeId = fs.Id.IntegerValue })
                            .Take(5)
                            .ToList();

                        // 列出每扇固定窗的具體資訊
                        var fixedWindowDetails = windowDetailsList
                            .Where(w => w.opType == "fixed" && w.areaInZone > 0)
                            .Select(w => new
                            {
                                WindowId = w.id,
                                CurrentType = w.familyName + ": " + w.typeName,
                                AreaInZone = Math.Round(w.areaInZone, 4),
                                PotentialGain = Math.Round(w.areaInZone, 4) // 從 0 變 1.0
                            })
                            .ToList();

                        double totalPotentialGain = fixedWindowsInZonePotentialGain;
                        remainingDeficit -= totalPotentialGain;

                        recommendations.Add(new
                        {
                            Type = "A",
                            Action = "將固定窗改為可開啟窗",
                            PotentialGain = Math.Round(totalPotentialGain, 2),
                            CanSolve = totalPotentialGain >= deficit,
                            FixedWindows = fixedWindowDetails,
                            AvailableCasementTypes = availableCasementTypes,
                            Note = $"將 {fixedWindowsInZoneCount} 扇固定窗改為 Casement，可補足 +{Math.Round(totalPotentialGain, 2)} m²"
                        });
                    }

                    // 建議 B：接近有效帶的窗戶上移/加高
                    if (nearZoneWindowsCount > 0)
                    {
                        var nearWindowDetails = windowDetailsList
                            .Where(w => !w.inZone && w.headHeight > smokeZoneBottom - 300)
                            .Select(w => new
                            {
                                WindowId = w.id,
                                CurrentType = w.familyName + ": " + w.typeName,
                                HeadHeight = Math.Round(w.headHeight, 1),
                                SmokeZoneBottom = Math.Round(smokeZoneBottom, 1),
                                GapToZone = Math.Round(smokeZoneBottom - w.headHeight, 1),
                                SuggestAction = $"上移 {Math.Round(smokeZoneBottom - w.headHeight, 0)}mm 即可進入有效帶"
                            })
                            .ToList();

                        recommendations.Add(new
                        {
                            Type = "B",
                            Action = "窗戶上移或加高進入有效帶",
                            WindowCount = nearZoneWindowsCount,
                            NearWindows = nearWindowDetails,
                            Note = $"有 {nearZoneWindowsCount} 扇窗接近有效帶（差 30cm 內），上移即可計入排煙面積"
                        });
                    }

                    // 建議 C：新增窗戶
                    if (remainingDeficit > 0 && remainingDeficit <= deficit)
                    {
                        // 查詢專案中所有可開啟窗型及其尺寸
                        var availableTypes = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Windows)
                            .WhereElementIsElementType()
                            .Cast<FamilySymbol>()
                            .Where(fs =>
                            {
                                var (opType2, ratio2, _, _) = GetWindowOperationType(fs.FamilyName, fs.Name);
                                return ratio2 > 0;
                            })
                            .Select(fs =>
                            {
                                double? tw = GetParamValue(fs, new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH }, new[] { "Width", "寬度" });
                                double? th = GetParamValue(fs, new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT }, new[] { "Height", "高度" });
                                double wMm = (tw ?? 0) * FEET_TO_MM;
                                double hMm = (th ?? 0) * FEET_TO_MM;
                                var (_, ratioC, _, _) = GetWindowOperationType(fs.FamilyName, fs.Name);
                                return new
                                {
                                    TypeName = fs.FamilyName + ": " + fs.Name,
                                    TypeId = fs.Id.IntegerValue,
                                    Width = Math.Round(wMm, 0),
                                    Height = Math.Round(hMm, 0),
                                    OpeningRatio = ratioC,
                                    EffectiveAreaPerWindow = Math.Round((wMm / 1000.0) * Math.Min(hMm, smokeZoneHeight) / 1000.0 * ratioC, 4)
                                };
                            })
                            .Where(t => t.EffectiveAreaPerWindow > 0)
                            .OrderByDescending(t => t.EffectiveAreaPerWindow)
                            .Take(5)
                            .ToList();

                        recommendations.Add(new
                        {
                            Type = "C",
                            Action = "新增可開啟窗",
                            RemainingDeficit = Math.Round(Math.Max(remainingDeficit, 0), 2),
                            AvailableWindowTypes = availableTypes,
                            Note = $"於天花板下 80cm 範圍內的外牆空白段新增可開啟窗，需補足 {Math.Round(Math.Max(remainingDeficit, 0), 2)} m²"
                        });
                    }

                    // 建議 D：改採機械排煙
                    if (deficit > totalEffectiveArea * 2 || totalEffectiveArea == 0)
                    {
                        recommendations.Add(new
                        {
                            Type = "D",
                            Action = "改採機械排煙",
                            Deficit = Math.Round(deficit, 2),
                            Note = "缺口過大或完全無排煙窗，建議改採機械排煙（排煙風機 + 風管）"
                        });
                    }
                }

                if (hasConfirmNeeded) roomsNeedConfirm++;
                if (isCompliant) roomsCompliant++;
                else roomsFailed++;

                // roomName 已在迴圈開頭取得

                roomResults.Add(new
                {
                    RoomId = room.Id.IntegerValue,
                    RoomName = roomName,
                    RoomNumber = room.Number,
                    RoomArea = Math.Round(roomAreaSqM, 2),
                    IsBasement = isBasement,
                    CeilingHeight = Math.Round(ceilingHeight, 1),
                    CeilingHeightSource = ceilingHeightSource,
                    SmokeZoneTop = Math.Round(smokeZoneTop, 1),
                    SmokeZoneBottom = Math.Round(smokeZoneBottom, 1),
                    ExceedsCompartmentThreshold = exceedsCompartment,
                    CompartmentNote = exceedsCompartment ? "房間面積 > 500 m²，須以防煙壁分割為多個區劃" : null,
                    Windows = windowResults,
                    Summary = new
                    {
                        TotalWindowsChecked = windowResults.Count,
                        WindowsInSmokeZone = countInSmokeZone,
                        WindowsEffective = countEffective,
                        NeedsConfirmCount = countNeedsConfirm,
                        TotalEffectiveArea = Math.Round(totalEffectiveArea, 4),
                        RequiredArea = Math.Round(requiredArea, 4),
                        Ratio = Math.Round(ratio, 4),
                        RequiredRatio = 0.02,
                        Deficit = Math.Round(deficit, 4),
                        IsWindowlessRoom = isWindowlessRoom,
                        IsCompliant = isCompliant,
                        Result = isCompliant ? "PASS" : "FAIL",
                        Recommendations = recommendations
                    }
                });
            }

            // 收集所有房間的天花板高度（用於畫線）
            var uniqueCeilingHeights = new HashSet<double>();
            foreach (var rd in roomCeilingHeights)
            {
                uniqueCeilingHeights.Add(rd);
            }

            // 執行視覺化：四向立面複製 + 上色 + 標註
            var createdViewIds = new List<object>();
            int? firstCreatedViewId = null;
            if (colorize)
            {
                ElementId solidPatternId = GetSolidFillPatternId(doc);

                // 找出所有立面視圖
                var elevationViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Elevation && !v.IsTemplate && v.CanBePrinted)
                    .ToList();

                if (elevationViews.Count == 0)
                {
                    // fallback：找剖面或 3D
                    elevationViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => (v.ViewType == ViewType.Section || v.ViewType == ViewType.ThreeD) &&
                                    !v.IsTemplate && v.CanBePrinted)
                        .Take(1)
                        .ToList();
                }

                // 取得文字類型
                TextNoteType textType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault();

                using (Transaction trans = new Transaction(doc, "排煙檢討四向立面"))
                {
                    trans.Start();

                    foreach (View sourceView in elevationViews)
                    {
                        // 複製視圖
                        ElementId newViewId;
                        try
                        {
                            newViewId = sourceView.Duplicate(ViewDuplicateOption.Duplicate);
                        }
                        catch { continue; }

                        View newView = doc.GetElement(newViewId) as View;
                        string timestamp = DateTime.Now.ToString("MMdd_HHmm");
                        try { newView.Name = $"排煙檢討_{sourceView.Name}_{level.Name}_{timestamp}"; }
                        catch { newView.Name = $"排煙檢討_{newViewId.IntegerValue}_{timestamp}"; }

                        // 1. 上色窗戶
                        foreach (var (elemId, opType) in colorizeList)
                        {
                            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                            Color color;
                            switch (opType)
                            {
                                case "casement": case "pivot": color = new Color(0, 180, 0); break;
                                case "sliding": case "projected": case "louver": color = new Color(255, 200, 0); break;
                                case "fixed": color = new Color(255, 50, 50); break;
                                default: color = new Color(180, 180, 180); break;
                            }
                            ogs.SetSurfaceForegroundPatternColor(color);
                            if (solidPatternId != ElementId.InvalidElementId)
                            {
                                ogs.SetSurfaceForegroundPatternId(solidPatternId);
                                ogs.SetSurfaceForegroundPatternVisible(true);
                            }
                            ogs.SetProjectionLineColor(color);
                            newView.SetElementOverrides(new ElementId(elemId), ogs);
                        }

                        // 2. 繪製天花板線和有效帶線
                        BoundingBoxXYZ cb = newView.CropBox;
                        if (cb != null)
                        {
                            Transform t = cb.Transform;
                            double leftX = cb.Min.X - 1.0;  // 左右多延伸 1ft
                            double rightX = cb.Max.X + 1.0;

                            foreach (double ceilingMM in uniqueCeilingHeights)
                            {
                                double ceilingFeet = ceilingMM / FEET_TO_MM;
                                double smokeBottomFeet = (ceilingMM - smokeZoneHeight) / FEET_TO_MM;
                                double modelZ_ceiling = levelElevation + ceilingFeet;
                                double modelZ_smokeBottom = levelElevation + smokeBottomFeet;

                                // 將模型 Z 座標轉為視圖局部 Y 座標
                                double localY_ceiling, localY_smokeBottom;
                                if (Math.Abs(t.BasisY.Z) > 0.001)
                                {
                                    localY_ceiling = (modelZ_ceiling - t.Origin.Z) / t.BasisY.Z;
                                    localY_smokeBottom = (modelZ_smokeBottom - t.Origin.Z) / t.BasisY.Z;
                                }
                                else continue;

                                // 天花板線（紅色）
                                try
                                {
                                    XYZ ceilStart = t.OfPoint(new XYZ(leftX, localY_ceiling, 0));
                                    XYZ ceilEnd = t.OfPoint(new XYZ(rightX, localY_ceiling, 0));
                                    if (ceilStart.DistanceTo(ceilEnd) > 0.01)
                                    {
                                        DetailCurve ceilDC = doc.Create.NewDetailCurve(newView,
                                            Line.CreateBound(ceilStart, ceilEnd));
                                        OverrideGraphicSettings lineOgs = new OverrideGraphicSettings();
                                        lineOgs.SetProjectionLineColor(new Color(255, 0, 0));
                                        newView.SetElementOverrides(ceilDC.Id, lineOgs);
                                    }
                                }
                                catch { }

                                // 有效帶下緣線（綠色）
                                try
                                {
                                    XYZ smokeStart = t.OfPoint(new XYZ(leftX, localY_smokeBottom, 0));
                                    XYZ smokeEnd = t.OfPoint(new XYZ(rightX, localY_smokeBottom, 0));
                                    if (smokeStart.DistanceTo(smokeEnd) > 0.01)
                                    {
                                        DetailCurve smokeDC = doc.Create.NewDetailCurve(newView,
                                            Line.CreateBound(smokeStart, smokeEnd));
                                        OverrideGraphicSettings lineOgs = new OverrideGraphicSettings();
                                        lineOgs.SetProjectionLineColor(new Color(0, 180, 0));
                                        newView.SetElementOverrides(smokeDC.Id, lineOgs);
                                    }
                                }
                                catch { }

                                // 3. 標註文字：右側標示天花板高度和有效帶
                                if (textType != null)
                                {
                                    try
                                    {
                                        double textLocalY = (localY_ceiling + localY_smokeBottom) / 2.0;
                                        XYZ textPos = t.OfPoint(new XYZ(rightX + 0.5, textLocalY, 0));
                                        TextNoteOptions opts = new TextNoteOptions { TypeId = textType.Id };
                                        string annotText = $"天花板 H={ceilingMM}mm\n" +
                                                          $"↕ 有效帶 {smokeZoneHeight}mm\n" +
                                                          $"下緣 H-{smokeZoneHeight}={ceilingMM - smokeZoneHeight}mm";
                                        TextNote.Create(doc, newView.Id, textPos, annotText, opts);
                                    }
                                    catch { }
                                }
                            }

                            // 4. 窗戶標註：帶內寬×高
                            XYZ viewDir = t.BasisZ; // 視圖看向的方向
                            foreach (var wd in allWindowDetails)
                            {
                                if (!wd.inZone || wd.areaInZone <= 0) continue;

                                Element winElem = doc.GetElement(new ElementId(wd.id));
                                if (winElem == null) continue;

                                FamilyInstance winFI = winElem as FamilyInstance;
                                if (winFI == null) continue;

                                // 檢查窗戶的宿主牆是否面向此立面視圖
                                Wall hostWall = winFI.Host as Wall;
                                if (hostWall == null) continue;

                                LocationCurve wallLoc = hostWall.Location as LocationCurve;
                                if (wallLoc == null) continue;

                                XYZ wallDirection = (wallLoc.Curve.GetEndPoint(1) - wallLoc.Curve.GetEndPoint(0)).Normalize();
                                XYZ wallNormal = wallDirection.CrossProduct(XYZ.BasisZ).Normalize();

                                // 牆法線與視圖方向的點積 > 0.7 → 窗戶面對此視圖
                                double dot = Math.Abs(wallNormal.DotProduct(viewDir));
                                if (dot < 0.5) continue;

                                // 取得窗戶位置
                                LocationPoint winLoc = winElem.Location as LocationPoint;
                                if (winLoc == null) continue;

                                // 投影到視圖平面
                                XYZ winModelPos = winLoc.Point;
                                double depth = (winModelPos - t.Origin).DotProduct(t.BasisZ);
                                XYZ projected = winModelPos - depth * t.BasisZ;

                                // 標註在窗戶下方
                                XYZ textOffset = new XYZ(0, 0, -0.5); // 下方 0.5ft
                                XYZ textPos = projected + textOffset;

                                if (textType != null)
                                {
                                    try
                                    {
                                        TextNoteOptions opts = new TextNoteOptions
                                        {
                                            TypeId = textType.Id,
                                            HorizontalAlignment = HorizontalTextAlignment.Center
                                        };
                                        // 窗寬 × 帶內高（有效面積）
                                        double winWidthMM = wd.width;
                                        double heightInZoneMM = wd.heightInZone;
                                        string winText = $"帶內 {winWidthMM:F0}×{heightInZoneMM:F0}mm\n" +
                                                        $"={wd.areaInZone:F3}m²";
                                        TextNote.Create(doc, newView.Id, textPos, winText, opts);
                                    }
                                    catch { }
                                }
                            }
                        }

                        if (firstCreatedViewId == null) firstCreatedViewId = newViewId.IntegerValue;

                        createdViewIds.Add(new
                        {
                            ViewId = newViewId.IntegerValue,
                            ViewName = newView.Name,
                            SourceView = sourceView.Name
                        });
                    }

                    trans.Commit();
                }

                // 切換到第一個新建的立面
                if (firstCreatedViewId != null)
                {
                    _uiApp.ActiveUIDocument.ActiveView = doc.GetElement(new ElementId(firstCreatedViewId.Value)) as View;
                }
            }

            return new
            {
                LevelName = level.Name,
                LevelElevation = Math.Round(levelElevation * FEET_TO_MM, 1),
                IsBasement = isBasement,
                CeilingHeightSource = ceilingHeightSource,
                SmokeZoneHeight = smokeZoneHeight,
                AnnotatedViews = createdViewIds,
                LegalBasis = new
                {
                    WindowlessRoom = "建技規§1第35款第三目 + §100②：>50m²居室，天花板下80cm通風面積<2%",
                    SmokeExhaust = "建技規§101① + 消防§188③⑦：排煙口面積≥防煙區劃2%，設於天花板下80cm內",
                    Compartment = "建技規§101① + 消防§188①：每500m²以防煙壁區劃",
                    HorizontalDistance = "消防§188③：任一位置至排煙口水平距離≤30m"
                },
                Rooms = roomResults,
                LevelSummary = new
                {
                    TotalRooms = totalRooms,
                    RoomsSkippedNonResidential = roomsSkippedNonResidential,
                    NonResidentialReason = "非居室空間（走廊、樓梯等）免檢討",
                    RoomsSkippedSmall = roomsSkipped,
                    SmallRoomReason = "面積 ≤ 50 m² 免檢討",
                    RoomsChecked = roomsChecked,
                    RoomsCompliant = roomsCompliant,
                    RoomsFailed = roomsFailed,
                    RoomsNeedConfirm = roomsNeedConfirm
                }
            };
        }

        /// <summary>
        /// 無開口樓層判定（Step 1）
        /// 法源：消防設置標準§4 + §28③
        /// </summary>
        private object CheckFloorEffectiveOpenings(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["levelName"]?.Value<string>();
            bool colorize = parameters["colorize"]?.Value<bool>() ?? true;

            const double FEET_TO_MM = 304.8;
            const double SQ_FEET_TO_SQ_M = 0.092903;

            Level level = FindLevel(doc, levelName, false);
            double levelElevation = level.Elevation;

            // 判定樓層數（十層以上或以下有不同標準）
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            int floorNumber = allLevels.IndexOf(allLevels.First(l => l.Id == level.Id)) + 1;
            bool isAbove10F = floorNumber > 10;

            // 取得該樓層總地板面積
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == level.Id && r.Area > 0)
                .ToList();
            double totalFloorArea = rooms.Sum(r => r.Area * SQ_FEET_TO_SQ_M);

            // 找出該樓層所有外牆
            var exteriorWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .Where(w => w.WallType.Function == WallFunction.Exterior)
                .Where(w =>
                {
                    Parameter levelParam = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                    return levelParam != null && levelParam.AsElementId() == level.Id;
                })
                .ToList();

            var openingResults = new List<object>();
            var colorizeList = new List<(int elementId, bool isEffective)>();
            int largeOpeningCount = 0; // 十層以下：≥1m 圓 或 75cm×120cm 的開口數

            // 平行追蹤變數（避免 Cast<dynamic>()）
            double sumOpeningEffectiveArea = 0;
            int countOpeningEffective = 0;
            int countOpeningNeedsConfirm = 0;

            foreach (Wall wall in exteriorWalls)
            {
                IList<ElementId> insertIds = wall.FindInserts(true, false, false, false);
                foreach (ElementId insertId in insertIds)
                {
                    Element insert = doc.GetElement(insertId);
                    if (insert is FamilyInstance fi &&
                        (fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Windows ||
                         fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Doors))
                    {
                        Element symbol = fi.Symbol;

                        BuiltInParameter[] widthBips = { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH };
                        string[] widthNames = { "粗略寬度", "寬度", "Width", "寬" };
                        BuiltInParameter[] heightBips = { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT };
                        string[] heightNames = { "粗略高度", "高度", "Height", "高" };
                        BuiltInParameter[] sillBips = { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM };
                        string[] sillNames = { "窗台高度", "Sill Height", "底高度", "窗臺高度" };

                        double? wValRaw2 = GetParamValue(fi, widthBips, widthNames);
                        if (wValRaw2 == null || wValRaw2 == 0)
                            wValRaw2 = GetParamValue(symbol, widthBips, widthNames);
                        double wVal = wValRaw2 ?? 0;
                        double width = wVal * FEET_TO_MM;

                        double? hValRaw2 = GetParamValue(fi, heightBips, heightNames);
                        if (hValRaw2 == null || hValRaw2 == 0)
                            hValRaw2 = GetParamValue(symbol, heightBips, heightNames);
                        double hVal = hValRaw2 ?? 0;
                        double height = hVal * FEET_TO_MM;

                        double sillHeightRaw = GetParamValue(fi, sillBips, sillNames) ?? 0;
                        double sillHeight = sillHeightRaw * FEET_TO_MM;

                        // 可內接圓直徑 = min(寬, 高)
                        double minDimension = Math.Min(width, height);

                        // 判定條件
                        bool isValidSize = minDimension >= 500; // 可容納直徑 50cm 圓
                        bool isValidHeight = sillHeight <= 1200; // 下緣 ≤ 1.2m

                        // 開啟方式判定
                        var (operationType, _, needsConfirm, note) =
                            GetWindowOperationType(fi.Symbol.FamilyName, fi.Symbol.Name);

                        // Step 1 的判定：固定窗也算有效（可破壞），但加註
                        bool isOpenable = operationType != "unknown";
                        string confirmNote = null;
                        bool needsManualConfirm = false;

                        if (operationType == "fixed")
                        {
                            isOpenable = true; // 固定窗可破壞進入
                            needsManualConfirm = true;
                            confirmNote = "固定窗：需確認為普通玻璃（厚度≤6mm，非強化/膠合），且無鐵窗";
                        }
                        else if (operationType == "unknown")
                        {
                            isOpenable = false;
                            needsManualConfirm = true;
                            confirmNote = "無法判定開啟方式，需人工確認";
                        }

                        bool isEffective = isValidSize && isValidHeight && isOpenable;
                        double openingArea = isEffective ? (width / 1000.0) * (height / 1000.0) : 0;

                        // 十層以下加嚴：檢查是否為大開口
                        bool isLargeOpening = false;
                        if (!isAbove10F && isEffective)
                        {
                            // 直徑 ≥ 1m 或 寬 ≥ 75cm × 高 ≥ 120cm
                            if (minDimension >= 1000 || (width >= 750 && height >= 1200))
                            {
                                isLargeOpening = true;
                                largeOpeningCount++;
                            }
                        }

                        if (colorize)
                        {
                            colorizeList.Add((insertId.IntegerValue, isEffective));
                        }

                        openingResults.Add(new
                        {
                            ElementId = insertId.IntegerValue,
                            Category = fi.Category.Name,
                            FamilyName = fi.Symbol.FamilyName,
                            TypeName = fi.Symbol.Name,
                            Width = Math.Round(width, 1),
                            Height = Math.Round(height, 1),
                            SillHeight = Math.Round(sillHeight, 1),
                            MinInscribedCircleDiameter = Math.Round(minDimension, 1),
                            IsValidSize = isValidSize,
                            IsValidHeight = isValidHeight,
                            OperationType = operationType,
                            IsOpenable = isOpenable,
                            IsEffective = isEffective,
                            IsLargeOpening = isLargeOpening,
                            EffectiveArea = Math.Round(openingArea, 4),
                            NeedsManualConfirm = needsManualConfirm,
                            ConfirmNote = confirmNote,
                            HostWallId = wall.Id.IntegerValue
                        });

                        // 更新平行追蹤變數
                        sumOpeningEffectiveArea += Math.Round(openingArea, 4);
                        if (isEffective) countOpeningEffective++;
                        if (needsManualConfirm) countOpeningNeedsConfirm++;
                    }
                }
            }

            // 計算總有效開口面積（使用平行追蹤變數，避免 Cast<dynamic>()）
            double totalEffectiveArea = sumOpeningEffectiveArea;
            double threshold_1_30 = totalFloorArea / 30.0;
            double ratio = totalFloorArea > 0 ? totalEffectiveArea / totalFloorArea : 0;
            bool isNoOpeningFloor = totalEffectiveArea < threshold_1_30;

            // 十層以下加嚴檢查
            bool meetsLargeOpeningReq = isAbove10F || largeOpeningCount >= 2;

            // 最終判定
            bool finalIsNoOpening = isNoOpeningFloor || (!isAbove10F && !meetsLargeOpeningReq);

            // 後果判定
            var consequences = new List<string>();
            if (finalIsNoOpening)
            {
                consequences.Add("判定為「無開口樓層」");
                if (totalFloorArea >= 1000)
                {
                    consequences.Add("依消防設置標準§28③：樓地板面積 ≥ 1000m² 之無開口樓層，須設排煙設備");
                }
                consequences.Add("依消防設置標準§17：須設自動灑水設備（不受面積限制）");
            }

            // 執行上色
            if (colorize && colorizeList.Count > 0)
            {
                View activeView = _uiApp.ActiveUIDocument.ActiveView;
                ElementId solidPatternId = GetSolidFillPatternId(doc);

                using (Transaction trans = new Transaction(doc, "無開口樓層檢討上色"))
                {
                    trans.Start();
                    foreach (var (elementId, isEffective) in colorizeList)
                    {
                        OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                        Color color = isEffective ? new Color(0, 180, 0) : new Color(255, 50, 50);

                        bool useCut = (activeView.ViewType == ViewType.FloorPlan ||
                                       activeView.ViewType == ViewType.CeilingPlan);
                        if (useCut)
                        {
                            ogs.SetCutForegroundPatternColor(color);
                            if (solidPatternId != ElementId.InvalidElementId)
                            {
                                ogs.SetCutForegroundPatternId(solidPatternId);
                                ogs.SetCutForegroundPatternVisible(true);
                            }
                        }
                        else
                        {
                            ogs.SetSurfaceForegroundPatternColor(color);
                            if (solidPatternId != ElementId.InvalidElementId)
                            {
                                ogs.SetSurfaceForegroundPatternId(solidPatternId);
                                ogs.SetSurfaceForegroundPatternVisible(true);
                            }
                        }
                        ogs.SetProjectionLineColor(color);
                        activeView.SetElementOverrides(new ElementId(elementId), ogs);
                    }
                    trans.Commit();
                }
            }

            return new
            {
                LevelName = level.Name,
                FloorNumber = floorNumber,
                IsAbove10F = isAbove10F,
                TotalFloorArea = Math.Round(totalFloorArea, 2),
                Threshold_1_30 = Math.Round(threshold_1_30, 4),
                LegalBasis = new
                {
                    Definition = "消防設置標準§4：有效開口面積 < 樓地板面積 1/30 → 無開口樓層",
                    SmokeExhaust = "消防設置標準§28③：≥ 1000m² 之無開口樓層須設排煙設備",
                    Sprinkler = "消防設置標準§17：無開口樓層須設自動灑水設備"
                },
                ExteriorOpenings = openingResults,
                Summary = new
                {
                    TotalOpenings = openingResults.Count,
                    EffectiveOpenings = countOpeningEffective,
                    NeedsConfirmCount = countOpeningNeedsConfirm,
                    LargeOpeningCount = largeOpeningCount,
                    LargeOpeningRequired = isAbove10F ? 0 : 2,
                    MeetsLargeOpeningReq = meetsLargeOpeningReq,
                    TotalEffectiveArea = Math.Round(totalEffectiveArea, 4),
                    Ratio = Math.Round(ratio, 6),
                    Threshold = Math.Round(1.0 / 30.0, 6),
                    IsNoOpeningFloor = finalIsNoOpening,
                    Result = finalIsNoOpening ? "FAIL" : "PASS"
                },
                Consequences = consequences
            };
        }

        #endregion

        #region 視覺化工具

        /// <summary>
        /// 建立剖面視圖（用於排煙窗檢討的立面檢視）
        /// </summary>
        private object CreateSectionView(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int wallId = parameters["wallId"].Value<int>();
            string viewName = parameters["viewName"]?.Value<string>() ?? "排煙檢討剖面";
            double offset = parameters["offset"]?.Value<double>() ?? 1000; // 剖面偏移距離（mm）
            int scale = parameters["scale"]?.Value<int>() ?? 50;

            const double MM_TO_FEET = 1.0 / 304.8;

            Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
            if (wall == null) throw new Exception($"找不到牆 ID: {wallId}");

            LocationCurve locCurve = wall.Location as LocationCurve;
            if (locCurve == null) throw new Exception("牆沒有位置曲線");

            Curve wallCurve = locCurve.Curve;
            XYZ start = wallCurve.GetEndPoint(0);
            XYZ end = wallCurve.GetEndPoint(1);

            // 牆的方向向量
            XYZ wallDir = (end - start).Normalize();
            // 垂直於牆的方向（朝外）
            XYZ viewDir = wallDir.CrossProduct(XYZ.BasisZ).Normalize();

            // 牆的中點
            XYZ midPoint = (start + end) / 2.0;

            // 剖面框的尺寸
            double wallLength = wallCurve.Length;
            double wallHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? (4000 * MM_TO_FEET);

            // 建立 BoundingBoxXYZ 作為剖面範圍
            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ();

            // Transform：X 沿牆方向，Y 向上，Z 向視圖方向（面對牆面）
            Transform transform = Transform.Identity;
            transform.Origin = midPoint - viewDir * (offset * MM_TO_FEET);
            transform.BasisX = wallDir;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = -viewDir; // 看向牆面

            sectionBox.Transform = transform;

            // 設定剖面框範圍（局部座標）
            double halfLength = wallLength / 2.0 + 2.0; // 左右多 2 英尺
            double topMargin = 2.0; // 頂部多 2 英尺
            double bottomMargin = 1.0; // 底部多 1 英尺
            double depthNear = 0;
            double depthFar = (offset * MM_TO_FEET) + wall.Width + 2.0;

            sectionBox.Min = new XYZ(-halfLength, -bottomMargin, depthNear);
            sectionBox.Max = new XYZ(halfLength, wallHeight + topMargin, depthFar);

            int viewIdResult;
            using (Transaction trans = new Transaction(doc, "建立排煙檢討剖面"))
            {
                trans.Start();

                // 取得剖面視圖的 ViewFamilyType
                ViewFamilyType sectionType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

                if (sectionType == null)
                    throw new Exception("找不到剖面視圖類型");

                ViewSection sectionView = ViewSection.CreateSection(doc, sectionType.Id, sectionBox);
                sectionView.Name = viewName;
                sectionView.Scale = scale;

                viewIdResult = sectionView.Id.IntegerValue;

                trans.Commit();
            }

            return new
            {
                ViewId = viewIdResult,
                ViewName = viewName,
                WallId = wallId,
                Scale = scale,
                Message = $"已建立排煙檢討剖面視圖：{viewName}"
            };
        }

        /// <summary>
        /// 在視圖上繪製詳圖線（天花板線、有效帶線等）
        /// </summary>
        private object CreateDetailLines(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int viewId = parameters["viewId"].Value<int>();
            var linesArray = parameters["lines"] as JArray;
            if (linesArray == null || linesArray.Count == 0)
                throw new Exception("需要提供 lines 陣列");

            const double MM_TO_FEET = 1.0 / 304.8;

            View view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null) throw new Exception($"找不到視圖 ID: {viewId}");

            var createdLines = new List<object>();

            using (Transaction trans = new Transaction(doc, "繪製詳圖線"))
            {
                trans.Start();

                foreach (JObject lineObj in linesArray)
                {
                    double startX = lineObj["startX"].Value<double>() * MM_TO_FEET;
                    double startY = lineObj["startY"].Value<double>() * MM_TO_FEET;
                    double endX = lineObj["endX"].Value<double>() * MM_TO_FEET;
                    double endY = lineObj["endY"].Value<double>() * MM_TO_FEET;

                    XYZ startPt = new XYZ(startX, startY, 0);
                    XYZ endPt = new XYZ(endX, endY, 0);

                    if (startPt.DistanceTo(endPt) < 0.001) continue;

                    Line line = Line.CreateBound(startPt, endPt);
                    DetailCurve detailLine = doc.Create.NewDetailCurve(view, line);

                    // 設定線條樣式
                    string lineStyle = lineObj["lineStyle"]?.Value<string>();
                    if (!string.IsNullOrEmpty(lineStyle))
                    {
                        var lineStyles = detailLine.GetLineStyleIds();
                        foreach (ElementId styleId in lineStyles)
                        {
                            Element style = doc.GetElement(styleId);
                            if (style != null && style.Name.Contains(lineStyle))
                            {
                                detailLine.LineStyle = style;
                                break;
                            }
                        }
                    }

                    // 設定顏色覆寫
                    if (lineObj["color"] != null)
                    {
                        var colorObj = lineObj["color"];
                        byte r = (byte)colorObj["r"].Value<int>();
                        byte g = (byte)colorObj["g"].Value<int>();
                        byte b = (byte)colorObj["b"].Value<int>();
                        Color color = new Color(r, g, b);

                        OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(color);
                        view.SetElementOverrides(detailLine.Id, ogs);
                    }

                    createdLines.Add(new
                    {
                        ElementId = detailLine.Id.IntegerValue,
                        Label = lineObj["label"]?.Value<string>()
                    });
                }

                trans.Commit();
            }

            return new
            {
                ViewId = viewId,
                LinesCreated = createdLines.Count,
                Lines = createdLines
            };
        }

        /// <summary>
        /// 建立填充區域（有效帶範圍的半透明色塊）
        /// </summary>
        private object CreateFilledRegion(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int viewId = parameters["viewId"].Value<int>();
            var pointsArray = parameters["points"] as JArray;
            if (pointsArray == null || pointsArray.Count < 3)
                throw new Exception("需要至少 3 個點來定義填充區域");

            const double MM_TO_FEET = 1.0 / 304.8;

            View view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null) throw new Exception($"找不到視圖 ID: {viewId}");

            // 找到填充區域類型
            string regionTypeName = parameters["regionType"]?.Value<string>();
            FilledRegionType regionType = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(frt => !string.IsNullOrEmpty(regionTypeName) ?
                    frt.Name.Contains(regionTypeName) : true);

            if (regionType == null)
                throw new Exception("找不到填充區域類型");

            // 建立邊界曲線
            var curveLoop = new CurveLoop();
            var points = new List<XYZ>();

            foreach (JObject ptObj in pointsArray)
            {
                double x = ptObj["x"].Value<double>() * MM_TO_FEET;
                double y = ptObj["y"].Value<double>() * MM_TO_FEET;
                points.Add(new XYZ(x, y, 0));
            }

            for (int i = 0; i < points.Count; i++)
            {
                int nextIdx = (i + 1) % points.Count;
                if (points[i].DistanceTo(points[nextIdx]) > 0.001)
                {
                    curveLoop.Append(Line.CreateBound(points[i], points[nextIdx]));
                }
            }

            int regionId;
            using (Transaction trans = new Transaction(doc, "建立填充區域"))
            {
                trans.Start();

                FilledRegion filledRegion = FilledRegion.Create(
                    doc, regionType.Id, view.Id,
                    new List<CurveLoop> { curveLoop });

                regionId = filledRegion.Id.IntegerValue;

                // 設定顏色覆寫
                if (parameters["color"] != null)
                {
                    var colorObj = parameters["color"];
                    byte r = (byte)colorObj["r"].Value<int>();
                    byte g = (byte)colorObj["g"].Value<int>();
                    byte b = (byte)colorObj["b"].Value<int>();
                    Color color = new Color(r, g, b);

                    OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetSurfaceTransparency(parameters["transparency"]?.Value<int>() ?? 50);
                    view.SetElementOverrides(filledRegion.Id, ogs);
                }

                trans.Commit();
            }

            return new
            {
                ElementId = regionId,
                ViewId = viewId,
                Message = "填充區域已建立"
            };
        }

        /// <summary>
        /// 在視圖上建立文字標註
        /// </summary>
        private object CreateTextNote(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int viewId = parameters["viewId"].Value<int>();
            double x = parameters["x"].Value<double>() / 304.8; // mm to feet
            double y = parameters["y"].Value<double>() / 304.8;
            string text = parameters["text"].Value<string>();
            double textSize = parameters["textSize"]?.Value<double>() ?? 2.5; // mm

            View view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null) throw new Exception($"找不到視圖 ID: {viewId}");

            // 取得或建立文字類型
            TextNoteType textType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();

            if (textType == null)
                throw new Exception("找不到文字標註類型");

            int textNoteId;
            using (Transaction trans = new Transaction(doc, "建立文字標註"))
            {
                trans.Start();

                TextNoteOptions options = new TextNoteOptions
                {
                    TypeId = textType.Id,
                    HorizontalAlignment = HorizontalTextAlignment.Left
                };

                TextNote textNote = TextNote.Create(doc, view.Id, new XYZ(x, y, 0), text, options);
                textNoteId = textNote.Id.IntegerValue;

                trans.Commit();
            }

            return new
            {
                ElementId = textNoteId,
                ViewId = viewId,
                Text = text
            };
        }

        #endregion

        #region Excel 匯出

        /// <summary>
        /// 匯出排煙窗檢討結果為 Excel (.xlsx)
        /// 使用 ClosedXML，多工作表 + 淺底色 + 改善建議
        /// </summary>
        private object ExportSmokeReviewExcel(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["levelName"]?.Value<string>();
            string ceilingHeightSource = parameters["ceilingHeightSource"]?.Value<string>() ?? "room_parameter";
            string outputPath = parameters["outputPath"]?.Value<string>();

            if (string.IsNullOrEmpty(outputPath))
            {
                string projectPath = doc.PathName;
                string projectDir = string.IsNullOrEmpty(projectPath) ?
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop) :
                    System.IO.Path.GetDirectoryName(projectPath);
                outputPath = System.IO.Path.Combine(projectDir,
                    $"排煙窗檢討_{levelName ?? "全部"}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            }

            // 執行檢討
            var checkParams = new JObject { ["levelName"] = levelName, ["ceilingHeightSource"] = ceilingHeightSource, ["colorize"] = false };
            var checkResult = JObject.FromObject(CheckSmokeExhaustWindows(checkParams));

            var floorParams = new JObject { ["levelName"] = levelName, ["colorize"] = false };
            var floorResult = JObject.FromObject(CheckFloorEffectiveOpenings(floorParams));

            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                // 色彩定義
                var headerBg = ClosedXML.Excel.XLColor.FromHtml("#4472C4");     // 深藍
                var headerFg = ClosedXML.Excel.XLColor.White;
                var passBg = ClosedXML.Excel.XLColor.FromHtml("#E2EFDA");       // 淺綠
                var failBg = ClosedXML.Excel.XLColor.FromHtml("#FCE4EC");       // 淺紅
                var altRowBg = ClosedXML.Excel.XLColor.FromHtml("#F2F2F2");     // 淺灰交替行
                var warnBg = ClosedXML.Excel.XLColor.FromHtml("#FFF3E0");       // 淺橘

                // ===== Sheet 1：樓層總覽 =====
                var ws1 = wb.Worksheets.Add("樓層總覽");
                ws1.Cell(1, 1).Value = $"排煙窗檢討報告 — {levelName}";
                ws1.Range(1, 1, 1, 6).Merge().Style.Font.SetBold().Font.SetFontSize(14).Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Center);
                ws1.Cell(2, 1).Value = $"產出時間：{DateTime.Now:yyyy/MM/dd HH:mm}";
                ws1.Range(2, 1, 2, 6).Merge().Style.Font.SetFontColor(ClosedXML.Excel.XLColor.Gray);

                int r = 4;
                string[] h1 = { "樓層", "樓地板面積(m²)", "有效開口面積(m²)", "開口比值", "1/30門檻(m²)", "無開口樓層判定" };
                for (int c = 0; c < h1.Length; c++)
                {
                    ws1.Cell(r, c + 1).Value = h1[c];
                    ws1.Cell(r, c + 1).Style.Fill.SetBackgroundColor(headerBg).Font.SetFontColor(headerFg).Font.SetBold();
                }
                r++;
                ws1.Cell(r, 1).Value = floorResult["LevelName"]?.ToString();
                ws1.Cell(r, 2).Value = (double)floorResult["TotalFloorArea"];
                ws1.Cell(r, 3).Value = (double)floorResult["Summary"]["TotalEffectiveArea"];
                ws1.Cell(r, 4).Value = (double)floorResult["Summary"]["Ratio"];
                ws1.Cell(r, 4).Style.NumberFormat.Format = "0.0000%";
                ws1.Cell(r, 5).Value = (double)floorResult["Threshold_1_30"];
                string floorResultStr = floorResult["Summary"]["Result"]?.ToString();
                ws1.Cell(r, 6).Value = floorResultStr;
                ws1.Cell(r, 6).Style.Fill.SetBackgroundColor(floorResultStr == "PASS" ? passBg : failBg);

                // 法規依據
                r += 2;
                ws1.Cell(r, 1).Value = "法規依據";
                ws1.Cell(r, 1).Style.Font.SetBold();
                r++;
                ws1.Cell(r, 1).Value = "無開口樓層：消防設置標準§4（有效開口 < 1/30）";
                r++;
                ws1.Cell(r, 1).Value = "排煙觸發：消防設置標準§28③（≥1000m² 無開口樓層須設排煙）";
                r++;
                ws1.Cell(r, 1).Value = "排煙窗：建技規§101① + 消防§188（天花板下80cm，≥區劃面積2%）";
                r++;
                ws1.Cell(r, 1).Value = "無窗居室：建技規§1第35款第三目（>50m²居室，通風面積<2%）";

                ws1.Columns().AdjustToContents();

                // ===== Sheet 2：房間排煙檢討 =====
                var ws2 = wb.Worksheets.Add("房間檢討明細");
                string[] h2 = { "房間名稱", "編號", "面積(m²)", "天花板高(mm)", "有效帶頂(mm)", "有效帶底(mm)",
                                "有效排煙面積(m²)", "需求面積(m²)", "比值", "判定", "無窗居室", "防煙區劃警告", "改善建議" };
                for (int c = 0; c < h2.Length; c++)
                {
                    ws2.Cell(1, c + 1).Value = h2[c];
                    ws2.Cell(1, c + 1).Style.Fill.SetBackgroundColor(headerBg).Font.SetFontColor(headerFg).Font.SetBold();
                }

                r = 2;
                foreach (JToken room in (JArray)checkResult["Rooms"])
                {
                    bool isAltRow = (r % 2 == 0);
                    string result = room["Summary"]["Result"]?.ToString();
                    bool isFail = result == "FAIL";

                    ws2.Cell(r, 1).Value = room["RoomName"]?.ToString();
                    ws2.Cell(r, 2).Value = room["RoomNumber"]?.ToString();
                    ws2.Cell(r, 3).Value = (double)room["RoomArea"];
                    ws2.Cell(r, 4).Value = (double)room["CeilingHeight"];
                    ws2.Cell(r, 5).Value = (double)room["SmokeZoneTop"];
                    ws2.Cell(r, 6).Value = (double)room["SmokeZoneBottom"];
                    ws2.Cell(r, 7).Value = (double)room["Summary"]["TotalEffectiveArea"];
                    ws2.Cell(r, 8).Value = (double)room["Summary"]["RequiredArea"];
                    ws2.Cell(r, 9).Value = (double)room["Summary"]["Ratio"];
                    ws2.Cell(r, 9).Style.NumberFormat.Format = "0.00%";
                    ws2.Cell(r, 10).Value = result;
                    ws2.Cell(r, 11).Value = (bool)room["Summary"]["IsWindowlessRoom"] ? "是" : "否";
                    ws2.Cell(r, 12).Value = (bool)room["ExceedsCompartmentThreshold"] ? "⚠ >500m²" : "";

                    // 改善建議：直接寫入，每個建議換行
                    var recs = (JArray)room["Summary"]["Recommendations"];
                    if (recs != null && recs.Count > 0)
                    {
                        var recTexts = new List<string>();
                        foreach (JToken rec in recs)
                        {
                            string note = rec["Note"]?.ToString();
                            if (!string.IsNullOrEmpty(note)) recTexts.Add(note);
                            // 列出可用窗型
                            var availTypes = rec["AvailableWindowTypes"] as JArray ?? rec["AvailableCasementTypes"] as JArray;
                            if (availTypes != null)
                            {
                                foreach (JToken at in availTypes)
                                {
                                    string typeLine = $"  → {at["TypeName"]} (每扇可補 {at["EffectiveAreaPerWindow"] ?? at["PotentialGain"]} m²)";
                                    recTexts.Add(typeLine);
                                }
                            }
                            // 列出固定窗
                            var fixedWins = rec["FixedWindows"] as JArray;
                            if (fixedWins != null)
                            {
                                foreach (JToken fw in fixedWins)
                                {
                                    recTexts.Add($"  → 窗ID {fw["WindowId"]}：{fw["CurrentType"]}，帶內 {fw["AreaInZone"]} m²");
                                }
                            }
                        }
                        ws2.Cell(r, 13).Value = string.Join("\n", recTexts);
                        ws2.Cell(r, 13).Style.Alignment.SetWrapText(true);
                    }

                    // 行底色
                    var rowBg = isFail ? failBg : (isAltRow ? altRowBg : ClosedXML.Excel.XLColor.NoColor);
                    if (rowBg != ClosedXML.Excel.XLColor.NoColor)
                    {
                        ws2.Range(r, 1, r, h2.Length).Style.Fill.SetBackgroundColor(rowBg);
                    }
                    // 判定欄加強底色
                    ws2.Cell(r, 10).Style.Fill.SetBackgroundColor(isFail ? failBg : passBg);
                    if ((bool)room["ExceedsCompartmentThreshold"])
                    {
                        ws2.Cell(r, 12).Style.Fill.SetBackgroundColor(warnBg);
                    }

                    r++;
                }
                ws2.Columns().AdjustToContents();
                ws2.Column(13).Width = 50; // 建議欄加寬

                // ===== Sheet 3：窗戶明細 =====
                var ws3 = wb.Worksheets.Add("窗戶明細");
                string[] h3 = { "房間", "窗戶ID", "族群名稱", "類型名稱", "寬(mm)", "高(mm)",
                                "窗台高(mm)", "窗頂高(mm)", "在有效帶內", "帶內高度(mm)",
                                "帶內面積(m²)", "開啟方式", "折減係數", "有效面積(m²)", "需人工確認", "備註" };
                for (int c = 0; c < h3.Length; c++)
                {
                    ws3.Cell(1, c + 1).Value = h3[c];
                    ws3.Cell(1, c + 1).Style.Fill.SetBackgroundColor(headerBg).Font.SetFontColor(headerFg).Font.SetBold();
                }

                r = 2;
                foreach (JToken room in (JArray)checkResult["Rooms"])
                {
                    foreach (JToken w in (JArray)room["Windows"])
                    {
                        bool isAltRow = (r % 2 == 0);
                        string opType = w["OperationType"]?.ToString();

                        ws3.Cell(r, 1).Value = room["RoomName"]?.ToString();
                        ws3.Cell(r, 2).Value = (int)w["WindowId"];
                        ws3.Cell(r, 3).Value = w["FamilyName"]?.ToString();
                        ws3.Cell(r, 4).Value = w["TypeName"]?.ToString();
                        ws3.Cell(r, 5).Value = (double)w["Width"];
                        ws3.Cell(r, 6).Value = (double)w["Height"];
                        ws3.Cell(r, 7).Value = (double)w["SillHeight"];
                        ws3.Cell(r, 8).Value = (double)w["HeadHeight"];
                        ws3.Cell(r, 9).Value = (bool)w["IsInSmokeZone"] ? "是" : "否";
                        ws3.Cell(r, 10).Value = (double)w["HeightInZone"];
                        ws3.Cell(r, 11).Value = (double)w["AreaInZone"];
                        ws3.Cell(r, 12).Value = opType;
                        ws3.Cell(r, 13).Value = (double)w["OpeningRatio"];
                        ws3.Cell(r, 14).Value = (double)w["EffectiveArea"];
                        ws3.Cell(r, 15).Value = (bool)w["NeedsManualConfirm"] ? "⚠ 需確認" : "";
                        string noteStr = w["Note"]?.Type == JTokenType.Null ? "" : (w["Note"]?.ToString() ?? "");
                        ws3.Cell(r, 16).Value = noteStr;

                        // 開啟方式底色
                        ClosedXML.Excel.XLColor opBg;
                        switch (opType)
                        {
                            case "casement": case "pivot": opBg = passBg; break;
                            case "sliding": case "projected": case "louver": opBg = warnBg; break;
                            case "fixed": opBg = failBg; break;
                            default: opBg = ClosedXML.Excel.XLColor.FromHtml("#E0E0E0"); break;
                        }
                        ws3.Cell(r, 12).Style.Fill.SetBackgroundColor(opBg);
                        ws3.Cell(r, 14).Style.Fill.SetBackgroundColor((double)w["EffectiveArea"] > 0 ? passBg : failBg);

                        // 交替行底色
                        if (isAltRow)
                        {
                            ws3.Range(r, 1, r, h3.Length).Style.Fill.SetBackgroundColor(altRowBg);
                            // 重新套用特殊欄底色
                            ws3.Cell(r, 12).Style.Fill.SetBackgroundColor(opBg);
                            ws3.Cell(r, 14).Style.Fill.SetBackgroundColor((double)w["EffectiveArea"] > 0 ? passBg : failBg);
                        }

                        r++;
                    }
                }
                ws3.Columns().AdjustToContents();

                // ===== Sheet 4：改善建議 =====
                var ws4 = wb.Worksheets.Add("改善建議");
                string[] h4 = { "房間", "面積(m²)", "缺口(m²)", "建議類型", "建議說明", "具體標的", "可補面積(m²)" };
                for (int c = 0; c < h4.Length; c++)
                {
                    ws4.Cell(1, c + 1).Value = h4[c];
                    ws4.Cell(1, c + 1).Style.Fill.SetBackgroundColor(headerBg).Font.SetFontColor(headerFg).Font.SetBold();
                }

                r = 2;
                foreach (JToken room in (JArray)checkResult["Rooms"])
                {
                    if (room["Summary"]["Result"]?.ToString() != "FAIL") continue;

                    string roomName = room["RoomName"]?.ToString();
                    double roomArea = (double)room["RoomArea"];
                    double deficitVal = (double)room["Summary"]["Deficit"];

                    foreach (JToken rec in (JArray)room["Summary"]["Recommendations"])
                    {
                        string recType = rec["Type"]?.ToString() ?? "";
                        string recNote = rec["Note"]?.ToString() ?? rec["Action"]?.ToString() ?? "";

                        // 固定窗明細
                        var fixedWins = rec["FixedWindows"] as JArray;
                        var availTypes = rec["AvailableWindowTypes"] as JArray ?? rec["AvailableCasementTypes"] as JArray;
                        var nearWins = rec["NearWindows"] as JArray;

                        if (fixedWins != null && fixedWins.Count > 0)
                        {
                            foreach (JToken fw in fixedWins)
                            {
                                ws4.Cell(r, 1).Value = roomName;
                                ws4.Cell(r, 2).Value = roomArea;
                                ws4.Cell(r, 3).Value = deficitVal;
                                ws4.Cell(r, 4).Value = recType;
                                ws4.Cell(r, 5).Value = "固定窗改為可開啟窗";
                                ws4.Cell(r, 6).Value = $"窗ID {fw["WindowId"]}：{fw["CurrentType"]}";
                                ws4.Cell(r, 7).Value = (double)fw["PotentialGain"];
                                ws4.Range(r, 1, r, 7).Style.Fill.SetBackgroundColor(warnBg);
                                r++;
                            }
                        }
                        else if (availTypes != null && availTypes.Count > 0)
                        {
                            foreach (JToken at in availTypes)
                            {
                                ws4.Cell(r, 1).Value = roomName;
                                ws4.Cell(r, 2).Value = roomArea;
                                ws4.Cell(r, 3).Value = deficitVal;
                                ws4.Cell(r, 4).Value = recType;
                                ws4.Cell(r, 5).Value = recNote;
                                ws4.Cell(r, 6).Value = at["TypeName"]?.ToString();
                                double epa = 0;
                                if (at["EffectiveAreaPerWindow"] != null) epa = (double)at["EffectiveAreaPerWindow"];
                                ws4.Cell(r, 7).Value = epa;
                                if (r % 2 == 0) ws4.Range(r, 1, r, 7).Style.Fill.SetBackgroundColor(altRowBg);
                                r++;
                            }
                        }
                        else if (nearWins != null && nearWins.Count > 0)
                        {
                            foreach (JToken nw in nearWins)
                            {
                                ws4.Cell(r, 1).Value = roomName;
                                ws4.Cell(r, 2).Value = roomArea;
                                ws4.Cell(r, 3).Value = deficitVal;
                                ws4.Cell(r, 4).Value = recType;
                                ws4.Cell(r, 5).Value = nw["SuggestAction"]?.ToString();
                                ws4.Cell(r, 6).Value = $"窗ID {nw["WindowId"]}：距有效帶 {nw["GapToZone"]}mm";
                                ws4.Cell(r, 7).Value = "";
                                ws4.Range(r, 1, r, 7).Style.Fill.SetBackgroundColor(warnBg);
                                r++;
                            }
                        }
                        else
                        {
                            // 一般建議（如機械排煙）
                            ws4.Cell(r, 1).Value = roomName;
                            ws4.Cell(r, 2).Value = roomArea;
                            ws4.Cell(r, 3).Value = deficitVal;
                            ws4.Cell(r, 4).Value = recType;
                            ws4.Cell(r, 5).Value = recNote;
                            ws4.Cell(r, 6).Value = "";
                            ws4.Cell(r, 7).Value = "";
                            ws4.Range(r, 1, r, 7).Style.Fill.SetBackgroundColor(failBg);
                            r++;
                        }
                    }
                }
                ws4.Columns().AdjustToContents();
                ws4.Column(5).Width = 45;
                ws4.Column(6).Width = 35;

                // 全域框線
                foreach (var ws in wb.Worksheets)
                {
                    var usedRange = ws.RangeUsed();
                    if (usedRange != null)
                    {
                        usedRange.Style.Border.SetOutsideBorder(ClosedXML.Excel.XLBorderStyleValues.Thin);
                        usedRange.Style.Border.SetInsideBorder(ClosedXML.Excel.XLBorderStyleValues.Thin);
                        usedRange.Style.Font.SetFontName("Microsoft JhengHei");
                    }
                }

                wb.SaveAs(outputPath);
            }

            return new
            {
                OutputPath = outputPath,
                LevelName = levelName ?? "全部",
                RoomsChecked = (int)checkResult["LevelSummary"]["RoomsChecked"],
                RoomsFailed = (int)checkResult["LevelSummary"]["RoomsFailed"],
                Message = $"排煙窗檢討報告已匯出至：{outputPath}"
            };
        }

        #endregion
    }
}



