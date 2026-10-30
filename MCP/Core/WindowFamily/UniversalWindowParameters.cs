using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>共用參數的資料型別（對應 Revit 共用參數檔的 DATATYPE 欄）。</summary>
    public enum SharedParamDataType
    {
        Integer,
        Text,
        Area,
        Length,
        Number
    }

    /// <summary>一個 UW_ 共用參數的定義。GUID 為正本，永不更動。</summary>
    public class UniversalWindowParameter
    {
        public string Name { get; set; }
        public SharedParamDataType DataType { get; set; }
        public Guid Guid { get; set; }
        public string Description { get; set; }
        /// <summary>true = 由生成器填值；false = 留白給使用者。</summary>
        public bool FilledByGenerator { get; set; }

        public string DataTypeToken
        {
            get
            {
                switch (DataType)
                {
                    case SharedParamDataType.Integer: return "INTEGER";
                    case SharedParamDataType.Text: return "TEXT";
                    case SharedParamDataType.Area: return "AREA";
                    case SharedParamDataType.Length: return "LENGTH";
                    case SharedParamDataType.Number: return "NUMBER";
                    default: throw new InvalidOperationException("未知的共用參數型別");
                }
            }
        }
    }

    /// <summary>
    /// 萬用窗共用參數的唯一正本（SPEC §7）。
    /// 這份 C# 表與 MCP/Resources/shared-parameters/universal-window.txt 必須逐字一致，
    /// 由 MCP.Tests/WindowFamily/UniversalWindowParametersTests.cs 機械比對，避免兩處漂移。
    /// GUID 一旦發布即凍結：改 GUID 等於換成另一個參數，既有專案的明細表會斷掉。
    /// </summary>
    public static class UniversalWindowParameters
    {
        /// <summary>共用參數檔中的群組 ID 與名稱。</summary>
        public const int GroupId = 1;
        public const string GroupName = "UniversalWindow";

        // 參數名稱常數（Revit 端設值時引用，避免字串散落）
        public const string RowCount = "UW_排數";
        public const string ColCount = "UW_樘數";
        public const string SashCount = "UW_扇數";
        public const string OpenableCount = "UW_可開扇數";
        public const string Operations = "UW_開啟方式";
        public const string GlassArea = "UW_玻璃面積";
        public const string DaylightArea = "UW_有效採光面積";
        public const string OpenableArea = "UW_可開口面積";
        public const string MoWidth = "UW_MO寬";
        public const string MoHeight = "UW_MO高";
        public const string AirtightnessClass = "UW_氣密等級";
        public const string WatertightnessClass = "UW_水密等級";
        public const string WindLoad = "UW_耐風壓";
        public const string GlassKind = "UW_玻璃種類";
        public const string FireRating = "UW_防火時效";
        public const string BrandSeries = "UW_廠牌系列";
        public const string Uaf = "UW_Uaf";
        public const string SolarFactor = "UW_SF";

        private static readonly List<UniversalWindowParameter> _all = new List<UniversalWindowParameter>
        {
            P(RowCount,             SharedParamDataType.Integer, "7a1e0001-9c4b-4f2a-9d3e-b0d500000001", "排數 R（上到下）",                         true),
            P(ColCount,             SharedParamDataType.Integer, "7a1e0002-9c4b-4f2a-9d3e-b0d500000002", "樘數 C（自室外看，左到右）",               true),
            P(SashCount,            SharedParamDataType.Integer, "7a1e0003-9c4b-4f2a-9d3e-b0d500000003", "扇總數",                                   true),
            P(OpenableCount,        SharedParamDataType.Integer, "7a1e0004-9c4b-4f2a-9d3e-b0d500000004", "可開扇數（非固定扇）",                     true),
            P(Operations,           SharedParamDataType.Text,    "7a1e0005-9c4b-4f2a-9d3e-b0d500000005", "開啟方式摘要，例 R1: F | R2: CL,CR",        true),
            P(GlassArea,            SharedParamDataType.Area,    "7a1e0006-9c4b-4f2a-9d3e-b0d500000006", "玻璃面積合計",                             true),
            P(DaylightArea,         SharedParamDataType.Area,    "7a1e0007-9c4b-4f2a-9d3e-b0d500000007", "有效採光面積（扣 FL+75 cm 以下）",          true),
            P(OpenableArea,         SharedParamDataType.Area,    "7a1e0008-9c4b-4f2a-9d3e-b0d500000008", "可開口面積（橫拉以半扇計）",               true),
            P(MoWidth,              SharedParamDataType.Length,  "7a1e0009-9c4b-4f2a-9d3e-b0d500000009", "開口寬（框外緣每側加預留）",               true),
            P(MoHeight,             SharedParamDataType.Length,  "7a1e000a-9c4b-4f2a-9d3e-b0d50000000a", "開口高（框外緣每側加預留）",               true),
            P(AirtightnessClass,    SharedParamDataType.Text,    "7a1e000b-9c4b-4f2a-9d3e-b0d50000000b", "氣密等級 CNS 11527，預設 8 級",             true),
            P(WatertightnessClass,  SharedParamDataType.Text,    "7a1e000c-9c4b-4f2a-9d3e-b0d50000000c", "水密等級 CNS 11528，預設 35 級",            true),
            P(WindLoad,             SharedParamDataType.Number,  "7a1e000d-9c4b-4f2a-9d3e-b0d50000000d", "耐風壓 CNS 11526，kgf/m²，預設 280",        true),
            P(GlassKind,            SharedParamDataType.Text,    "7a1e000e-9c4b-4f2a-9d3e-b0d50000000e", "玻璃種類，留白給使用者",                   false),
            P(FireRating,           SharedParamDataType.Text,    "7a1e000f-9c4b-4f2a-9d3e-b0d50000000f", "防火時效，留白給使用者",                   false),
            P(BrandSeries,          SharedParamDataType.Text,    "7a1e0010-9c4b-4f2a-9d3e-b0d500000010", "廠牌系列，留白給使用者",                   false),
            P(Uaf,                  SharedParamDataType.Number,  "7a1e0011-9c4b-4f2a-9d3e-b0d500000011", "外殼平均熱傳透率用 Uaf，留白",             false),
            P(SolarFactor,          SharedParamDataType.Number,  "7a1e0012-9c4b-4f2a-9d3e-b0d500000012", "遮陽係數 SF，留白",                        false)
        };

        /// <summary>全部 UW_ 參數定義（順序即共用參數檔的行序）。</summary>
        public static IReadOnlyList<UniversalWindowParameter> All { get { return _all; } }

        private static UniversalWindowParameter P(string name, SharedParamDataType type, string guid, string description, bool filled)
        {
            return new UniversalWindowParameter
            {
                Name = name,
                DataType = type,
                Guid = new Guid(guid),
                Description = description,
                FilledByGenerator = filled
            };
        }

        /// <summary>
        /// 產生 Revit 共用參數檔（.txt）內容。Tab 分隔、CRLF 換行。
        /// 寫檔請用 UTF-8 with BOM（Revit 2019 起可讀）。
        /// </summary>
        public static string BuildSharedParameterFileContent()
        {
            var sb = new StringBuilder();
            const string nl = "\r\n";
            sb.Append("# This is a Revit shared parameter file.").Append(nl);
            sb.Append("# Do not edit manually.").Append(nl);
            sb.Append("# 正本：MCP/Core/WindowFamily/UniversalWindowParameters.cs（GUID 凍結，勿改）").Append(nl);
            sb.Append("*META\tVERSION\tMINVERSION").Append(nl);
            sb.Append("META\t2\t1").Append(nl);
            sb.Append("*GROUP\tID\tNAME").Append(nl);
            sb.Append("GROUP\t").Append(GroupId.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(GroupName).Append(nl);
            sb.Append("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE").Append(nl);
            foreach (var p in _all)
            {
                sb.Append("PARAM\t")
                  .Append(p.Guid.ToString("D")).Append('\t')
                  .Append(p.Name).Append('\t')
                  .Append(p.DataTypeToken).Append('\t')
                  .Append('\t')                                   // DATACATEGORY：非族群型別參數，留空
                  .Append(GroupId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append('1').Append('\t')                        // VISIBLE
                  .Append(p.Description).Append('\t')
                  .Append('1').Append('\t')                        // USERMODIFIABLE
                  .Append('0')                                     // HIDEWHENNOVALUE
                  .Append(nl);
            }
            return sb.ToString();
        }
    }
}
