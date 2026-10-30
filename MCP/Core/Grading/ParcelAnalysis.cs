using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitMCP.Core.Grading
{
    // 坵塊法的「縣市預設檔」、統計彙總與計算書 CSV。純函式、零 Revit 相依，可 NUnit 測試。
    // 方法定義見 domain/parcel-slope-analysis.md。

    /// <summary>
    /// 某法規體系（中央／水保／縣市）對坵塊法的參數規定。
    /// <b>只有 <see cref="Verified"/>=true 的預設檔核過原文</b>；其餘僅為摘要，使用前須人工核對。
    /// </summary>
    public sealed class ParcelPreset
    {
        public ParcelPreset(
            string key, string displayName,
            double maxCellSizeMeters, bool maxCellSizeExclusive,
            IReadOnlyList<double> allowedCellSizes,
            int? minMapScaleDenominator,
            IReadOnlyList<double> gradeThresholdsPercent,
            bool requiresOriginalAndCurrent,
            string legalSource, bool verified, string notes)
        {
            Key = key;
            DisplayName = displayName;
            MaxCellSizeMeters = maxCellSizeMeters;
            MaxCellSizeExclusive = maxCellSizeExclusive;
            AllowedCellSizes = allowedCellSizes;
            MinMapScaleDenominator = minMapScaleDenominator;
            GradeThresholdsPercent = gradeThresholdsPercent;
            RequiresOriginalAndCurrent = requiresOriginalAndCurrent;
            LegalSource = legalSource;
            Verified = verified;
            Notes = notes;
        }

        public string Key { get; }

        public string DisplayName { get; }

        /// <summary>格邊長上限（公尺）。</summary>
        public double MaxCellSizeMeters { get; }

        /// <summary>true＝必須「小於」上限（例：臺北 &lt;10m）；false＝「不大於」（例：中央 ≤25m）。</summary>
        public bool MaxCellSizeExclusive { get; }

        /// <summary>只允許這幾個邊長（例：水保 10m 或 25m）；null＝不限制，只受上限約束。</summary>
        public IReadOnlyList<double> AllowedCellSizes { get; }

        /// <summary>地形圖最小比例尺的分母（1200 ＝ 比例尺不小於 1/1200）；null＝未查證。</summary>
        public int? MinMapScaleDenominator { get; }

        /// <summary>坡度分級門檻（%，由小到大，右閉）。n 個門檻分成 n+1 級。</summary>
        public IReadOnlyList<double> GradeThresholdsPercent { get; }

        /// <summary>是否須原始地形與現況地形各算一次並附套疊。第一期工具只算一組，以警告提示。</summary>
        public bool RequiresOriginalAndCurrent { get; }

        public string LegalSource { get; }

        /// <summary>true＝已核對法規原文；false＝僅摘要，待人工核對。</summary>
        public bool Verified { get; }

        public string Notes { get; }
    }

    /// <summary>jurisdiction 解析結果。</summary>
    public sealed class ParcelPresetResolution
    {
        public ParcelPresetResolution(ParcelPreset preset, string requested, bool isFallback, string fallbackNote)
        {
            Preset = preset;
            Requested = requested;
            IsFallback = isFallback;
            FallbackNote = fallbackNote;
        }

        public ParcelPreset Preset { get; }

        /// <summary>使用者原始輸入（可為空）。</summary>
        public string Requested { get; }

        /// <summary>true＝輸入的縣市沒有預設檔，落回 central。</summary>
        public bool IsFallback { get; }

        public string FallbackNote { get; }
    }

    public static class ParcelPresets
    {
        /// <summary>落回 central 時固定附上的說明。</summary>
        public const string FallbackNoteText = "本縣市無查證到的特別規定，沿用中央";

        public const string Central = "central";
        public const string SoilConservation = "soil_conservation";
        public const string Taipei = "taipei";
        public const string NewTaipei = "new_taipei";

        private static readonly ParcelPreset CentralPreset = new ParcelPreset(
            Central, "中央（建築技術規則）",
            maxCellSizeMeters: 25.0, maxCellSizeExclusive: false,
            allowedCellSizes: null,
            minMapScaleDenominator: 1200,
            gradeThresholdsPercent: new[] { ParcelSlopeMath.SteepThresholdPercent, ParcelSlopeMath.NoOpenSpaceThresholdPercent },
            requiresOriginalAndCurrent: false,
            legalSource: "建築技術規則建築設計施工編第261、262條（已核原文）",
            verified: true,
            notes: "格邊長不大於25m；比例尺不小於1/1200實測地形圖；>30%、>55% 兩個判定門檻。");

        private static readonly ParcelPreset SoilPreset = new ParcelPreset(
            SoilConservation, "水土保持計畫",
            maxCellSizeMeters: 25.0, maxCellSizeExclusive: false,
            allowedCellSizes: new[] { 10.0, 25.0 },
            minMapScaleDenominator: null,
            gradeThresholdsPercent: new[] { 5.0, 15.0, 30.0, 40.0, 55.0, 100.0 },
            requiresOriginalAndCurrent: false,
            legalSource: "水土保持技術規範第25、23條（僅摘要，未核原文）",
            verified: false,
            notes: "格邊長10m或25m二選一；坡度分級5/15/30/40/55/100%（七級）。比例尺未查證。"
                 + "§262 的判定欄（30%／55%）仍依建築技術規則計。");

        private static readonly ParcelPreset TaipeiPreset = new ParcelPreset(
            Taipei, "臺北市",
            maxCellSizeMeters: 10.0, maxCellSizeExclusive: true,
            allowedCellSizes: null,
            minMapScaleDenominator: 1000,
            gradeThresholdsPercent: new[] { ParcelSlopeMath.SteepThresholdPercent, ParcelSlopeMath.NoOpenSpaceThresholdPercent },
            requiresOriginalAndCurrent: false,
            legalSource: "臺北市規定（僅搜尋摘要，未核原文，待人工核對）",
            verified: false,
            notes: "格邊長須小於10m；地形圖比例尺不小於1/1000。未另列的門檻沿用中央。");

        private static readonly ParcelPreset NewTaipeiPreset = new ParcelPreset(
            NewTaipei, "新北市",
            maxCellSizeMeters: 25.0, maxCellSizeExclusive: false,
            allowedCellSizes: null,
            minMapScaleDenominator: null,
            gradeThresholdsPercent: new[] { ParcelSlopeMath.SteepThresholdPercent, ParcelSlopeMath.NoOpenSpaceThresholdPercent },
            requiresOriginalAndCurrent: true,
            legalSource: "新北市規定（僅搜尋摘要，未核原文，待人工核對）",
            verified: false,
            notes: "沿用25m；須以原始地形與現況地形各算一次並附套疊。比例尺與其餘門檻未另查證，沿用中央。");

        public static IReadOnlyList<ParcelPreset> All { get; } =
            new[] { CentralPreset, SoilPreset, TaipeiPreset, NewTaipeiPreset };

        /// <summary>
        /// 解析 jurisdiction。null／空白／central 直接回中央（非 fallback）；
        /// 已知別名回對應預設檔；其餘落回中央並附 <see cref="FallbackNoteText"/>。
        /// </summary>
        public static ParcelPresetResolution Resolve(string jurisdiction)
        {
            var raw = jurisdiction == null ? string.Empty : jurisdiction.Trim();
            if (raw.Length == 0)
            {
                return new ParcelPresetResolution(CentralPreset, raw, false, null);
            }

            // 正規化：不分大小寫、去掉空白／底線／連字號、台＝臺。
            var norm = raw.ToLowerInvariant()
                .Replace(" ", string.Empty).Replace("\t", string.Empty)
                .Replace("_", string.Empty).Replace("-", string.Empty)
                .Replace('台', '臺');
            switch (norm)
            {
                case "central":
                case "中央":
                    return new ParcelPresetResolution(CentralPreset, raw, false, null);
                case "soilconservation":
                case "水保":
                case "水土保持":
                case "水土保持計畫":
                    return new ParcelPresetResolution(SoilPreset, raw, false, null);
                case "taipei":
                case "taipeicity":
                case "臺北":
                case "臺北市":
                case "臺北市政府":
                    return new ParcelPresetResolution(TaipeiPreset, raw, false, null);
                case "newtaipei":
                case "newtaipeicity":
                case "新北":
                case "新北市":
                case "新北市政府":
                    return new ParcelPresetResolution(NewTaipeiPreset, raw, false, null);
                default:
                    return new ParcelPresetResolution(CentralPreset, raw, true, FallbackNoteText);
            }
        }

        /// <summary>
        /// 驗證格邊長符合該預設檔規定，違反即丟 <see cref="ArgumentOutOfRangeException"/>（拒絕執行，不是警告）。
        /// </summary>
        public static void ValidateCellSize(ParcelPreset preset, double cellSizeMeters)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            if (double.IsNaN(cellSizeMeters) || double.IsInfinity(cellSizeMeters) || cellSizeMeters <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters, "坵塊邊長必須是大於 0 的有限數值。");
            }

            var tooBig = preset.MaxCellSizeExclusive
                ? cellSizeMeters >= preset.MaxCellSizeMeters
                : cellSizeMeters > preset.MaxCellSizeMeters;
            if (tooBig)
            {
                var op = preset.MaxCellSizeExclusive ? "須小於" : "不得超過";
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters,
                    $"預設檔「{preset.DisplayName}」（jurisdiction={preset.Key}）規定坵塊邊長{op} "
                    + $"{preset.MaxCellSizeMeters.ToString("0.##", CultureInfo.InvariantCulture)} 公尺；"
                    + $"目前 {cellSizeMeters.ToString("0.##", CultureInfo.InvariantCulture)} m。來源：{preset.LegalSource}。");
            }

            if (preset.AllowedCellSizes != null
                && !preset.AllowedCellSizes.Any(a => Math.Abs(a - cellSizeMeters) < 1e-9))
            {
                var options = string.Join("m 或 ", preset.AllowedCellSizes.Select(
                    a => a.ToString("0.##", CultureInfo.InvariantCulture))) + "m";
                throw new ArgumentOutOfRangeException(
                    nameof(cellSizeMeters), cellSizeMeters,
                    $"預設檔「{preset.DisplayName}」（jurisdiction={preset.Key}）規定坵塊邊長只能是 {options}；"
                    + $"目前 {cellSizeMeters.ToString("0.##", CultureInfo.InvariantCulture)} m。來源：{preset.LegalSource}。");
            }
        }

        /// <summary>該預設檔應附上的警告（未驗證、須兩組地形等）。</summary>
        public static IReadOnlyList<string> Warnings(ParcelPresetResolution resolution)
        {
            var list = new List<string>();
            var p = resolution.Preset;

            if (resolution.IsFallback)
            {
                list.Add($"jurisdiction=「{resolution.Requested}」未辨識縣市，沿用中央（{ParcelPresets.FallbackNoteText}；"
                    + "已改用 central 預設檔）。");
            }

            if (!p.Verified)
            {
                list.Add($"預設檔「{p.DisplayName}」verified=false：{p.LegalSource}。"
                    + "僅為摘要、未核對法規原文，送審前請人工核對最新條文。");
            }

            if (p.RequiresOriginalAndCurrent)
            {
                list.Add($"預設檔「{p.DisplayName}」要求以原始地形與現況地形各算一次並附套疊；"
                    + "本次只計算一組（目前指定的等高線圖層），請另以另一組地形圖層再執行一次。");
            }

            if (p.MinMapScaleDenominator.HasValue)
            {
                list.Add($"預設檔「{p.DisplayName}」要求地形圖比例尺不小於 1/{p.MinMapScaleDenominator.Value}；"
                    + "工具無法驗證來源圖，請自行確認並記錄於計算書。");
            }

            return list;
        }
    }

    /// <summary>一個坵塊的完整計算結果（逐格明細）。</summary>
    public sealed class ParcelCellRecord
    {
        public string Label { get; set; }

        public int Col { get; set; }

        public int Row { get; set; }

        public ParcelPoint Center { get; set; }

        public IReadOnlyList<ParcelPoint> Corners { get; set; }

        /// <summary>四邊各自的交點數（順序同 Corners：邊 i = 角 i→角 i+1）。</summary>
        public int[] EdgeCounts { get; set; } = new int[4];

        /// <summary>n：四邊交點總和。</summary>
        public int IntersectionCount { get; set; }

        public double SlopePercent { get; set; }

        /// <summary>A_i：落在基地內的面積（㎡）；無基地範圍時為整格 L²。</summary>
        public double AreaSquareMeters { get; set; }

        public ParcelRestriction Restriction { get; set; }

        /// <summary>依預設檔分級門檻得到的等級標籤。</summary>
        public string GradeLabel { get; set; }
    }

    public sealed class ParcelGradeStat
    {
        public string Label { get; set; }

        public int CellCount { get; set; }

        public double AreaSquareMeters { get; set; }
    }

    /// <summary>一組網格的統計。平均坡度為面積加權：Σ(S×A)/ΣA。</summary>
    public sealed class ParcelStatistics
    {
        public int CellCount { get; set; }

        public double TotalAreaSquareMeters { get; set; }

        /// <summary>Σ(S_i × A_i)。</summary>
        public double SlopeAreaSum { get; set; }

        public double WeightedAverageSlopePercent { get; set; }

        public double MaxSlopePercent { get; set; }

        public double MinSlopePercent { get; set; }

        public int CountNone { get; set; }

        public int CountOpenSpaceOnly { get; set; }

        public int CountExcluded { get; set; }

        public double AreaNoneSquareMeters { get; set; }

        public double AreaOpenSpaceOnlySquareMeters { get; set; }

        public double AreaExcludedSquareMeters { get; set; }

        /// <summary>平均坡度超過 30% 的坵塊面積（§262 第1項第1款所指情形；均質區與但書須人工判讀，非可否開發結論）。</summary>
        public double AreaOver30SquareMeters { get; set; }

        public IReadOnlyList<ParcelGradeStat> Grades { get; set; } = new List<ParcelGradeStat>();
    }

    /// <summary>主組與半格平移組的差異（shifted − main）。</summary>
    public sealed class ParcelStatisticsDelta
    {
        public double AverageSlopePercent { get; set; }

        public double AreaOver30SquareMeters { get; set; }

        public double AreaOpenSpaceOnlySquareMeters { get; set; }

        public double AreaExcludedSquareMeters { get; set; }

        public double TotalAreaSquareMeters { get; set; }

        public int CellCount { get; set; }
    }

    public sealed class ParcelCsvContext
    {
        public ParcelPresetResolution Resolution { get; set; }

        public double CellSizeMeters { get; set; }

        public double ContourIntervalMeters { get; set; }

        public double RotationDegrees { get; set; }

        public double OriginOffsetXMeters { get; set; }

        public double OriginOffsetYMeters { get; set; }

        /// <summary>基地範圍來源說明；null＝未指定（整格計）。</summary>
        public string BoundaryDescription { get; set; }
    }

    public static class ParcelReport
    {
        /// <summary>判定欄文字（§262 逐格分段結果，非終局開發結論）。</summary>
        public static string VerdictLabel(ParcelRestriction restriction)
        {
            switch (restriction)
            {
                case ParcelRestriction.None:
                    return "≤30%：無§262坡度限制";
                case ParcelRestriction.OpenSpaceOnly:
                    return "30–55%：得作法定空地，不得配置建築物（§262Ⅲ）";
                case ParcelRestriction.ExcludedFromOpenSpace:
                    return ">55%：不得計入法定空地（§262Ⅲ）";
                default:
                    throw new ArgumentOutOfRangeException(nameof(restriction), restriction, "未知的限制類別。");
            }
        }

        /// <summary>
        /// 依門檻（右閉）給等級標籤。門檻 [30,55] → "≤30%"、"30–55%"、"&gt;55%"。
        /// </summary>
        public static string GradeLabel(double slopePercent, IReadOnlyList<double> thresholds)
        {
            if (double.IsNaN(slopePercent) || double.IsInfinity(slopePercent))
            {
                throw new ArgumentException("坡度百分比必須是有限數值。", nameof(slopePercent));
            }

            var labels = GradeLabels(thresholds);
            for (var i = 0; i < thresholds.Count; i++)
            {
                if (slopePercent <= thresholds[i])
                {
                    return labels[i];
                }
            }

            return labels[labels.Count - 1];
        }

        public static IReadOnlyList<string> GradeLabels(IReadOnlyList<double> thresholds)
        {
            var labels = new List<string>();
            for (var i = 0; i < thresholds.Count; i++)
            {
                labels.Add(i == 0
                    ? "≤" + Num(thresholds[0]) + "%"
                    : Num(thresholds[i - 1]) + "–" + Num(thresholds[i]) + "%");
            }

            labels.Add(">" + Num(thresholds[thresholds.Count - 1]) + "%");
            return labels;
        }

        public static ParcelStatistics Summarize(IReadOnlyList<ParcelCellRecord> cells, ParcelPreset preset)
        {
            var stats = new ParcelStatistics { CellCount = cells.Count };

            var grades = GradeLabels(preset.GradeThresholdsPercent)
                .Select(l => new ParcelGradeStat { Label = l }).ToList();

            if (cells.Count == 0)
            {
                stats.Grades = grades;
                return stats;
            }

            stats.TotalAreaSquareMeters = cells.Sum(c => c.AreaSquareMeters);
            stats.SlopeAreaSum = cells.Sum(c => c.SlopePercent * c.AreaSquareMeters);
            stats.WeightedAverageSlopePercent = stats.TotalAreaSquareMeters > 0
                ? stats.SlopeAreaSum / stats.TotalAreaSquareMeters
                : 0.0;
            stats.MaxSlopePercent = cells.Max(c => c.SlopePercent);
            stats.MinSlopePercent = cells.Min(c => c.SlopePercent);

            foreach (var c in cells)
            {
                switch (c.Restriction)
                {
                    case ParcelRestriction.None:
                        stats.CountNone++;
                        stats.AreaNoneSquareMeters += c.AreaSquareMeters;
                        break;
                    case ParcelRestriction.OpenSpaceOnly:
                        stats.CountOpenSpaceOnly++;
                        stats.AreaOpenSpaceOnlySquareMeters += c.AreaSquareMeters;
                        break;
                    default:
                        stats.CountExcluded++;
                        stats.AreaExcludedSquareMeters += c.AreaSquareMeters;
                        break;
                }

                var label = GradeLabel(c.SlopePercent, preset.GradeThresholdsPercent);
                var g = grades.First(x => x.Label == label);
                g.CellCount++;
                g.AreaSquareMeters += c.AreaSquareMeters;
            }

            stats.AreaOver30SquareMeters = stats.AreaOpenSpaceOnlySquareMeters + stats.AreaExcludedSquareMeters;
            stats.Grades = grades;
            return stats;
        }

        public static ParcelStatisticsDelta Delta(ParcelStatistics main, ParcelStatistics shifted)
        {
            return new ParcelStatisticsDelta
            {
                AverageSlopePercent = shifted.WeightedAverageSlopePercent - main.WeightedAverageSlopePercent,
                AreaOver30SquareMeters = shifted.AreaOver30SquareMeters - main.AreaOver30SquareMeters,
                AreaOpenSpaceOnlySquareMeters = shifted.AreaOpenSpaceOnlySquareMeters - main.AreaOpenSpaceOnlySquareMeters,
                AreaExcludedSquareMeters = shifted.AreaExcludedSquareMeters - main.AreaExcludedSquareMeters,
                TotalAreaSquareMeters = shifted.TotalAreaSquareMeters - main.TotalAreaSquareMeters,
                CellCount = shifted.CellCount - main.CellCount,
            };
        }

        /// <summary>
        /// 計算書 CSV（內容為字串，寫檔時請用 UTF-8 with BOM）。
        /// 欄位：編號、交點數(n)、坡度(%)、基地內面積(㎡)、坡度面積(S×A)、坡度等級、判定；
        /// 空一列後接 key,value 摘要列。
        /// </summary>
        public static string BuildCsv(
            IReadOnlyList<ParcelCellRecord> cells, ParcelStatistics stats, ParcelCsvContext ctx)
        {
            var sb = new StringBuilder();
            // 四邊交點數的方向以「網格自身座標軸」為準：下＝角0→角1、右＝角1→角2、上＝角2→角3、左＝角3→角0；
            // 旋轉角為 0 時即圖面的下／右／上／左，旋轉後則隨網格一起轉。四邊之和 = 交點數(n)。
            sb.Append("編號,交點數(n),坡度(%),基地內面積(㎡),坡度面積(S×A),坡度等級,判定,"
                + "下邊交點數,右邊交點數,上邊交點數,左邊交點數\r\n");

            foreach (var c in cells)
            {
                sb.Append(Csv(c.Label)).Append(',')
                  .Append(c.IntersectionCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Fixed(c.SlopePercent, 2)).Append(',')
                  .Append(Fixed(c.AreaSquareMeters, 2)).Append(',')
                  .Append(Fixed(c.SlopePercent * c.AreaSquareMeters, 2)).Append(',')
                  .Append(Csv(c.GradeLabel)).Append(',')
                  .Append(Csv(VerdictLabel(c.Restriction))).Append(',')
                  .Append(EdgeCount(c, 0)).Append(',').Append(EdgeCount(c, 1)).Append(',')
                  .Append(EdgeCount(c, 2)).Append(',').Append(EdgeCount(c, 3))
                  .Append("\r\n");
            }

            sb.Append("\r\n");

            var p = ctx.Resolution.Preset;
            Row(sb, "jurisdiction",
                p.Key + "（" + p.DisplayName + "）" + (ctx.Resolution.IsFallback
                    ? "；輸入「" + ctx.Resolution.Requested + "」" + ParcelPresets.FallbackNoteText
                    : string.Empty));
            Row(sb, "法規來源", p.LegalSource);
            Row(sb, "預設檔已核原文(verified)", p.Verified ? "true" : "false（僅摘要，送審前須人工核對）");
            Row(sb, "方格邊長L(m)", Fixed(ctx.CellSizeMeters, 2));
            Row(sb, "等高線首曲線間距h(m)", Fixed(ctx.ContourIntervalMeters, 3));
            Row(sb, "網格旋轉角(度)", Fixed(ctx.RotationDegrees, 2));
            Row(sb, "原點平移X/Y(m)",
                Fixed(ctx.OriginOffsetXMeters, 3) + " / " + Fixed(ctx.OriginOffsetYMeters, 3));
            Row(sb, "基地範圍", ctx.BoundaryDescription ?? "未指定（以整格計，A=L²）");
            Row(sb, "公式", "S(%)=n×3.14×h/(8×L)×100（建築技術規則建築設計施工編第261條）");
            Row(sb, "坵塊數", stats.CellCount.ToString(CultureInfo.InvariantCulture));
            Row(sb, "面積總和ΣA(㎡)", Fixed(stats.TotalAreaSquareMeters, 2));
            Row(sb, "坡度面積總和Σ(S×A)", Fixed(stats.SlopeAreaSum, 2));
            Row(sb, "平均坡度(%)＝Σ(S×A)/ΣA",
                Fixed(stats.SlopeAreaSum, 2) + " / " + Fixed(stats.TotalAreaSquareMeters, 2)
                + " = " + Fixed(stats.WeightedAverageSlopePercent, 2));
            Row(sb, "備註", "判定欄為§262逐格坡度分段結果，非可否開發結論；均質區劃分與但書須人工判讀。");
            return sb.ToString();
        }

        /// <summary>
        /// csvPath 是否為絕對路徑：磁碟機代號開頭且後接分隔符（C:\ 或 C:/），或 UNC（\\server\share）。
        /// 相對路徑、磁碟機相對（C:foo）、根相對（\foo）都不算。
        /// </summary>
        public static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            {
                return true;
            }

            return path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        private static string EdgeCount(ParcelCellRecord c, int edge)
        {
            return (c.EdgeCounts != null && c.EdgeCounts.Length > edge ? c.EdgeCounts[edge] : 0)
                .ToString(CultureInfo.InvariantCulture);
        }

        private static void Row(StringBuilder sb, string key, string value)
        {
            sb.Append(Csv(key)).Append(',').Append(Csv(value)).Append("\r\n");
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        private static string Fixed(double v, int digits)
        {
            return v.ToString("F" + digits, CultureInfo.InvariantCulture);
        }

        private static string Num(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
