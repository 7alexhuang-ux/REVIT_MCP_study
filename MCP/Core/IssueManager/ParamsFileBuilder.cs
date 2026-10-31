#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    // params.json（im-revit-params/1，Issue Manager docs/spec/revit-params.md §4；加上 schedules，export-bundle.md §2.3；D-802）。
    // 這個檔案只做「資料 → JSON」：不碰 Revit API，所以能在 MCP.Tests 離線測試。Revit 端讀值在 RevitParamsReader。
    // 單位固定：長度類 mm、面積 m²、體積 m³（檔案不能改單位）；讀不到的欄位一律省略（不補 0、不猜），schedule 的空格子寫 null。

    public sealed class ParamsLevelData
    {
        public string Name { get; set; }
        public string RevitName { get; set; }
        /// <summary>公尺，已扣掉與 GLB 相同的原點位移（glTF Y）。</summary>
        public double ElevationMeters { get; set; }
    }

    public sealed class ParamsElementData
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public string Source { get; set; } = "(host)";
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Level { get; set; }
        public List<string> Materials { get; set; } = new List<string>();
        public double? WidthMm { get; set; }
        public double? HeightMm { get; set; }
        public double? LengthMm { get; set; }
        public double? ThicknessMm { get; set; }
        public double? AreaSqm { get; set; }
        public double? VolumeCum { get; set; }
        public Dictionary<string, string> Extra { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ParamsColumnData
    {
        public string Key { get; set; }
        public string Label { get; set; }
        /// <summary>text｜number｜area｜length｜volume</summary>
        public string Type { get; set; } = "text";
        public string Unit { get; set; }
    }

    public sealed class ParamsRowData
    {
        public string UniqueId { get; set; }
        /// <summary>字串、數字或 null（缺的格子）。</summary>
        public Dictionary<string, object> Cells { get; set; } = new Dictionary<string, object>();
    }

    public sealed class ParamsScheduleData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; } = "area";
        public List<ParamsColumnData> Columns { get; set; } = new List<ParamsColumnData>();
        public List<ParamsRowData> Rows { get; set; } = new List<ParamsRowData>();
    }

    public sealed class ParamsFileData
    {
        public bool IsTestData { get; set; }
        public string Tool { get; set; } = "IM Export Add-in";
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Note { get; set; } = string.Empty;
        public List<ParamsLevelData> Levels { get; set; } = new List<ParamsLevelData>();
        public List<ParamsElementData> Elements { get; set; } = new List<ParamsElementData>();
        public List<ParamsScheduleData> Schedules { get; set; } = new List<ParamsScheduleData>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public static class ParamsFileBuilder
    {
        public const string Schema = "im-revit-params/1";

        /// <summary>網站端單檔上限（D-802）：8 MiB。留一點餘裕給 manifest 之外的計算誤差。</summary>
        public const long MaxBytes = 8L * 1024 * 1024;
        private const long TargetBytes = MaxBytes - 256 * 1024;
        public const int MaxScheduleColumns = 60;
        public const int MaxScheduleRows = 20000;
        public const int MaxSchedules = 20;

        // 超過上限時的保留順序：房間（面積表要用）→ 門窗 → 其他；同一級以 UniqueId 排序，結果可重現。
        private static int Priority(ParamsElementData element)
        {
            switch (element.Category)
            {
                case "Rooms": return 0;
                case "Doors":
                case "Windows": return 1;
                default: return 2;
            }
        }

        public static JObject Build(ParamsFileData data, int keepElements = int.MaxValue)
        {
            if (data == null) throw new ArgumentNullException("data");
            var levels = new JArray();
            foreach (var level in data.Levels)
            {
                if (string.IsNullOrWhiteSpace(level.Name) || double.IsNaN(level.ElevationMeters) || double.IsInfinity(level.ElevationMeters)) continue;
                levels.Add(new JObject
                {
                    ["name"] = Clip(level.Name, 40),
                    ["revitName"] = Clip(string.IsNullOrWhiteSpace(level.RevitName) ? level.Name : level.RevitName, 80),
                    ["elevation"] = Round(level.ElevationMeters, 4)
                });
            }

            var elements = new JObject();
            foreach (var element in OrderedElements(data.Elements).Take(keepElements))
                elements[element.UniqueId] = ElementJson(element);

            var root = new JObject
            {
                ["schema"] = Schema,
                ["isTestData"] = data.IsTestData,
                ["source"] = new JObject
                {
                    ["kind"] = "revit-addin",
                    ["tool"] = Clip(data.Tool, 80),
                    ["generatedAt"] = data.GeneratedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                    ["note"] = Clip(data.Note, 300)
                },
                ["levels"] = levels,
                ["elements"] = elements
            };
            var schedules = BuildSchedules(data.Schedules);
            if (schedules.Count > 0) root["schedules"] = schedules;
            return root;
        }

        /// <summary>寫檔；超過 8 MiB 時依優先順序丟掉後面的構件（房間與門窗最後才丟），並回報實際保留數與警告。</summary>
        public static ParamsExportResult Write(ParamsFileData data, string path)
        {
            var total = data.Elements.Count(e => !string.IsNullOrWhiteSpace(e.UniqueId));
            var keep = total;
            var json = Build(data, keep).ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetByteCount(json);
            var result = new ParamsExportResult { Levels = data.Levels.Count, Schedules = BuildSchedules(data.Schedules).Count };
            result.Warnings.AddRange(data.Warnings);
            if (bytes > TargetBytes && total > 0)
            {
                // 以比例估算保留數再逐步收斂（避免重複序列化數十次）。
                keep = (int)Math.Max(1, Math.Floor(total * (double)TargetBytes / bytes * 0.95));
                while (true)
                {
                    json = Build(data, keep).ToString(Formatting.None);
                    bytes = Encoding.UTF8.GetByteCount(json);
                    if (bytes <= TargetBytes || keep <= 1) break;
                    keep = Math.Max(1, (int)(keep * 0.9));
                }
                result.Warnings.Add("參數檔超過 8 MB 上限，已略過 " + (total - keep) + " 個構件的參數（保留房間與門窗，其餘依序保留）。");
            }
            if (bytes > MaxBytes) throw new InvalidOperationException("參數檔太大（超過 8 MB），即使只保留部分構件也放不下；請取消勾選「匯出參數與面積表」。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, json, new UTF8Encoding(false));
            result.Elements = Math.Min(keep, total);
            return result;
        }

        private static IEnumerable<ParamsElementData> OrderedElements(IEnumerable<ParamsElementData> elements)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return elements.Where(e => e != null && !string.IsNullOrWhiteSpace(e.UniqueId) && seen.Add(e.UniqueId.Trim().ToLowerInvariant()))
                .OrderBy(Priority).ThenBy(e => e.UniqueId, StringComparer.Ordinal);
        }

        private static JObject ElementJson(ParamsElementData e)
        {
            var o = new JObject();
            if (e.ElementId > 0) o["elementId"] = e.ElementId;
            if (!string.IsNullOrWhiteSpace(e.Source)) o["source"] = Clip(e.Source, 80);
            Put(o, "category", e.Category, 200);
            Put(o, "family", e.Family, 200);
            Put(o, "type", e.Type, 200);
            Put(o, "name", e.Name, 200);
            Put(o, "level", e.Level, 80);
            var materials = (e.Materials ?? new List<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => Clip(m, 80)).Distinct().Take(20).ToList();
            if (materials.Count > 0) o["materials"] = new JArray(materials.Cast<object>().ToArray());
            var values = new JObject();
            PutNumber(values, "width", e.WidthMm);
            PutNumber(values, "height", e.HeightMm);
            PutNumber(values, "length", e.LengthMm);
            PutNumber(values, "thickness", e.ThicknessMm);
            PutNumber(values, "area", e.AreaSqm);
            PutNumber(values, "volume", e.VolumeCum);
            if (values.Count > 0) o["values"] = values;
            var extra = new JObject();
            foreach (var pair in (e.Extra ?? new Dictionary<string, string>()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)).Take(200))
                extra[Clip(pair.Key, 80)] = Clip(pair.Value, 200);
            if (extra.Count > 0) o["extra"] = extra;
            return o;
        }

        private static JArray BuildSchedules(IEnumerable<ParamsScheduleData> schedules)
        {
            var result = new JArray();
            foreach (var schedule in (schedules ?? Enumerable.Empty<ParamsScheduleData>()).Take(MaxSchedules))
            {
                var columns = (schedule.Columns ?? new List<ParamsColumnData>()).Where(c => !string.IsNullOrWhiteSpace(c.Key)).Take(MaxScheduleColumns).ToList();
                if (columns.Count == 0) continue;
                var cols = new JArray();
                foreach (var c in columns)
                {
                    var column = new JObject { ["key"] = Clip(c.Key, 80), ["label"] = Clip(string.IsNullOrWhiteSpace(c.Label) ? c.Key : c.Label, 80), ["type"] = Clip(c.Type ?? "text", 20) };
                    if (!string.IsNullOrWhiteSpace(c.Unit)) column["unit"] = Clip(c.Unit, 20);
                    cols.Add(column);
                }
                var rows = new JArray();
                foreach (var row in (schedule.Rows ?? new List<ParamsRowData>()).Take(MaxScheduleRows))
                {
                    var cells = new JObject();
                    foreach (var c in columns)
                    {
                        object value;
                        row.Cells.TryGetValue(c.Key, out value);
                        var number = value as double?;
                        if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value))) value = null;
                        if (value is string) cells[c.Key] = Clip((string)value, 500);
                        else if (value is double) cells[c.Key] = Round((double)value, 4);
                        else if (value is int || value is long) cells[c.Key] = JToken.FromObject(value);
                        else cells[c.Key] = JValue.CreateNull();
                    }
                    var rowObject = new JObject();
                    if (!string.IsNullOrWhiteSpace(row.UniqueId)) rowObject["uniqueId"] = Clip(row.UniqueId, 80);
                    rowObject["cells"] = cells;
                    rows.Add(rowObject);
                }
                result.Add(new JObject
                {
                    ["id"] = Clip(string.IsNullOrWhiteSpace(schedule.Id) ? "schedule-" + (result.Count + 1) : schedule.Id, 80),
                    ["name"] = Clip(string.IsNullOrWhiteSpace(schedule.Name) ? "面積表" : schedule.Name, 120),
                    ["kind"] = Clip(schedule.Kind ?? "area", 20),
                    ["columns"] = cols,
                    ["rows"] = rows
                });
            }
            return result;
        }

        private static void Put(JObject o, string key, string value, int max)
        {
            if (!string.IsNullOrWhiteSpace(value)) o[key] = Clip(value.Trim(), max);
        }

        private static void PutNumber(JObject o, string key, double? value)
        {
            if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)) o[key] = Round(value.Value, 4);
        }

        private static double Round(double value, int digits) { return Math.Round(value, digits); }

        private static string Clip(string text, int max)
        {
            if (text == null) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
