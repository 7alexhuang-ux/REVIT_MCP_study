#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Models;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 從 Revit 文件讀出 params.json 的資料（D-802）：樓層、構件參數、「房間面積表」。唯讀（不開 Transaction）；
    /// 必須在 Revit API 執行緒呼叫（由 RevitApiThreadRunner 保證）。依賴 Revit API，所以不納入 MCP.Tests；
    /// 資料→JSON 的規則在 <see cref="ParamsFileBuilder"/>（有離線測試）。
    /// 讀不到的欄位一律不填（null），不猜值、不補 0。連結模型的構件不納入（只讀目前開啟的文件），並在警告說明。
    /// </summary>
    internal static class RevitParamsReader
    {
        private const double FeetToMm = 304.8;
        private const double FeetToMeters = 0.3048;
        private const double SqFeetToSqMeters = 0.09290304;
        private const double CuFeetToCuMeters = 0.028316846592;

        // 要讀的類別：結構與空間的主要構件。其他類別（家具、管線…）參數量大、對版本比對價值低，不納入。
        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Rooms
        };

        public static ParamsFileData Read(Document doc, double[] originOffsetMeters, string revitVersion)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            var originZ = originOffsetMeters != null && originOffsetMeters.Length == 3 ? originOffsetMeters[2] : 0.0;
            var data = new ParamsFileData { Note = "Revit " + revitVersion + "：" + doc.Title };

            var levelNames = new Dictionary<long, string>();
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ThenBy(l => l.UniqueId, StringComparer.Ordinal))
            {
                levelNames[IdOf(level.Id)] = level.Name;
                data.Levels.Add(new ParamsLevelData { Name = level.Name, RevitName = level.Name, ElevationMeters = level.ProjectElevation * FeetToMeters - originZ });
            }

            var unreadable = 0;
            var rooms = new List<Room>();
            var filter = new ElementMulticategoryFilter(Categories.ToList());
            foreach (var element in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType())
            {
                try
                {
                    var room = element as Room;
                    if (room != null) rooms.Add(room);
                    data.Elements.Add(ReadElement(doc, element, levelNames));
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }
            if (unreadable > 0) data.Warnings.Add(unreadable + " 個構件的參數讀取失敗，已略過。");
            data.Warnings.Add("只包含目前開啟的專案（連結模型的構件未納入參數檔）。");

            var schedule = BuildRoomAreaSchedule(rooms, levelNames, data.Warnings);
            if (schedule != null) data.Schedules.Add(schedule);
            return data;
        }

        private static ParamsElementData ReadElement(Document doc, Element e, Dictionary<long, string> levelNames)
        {
            var item = new ParamsElementData { UniqueId = e.UniqueId, ElementId = IdOf(e.Id), Category = CategoryKey(e.Category), Name = e.Name };
            var type = doc.GetElement(e.GetTypeId()) as ElementType;
            if (type != null)
            {
                item.Family = type.FamilyName;
                item.Type = type.Name;
            }
            item.Level = LevelName(e, levelNames);

            switch (e.Category != null ? (BuiltInCategory)(int)IdOf(e.Category.Id) : BuiltInCategory.INVALID)
            {
                case BuiltInCategory.OST_Doors:
                case BuiltInCategory.OST_Windows:
                    item.WidthMm = Length(e, BuiltInParameter.FAMILY_WIDTH_PARAM) ?? Length(type, BuiltInParameter.FAMILY_WIDTH_PARAM) ?? Length(e, BuiltInParameter.DOOR_WIDTH) ?? Length(e, BuiltInParameter.WINDOW_WIDTH);
                    item.HeightMm = Length(e, BuiltInParameter.FAMILY_HEIGHT_PARAM) ?? Length(type, BuiltInParameter.FAMILY_HEIGHT_PARAM) ?? Length(e, BuiltInParameter.DOOR_HEIGHT) ?? Length(e, BuiltInParameter.WINDOW_HEIGHT);
                    break;
                case BuiltInCategory.OST_Walls:
                    item.LengthMm = Length(e, BuiltInParameter.CURVE_ELEM_LENGTH);
                    item.HeightMm = Length(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    var wall = e as Wall;
                    if (wall != null) item.ThicknessMm = wall.Width * FeetToMm;
                    break;
                case BuiltInCategory.OST_Floors:
                case BuiltInCategory.OST_Roofs:
                case BuiltInCategory.OST_Ceilings:
                    item.ThicknessMm = Length(e, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM) ?? Length(e, BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM);
                    break;
                case BuiltInCategory.OST_StructuralFraming:
                    item.LengthMm = Length(e, BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH) ?? Length(e, BuiltInParameter.CURVE_ELEM_LENGTH);
                    break;
                case BuiltInCategory.OST_Columns:
                case BuiltInCategory.OST_StructuralColumns:
                    item.HeightMm = Length(e, BuiltInParameter.INSTANCE_LENGTH_PARAM);
                    break;
            }

            var room = e as Room;
            if (room != null)
            {
                item.Name = room.Name;
                item.Source = "(host)";
                var area = room.Area;
                item.AreaSqm = area > 0 ? (double?)(area * SqFeetToSqMeters) : null;
                var number = room.Number;
                if (!string.IsNullOrWhiteSpace(number)) item.Extra["Number"] = number;
            }
            else
            {
                item.AreaSqm = Number(e, BuiltInParameter.HOST_AREA_COMPUTED, SqFeetToSqMeters);
                item.VolumeCum = Number(e, BuiltInParameter.HOST_VOLUME_COMPUTED, CuFeetToCuMeters);
            }

            var mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (mark != null && mark.HasValue && !string.IsNullOrWhiteSpace(mark.AsString())) item.Extra["Mark"] = mark.AsString();

            try
            {
                foreach (var id in e.GetMaterialIds(false).Take(20))
                {
                    var material = doc.GetElement(id) as Material;
                    if (material != null && !string.IsNullOrWhiteSpace(material.Name)) item.Materials.Add(material.Name);
                }
            }
            catch (Exception)
            {
                // 材料讀不到就不填（不影響其他欄位）。
            }
            return item;
        }

        private static ParamsScheduleData BuildRoomAreaSchedule(List<Room> rooms, Dictionary<long, string> levelNames, List<string> warnings)
        {
            var placed = rooms.Where(r => r.Location != null).ToList();
            if (placed.Count == 0) return null;
            var schedule = new ParamsScheduleData
            {
                Id = "area-by-room", Name = "房間面積表", Kind = "area",
                Columns = new List<ParamsColumnData>
                {
                    new ParamsColumnData { Key = "number", Label = "房號", Type = "text" },
                    new ParamsColumnData { Key = "name", Label = "房間名稱", Type = "text" },
                    new ParamsColumnData { Key = "level", Label = "樓層", Type = "text" },
                    new ParamsColumnData { Key = "area", Label = "面積", Type = "area", Unit = "m2" },
                    new ParamsColumnData { Key = "usage", Label = "用途分類", Type = "text" }
                }
            };
            var withoutArea = 0;
            foreach (var room in placed.OrderBy(r => r.Level != null ? r.Level.ProjectElevation : 0.0).ThenBy(r => r.Number, StringComparer.Ordinal).ThenBy(r => r.UniqueId, StringComparer.Ordinal))
            {
                var area = room.Area > 0 ? (object)(room.Area * SqFeetToSqMeters) : null;
                if (area == null) withoutArea++;
                var department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                var usage = department != null && department.HasValue ? department.AsString() : null;
                schedule.Rows.Add(new ParamsRowData
                {
                    UniqueId = room.UniqueId,
                    Cells = new Dictionary<string, object>
                    {
                        { "number", room.Number },
                        { "name", room.get_Parameter(BuiltInParameter.ROOM_NAME) != null ? room.get_Parameter(BuiltInParameter.ROOM_NAME).AsString() : room.Name },
                        { "level", LevelName(room, levelNames) },
                        { "area", area },
                        { "usage", string.IsNullOrWhiteSpace(usage) ? null : usage }
                    }
                });
            }
            if (withoutArea > 0) warnings.Add(withoutArea + " 間房間沒有面積（未封閉的房間），面積表中該格留空。");
            var unplaced = rooms.Count - placed.Count;
            if (unplaced > 0) warnings.Add(unplaced + " 間房間尚未放置，未列入面積表。");
            return schedule;
        }

        /// <summary>
        /// 類別鍵用 Revit 內建類別的英文名（Walls、Doors、Rooms…），不用介面語言的顯示名稱（實機：繁中 Revit 會得到「牆」「門」），
        /// 這樣網站端與 params 規格（revit-params.md §4 的 category）在任何語言的 Revit 上都對得上。
        /// </summary>
        private static string CategoryKey(Category category)
        {
            if (category == null) return null;
            try
            {
                var name = ((BuiltInCategory)(int)IdOf(category.Id)).ToString();
                if (name.StartsWith("OST_", StringComparison.Ordinal)) return name.Substring(4);
            }
            catch (Exception) { }
            return category.Name;
        }

        private static string LevelName(Element e, Dictionary<long, string> levelNames)
        {
            string name;
            var levelId = e.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId && levelNames.TryGetValue(IdOf(levelId), out name)) return name;
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT })
            {
                var p = e.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.ElementId && levelNames.TryGetValue(IdOf(p.AsElementId()), out name)) return name;
            }
            return null;
        }

        private static double? Length(Element e, BuiltInParameter bip)
        {
            return Number(e, bip, FeetToMm);
        }

        private static double? Number(Element e, BuiltInParameter bip, double factor)
        {
            if (e == null) return null;
            var p = e.get_Parameter(bip);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
            return p.AsDouble() * factor;
        }

        private static long IdOf(ElementId id)
        {
            return (long)id.GetIdValue();
        }
    }
}
