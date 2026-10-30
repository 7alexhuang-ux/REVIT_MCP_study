using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>範例窗型（對應 configurator-demo.html 的 PRESETS）。</summary>
    public class WindowPreset
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double SillHeight { get; set; }
        public double[] RowRatios { get; set; }
        public double[] ColRatios { get; set; }
        /// <summary>
        /// 格子編碼：排以 `/` 分隔（上→下），樘以 `|` 分隔（左→右），同格的扇以 `+` 分隔；
        /// 扇代碼＝F 固定、CL／CR 外推（鉸鏈左／右）、S 橫拉、A 上懸、H 下懸、V 垂直上下拉、SC 紗窗。
        /// 例：`F|F/CL+CR|CL+CR`＝上排兩樘固定、下排兩樘各兩扇外推。
        /// </summary>
        public string Layout { get; set; }

        public override string ToString() { return Name; }
    }

    /// <summary>
    /// 萬用窗 add-in 對話框的「規格組裝層」：保存 UI 狀態、組出 generate_window_family 的規格 JSON。
    /// 純 C#，不依賴 WPF／Revit API，可單元測試。add-in 與 MCP 兩個入口共用同一個引擎
    /// （<see cref="WindowFamilySpec"/>／<see cref="WindowLayoutMath"/>），本類別只負責「狀態 → 規格 JSON」。
    /// </summary>
    public class WindowSpecBuilder
    {
        // ── 預設值（與 SPEC §5、demo FRAME_DEF 一致）──
        public const double DefaultFrameOuter = 4.5;
        public const double DefaultMullion = 4.4;
        public const double DefaultSash = 5.0;
        public const double DefaultFrameDepth = 7.0;
        public const double DefaultGlassMm = 8.0;

        /// <summary>扇代碼 → 型式（含鉸鏈）。</summary>
        private static readonly string[] SashCodes = { "F", "CL", "CR", "S", "A", "H", "V", "SC" };

        // ── 狀態 ──
        public int Rows { get; private set; }
        public int Cols { get; private set; }
        /// <summary>Cells[row][col] = 該格的扇（row 0＝最上排，col 0＝最左樘，自室外看）。</summary>
        public List<SashSpec>[][] Cells { get; private set; }
        /// <summary>已正規化（總和 100）的排高比。</summary>
        public double[] RowRatios { get; private set; }
        /// <summary>已正規化（總和 100）的樘寬比。</summary>
        public double[] ColRatios { get; private set; }

        public double Width { get; set; }
        public double Height { get; set; }
        public double SillHeight { get; set; }

        public double FrameOuter { get; set; }
        public double Mullion { get; set; }
        public double Sash { get; set; }
        public double FrameDepth { get; set; }
        public double GlassMm { get; set; }

        /// <summary>true＝交疊同扇框寬（預設，ADR D-005）；false＝用 <see cref="OverlapMm"/>。</summary>
        public bool OverlapEqualsSash { get; set; }
        public double OverlapMm { get; set; }

        /// <summary>true＝族名／型別名由目前狀態自動產生。</summary>
        public bool AutoNames { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }

        public string OutputDir { get; set; }
        public bool LoadIntoProject { get; set; }
        public bool Overwrite { get; set; }

        public WindowSpecBuilder()
        {
            FrameOuter = DefaultFrameOuter;
            Mullion = DefaultMullion;
            Sash = DefaultSash;
            FrameDepth = DefaultFrameDepth;
            GlassMm = DefaultGlassMm;
            OverlapEqualsSash = true;
            OverlapMm = 50;
            AutoNames = true;
            FamilyName = "";
            TypeName = "";
            OutputDir = "";
            LoadIntoProject = true;
            Overwrite = false;
            ApplyPreset(Presets[0]);
        }

        // ────────────────────────────────────────────────
        // 範例
        // ────────────────────────────────────────────────

        private static readonly List<WindowPreset> _presets = new List<WindowPreset>
        {
            P("ufl", "上固定下外推", 240, 180, 90, new double[] { 35, 65 }, new double[] { 50, 50 }, "F|F/CL+CR|CL+CR"),
            P("sl", "橫拉窗", 180, 150, 90, new double[] { 100 }, new double[] { 100 }, "S+S"),
            P("fx", "固定窗", 120, 150, 90, new double[] { 100 }, new double[] { 100 }, "F"),
            P("up", "上窗（上懸）", 180, 210, 60, new double[] { 30, 70 }, new double[] { 100 }, "A/F"),
            P("dn", "下窗（橫拉）", 180, 210, 60, new double[] { 40, 60 }, new double[] { 100 }, "F/S+S"),
            P("tri", "三樘組合", 360, 180, 90, new double[] { 100 }, new double[] { 30, 40, 30 }, "F|S+S|F"),
            P("tk", "三軌橫拉含紗窗", 180, 150, 90, new double[] { 100 }, new double[] { 100 }, "S+S+SC"),
            P("dh", "垂直上下拉", 90, 150, 90, new double[] { 100 }, new double[] { 100 }, "V+V"),
            P("hp", "下懸內倒氣窗", 60, 60, 150, new double[] { 100 }, new double[] { 100 }, "H"),
            P("three", "上中下三排", 240, 240, 45, new double[] { 25, 50, 25 }, new double[] { 50, 50 }, "A|A/S+S|S+S/F|F"),
        };

        private static WindowPreset P(string key, string name, double w, double h, double sill,
            double[] rows, double[] cols, string layout)
        {
            return new WindowPreset
            {
                Key = key, Name = name, Width = w, Height = h, SillHeight = sill,
                RowRatios = rows, ColRatios = cols, Layout = layout
            };
        }

        /// <summary>內建範例（demo 的 10 個）。</summary>
        public static IList<WindowPreset> Presets { get { return _presets; } }

        /// <summary>套用範例：覆蓋格子、尺寸、比例；框料／輸出設定維持不變（與 demo 行為一致）。</summary>
        public void ApplyPreset(WindowPreset preset)
        {
            if (preset == null) throw new ArgumentNullException("preset");
            var rowTexts = preset.Layout.Split('/');
            int rows = rowTexts.Length;
            int cols = rowTexts[0].Split('|').Length;

            var cells = new List<SashSpec>[rows][];
            for (int r = 0; r < rows; r++)
            {
                var cellTexts = rowTexts[r].Split('|');
                if (cellTexts.Length != cols)
                    throw new ArgumentException("範例格子編碼每排樘數不一致：" + preset.Layout);
                cells[r] = new List<SashSpec>[cols];
                for (int c = 0; c < cols; c++)
                    cells[r][c] = cellTexts[c].Split('+').Select(ParseSashCode).ToList();
            }

            Rows = rows;
            Cols = cols;
            Cells = cells;
            RowRatios = (double[])preset.RowRatios.Clone();
            ColRatios = (double[])preset.ColRatios.Clone();
            Width = preset.Width;
            Height = preset.Height;
            SillHeight = preset.SillHeight;
        }

        /// <summary>扇代碼（F／CL／CR／S／A／H／V／SC）轉 <see cref="SashSpec"/>。</summary>
        public static SashSpec ParseSashCode(string code)
        {
            switch ((code ?? "").Trim().ToUpperInvariant())
            {
                case "F": return new SashSpec { Type = SashType.Fixed };
                case "CL": return new SashSpec { Type = SashType.Casement, Hinge = HingeSide.Left };
                case "CR": return new SashSpec { Type = SashType.Casement, Hinge = HingeSide.Right };
                case "S": return new SashSpec { Type = SashType.Sliding };
                case "A": return new SashSpec { Type = SashType.Awning };
                case "H": return new SashSpec { Type = SashType.Hopper };
                case "V": return new SashSpec { Type = SashType.SlidingV };
                case "SC": return new SashSpec { Type = SashType.Screen };
                default:
                    throw new ArgumentException("未知的扇代碼「" + code + "」，只接受 " + string.Join("／", SashCodes) + "。");
            }
        }

        // ────────────────────────────────────────────────
        // 格子編輯
        // ────────────────────────────────────────────────

        /// <summary>
        /// 改排數／樘數。既有格子（左上對齊）保留設定；新增的格子預設為單扇固定；
        /// 排高比或樘寬比只有在該方向的數量改變時才重設為等分。
        /// </summary>
        public void SetGrid(int rows, int cols)
        {
            rows = Clamp(rows, 1, WindowFamilySpec.MaxRows);
            cols = Clamp(cols, 1, WindowFamilySpec.MaxCols);

            var cells = new List<SashSpec>[rows][];
            for (int r = 0; r < rows; r++)
            {
                cells[r] = new List<SashSpec>[cols];
                for (int c = 0; c < cols; c++)
                {
                    cells[r][c] = (r < Rows && c < Cols)
                        ? Cells[r][c]
                        : new List<SashSpec> { new SashSpec { Type = SashType.Fixed } };
                }
            }

            if (rows != Rows) RowRatios = EvenRatios(rows);
            if (cols != Cols) ColRatios = EvenRatios(cols);
            Rows = rows;
            Cols = cols;
            Cells = cells;
        }

        /// <summary>改某格的扇數（1–4）。增加時複製最後一扇的型式（外推的鉸鏈左右互換），減少時截尾。</summary>
        public void SetSashCount(int row, int col, int count)
        {
            count = Clamp(count, 1, WindowFamilySpec.MaxSashesPerCell);
            var list = Cells[row][col];
            while (list.Count > count) list.RemoveAt(list.Count - 1);
            while (list.Count < count)
            {
                var last = list[list.Count - 1];
                var add = new SashSpec { Type = last.Type, Hinge = last.Hinge, SwingRaw = last.SwingRaw };
                if (add.Type == SashType.Casement)
                    add.Hinge = last.Hinge == HingeSide.Left ? HingeSide.Right : HingeSide.Left;
                list.Add(add);
            }
        }

        /// <summary>改某扇的型式，並清掉該型式不適用的欄位（軌序、開向），避免輸出無意義的 JSON。</summary>
        public void SetSashType(SashSpec sash, SashType type)
        {
            if (sash == null) throw new ArgumentNullException("sash");
            sash.Type = type;
            if (!sash.IsSlidingClass) sash.Track = null;
            if (!(type == SashType.Casement || type == SashType.Awning || type == SashType.Hopper))
                sash.SwingRaw = null;
        }

        /// <summary>某型式是否有「鉸鏈左右」「開向」「軌序」可調（UI 用來啟用／停用控制項）。</summary>
        public static bool UsesHinge(SashType t) { return t == SashType.Casement; }
        public static bool UsesSwing(SashType t) { return t == SashType.Casement || t == SashType.Awning || t == SashType.Hopper; }
        public static bool UsesTrack(SashType t) { return t == SashType.Sliding || t == SashType.SlidingV || t == SashType.Screen; }

        /// <summary>小地圖用：某格的扇縮寫，例 `CL+CR`。</summary>
        public string CellSummary(int row, int col)
        {
            return string.Join("+", Cells[row][col].Select(s => s.Abbreviation).ToArray());
        }

        // ────────────────────────────────────────────────
        // 比例
        // ────────────────────────────────────────────────

        /// <summary>等分比例（總和精確 100，最後一個吸收進位誤差）。</summary>
        public static double[] EvenRatios(int count)
        {
            return NormalizeRatios(Enumerable.Repeat(1.0, count).ToArray(), count);
        }

        /// <summary>
        /// 把使用者輸入的比例補滿 100：
        /// ① 少給一個值且總和小於 100 → 最後一個自動補足（例 2 排輸入 35 → 35／65）；
        /// ② 個數正確 → 依比例縮放到總和 100（例 1:2 → 33.33／66.67）；
        /// 結果取到小數 2 位，總和精確為 100（最後一個吸收誤差）。個數不符、非正值丟 <see cref="WindowSpecException"/>。
        /// </summary>
        public static double[] NormalizeRatios(double[] values, int expectedCount)
        {
            if (values == null) throw new WindowSpecException("比例不可為空。");
            var v = values.ToList();
            double sum0 = v.Sum();
            if (v.Count == expectedCount - 1 && v.Count > 0 && sum0 < 100.0 && v.All(x => x > 0))
                v.Add(100.0 - sum0);
            if (v.Count != expectedCount)
                throw new WindowSpecException(
                    string.Format(CultureInfo.InvariantCulture, "比例需要 {0} 個值，目前給了 {1} 個。", expectedCount, values.Length));
            if (v.Any(x => x <= 0 || double.IsNaN(x) || double.IsInfinity(x)))
                throw new WindowSpecException("比例必須都大於 0。");

            double total = v.Sum();
            var result = new double[expectedCount];
            double acc = 0;
            for (int i = 0; i < expectedCount - 1; i++)
            {
                result[i] = Math.Round(v[i] * 100.0 / total, 2);
                acc += result[i];
            }
            result[expectedCount - 1] = Math.Round(100.0 - acc, 2);
            if (result[expectedCount - 1] <= 0)
                throw new WindowSpecException("比例差距過大，最後一項算出非正值。");
            return result;
        }

        /// <summary>解析比例文字（以逗號、冒號、空白、分號分隔），再依 <see cref="NormalizeRatios"/> 補滿 100。</summary>
        public static double[] ParseRatioText(string text, int expectedCount)
        {
            if (string.IsNullOrWhiteSpace(text))
                return EvenRatios(expectedCount);
            var parts = text.Split(new[] { ',', '，', ':', '：', ';', '；', ' ', '\t', '、', '%' }, StringSplitOptions.RemoveEmptyEntries);
            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                double d;
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new WindowSpecException("比例「" + parts[i] + "」不是數字。");
                values[i] = d;
            }
            return NormalizeRatios(values, expectedCount);
        }

        public void SetRowRatios(string text) { RowRatios = ParseRatioText(text, Rows); }
        public void SetColRatios(string text) { ColRatios = ParseRatioText(text, Cols); }

        /// <summary>比例陣列轉輸入框文字，例 `35 : 65`。</summary>
        public static string RatioText(double[] ratios)
        {
            return string.Join(" : ", ratios.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture)).ToArray());
        }

        // ────────────────────────────────────────────────
        // 命名
        // ────────────────────────────────────────────────

        /// <summary>自動族名：`UW-R{排}C{樘}-{不重複的扇縮寫，依出現順序以 - 相連}`，例 `UW-R2C2-F-CL-CR`。</summary>
        public string AutoFamilyName()
        {
            var ops = new List<string>();
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    foreach (var s in Cells[r][c])
                        if (!ops.Contains(s.Abbreviation)) ops.Add(s.Abbreviation);
            return string.Format(CultureInfo.InvariantCulture, "UW-R{0}C{1}-{2}", Rows, Cols, string.Join("-", ops.ToArray()));
        }

        /// <summary>自動型別名：`{總寬}x{總高}`（cm，最多 2 位小數），例 `240x180`。</summary>
        public string AutoTypeName()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}x{1}",
                Width.ToString("0.##", CultureInfo.InvariantCulture),
                Height.ToString("0.##", CultureInfo.InvariantCulture));
        }

        public string EffectiveFamilyName { get { return AutoNames ? AutoFamilyName() : (FamilyName ?? ""); } }
        public string EffectiveTypeName { get { return AutoNames ? AutoTypeName() : (TypeName ?? ""); } }

        // ────────────────────────────────────────────────
        // 輸出規格 JSON
        // ────────────────────────────────────────────────

        /// <summary>型式 → 規格 JSON 的 type 字串。</summary>
        public static string TypeToken(SashType t)
        {
            switch (t)
            {
                case SashType.Fixed: return "fixed";
                case SashType.Casement: return "casement";
                case SashType.Sliding: return "sliding";
                case SashType.Awning: return "awning";
                case SashType.Hopper: return "hopper";
                case SashType.SlidingV: return "sliding_v";
                case SashType.Screen: return "screen";
                default: throw new ArgumentOutOfRangeException("t");
            }
        }

        /// <summary>組出 generate_window_family 的規格 JSON（與 MCP 工具同形；比例已補滿 100）。</summary>
        public JObject ToJson()
        {
            var cells = new JArray();
            for (int r = 0; r < Rows; r++)
            {
                var row = new JArray();
                for (int c = 0; c < Cols; c++)
                {
                    var sashes = new JArray();
                    foreach (var s in Cells[r][c])
                    {
                        var o = new JObject { ["type"] = TypeToken(s.Type) };
                        if (UsesHinge(s.Type)) o["hinge"] = s.Hinge == HingeSide.Left ? "L" : "R";
                        if (UsesSwing(s.Type) && s.SwingRaw.HasValue)
                            o["swing"] = s.SwingRaw.Value == SwingDirection.In ? "in" : "out";
                        if (UsesTrack(s.Type) && s.Track.HasValue) o["track"] = s.Track.Value;
                        sashes.Add(o);
                    }
                    row.Add(new JObject
                    {
                        ["id"] = string.Format(CultureInfo.InvariantCulture, "R{0}C{1}", r + 1, c + 1),
                        ["sashes"] = sashes
                    });
                }
                cells.Add(row);
            }

            var typeParams = new JObject
            {
                ["frameOuter"] = FrameOuter,
                ["mullion"] = Mullion,
                ["sash"] = Sash,
                ["frameDepth"] = FrameDepth,
                ["glass_mm"] = GlassMm
            };
            if (!OverlapEqualsSash)
            {
                typeParams["meetingRailOverlapMode"] = "manual";
                typeParams["meetingRailOverlap_mm"] = OverlapMm;
            }

            var json = new JObject
            {
                ["familyName"] = EffectiveFamilyName,
                ["typeName"] = EffectiveTypeName,
                ["generation"] = new JObject
                {
                    ["rows"] = Rows,
                    ["cols"] = Cols,
                    ["cells"] = cells
                },
                ["typeParams"] = typeParams,
                ["instanceParams"] = new JObject
                {
                    ["width"] = Width,
                    ["height"] = Height,
                    ["sillHeight"] = SillHeight,
                    ["rowRatios"] = new JArray(RowRatios.Cast<object>().ToArray()),
                    ["colRatios"] = new JArray(ColRatios.Cast<object>().ToArray())
                },
                ["loadIntoProject"] = LoadIntoProject,
                ["overwrite"] = Overwrite
            };
            if (!string.IsNullOrWhiteSpace(OutputDir)) json["outputDir"] = OutputDir.Trim();
            return json;
        }

        /// <summary>組 JSON 並交給引擎的解析／驗證；無效時丟 <see cref="WindowSpecException"/>（訊息可直接顯示給使用者）。</summary>
        public WindowFamilySpec BuildSpec()
        {
            return WindowFamilySpec.Parse(ToJson());
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
