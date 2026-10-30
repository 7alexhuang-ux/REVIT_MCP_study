using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>
    /// 萬用窗規格錯誤（規格無效、尺寸算出負值…）。
    /// 訊息為使用者可讀的繁體中文，直接回傳給 MCP 端。
    /// </summary>
    public class WindowSpecException : Exception
    {
        public WindowSpecException(string message) : base(message) { }
    }

    /// <summary>扇的開啟型式。</summary>
    public enum SashType
    {
        /// <summary>固定</summary>
        Fixed,
        /// <summary>外推（側懸）</summary>
        Casement,
        /// <summary>橫拉</summary>
        Sliding,
        /// <summary>上懸</summary>
        Awning,
        /// <summary>下懸／內倒（鉸鏈在下緣；v0.2，ADR D-009）</summary>
        Hopper,
        /// <summary>垂直上下拉（交疊沿垂直軸；v0.2，ADR D-009）</summary>
        SlidingV,
        /// <summary>紗窗扇（滑動類，佔一軌，不計玻璃／採光／可開口面積；v0.2，ADR D-009）</summary>
        Screen
    }

    /// <summary>開啟方向（casement／awning／hopper 適用；v0.2）。</summary>
    public enum SwingDirection
    {
        /// <summary>向室外開</summary>
        Out,
        /// <summary>向室內開（hopper 的預設＝內倒）</summary>
        In
    }

    /// <summary>外推扇的鉸鏈側（自室外看）。</summary>
    public enum HingeSide
    {
        Left,
        Right
    }

    /// <summary>橫拉交疊量的決定方式（ADR D-005）。</summary>
    public enum OverlapMode
    {
        /// <summary>交疊＝扇框寬（預設，兩支勾企完全重疊）</summary>
        EqualsSashFrameWidth,
        /// <summary>由 meetingRailOverlap_mm 指定</summary>
        Manual
    }

    /// <summary>單一扇的規格。</summary>
    public class SashSpec
    {
        public SashType Type { get; set; }
        /// <summary>僅 Casement 有意義。</summary>
        public HingeSide Hinge { get; set; } = HingeSide.Left;

        /// <summary>使用者明確指定的開啟方向；null＝用型式預設。</summary>
        public SwingDirection? SwingRaw { get; set; }

        /// <summary>
        /// 開啟方向（casement／awning／hopper 適用）。未指定時：hopper＝In（內倒），其餘＝Out。
        /// </summary>
        public SwingDirection Swing
        {
            get { return SwingRaw ?? (Type == SashType.Hopper ? SwingDirection.In : SwingDirection.Out); }
        }

        /// <summary>
        /// 軌序（0＝最外軌，整數）；僅滑動類（Sliding／SlidingV／Screen）有意義。
        /// null＝未指定，由 <see cref="CellSpec.ResolveSlidePlacements"/> 自動分配。
        /// </summary>
        public int? Track { get; set; }

        /// <summary>是否為滑動類（橫拉／垂直上下拉／紗窗）。</summary>
        public bool IsSlidingClass
        {
            get { return Type == SashType.Sliding || Type == SashType.SlidingV || Type == SashType.Screen; }
        }

        /// <summary>是否為玻璃滑動扇（橫拉或垂直上下拉，不含紗窗）。</summary>
        public bool IsGlassSlider
        {
            get { return Type == SashType.Sliding || Type == SashType.SlidingV; }
        }

        /// <summary>
        /// 縮寫，用於 UW_開啟方式：F／CL／CR／S／A／H／V／SC。
        /// 開啟方向與型式預設相反時加尾碼：casement／awning 內開 → i（例 CLi）、hopper 外開 → o（Ho）。
        /// </summary>
        public string Abbreviation
        {
            get
            {
                switch (Type)
                {
                    case SashType.Fixed: return "F";
                    case SashType.Casement: return (Hinge == HingeSide.Left ? "CL" : "CR") + (Swing == SwingDirection.In ? "i" : "");
                    case SashType.Sliding: return "S";
                    case SashType.Awning: return "A" + (Swing == SwingDirection.In ? "i" : "");
                    case SashType.Hopper: return "H" + (Swing == SwingDirection.Out ? "o" : "");
                    case SashType.SlidingV: return "V";
                    case SashType.Screen: return "SC";
                    default: return "?";
                }
            }
        }

        /// <summary>
        /// IFC WindowPanelOperationEnum 對應（demo specJSON 的 ifcOperationType）。
        /// Screen 不輸出玻璃扇的操作屬性（NOTDEFINED，呼叫端應略過該扇）。
        /// </summary>
        public string IfcOperationType
        {
            get
            {
                switch (Type)
                {
                    case SashType.Fixed: return "FIXEDCASEMENT";
                    case SashType.Casement: return Hinge == HingeSide.Left ? "SIDEHUNGLEFTHAND" : "SIDEHUNGRIGHTHAND";
                    case SashType.Sliding: return "SLIDINGHORIZONTAL";
                    case SashType.Awning: return "TOPHUNG";
                    case SashType.Hopper: return "BOTTOMHUNG";
                    case SashType.SlidingV: return "SLIDINGVERTICAL";
                    default: return "NOTDEFINED";
                }
            }
        }
    }

    /// <summary>滑動格內單一扇的軌序與位置（由 <see cref="CellSpec.ResolveSlidePlacements"/> 算出）。</summary>
    public struct SlidePlacement
    {
        /// <summary>實際軌序（0＝最外軌）。</summary>
        public int Track;
        /// <summary>
        /// 位置索引：玻璃扇＝玻璃扇序（0 起算，只計玻璃滑動扇）；
        /// 紗窗＝所罩的玻璃扇序（第 j 片紗窗罩第 j mod G 片玻璃）。
        /// </summary>
        public int Position;
        /// <summary>true＝紗窗。</summary>
        public bool IsScreen;
    }

    /// <summary>一格（R#C#）的規格。</summary>
    public class CellSpec
    {
        /// <summary>0-based 排索引（0 = 最上排）。</summary>
        public int Row { get; set; }
        /// <summary>0-based 樘索引（0 = 最左樘，自室外看）。</summary>
        public int Col { get; set; }
        public List<SashSpec> Sashes { get; set; } = new List<SashSpec>();

        public string Id
        {
            get { return string.Format(CultureInfo.InvariantCulture, "R{0}C{1}", Row + 1, Col + 1); }
        }

        /// <summary>是否為滑動格：含任何滑動類扇（橫拉／垂直上下拉／紗窗）。驗證後保證整格都是滑動類。</summary>
        public bool IsSlidingCell
        {
            get { return Sashes.Any(s => s.IsSlidingClass); }
        }

        /// <summary>整格都是滑動類（橫拉／垂直上下拉／紗窗），且至少一扇。</summary>
        public bool AllSlidingClass
        {
            get { return Sashes.Count > 0 && Sashes.All(s => s.IsSlidingClass); }
        }

        /// <summary>垂直滑動格：含 sliding_v（交疊沿垂直軸）。</summary>
        public bool IsVerticalSlide
        {
            get { return Sashes.Any(s => s.Type == SashType.SlidingV); }
        }

        /// <summary>玻璃滑動扇數（橫拉＋垂直上下拉，不含紗窗）。</summary>
        public int GlassSliderCount
        {
            get { return Sashes.Count(s => s.IsGlassSlider); }
        }

        /// <summary>紗窗扇數。</summary>
        public int ScreenCount
        {
            get { return Sashes.Count(s => s.Type == SashType.Screen); }
        }

        /// <summary>
        /// 解析滑動格內每扇的軌序與位置（非滑動格回傳 null）。
        /// 規則（SPEC §3、ADR D-009）：
        /// 玻璃扇未指定 track → 依玻璃扇序交錯 0／1；紗窗未指定 track → 玻璃最大軌＋1（多片依序再加）；
        /// 明確指定的 track 一律採用。紗窗位置：第 j 片罩第 (j mod G) 片玻璃扇。
        /// </summary>
        public SlidePlacement[] ResolveSlidePlacements()
        {
            if (!IsSlidingCell) return null;

            var result = new SlidePlacement[Sashes.Count];
            int g = 0;
            int maxGlassTrack = -1;
            for (int k = 0; k < Sashes.Count; k++)
            {
                var s = Sashes[k];
                if (!s.IsGlassSlider) continue;
                int track = s.Track ?? (g % 2);
                result[k] = new SlidePlacement { Track = track, Position = g, IsScreen = false };
                if (track > maxGlassTrack) maxGlassTrack = track;
                g++;
            }

            int glassCount = g;
            int j = 0;
            for (int k = 0; k < Sashes.Count; k++)
            {
                var s = Sashes[k];
                if (s.Type != SashType.Screen) continue;
                int track = s.Track ?? (maxGlassTrack + 1 + j);
                int pos = glassCount > 0 ? j % glassCount : 0;
                result[k] = new SlidePlacement { Track = track, Position = pos, IsScreen = true };
                j++;
            }
            return result;
        }

        /// <summary>軌數＝最大軌序＋1（非滑動格回傳 0）。</summary>
        public int TrackCount
        {
            get
            {
                var placements = ResolveSlidePlacements();
                return placements == null ? 0 : placements.Max(p => p.Track) + 1;
            }
        }
    }

    /// <summary>Type 層參數（框料斷面），單位 cm（玻璃厚為 mm）。</summary>
    public class WindowTypeParams
    {
        public double FrameOuter { get; set; } = 4.5;
        public double Mullion { get; set; } = 4.4;
        public double Sash { get; set; } = 5.0;
        /// <summary>框深（cm）。預設 7＝YKK AP YRB-A70（ADR D-010；A100 為 10）。</summary>
        public double FrameDepth { get; set; } = 7.0;
        public double GlassMm { get; set; } = 8.0;
        public double? MeetingRailOverlapMm { get; set; }
        public OverlapMode OverlapMode { get; set; } = OverlapMode.EqualsSashFrameWidth;

        /// <summary>實際交疊量（cm）。ADR D-005：預設＝扇框寬。</summary>
        public double OverlapCm
        {
            get
            {
                if (OverlapMode == OverlapMode.Manual && MeetingRailOverlapMm.HasValue)
                    return MeetingRailOverlapMm.Value / 10.0;
                return Sash;
            }
        }
    }

    /// <summary>MO（開口）規格，單位 cm。</summary>
    public class MoSpec
    {
        public double GapPerSide { get; set; } = 2.5;
        public double CutBeyondExterior { get; set; } = 5.0;
        public double CutBeyondInterior { get; set; } = 5.0;
        /// <summary>空心擠出的總深度（cm）。未指定時由 <see cref="WindowFamilySpec.ResolveVoidDepth"/> 推定。</summary>
        public double? VoidDepth { get; set; }
    }

    /// <summary>Instance 層參數（安裝尺寸），單位 cm。</summary>
    public class WindowInstanceParams
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double SillHeight { get; set; } = 90.0;
        public double[] RowRatios { get; set; } = new double[] { 100.0 };
        public double[] ColRatios { get; set; } = new double[] { 100.0 };
        /// <summary>框深中心相對牆心偏移，+ 為向室外。</summary>
        public double FrameOffsetFromWallCentre { get; set; }
        public MoSpec Mo { get; set; } = new MoSpec();
    }

    /// <summary>圖面符號旗標（ADR D-004）。</summary>
    public class SymbolSpec
    {
        /// <summary>立面三角形頂點在鉸鏈側（拍板預設 true）。</summary>
        public bool ApexAtHinge { get; set; } = true;
        /// <summary>平面外推示意角度（度），預設 30°。</summary>
        public double PlanSwingAngle { get; set; } = 30.0;
    }

    /// <summary>
    /// generate_window_family 的完整規格（SPEC §8）。純 C#，不依賴 Revit API。
    /// </summary>
    public class WindowFamilySpec
    {
        public const int MaxRows = 3;
        public const int MaxCols = 4;
        public const int MaxSashesPerCell = 4;
        /// <summary>軌序上限（0 起算；最多 4 軌）。</summary>
        public const int MaxTrack = 3;

        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public int Rows { get; set; }
        public int Cols { get; set; }
        /// <summary>Cells[row][col]，row 0 = 最上排。</summary>
        public CellSpec[][] Cells { get; set; }
        public WindowTypeParams TypeParams { get; set; } = new WindowTypeParams();
        public WindowInstanceParams InstanceParams { get; set; } = new WindowInstanceParams();
        public SymbolSpec Symbols { get; set; } = new SymbolSpec();
        public string OutputDir { get; set; }
        public bool LoadIntoProject { get; set; } = true;
        public bool Overwrite { get; set; }

        public IEnumerable<CellSpec> AllCells
        {
            get
            {
                for (int r = 0; r < Rows; r++)
                    for (int c = 0; c < Cols; c++)
                        yield return Cells[r][c];
            }
        }

        /// <summary>
        /// 空心擠出深度（cm）。未指定時採「框深 ＋ 內外切透 ＋ 20 cm 餘裕」與 50 cm 取大者。
        /// Phase 1 族不知道主體牆厚，故只能保守給深；呼叫端會發 warning 說明。
        /// </summary>
        public double ResolveVoidDepth()
        {
            if (InstanceParams.Mo.VoidDepth.HasValue && InstanceParams.Mo.VoidDepth.Value > 0)
                return InstanceParams.Mo.VoidDepth.Value;
            double byParts = TypeParams.FrameDepth
                             + InstanceParams.Mo.CutBeyondExterior
                             + InstanceParams.Mo.CutBeyondInterior
                             + 20.0;
            return Math.Max(50.0, byParts);
        }

        /// <summary>UW_開啟方式 的字串，例：`R1: F,F / R2: CL,CR`。</summary>
        public string DescribeOperations()
        {
            var rowTexts = new List<string>();
            for (int r = 0; r < Rows; r++)
            {
                var cellTexts = new List<string>();
                for (int c = 0; c < Cols; c++)
                    cellTexts.Add(string.Join(",", Cells[r][c].Sashes.Select(s => s.Abbreviation).ToArray()));
                rowTexts.Add(string.Format(CultureInfo.InvariantCulture, "R{0}: {1}", r + 1, string.Join(" | ", cellTexts.ToArray())));
            }
            return string.Join(" / ", rowTexts.ToArray());
        }

        /// <summary>IFC PartitioningType（沿用 demo 的 ifcPartition()）。</summary>
        public string IfcPartitioningType()
        {
            // v0.2：紗窗不是玻璃扇，不計入分割數；垂直上下拉的扇上下疊（分割線水平）
            if (Rows == 1 && Cols == 1)
            {
                var cell = Cells[0][0];
                int n = PanelCount(cell);
                if (n == 1) return "SINGLE_PANEL";
                if (cell.IsVerticalSlide)
                {
                    if (n == 2) return "DOUBLE_PANEL_HORIZONTAL";
                    if (n == 3) return "TRIPLE_PANEL_HORIZONTAL";
                    return "USERDEFINED";
                }
                if (n == 2) return "DOUBLE_PANEL_VERTICAL";
                if (n == 3) return "TRIPLE_PANEL_VERTICAL";
                return "USERDEFINED";
            }
            if (Rows == 2 && Cols == 1)
            {
                int a = PanelCount(Cells[0][0]);
                int b = PanelCount(Cells[1][0]);
                if (a == 1 && b == 1) return "DOUBLE_PANEL_HORIZONTAL";
                if (a == 1 && b == 2) return "TRIPLE_PANEL_TOP";
                if (a == 2 && b == 1) return "TRIPLE_PANEL_BOTTOM";
            }
            if (Rows == 1 && Cols == 2 && Cells[0].All(c => PanelCount(c) == 1))
                return "DOUBLE_PANEL_VERTICAL";
            return "USERDEFINED";
        }

        /// <summary>格內「面板」數：不含紗窗。</summary>
        private static int PanelCount(CellSpec cell)
        {
            return cell.Sashes.Count(s => s.Type != SashType.Screen);
        }

        // ────────────────────────────────────────────────
        // 解析
        // ────────────────────────────────────────────────

        /// <summary>從 MCP 參數 JSON 解析規格；格式錯誤直接丟 <see cref="WindowSpecException"/>。</summary>
        public static WindowFamilySpec Parse(JObject json)
        {
            if (json == null) throw new WindowSpecException("缺少參數物件。");

            var spec = new WindowFamilySpec();
            spec.FamilyName = (json["familyName"] ?? json["family"])?.Value<string>();
            spec.TypeName = json["typeName"]?.Value<string>();
            spec.OutputDir = json["outputDir"]?.Value<string>();
            spec.LoadIntoProject = json["loadIntoProject"]?.Value<bool?>() ?? true;
            spec.Overwrite = json["overwrite"]?.Value<bool?>() ?? false;

            var gen = json["generation"] as JObject;
            if (gen == null) throw new WindowSpecException("缺少 generation（拓樸）區塊。");
            spec.Rows = gen["rows"]?.Value<int?>() ?? 0;
            spec.Cols = gen["cols"]?.Value<int?>() ?? 0;
            if (spec.Rows < 1 || spec.Rows > MaxRows)
                throw new WindowSpecException($"generation.rows 必須是 1–{MaxRows}，收到 {spec.Rows}。");
            if (spec.Cols < 1 || spec.Cols > MaxCols)
                throw new WindowSpecException($"generation.cols 必須是 1–{MaxCols}，收到 {spec.Cols}。");

            var cellsArray = gen["cells"] as JArray;
            if (cellsArray == null || cellsArray.Count != spec.Rows)
                throw new WindowSpecException($"generation.cells 必須是 {spec.Rows} 列（每列一排）。");

            spec.Cells = new CellSpec[spec.Rows][];
            for (int r = 0; r < spec.Rows; r++)
            {
                var rowArray = cellsArray[r] as JArray;
                if (rowArray == null || rowArray.Count != spec.Cols)
                    throw new WindowSpecException($"generation.cells[{r}] 必須有 {spec.Cols} 格（每格一樘）。");
                spec.Cells[r] = new CellSpec[spec.Cols];
                for (int c = 0; c < spec.Cols; c++)
                {
                    var cellObj = rowArray[c] as JObject;
                    if (cellObj == null)
                        throw new WindowSpecException($"generation.cells[{r}][{c}] 不是物件。");
                    var cell = new CellSpec { Row = r, Col = c };
                    var sashArray = cellObj["sashes"] as JArray;
                    if (sashArray == null || sashArray.Count == 0)
                        throw new WindowSpecException($"格 R{r + 1}C{c + 1} 缺少 sashes。");
                    if (sashArray.Count > MaxSashesPerCell)
                        throw new WindowSpecException($"格 R{r + 1}C{c + 1} 的扇數 {sashArray.Count} 超過上限 {MaxSashesPerCell}。");
                    foreach (var sashToken in sashArray)
                    {
                        var sashObj = sashToken as JObject;
                        if (sashObj == null)
                            throw new WindowSpecException($"格 R{r + 1}C{c + 1} 的 sashes 內含非物件項目。");
                        cell.Sashes.Add(ParseSash(sashObj, cell.Id));
                    }
                    spec.Cells[r][c] = cell;
                }
            }

            var tp = json["typeParams"] as JObject;
            if (tp != null)
            {
                spec.TypeParams.FrameOuter = tp["frameOuter"]?.Value<double?>() ?? spec.TypeParams.FrameOuter;
                spec.TypeParams.Mullion = tp["mullion"]?.Value<double?>() ?? spec.TypeParams.Mullion;
                spec.TypeParams.Sash = tp["sash"]?.Value<double?>() ?? spec.TypeParams.Sash;
                spec.TypeParams.FrameDepth = tp["frameDepth"]?.Value<double?>() ?? spec.TypeParams.FrameDepth;
                spec.TypeParams.GlassMm = tp["glass_mm"]?.Value<double?>() ?? spec.TypeParams.GlassMm;
                spec.TypeParams.MeetingRailOverlapMm = tp["meetingRailOverlap_mm"]?.Value<double?>();
                string mode = tp["meetingRailOverlapMode"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
                        spec.TypeParams.OverlapMode = OverlapMode.Manual;
                    else if (mode.Equals("equalsSashFrameWidth", StringComparison.OrdinalIgnoreCase))
                        spec.TypeParams.OverlapMode = OverlapMode.EqualsSashFrameWidth;
                    else
                        throw new WindowSpecException($"typeParams.meetingRailOverlapMode 只接受 equalsSashFrameWidth 或 manual，收到 {mode}。");
                }
            }

            var ip = json["instanceParams"] as JObject;
            if (ip == null) throw new WindowSpecException("缺少 instanceParams（寬高與比例）區塊。");
            spec.InstanceParams.Width = ip["width"]?.Value<double?>() ?? 0;
            spec.InstanceParams.Height = ip["height"]?.Value<double?>() ?? 0;
            spec.InstanceParams.SillHeight = ip["sillHeight"]?.Value<double?>() ?? 90.0;
            spec.InstanceParams.FrameOffsetFromWallCentre = ip["frameOffsetFromWallCentre"]?.Value<double?>() ?? 0.0;
            spec.InstanceParams.RowRatios = ParseRatios(ip["rowRatios"] as JArray, spec.Rows, "rowRatios");
            spec.InstanceParams.ColRatios = ParseRatios(ip["colRatios"] as JArray, spec.Cols, "colRatios");

            var mo = ip["mo"] as JObject;
            if (mo != null)
            {
                spec.InstanceParams.Mo.GapPerSide = mo["gapPerSide"]?.Value<double?>() ?? spec.InstanceParams.Mo.GapPerSide;
                spec.InstanceParams.Mo.CutBeyondExterior = mo["cutBeyondExterior"]?.Value<double?>() ?? spec.InstanceParams.Mo.CutBeyondExterior;
                spec.InstanceParams.Mo.CutBeyondInterior = mo["cutBeyondInterior"]?.Value<double?>() ?? spec.InstanceParams.Mo.CutBeyondInterior;
                spec.InstanceParams.Mo.VoidDepth = mo["voidDepth"]?.Value<double?>();
            }

            var sym = json["symbols"] as JObject;
            if (sym != null)
            {
                spec.Symbols.ApexAtHinge = sym["apexAtHinge"]?.Value<bool?>() ?? true;
                spec.Symbols.PlanSwingAngle = sym["planSwingAngle"]?.Value<double?>() ?? 30.0;
            }

            spec.Validate();
            return spec;
        }

        private static SashSpec ParseSash(JObject obj, string cellId)
        {
            string typeText = (obj["type"] ?? obj["t"])?.Value<string>();
            if (string.IsNullOrWhiteSpace(typeText))
                throw new WindowSpecException($"格 {cellId} 有一扇缺少 type。");

            SashType type;
            switch (typeText.Trim().ToLowerInvariant())
            {
                case "fixed": type = SashType.Fixed; break;
                case "casement": type = SashType.Casement; break;
                case "sliding": type = SashType.Sliding; break;
                case "awning": type = SashType.Awning; break;
                case "hopper": type = SashType.Hopper; break;
                case "sliding_v": type = SashType.SlidingV; break;
                case "screen": type = SashType.Screen; break;
                default:
                    throw new WindowSpecException($"格 {cellId} 的 type「{typeText}」無效，只接受 fixed／casement／sliding／awning／hopper／sliding_v／screen。");
            }

            var sash = new SashSpec { Type = type };

            string swingText = obj["swing"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(swingText))
            {
                string sw = swingText.Trim().ToLowerInvariant();
                if (sw == "out") sash.SwingRaw = SwingDirection.Out;
                else if (sw == "in") sash.SwingRaw = SwingDirection.In;
                else throw new WindowSpecException($"格 {cellId} 的 swing「{swingText}」無效，只接受 out 或 in。");
            }

            JToken trackToken = obj["track"];
            if (trackToken != null && trackToken.Type != JTokenType.Null)
                sash.Track = ParseTrack(trackToken, cellId);

            string hinge = (obj["hinge"] ?? obj["h"])?.Value<string>();
            if (!string.IsNullOrWhiteSpace(hinge))
            {
                string h = hinge.Trim().ToUpperInvariant();
                if (h == "L" || h == "LEFT") sash.Hinge = HingeSide.Left;
                else if (h == "R" || h == "RIGHT") sash.Hinge = HingeSide.Right;
                else throw new WindowSpecException($"格 {cellId} 的 hinge「{hinge}」無效，只接受 L 或 R。");
            }
            return sash;
        }

        /// <summary>
        /// 解析軌序：整數（0＝最外軌）；相容 v0.1 的字串 "outer"→0、"inner"→1，亦接受數字字串。
        /// </summary>
        private static int ParseTrack(JToken token, string cellId)
        {
            int value;
            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
            }
            else if (token.Type == JTokenType.Float)
            {
                double d = token.Value<double>();
                if (Math.Abs(d - Math.Round(d)) > 1e-9)
                    throw new WindowSpecException($"格 {cellId} 的 track {d.ToString(CultureInfo.InvariantCulture)} 必須是整數。");
                value = (int)Math.Round(d);
            }
            else if (token.Type == JTokenType.String)
            {
                string t = token.Value<string>().Trim();
                if (t.Equals("outer", StringComparison.OrdinalIgnoreCase)) value = 0;
                else if (t.Equals("inner", StringComparison.OrdinalIgnoreCase)) value = 1;
                else if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new WindowSpecException($"格 {cellId} 的 track「{t}」無效，請用整數（0＝最外軌），或舊值 outer／inner。");
            }
            else
            {
                throw new WindowSpecException($"格 {cellId} 的 track 必須是整數（0＝最外軌）。");
            }

            if (value < 0 || value > MaxTrack)
                throw new WindowSpecException($"格 {cellId} 的 track {value} 超出範圍 0–{MaxTrack}。");
            return value;
        }

        private static double[] ParseRatios(JArray array, int expectedCount, string fieldName)
        {
            if (array == null)
            {
                // 未給比例 → 等分
                var even = new double[expectedCount];
                for (int i = 0; i < expectedCount; i++) even[i] = 100.0 / expectedCount;
                return even;
            }
            if (array.Count != expectedCount)
                throw new WindowSpecException($"instanceParams.{fieldName} 必須有 {expectedCount} 個值，收到 {array.Count} 個。");
            var values = new double[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                double? v = array[i].Value<double?>();
                if (!v.HasValue) throw new WindowSpecException($"instanceParams.{fieldName}[{i}] 不是數字。");
                values[i] = v.Value;
            }
            return values;
        }

        // ────────────────────────────────────────────────
        // 驗證
        // ────────────────────────────────────────────────

        /// <summary>比例總和容差（%）。</summary>
        public const double RatioSumTolerance = 0.01;

        /// <summary>驗證規格；無效直接丟 <see cref="WindowSpecException"/>。</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(FamilyName)) throw new WindowSpecException("缺少 familyName。");
            if (string.IsNullOrWhiteSpace(TypeName)) throw new WindowSpecException("缺少 typeName。");
            if (Rows < 1 || Rows > MaxRows) throw new WindowSpecException($"rows 必須是 1–{MaxRows}。");
            if (Cols < 1 || Cols > MaxCols) throw new WindowSpecException($"cols 必須是 1–{MaxCols}。");
            if (Cells == null || Cells.Length != Rows) throw new WindowSpecException("cells 的排數與 rows 不符。");

            for (int r = 0; r < Rows; r++)
            {
                if (Cells[r] == null || Cells[r].Length != Cols)
                    throw new WindowSpecException($"cells[{r}] 的樘數與 cols 不符。");
                for (int c = 0; c < Cols; c++)
                {
                    var cell = Cells[r][c];
                    if (cell == null || cell.Sashes == null || cell.Sashes.Count == 0)
                        throw new WindowSpecException($"格 R{r + 1}C{c + 1} 沒有扇。");
                    if (cell.Sashes.Count > MaxSashesPerCell)
                        throw new WindowSpecException($"格 R{r + 1}C{c + 1} 的扇數超過上限 {MaxSashesPerCell}。");
                    ValidateSlidingCell(cell);
                }
            }

            ValidateRatios(InstanceParams.RowRatios, Rows, "rowRatios");
            ValidateRatios(InstanceParams.ColRatios, Cols, "colRatios");

            if (InstanceParams.Width <= 0) throw new WindowSpecException("instanceParams.width 必須大於 0。");
            if (InstanceParams.Height <= 0) throw new WindowSpecException("instanceParams.height 必須大於 0。");
            if (InstanceParams.SillHeight < 0) throw new WindowSpecException("instanceParams.sillHeight 不可為負。");

            if (TypeParams.FrameOuter <= 0) throw new WindowSpecException("typeParams.frameOuter 必須大於 0。");
            if (TypeParams.Mullion <= 0) throw new WindowSpecException("typeParams.mullion 必須大於 0。");
            if (TypeParams.Sash <= 0) throw new WindowSpecException("typeParams.sash 必須大於 0。");
            if (TypeParams.FrameDepth <= 0) throw new WindowSpecException("typeParams.frameDepth 必須大於 0。");
            if (TypeParams.GlassMm <= 0) throw new WindowSpecException("typeParams.glass_mm 必須大於 0。");
            if (TypeParams.GlassMm / 10.0 >= TypeParams.FrameDepth)
                throw new WindowSpecException("玻璃厚不可大於等於框深。");
            if (TypeParams.OverlapMode == OverlapMode.Manual && !TypeParams.MeetingRailOverlapMm.HasValue)
                throw new WindowSpecException("meetingRailOverlapMode = manual 時必須給 meetingRailOverlap_mm。");
            if (TypeParams.OverlapCm < 0) throw new WindowSpecException("交疊量不可為負。");

            if (InstanceParams.Mo.GapPerSide < 0) throw new WindowSpecException("mo.gapPerSide 不可為負。");
            if (Symbols.PlanSwingAngle < 0 || Symbols.PlanSwingAngle >= 90)
                throw new WindowSpecException("symbols.planSwingAngle 必須在 0–90 度之間。");
        }

        /// <summary>
        /// SPEC §3 滑動類規則（v0.2）：
        /// ① 滑動類（sliding／sliding_v／screen）不可與開啟類同格；② sliding 與 sliding_v 不可同格；
        /// ③ screen 不能單獨成格（至少一個玻璃滑動扇）；④ 同軌且位置相鄰（會互卡）的扇視為衝突。
        /// </summary>
        private static void ValidateSlidingCell(CellSpec cell)
        {
            if (!cell.IsSlidingCell) return;

            if (!cell.AllSlidingClass)
                throw new WindowSpecException(
                    $"格 {cell.Id} 內混用了滑動類（橫拉／垂直上下拉／紗窗）與其他開啟型式；滑動類必須整格都是滑動類。");

            bool anyH = cell.Sashes.Any(s => s.Type == SashType.Sliding);
            bool anyV = cell.Sashes.Any(s => s.Type == SashType.SlidingV);
            if (anyH && anyV)
                throw new WindowSpecException($"格 {cell.Id} 同時有橫拉與垂直上下拉；兩者不可同格。");

            if (cell.GlassSliderCount == 0)
                throw new WindowSpecException(
                    $"格 {cell.Id} 只有紗窗；紗窗不能單獨成格，至少要有一個玻璃滑動扇（橫拉或垂直上下拉）。");

            var placements = cell.ResolveSlidePlacements();
            for (int a = 0; a < placements.Length; a++)
            {
                for (int b = a + 1; b < placements.Length; b++)
                {
                    if (placements[a].Track != placements[b].Track) continue;
                    if (Math.Abs(placements[a].Position - placements[b].Position) > 1) continue;
                    throw new WindowSpecException(
                        $"格 {cell.Id} 第 {a + 1} 與第 {b + 1} 扇都在第 {placements[a].Track} 軌且位置相鄰（交疊處），軌道會互卡；請改用不同軌序。");
                }
            }
        }

        private static void ValidateRatios(double[] ratios, int expectedCount, string fieldName)
        {
            if (ratios == null || ratios.Length != expectedCount)
                throw new WindowSpecException($"{fieldName} 必須有 {expectedCount} 個值。");
            double sum = 0;
            for (int i = 0; i < ratios.Length; i++)
            {
                if (ratios[i] <= 0)
                    throw new WindowSpecException($"{fieldName}[{i}] = {ratios[i]}，比例必須大於 0。");
                sum += ratios[i];
            }
            if (Math.Abs(sum - 100.0) > RatioSumTolerance)
                throw new WindowSpecException($"{fieldName} 總和必須是 100，目前是 {sum.ToString("0.###", CultureInfo.InvariantCulture)}。");
        }
    }
}
