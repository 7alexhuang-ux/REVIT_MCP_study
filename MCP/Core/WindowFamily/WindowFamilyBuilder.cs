using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
// RevitMCP 命名空間下已有自家的 Application（IExternalApplication），故別名區分
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>實測樣板得到的事實（回報給使用者，用以確認室外方向等假設）。</summary>
    public class TemplateProbe
    {
        public string TemplatePath { get; set; }
        /// <summary>室外方向的 Y 分量正負號（族座標）。</summary>
        public double ExteriorSign { get; set; }
        /// <summary>室外方向的判定來源：exteriorView / hostWall / fallback。</summary>
        public string ExteriorSource { get; set; }
        public string ExteriorViewName { get; set; }
        public string ExteriorViewDirection { get; set; }
        public string HostWallOrientation { get; set; }
        public List<string> ReferencePlaneNames { get; set; } = new List<string>();
        public List<string> ViewNames { get; set; } = new List<string>();
    }

    /// <summary>generate_window_family 的產出。</summary>
    public class WindowFamilyBuildResult
    {
        public string FamilyPath { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public bool Loaded { get; set; }
        public long FamilyId { get; set; } = -1;
        public long SymbolId { get; set; } = -1;
        public WindowDerived Derived { get; set; }
        public string IfcPartitioningType { get; set; }
        public string Operations { get; set; }
        public TemplateProbe Probe { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 萬用窗族生成器（Phase 1，ADR D-002／D-003）。
    /// 以 Metric Window.rft 建族 → 擠出實體與空心 → 符號線 → 參數 → 存檔 → 載入專案。
    /// 所有 Revit 呼叫由 CommandExecutor 在 ExternalEventManager 的 UI 執行緒中觸發。
    /// </summary>
    public class WindowFamilyBuilder
    {
        // ── 單位 ─────────────────────────────────────────
        private const double CmToFeet = 1.0 / 30.48;
        private const double SqMToSqFeet = 10.7639104167097;

        // ── 幾何常數（相對框深 D 的比例；SPEC §5 與 demo 平剖圖一致）──
        /// <summary>非橫拉扇框的深度係數（× 框深）。</summary>
        private const double SashDepthFactor = 0.60;
        /// <summary>橫拉扇框的深度係數（× 框深）。</summary>
        private const double SlidingSashDepthFactor = 0.35;
        /// <summary>橫拉軌道中心距框心的係數（× 框深），外軌在室外側。</summary>
        private const double SlidingTrackOffsetFactor = 0.22;
        /// <summary>上懸平面示意虛線距框心的係數（× 框深，室外側）。</summary>
        private const double AwningPlanHintFactor = 0.90;

        /// <summary>
        /// 室外方向的保底常數：族座標 −Y。
        /// 依據：族樣板的 Front（＝Exterior）立面朝 +Y 觀看，觀察者站在 −Y 側。
        /// **未在 Revit 內實測**；執行時會先以 Exterior 立面的 ViewDirection／主體牆 Orientation 實測覆蓋，
        /// 兩者都取不到才用這個常數，並在 warnings 標明。
        /// </summary>
        public const double ExteriorSignFallback = -1.0;

        private readonly RevitApplication _app;
        private readonly List<string> _warnings = new List<string>();

        public WindowFamilyBuilder(RevitApplication app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
        }

        // ────────────────────────────────────────────────
        // 樣板
        // ────────────────────────────────────────────────

        /// <summary>找 Metric Window.rft：app.FamilyTemplatePath → English 子資料夾 → 遞迴搜尋。</summary>
        public static string FindWindowTemplate(RevitApplication app)
        {
            string root = app.FamilyTemplatePath;
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "English", "Metric Window.rft"));
                candidates.Add(Path.Combine(root, "Metric Window.rft"));
                candidates.Add(Path.Combine(root, "English-Imperial", "Metric Window.rft"));
            }
            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                try
                {
                    string found = Directory.GetFiles(root, "Metric Window.rft", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
                catch { /* 權限或路徑問題 → 視同找不到 */ }
            }
            return null;
        }

        // ────────────────────────────────────────────────
        // 主流程
        // ────────────────────────────────────────────────

        /// <summary>
        /// 依規格生成族並（選擇性）載入 targetDoc。
        /// targetDoc 上不可有開啟中的交易（LoadFamily 的限制）。
        /// </summary>
        public WindowFamilyBuildResult Build(WindowFamilySpec spec, Document targetDoc)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            _warnings.Clear();

            var layout = WindowLayoutMath.Compute(spec);

            string templatePath = FindWindowTemplate(_app);
            if (templatePath == null)
                throw new WindowSpecException(
                    "找不到窗族樣板 Metric Window.rft。請確認 Revit 的族群樣板路徑（選項 › 檔案位置 › 族群樣板檔案預設路徑）。" +
                    $"目前設定為：{_app.FamilyTemplatePath ?? "(未設定)"}");

            string outputDir = spec.OutputDir;
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitMCP", "families");
            }
            Directory.CreateDirectory(outputDir);

            string safeFamilyName = SanitizeFileName(spec.FamilyName);
            string familyPath = Path.Combine(outputDir, safeFamilyName + ".rfa");

            // 載入衝突：同名族已在專案內且未給 overwrite → 先擋，別白做一趟
            if (spec.LoadIntoProject && targetDoc != null && !spec.Overwrite)
            {
                var existing = new FilteredElementCollector(targetDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => string.Equals(f.Name, safeFamilyName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    throw new WindowSpecException(
                        $"專案內已有同名族「{safeFamilyName}」。要覆蓋請帶 overwrite = true。");
            }

            Document famDoc = null;
            var result = new WindowFamilyBuildResult
            {
                FamilyName = safeFamilyName,
                TypeName = spec.TypeName,
                FamilyPath = familyPath,
                Derived = layout.Derived,
                IfcPartitioningType = spec.IfcPartitioningType(),
                Operations = spec.DescribeOperations()
            };

            try
            {
                famDoc = _app.NewFamilyDocument(templatePath);
                if (famDoc == null)
                    throw new WindowSpecException($"無法以樣板建立族文件：{templatePath}");

                var probe = ProbeTemplate(famDoc, templatePath);
                result.Probe = probe;

                using (var tx = new Transaction(famDoc, "建立萬用窗幾何與參數"))
                {
                    tx.Start();
                    BuildFamilyContent(famDoc, spec, layout, probe.ExteriorSign);
                    tx.Commit();
                }

                var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
                famDoc.SaveAs(familyPath, saveOptions);

                if (spec.LoadIntoProject && targetDoc != null)
                {
                    Family loaded = famDoc.LoadFamily(targetDoc, new WindowFamilyLoadOptions(spec.Overwrite));
                    if (loaded == null)
                    {
                        _warnings.Add("LoadFamily 回傳 null：族未載入（多半是同名族存在且未允許覆蓋）。");
                    }
                    else
                    {
                        result.Loaded = true;
                        result.FamilyId = GetIdValueSafe(loaded.Id);
                        FamilySymbol symbol = loaded.GetFamilySymbolIds()
                            .Select(id => targetDoc.GetElement(id) as FamilySymbol)
                            .FirstOrDefault(s => s != null && s.Name == spec.TypeName);
                        if (symbol == null)
                        {
                            _warnings.Add($"族已載入，但找不到型別「{spec.TypeName}」。");
                        }
                        else
                        {
                            result.SymbolId = GetIdValueSafe(symbol.Id);
                            if (!symbol.IsActive)
                            {
                                using (var tx = new Transaction(targetDoc, "啟用萬用窗型別"))
                                {
                                    tx.Start();
                                    symbol.Activate();
                                    tx.Commit();
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                if (famDoc != null)
                {
                    try { famDoc.Close(false); } catch { /* 關檔失敗不影響已存檔的結果 */ }
                }
            }

            // ADR D-003：Phase 1 固定尺寸，Width／Height 值正確但幾何不跟動
            _warnings.Insert(0, "Phase 1 固定尺寸：Width/Height 改動幾何不跟動（ADR D-003）。要換尺寸請用 MCP 重新生成一個族。");
            result.Warnings = new List<string>(_warnings);
            return result;
        }

        // ────────────────────────────────────────────────
        // 樣板實測
        // ────────────────────────────────────────────────

        private TemplateProbe ProbeTemplate(Document famDoc, string templatePath)
        {
            var probe = new TemplateProbe
            {
                TemplatePath = templatePath,
                ExteriorSign = ExteriorSignFallback,
                ExteriorSource = "fallback"
            };

            try
            {
                probe.ReferencePlaneNames = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(ReferencePlane))
                    .Cast<ReferencePlane>()
                    .Select(rp => rp.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { /* 讀不到就留空 */ }

            try
            {
                probe.ViewNames = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => v.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { /* 同上 */ }

            // 1) 優先：名稱含 Exterior／室外 的立面。View.ViewDirection 指向觀察者，即室外側。
            try
            {
                View exterior = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Elevation &&
                                         (v.Name.IndexOf("Exterior", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          v.Name.Contains("室外")));
                if (exterior != null)
                {
                    XYZ dir = exterior.ViewDirection;
                    probe.ExteriorViewName = exterior.Name;
                    probe.ExteriorViewDirection = FormatXyz(dir);
                    if (Math.Abs(dir.Y) > 1e-6)
                    {
                        probe.ExteriorSign = dir.Y > 0 ? 1.0 : -1.0;
                        probe.ExteriorSource = "exteriorView.ViewDirection";
                    }
                }
            }
            catch { /* 探測失敗 → 往下退 */ }

            // 2) 次選：樣板內的主體牆，Wall.Orientation 指向牆的室外側
            try
            {
                Wall wall = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .FirstOrDefault();
                if (wall != null)
                {
                    XYZ o = wall.Orientation;
                    probe.HostWallOrientation = FormatXyz(o);
                    if (probe.ExteriorSource == "fallback" && Math.Abs(o.Y) > 1e-6)
                    {
                        probe.ExteriorSign = o.Y > 0 ? 1.0 : -1.0;
                        probe.ExteriorSource = "hostWall.Orientation";
                    }
                }
            }
            catch { /* 同上 */ }

            if (probe.ExteriorSource == "fallback")
            {
                _warnings.Add(
                    $"室外方向無法從樣板實測（找不到 Exterior 立面與主體牆），改用常數 −Y。若平面／剖面的內外側相反，請回報。");
            }
            else if (Math.Abs(probe.ExteriorSign - ExteriorSignFallback) > 0.5)
            {
                _warnings.Add(
                    $"實測室外方向為 +Y（來源 {probe.ExteriorSource}），與程式保底常數 −Y 相反，已採用實測值。");
            }

            return probe;
        }

        // ────────────────────────────────────────────────
        // 族內容
        // ────────────────────────────────────────────────

        private void BuildFamilyContent(Document famDoc, WindowFamilySpec spec, WindowLayout layout, double exteriorSign)
        {
            var fm = famDoc.FamilyManager;

            // 型別：一尺寸一型別（ADR D-001）
            try { fm.NewType(spec.TypeName); }
            catch (Exception ex) { _warnings.Add($"建立型別「{spec.TypeName}」失敗：{ex.Message}"); }

            SetBuiltInSize(fm, spec, layout);

            // 空心切割：載入專案時以空心切主體（ADR D-006）
            try
            {
                Parameter allowCut = famDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
                if (allowCut != null && !allowCut.IsReadOnly) allowCut.Set(1);
                else _warnings.Add("FAMILY_ALLOW_CUT_WITH_VOIDS 不可寫，未能啟用「載入時以空心切割」。");
            }
            catch (Exception ex)
            {
                _warnings.Add($"設定「載入時以空心切割」失敗：{ex.Message}");
            }

            double frameCentreY = exteriorSign * spec.InstanceParams.FrameOffsetFromWallCentre;
            double depth = spec.TypeParams.FrameDepth;

            SketchPlane elevationPlane = SketchPlane.Create(
                famDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero));
            SketchPlane planPlane = SketchPlane.Create(
                famDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

            BuildSolids(famDoc, spec, layout, elevationPlane, frameCentreY, depth, exteriorSign);
            BuildVoid(famDoc, spec, layout, elevationPlane, frameCentreY);
            BuildElevationSymbols(famDoc, spec, layout, elevationPlane);
            BuildPlanSymbols(famDoc, spec, layout, planPlane, frameCentreY, depth, exteriorSign);

            AddSharedParameters(famDoc, spec, layout);
        }

        private void SetBuiltInSize(FamilyManager fm, WindowFamilySpec spec, WindowLayout layout)
        {
            SetFamilyLength(fm, BuiltInParameter.WINDOW_WIDTH, "Width", layout.Width, "寬度");
            SetFamilyLength(fm, BuiltInParameter.WINDOW_HEIGHT, "Height", layout.Height, "高度");
        }

        private void SetFamilyLength(FamilyManager fm, BuiltInParameter bip, string fallbackName, double valueCm, string label)
        {
            FamilyParameter p = null;
            try { p = fm.get_Parameter(bip); } catch { }
            if (p == null)
            {
                p = fm.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(fp => fp.Definition != null &&
                                          string.Equals(fp.Definition.Name, fallbackName, StringComparison.OrdinalIgnoreCase));
            }
            if (p == null)
            {
                _warnings.Add($"樣板內找不到 {label} 參數（{fallbackName}），未設值。");
                return;
            }
            try { fm.Set(p, valueCm * CmToFeet); }
            catch (Exception ex) { _warnings.Add($"設定 {label} 失敗：{ex.Message}"); }
        }

        // ── 實體 ─────────────────────────────────────────

        private void BuildSolids(Document famDoc, WindowFamilySpec spec, WindowLayout layout,
                                 SketchPlane plane, double frameCentreY, double depth, double exteriorSign)
        {
            double W = layout.Width, H = layout.Height;
            double fw = spec.TypeParams.FrameOuter;
            double mw = spec.TypeParams.Mullion;
            double glassT = spec.TypeParams.GlassMm / 10.0;

            // 外框（粗略級唯一保留的實體）
            var frame = CreateExtrusion(famDoc, plane, true,
                new[]
                {
                    RectProfile(0, 0, W, H, W, H),
                    RectProfile(fw, fw, W - fw, H - fw, W, H)
                },
                frameCentreY - depth / 2, frameCentreY + depth / 2);
            SetDetailLevels(frame, true, true, true, "外框");

            // 中框：樘與樘之間的直料
            for (int c = 1; c < layout.Bays.Count; c++)
            {
                double mx = layout.Bays[c].X - mw;
                var mullion = CreateExtrusion(famDoc, plane, true,
                    new[] { RectProfile(mx, fw, mx + mw, H - fw, W, H) },
                    frameCentreY - depth / 2, frameCentreY + depth / 2);
                SetDetailLevels(mullion, false, true, true, "直中框");
            }

            // 中框：排與排之間的橫料（每樘各自一段）
            foreach (var bay in layout.Bays)
            {
                for (int r = 1; r < bay.Cells.Count; r++)
                {
                    double ty = bay.Cells[r].Y - mw;
                    var transom = CreateExtrusion(famDoc, plane, true,
                        new[] { RectProfile(bay.X, ty, bay.X + bay.Width, ty + mw, W, H) },
                        frameCentreY - depth / 2, frameCentreY + depth / 2);
                    SetDetailLevels(transom, false, true, true, "橫中框");
                }
            }

            // 扇框與玻璃
            foreach (var sash in layout.AllSashes)
            {
                double sashCentreY = SashCentreY(sash, frameCentreY, depth, exteriorSign);
                double sashDepth = depth * (sash.IsSlidingCell ? SlidingSashDepthFactor : SashDepthFactor);

                var sashFrame = CreateExtrusion(famDoc, plane, true,
                    new[]
                    {
                        RectProfile(sash.X, sash.Y, sash.X + sash.Width, sash.Y + sash.Height, W, H),
                        RectProfile(sash.GlassX, sash.GlassY, sash.GlassX + sash.GlassWidth, sash.GlassY + sash.GlassHeight, W, H)
                    },
                    sashCentreY - sashDepth / 2, sashCentreY + sashDepth / 2);
                SetDetailLevels(sashFrame, false, true, true, $"扇框 {sash.CellId}#{sash.Index + 1}");

                var glass = CreateExtrusion(famDoc, plane, true,
                    new[] { RectProfile(sash.GlassX, sash.GlassY, sash.GlassX + sash.GlassWidth, sash.GlassY + sash.GlassHeight, W, H) },
                    sashCentreY - glassT / 2, sashCentreY + glassT / 2);
                SetDetailLevels(glass, false, true, true, $"玻璃 {sash.CellId}#{sash.Index + 1}");
            }
        }

        /// <summary>扇框深度方向的中心（橫拉外軌偏室外、內軌偏室內）。</summary>
        private static double SashCentreY(SashLayout sash, double frameCentreY, double depth, double exteriorSign)
        {
            if (!sash.IsSlidingCell) return frameCentreY;
            double offset = depth * SlidingTrackOffsetFactor * exteriorSign;
            return sash.IsOuterTrack ? frameCentreY + offset : frameCentreY - offset;
        }

        private void BuildVoid(Document famDoc, WindowFamilySpec spec, WindowLayout layout,
                               SketchPlane plane, double frameCentreY)
        {
            double gap = spec.InstanceParams.Mo.GapPerSide;
            double voidDepth = spec.ResolveVoidDepth();

            var moVoid = CreateExtrusion(famDoc, plane, false,
                new[] { RectProfile(-gap, -gap, layout.Width + gap, layout.Height + gap, layout.Width, layout.Height) },
                frameCentreY - voidDepth / 2, frameCentreY + voidDepth / 2);

            if (moVoid == null) return;

            if (!spec.InstanceParams.Mo.VoidDepth.HasValue)
            {
                _warnings.Add(
                    $"空心（MO）深度用預設 {voidDepth.ToString("0.#", CultureInfo.InvariantCulture)} cm 並以框心對稱；" +
                    "族在生成時不知道主體牆厚，若牆＋內外飾板總厚超過此值請改帶 instanceParams.mo.voidDepth。");
            }
            _warnings.Add(
                "內外飾板不是主體牆，Revit 不會自動切；放窗後需由 MCP 對與空心相交的飾板逐個套用切割（ADR D-006，未實測）。");
        }

        // ── 符號線 ───────────────────────────────────────

        private void BuildElevationSymbols(Document famDoc, WindowFamilySpec spec, WindowLayout layout, SketchPlane plane)
        {
            double W = layout.Width, H = layout.Height;
            bool apexAtHinge = spec.Symbols.ApexAtHinge;
            double sashW = spec.TypeParams.Sash;

            foreach (var sash in layout.AllSashes)
            {
                var curves = new List<Curve>();
                double cx = sash.X + sash.Width / 2.0;
                double cy = sash.Y + sash.Height / 2.0;

                switch (sash.Type)
                {
                    case SashType.Casement:
                        {
                            // ADR D-004：頂點頂到扇框外緣的實際鉸鏈位置，底邊在把手側兩角
                            double hingeX = sash.Hinge == HingeSide.Left ? sash.X : sash.X + sash.Width;
                            double handleX = sash.Hinge == HingeSide.Left ? sash.X + sash.Width : sash.X;
                            double apexX = apexAtHinge ? hingeX : handleX;
                            double baseX = apexAtHinge ? handleX : hingeX;
                            curves.Add(ElevLine(baseX, sash.Y, apexX, cy, W, H));
                            curves.Add(ElevLine(baseX, sash.Y + sash.Height, apexX, cy, W, H));
                            break;
                        }
                    case SashType.Awning:
                        {
                            double ins = sash.SashFrameWidth + 1.0;
                            double apexY = apexAtHinge ? sash.Y + ins : sash.Y + sash.Height - ins;
                            double baseY = apexAtHinge ? sash.Y + sash.Height - ins : sash.Y + ins;
                            curves.Add(ElevLine(sash.X + ins, baseY, cx, apexY, W, H));
                            curves.Add(ElevLine(sash.X + sash.Width - ins, baseY, cx, apexY, W, H));
                            break;
                        }
                    case SashType.Sliding:
                        {
                            double len = sash.Width * 0.35;
                            int dir = sash.Index % 2 == 0 ? 1 : -1;
                            double x1, x2;
                            if (sash.Count == 1) { x1 = cx - len / 2; x2 = cx + len / 2; }
                            else { x1 = cx - dir * len / 2; x2 = cx + dir * len / 2; }
                            curves.Add(ElevLine(x1, cy, x2, cy, W, H));
                            // 箭頭：兩支倒鉤
                            double barb = Math.Min(6.0, Math.Abs(x2 - x1) * 0.25);
                            int sign = x2 > x1 ? 1 : -1;
                            curves.Add(ElevLine(x2, cy, x2 - sign * barb, cy - barb * 0.45, W, H));
                            curves.Add(ElevLine(x2, cy, x2 - sign * barb, cy + barb * 0.45, W, H));
                            break;
                        }
                    default:
                        break; // 固定扇無開啟符號
                }

                // SPEC §6：立面粗略級仍畫開啟符號
                foreach (var curve in curves)
                {
                    var sc = CreateSymbolicCurve(famDoc, curve, plane, $"立面符號 {sash.CellId}");
                    SetDetailLevels(sc, true, true, true, "立面符號");
                }
            }
        }

        private void BuildPlanSymbols(Document famDoc, WindowFamilySpec spec, WindowLayout layout,
                                      SketchPlane plane, double frameCentreY, double depth, double exteriorSign)
        {
            double W = layout.Width;
            double angle = spec.Symbols.PlanSwingAngle * Math.PI / 180.0;
            double exteriorFaceY = frameCentreY + exteriorSign * depth / 2.0;
            GraphicsStyle hiddenStyle = FindHiddenLineStyle(famDoc);

            // 只畫最下排（平面剖切位置）的開啟示意，與 demo drawPlan 一致
            foreach (var bay in layout.Bays)
            {
                var bottomCell = bay.Cells.LastOrDefault();
                if (bottomCell == null) continue;

                foreach (var sash in bottomCell.Sashes)
                {
                    if (sash.Type == SashType.Casement && spec.Symbols.PlanSwingAngle > 0)
                    {
                        double hingeXCm = sash.Hinge == HingeSide.Left ? sash.X : sash.X + sash.Width;
                        double hingeX = WindowLayoutMath.ToFamilyX(hingeXCm, W);
                        int dir = sash.Hinge == HingeSide.Left ? 1 : -1;

                        // ADR D-004：30° 實線自鉸鏈點畫出（不畫成門的 90°）
                        double tipX = hingeX + dir * Math.Cos(angle) * sash.Width;
                        double tipY = exteriorFaceY + exteriorSign * Math.Sin(angle) * sash.Width;
                        var swing = CreateSymbolicCurve(famDoc,
                            Line.CreateBound(PlanPoint(hingeX, exteriorFaceY), PlanPoint(tipX, tipY)),
                            plane, $"平面開啟線 {sash.CellId}");
                        SetDetailLevels(swing, false, true, true, "平面開啟線");

                        // 開啟弧（虛線）：自關閉位置轉到 30°
                        try
                        {
                            XYZ centre = PlanPoint(hingeX, exteriorFaceY);
                            XYZ xAxis = new XYZ(dir, 0, 0);
                            XYZ yAxis = new XYZ(0, exteriorSign, 0);
                            Arc arc = Arc.Create(centre, sash.Width * CmToFeet, 0.0, angle, xAxis, yAxis);
                            var arcCurve = CreateSymbolicCurve(famDoc, arc, plane, $"平面開啟弧 {sash.CellId}");
                            SetDetailLevels(arcCurve, false, true, true, "平面開啟弧");
                            ApplyHiddenStyle(arcCurve, hiddenStyle);
                        }
                        catch (Exception ex)
                        {
                            _warnings.Add($"平面開啟弧建立失敗（{sash.CellId}）：{ex.Message}");
                        }
                    }
                    else if (sash.Type == SashType.Awning)
                    {
                        double hintY = frameCentreY + exteriorSign * depth * AwningPlanHintFactor;
                        double x1 = WindowLayoutMath.ToFamilyX(sash.X + sash.SashFrameWidth, W);
                        double x2 = WindowLayoutMath.ToFamilyX(sash.X + sash.Width - sash.SashFrameWidth, W);
                        var hint = CreateSymbolicCurve(famDoc,
                            Line.CreateBound(PlanPoint(x1, hintY), PlanPoint(x2, hintY)),
                            plane, $"平面上懸示意 {sash.CellId}");
                        SetDetailLevels(hint, false, true, true, "平面上懸示意");
                        ApplyHiddenStyle(hint, hiddenStyle);
                    }
                }
            }

            if (hiddenStyle == null)
            {
                _warnings.Add("族樣板內找不到虛線線型（Hidden Lines），平面開啟弧與上懸示意沿用預設符號線線型；需要虛線請在族內指定線型。");
            }
        }

        private GraphicsStyle FindHiddenLineStyle(Document famDoc)
        {
            try
            {
                return new FilteredElementCollector(famDoc)
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>()
                    .FirstOrDefault(gs => gs.GraphicsStyleCategory != null &&
                                          (gs.Name.IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           gs.Name.Contains("隱藏")));
            }
            catch { return null; }
        }

        private void ApplyHiddenStyle(CurveElement curve, GraphicsStyle style)
        {
            if (curve == null || style == null) return;
            try { curve.LineStyle = style; }
            catch (Exception ex) { _warnings.Add($"套用虛線線型失敗：{ex.Message}"); }
        }

        // ── 共用參數 ─────────────────────────────────────

        private void AddSharedParameters(Document famDoc, WindowFamilySpec spec, WindowLayout layout)
        {
            string sharedFile = ResolveSharedParameterFile();
            if (sharedFile == null)
            {
                _warnings.Add("找不到也無法建立共用參數檔 universal-window.txt，UW_ 參數未加入。");
                return;
            }

            string previous = _app.SharedParametersFilename;
            try
            {
                _app.SharedParametersFilename = sharedFile;
                DefinitionFile df = _app.OpenSharedParameterFile();
                if (df == null)
                {
                    _warnings.Add($"Revit 無法讀取共用參數檔：{sharedFile}");
                    return;
                }

                DefinitionGroup group = df.Groups
                    .Cast<DefinitionGroup>()
                    .FirstOrDefault(g => g.Name == UniversalWindowParameters.GroupName);
                if (group == null)
                {
                    _warnings.Add($"共用參數檔內找不到群組 {UniversalWindowParameters.GroupName}。");
                    return;
                }

                var fm = famDoc.FamilyManager;
                var values = BuildParameterValues(spec, layout);

                foreach (var def in UniversalWindowParameters.All)
                {
                    var ext = group.Definitions
                        .Cast<Definition>()
                        .FirstOrDefault(d => d.Name == def.Name) as ExternalDefinition;
                    if (ext == null)
                    {
                        _warnings.Add($"共用參數檔缺少 {def.Name}。");
                        continue;
                    }

                    FamilyParameter fp;
                    try
                    {
                        // Type 參數（ADR D-001：明細表以型別統計）
                        fp = fm.AddParameter(ext, GroupTypeId.Data, false);
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add($"加入參數 {def.Name} 失敗：{ex.Message}");
                        continue;
                    }

                    object value;
                    if (!values.TryGetValue(def.Name, out value) || value == null) continue;
                    try
                    {
                        if (value is int) fm.Set(fp, (int)value);
                        else if (value is double) fm.Set(fp, (double)value);
                        else fm.Set(fp, value.ToString());
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add($"設定參數 {def.Name} 失敗：{ex.Message}");
                    }
                }

                // IFC：型別參數 IfcExportAs（SPEC §7）
                try
                {
                    FamilyParameter ifcParam = fm.AddParameter(
                        "IfcExportAs", GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    fm.Set(ifcParam, "IfcWindow");
                }
                catch (Exception ex)
                {
                    _warnings.Add($"加入 IfcExportAs 參數失敗：{ex.Message}");
                }
            }
            finally
            {
                try { _app.SharedParametersFilename = previous; } catch { }
            }
        }

        private static Dictionary<string, object> BuildParameterValues(WindowFamilySpec spec, WindowLayout layout)
        {
            var d = layout.Derived;
            return new Dictionary<string, object>
            {
                { UniversalWindowParameters.RowCount, spec.Rows },
                { UniversalWindowParameters.ColCount, spec.Cols },
                { UniversalWindowParameters.SashCount, d.SashCount },
                { UniversalWindowParameters.OpenableCount, d.OpenableCount },
                { UniversalWindowParameters.Operations, spec.DescribeOperations() },
                { UniversalWindowParameters.GlassArea, d.GlassAreaM2 * SqMToSqFeet },
                { UniversalWindowParameters.DaylightArea, d.DaylightAreaM2 * SqMToSqFeet },
                { UniversalWindowParameters.OpenableArea, d.OpenableAreaM2 * SqMToSqFeet },
                { UniversalWindowParameters.MoWidth, d.MoWidth * CmToFeet },
                { UniversalWindowParameters.MoHeight, d.MoHeight * CmToFeet },
                // SPEC §5 預設值表：CNS 11527／11528／11526
                { UniversalWindowParameters.AirtightnessClass, "8 級" },
                { UniversalWindowParameters.WatertightnessClass, "35 級" },
                { UniversalWindowParameters.WindLoad, 280.0 }
                // UW_玻璃種類／防火時效／廠牌系列／Uaf／SF：留白給使用者
            };
        }

        /// <summary>
        /// 找共用參數檔：dll 同層 Resources → 往上找開發樹 → %APPDATA%\RevitMCP。
        /// 都沒有就依 C# 正本寫一份到 %APPDATA%（GUID 相同，不會造成參數分裂）。
        /// </summary>
        public static string ResolveSharedParameterFile()
        {
            const string fileName = "universal-window.txt";
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!string.IsNullOrEmpty(asmDir))
            {
                string candidate = Path.Combine(asmDir, "Resources", "shared-parameters", fileName);
                if (File.Exists(candidate)) return candidate;

                var dir = new DirectoryInfo(asmDir);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    candidate = Path.Combine(dir.FullName, "MCP", "Resources", "shared-parameters", fileName);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCP", "shared-parameters", fileName);
            if (File.Exists(appData)) return appData;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(appData));
                File.WriteAllText(appData,
                    UniversalWindowParameters.BuildSharedParameterFileContent(),
                    new UTF8Encoding(true));
                return appData;
            }
            catch { return null; }
        }

        // ────────────────────────────────────────────────
        // 幾何小工具
        // ────────────────────────────────────────────────

        /// <summary>
        /// 以 demo 座標（x 自左外緣、y 自窗頂往下，cm）建立立面平面上的矩形輪廓。
        /// 轉成族座標：X 以寬度中心為 0、Z 自窗台往上。
        /// </summary>
        private static CurveArray RectProfile(double x1, double y1, double x2, double y2, double totalW, double totalH)
        {
            double fx1 = WindowLayoutMath.ToFamilyX(Math.Min(x1, x2), totalW) * CmToFeet;
            double fx2 = WindowLayoutMath.ToFamilyX(Math.Max(x1, x2), totalW) * CmToFeet;
            double fz1 = WindowLayoutMath.ToFamilyZ(Math.Max(y1, y2), totalH) * CmToFeet;
            double fz2 = WindowLayoutMath.ToFamilyZ(Math.Min(y1, y2), totalH) * CmToFeet;

            var p1 = new XYZ(fx1, 0, fz1);
            var p2 = new XYZ(fx2, 0, fz1);
            var p3 = new XYZ(fx2, 0, fz2);
            var p4 = new XYZ(fx1, 0, fz2);

            var arr = new CurveArray();
            arr.Append(Line.CreateBound(p1, p2));
            arr.Append(Line.CreateBound(p2, p3));
            arr.Append(Line.CreateBound(p3, p4));
            arr.Append(Line.CreateBound(p4, p1));
            return arr;
        }

        private static Line ElevLine(double x1, double y1, double x2, double y2, double totalW, double totalH)
        {
            return Line.CreateBound(
                new XYZ(WindowLayoutMath.ToFamilyX(x1, totalW) * CmToFeet, 0, WindowLayoutMath.ToFamilyZ(y1, totalH) * CmToFeet),
                new XYZ(WindowLayoutMath.ToFamilyX(x2, totalW) * CmToFeet, 0, WindowLayoutMath.ToFamilyZ(y2, totalH) * CmToFeet));
        }

        /// <summary>平面符號用點：x 為族座標 cm、y 為深度方向 cm，z = 0（Ref. Level）。</summary>
        private static XYZ PlanPoint(double xCm, double yCm)
        {
            return new XYZ(xCm * CmToFeet, yCm * CmToFeet, 0);
        }

        /// <summary>
        /// 建立擠出。先在 [0, 厚度] 建好，再把 EXTRUSION_START/END_PARAM 調到目標區間
        /// （這兩個參數是相對草圖平面法線的偏移）。設定順序依目標區間正負決定，
        /// 避免中途出現 start &gt; end 的非法狀態；設參數若失敗則退回整體平移。
        /// </summary>
        private Extrusion CreateExtrusion(Document famDoc, SketchPlane plane, bool isSolid,
                                          IEnumerable<CurveArray> loops, double yStartCm, double yEndCm)
        {
            double lo = Math.Min(yStartCm, yEndCm) * CmToFeet;
            double hi = Math.Max(yStartCm, yEndCm) * CmToFeet;
            double thickness = hi - lo;
            if (thickness <= 0)
            {
                _warnings.Add("擠出厚度非正值，已略過一個實體。");
                return null;
            }

            var profile = new CurveArrArray();
            foreach (var loop in loops) profile.Append(loop);

            Extrusion ex;
            try
            {
                ex = famDoc.FamilyCreate.NewExtrusion(isSolid, profile, plane, thickness);
            }
            catch (Exception e)
            {
                _warnings.Add($"建立{(isSolid ? "實體" : "空心")}擠出失敗：{e.Message}");
                return null;
            }

            try
            {
                Parameter start = ex.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
                Parameter end = ex.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                if (start == null || end == null) throw new InvalidOperationException("取不到擠出起訖參數");

                if (lo >= 0) { end.Set(hi); start.Set(lo); }   // 目標整段在正側：先推 end
                else { start.Set(lo); end.Set(hi); }           // 目標含負值：先拉 start
            }
            catch (Exception e)
            {
                try
                {
                    ElementTransformUtils.MoveElement(famDoc, ex.Id, new XYZ(0, lo, 0));
                }
                catch (Exception e2)
                {
                    _warnings.Add($"擠出定位失敗（參數：{e.Message}；平移：{e2.Message}）。");
                }
            }
            return ex;
        }

        private SymbolicCurve CreateSymbolicCurve(Document famDoc, Curve curve, SketchPlane plane, string label)
        {
            try
            {
                return famDoc.FamilyCreate.NewSymbolicCurve(curve, plane);
            }
            catch (Exception ex)
            {
                _warnings.Add($"建立符號線失敗（{label}）：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 設定詳細程度可見性。
        /// API：Autodesk.Revit.DB.FamilyElementVisibility（不是 FamilyElementVisibilitySettings）。
        /// 只設三個詳細程度旗標；視圖方向旗標（FrontBack／LeftRight／PlanRCPCut）對 Model 型別會丟例外，故不碰。
        /// </summary>
        private void SetDetailLevels(Element element, bool coarse, bool medium, bool fine, string label)
        {
            if (element == null) return;
            try
            {
                bool viewSpecific = element is SymbolicCurve;
                var vis = new FamilyElementVisibility(
                    viewSpecific ? FamilyElementVisibilityType.ViewSpecific : FamilyElementVisibilityType.Model)
                {
                    IsShownInCoarse = coarse,
                    IsShownInMedium = medium,
                    IsShownInFine = fine
                };

                var form = element as GenericForm;
                if (form != null) { form.SetVisibility(vis); return; }

                var curve = element as SymbolicCurve;
                if (curve != null) { curve.SetVisibility(vis); return; }

                _warnings.Add($"元素型別不支援可見性設定：{label}");
            }
            catch (Exception ex)
            {
                _warnings.Add($"設定詳細程度可見性失敗（{label}）：{ex.Message}");
            }
        }

        private static string FormatXyz(XYZ v)
        {
            if (v == null) return "(null)";
            return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", v.X, v.Y, v.Z);
        }

        private static long GetIdValueSafe(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "UW-window";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }

    /// <summary>族載入選項：依 overwrite 旗標決定同名族是否覆蓋。</summary>
    public class WindowFamilyLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;

        public WindowFamilyLoadOptions(bool overwrite)
        {
            _overwrite = overwrite;
        }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                                        out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }
    }
}
