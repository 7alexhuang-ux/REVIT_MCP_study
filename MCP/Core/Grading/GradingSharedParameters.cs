#if REVIT2024_OR_GREATER
using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
// RevitMCP.Application（外掛進入點類別）會遮蔽 Revit API 的 Application，故以別名區分。
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 整地方案共用參數：把登記簿（ES）指標回寫到設計地形實例參數，
    /// 讓原生明細表能讀（明細表讀不到 Extensible Storage）。
    /// 共用參數檔自動生成於 %APPDATA%\RevitMCP\GradingSharedParams.txt。
    /// </summary>
    public static class GradingSharedParameters
    {
        private const string GroupName = "RevitMCP整地";

        /// <summary>明細表欄位順序（= 建表欄位順序）。文字欄在前、數值欄在後。</summary>
        public static readonly string[] OrderedNames =
        {
            "RevitMCP方案名", "RevitMCP模式", "RevitMCP參數",
            "RevitMCP挖方m3", "RevitMCP填方m3", "RevitMCP淨土方m3", "RevitMCP挖填總量m3",
            "RevitMCP最大挖深m", "RevitMCP最大填高m", "RevitMCP擾動面積m2",
            "RevitMCP運土鬆方m3", "RevitMCP所需自然方m3", "RevitMCP實質淨土方m3"
        };

        private static readonly string[] TextNames = { "RevitMCP方案名", "RevitMCP模式", "RevitMCP參數" };

        /// <summary>綁定全部共用參數到 Toposolid 類別（實例、PG_DATA）；已綁定者跳過。</summary>
        public static void EnsureBindings(Document doc)
        {
            var app = doc.Application;
            var definitionFile = OpenOrCreateDefinitionFile(app);
            var group = definitionFile.Groups.get_Item(GroupName) ?? definitionFile.Groups.Create(GroupName);

            var categories = app.Create.NewCategorySet();
            categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Toposolid));
            var bindingMap = doc.ParameterBindings;

            foreach (var name in OrderedNames)
            {
                var definition = group.Definitions.get_Item(name);
                if (definition == null)
                {
                    var specType = TextNames.Contains(name) ? SpecTypeId.String.Text : SpecTypeId.Number;
                    definition = group.Definitions.Create(
                        new ExternalDefinitionCreationOptions(name, specType));
                }

                if (!bindingMap.Contains(definition))
                {
                    bindingMap.Insert(definition, app.Create.NewInstanceBinding(categories),
                        GroupTypeId.Data);
                }
            }
        }

        /// <summary>把方案記錄的指標寫入設計地形實例參數（無值欄位寫 0／空字串）。</summary>
        public static void WriteSchemeMetrics(Document doc, Toposolid design, JObject record)
        {
            SetText(design, "RevitMCP方案名", record.Value<string>("SchemeName") ?? string.Empty);
            SetText(design, "RevitMCP模式", record.Value<string>("Mode") ?? string.Empty);
            SetText(design, "RevitMCP參數", DescribeParameters(record));
            var cut = record.Value<double?>("CutCubicMeters") ?? 0;
            var fill = record.Value<double?>("FillCubicMeters") ?? 0;
            SetNumber(design, "RevitMCP挖方m3", cut);
            SetNumber(design, "RevitMCP填方m3", fill);
            SetNumber(design, "RevitMCP淨土方m3", cut - fill);
            SetNumber(design, "RevitMCP挖填總量m3", cut + fill);
            SetNumber(design, "RevitMCP最大挖深m", record.Value<double?>("MaxCutDepthMeters") ?? 0);
            SetNumber(design, "RevitMCP最大填高m", record.Value<double?>("MaxFillHeightMeters") ?? 0);
            SetNumber(design, "RevitMCP擾動面積m2", record.Value<double?>("DisturbedAreaSquareMeters") ?? 0);

            // 鬆實方三本帳：欄位鍵名以 MCP/Core/Grading/EarthworkLedger.cs 序列化名為準
            // （已核對：HaulVolumeLooseCubicMeters／RequiredBankForFillCubicMeters／EffectiveNetCubicMeters）；
            // 無 Ledger 的方案寫 0。
            var ledger = record["Ledger"] as JObject;
            SetNumber(design, "RevitMCP運土鬆方m3", ledger?.Value<double?>("HaulVolumeLooseCubicMeters") ?? 0);
            SetNumber(design, "RevitMCP所需自然方m3", ledger?.Value<double?>("RequiredBankForFillCubicMeters") ?? 0);
            SetNumber(design, "RevitMCP實質淨土方m3", cut - (ledger?.Value<double?>("RequiredBankForFillCubicMeters") ?? 0));
        }

        private static string DescribeParameters(JObject record)
        {
            var mode = record.Value<string>("Mode");
            if (mode == "offset_transition")
            {
                return $"外延 {record.Value<double?>("OffsetDistanceMeters") ?? 0:0.#}m";
            }

            if (mode == "slope_transition")
            {
                return $"坡率 {record.Value<string>("SlopeRatio") ?? "?"}";
            }

            return "投影直貼";
        }

        private static void SetText(Element element, string name, string value)
        {
            var parameter = element.LookupParameter(name);
            if (parameter != null && !parameter.IsReadOnly) { parameter.Set(value); }
        }

        private static void SetNumber(Element element, string name, double value)
        {
            var parameter = element.LookupParameter(name);
            if (parameter != null && !parameter.IsReadOnly) { parameter.Set(Math.Round(value, 2)); }
        }

        private static DefinitionFile OpenOrCreateDefinitionFile(RevitApp app)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCP", "GradingSharedParams.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
            }

            app.SharedParametersFilename = path;
            return app.OpenSharedParameterFile()
                ?? throw new InvalidOperationException($"無法開啟共用參數檔：{path}");
        }
    }
}
#endif
