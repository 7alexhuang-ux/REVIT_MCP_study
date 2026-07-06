# 整地工作流 V2 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 三鈕任務流（新整地方案／方案總覽／比較出圖）＋ A0 單張總比較圖紙（C-00c：3D／坡度／正剖面／剖面立體／明細表 × 一欄一方案），並修正排圖不縮放的根因缺陷。

**Architecture:** 純排版數學（`SheetLayoutMath`）以 NUnit TDD；Revit 端沿用 partial `CommandExecutor` ＋ dispatcher case 模式新增 `create_grading_comparison_sheet`、`remove_grading_scheme` 兩命令；WPF 視窗走 `LogViewerWindow` code-behind 模式（無 XAML）；模型指標經共用參數回寫 → 原生 ViewSchedule 上圖。Ribbon 面板改四鈕（新方案／總覽／比較出圖／成套圖）。

**Tech Stack:** C#（Revit API 2024+，`#if REVIT2024_OR_GREATER`）、WPF code-behind、NUnit（net48）、TypeScript（MCP-Server tool schema）、node:test。

## Global Constraints

- 規格正本：`docs/superpowers/specs/2026-07-06-grading-workflow-v2-design.md`（拍板：案乙三鈕流／C-00c A0／明細表基本組＋鬆實方三本帳／刪 Offset 10m）。
- 一律 `#if REVIT2024_OR_GREATER` 保護新增的整地程式碼（含 ribbon 與 WPF）。
- C# namespace `RevitMCP`；MCP 工具名 snake_case；命令走既有 `RevitCommandRequest/Response` 與 dispatcher switch。
- 元素名稱一律經 `GradingNames.SanitizeElementName`（`MCP/Core/Grading/GradingNames.cs`）。
- 模式顯示中英對照：投影直貼 Footprint／外延漸變 Offset／放坡銜接 Slope；參數欄位＝可編輯 ComboBox（常用選項＋自由輸入）。
- 禁止：版本特定 csproj/addin、動 `<AddInId>`、繞過 MCP bridge。
- 建置驗證：`dotnet test MCP.Tests\RevitMCP.Tests.csproj` 全綠 ＋ `dotnet build -c Release.R24 MCP\RevitMCP.csproj` 0 error（R23 於收尾任務一併驗）。
- 分支：`grading-workflow-v2`（自 main 開，**開分支與首次 commit 前先向使用者確認現有未 commit 批次的處置**）。
- 實機檢核點需 Revit 2024＋TOPO_example＋MCP 服務在線，逐點與使用者協作驗收。

---

### Task 1: SheetLayoutMath 純排版數學（TDD）

**Files:**
- Create: `MCP/Core/Grading/SheetLayoutMath.cs`
- Modify: `MCP.Tests/RevitMCP.Tests.csproj`（加 Compile Include）
- Test: `MCP.Tests/Grading/SheetLayoutMathTests.cs`

**Interfaces:**
- Consumes: 無（零相依純函式）
- Produces:
  - `readonly struct LayoutCell { double X; double Y; double Width; double Height; }`（左上原點、Y 向下、單位任意——呼叫端用 mm 或 feet 皆可）
  - `static int SheetLayoutMath.SnapToStandardScale(double rawScale)`（向上取最近標準比例）
  - `static IReadOnlyList<LayoutCell> SheetLayoutMath.ComputeUniformGrid(double width, double height, int columns, int rows, double gap)`（row-major）
  - `sealed class ComparisonGridLayout { IReadOnlyList<LayoutCell> RowLabelCells; LayoutCell[][] SchemeCells; LayoutCell ScheduleCell; }`
  - `static ComparisonGridLayout SheetLayoutMath.ComputeComparisonGrid(double width, double height, int schemeCount, double gap, double labelColumnWidth)`（列權重固定 1.1／1.1／1.0／1.0＋明細列 0.7；`SchemeCells[row][col]`，4 列 × schemeCount 欄；`RowLabelCells` 5 格含明細列標籤）

- [ ] **Step 1: 寫失敗測試**

```csharp
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class SheetLayoutMathTests
    {
        [TestCase(87.0, 100)]
        [TestCase(100.0, 100)]
        [TestCase(101.0, 150)]
        [TestCase(420.0, 500)]
        [TestCase(0.5, 1)]
        public void SnapToStandardScale_RoundsUp(double raw, int expected)
        {
            Assert.That(SheetLayoutMath.SnapToStandardScale(raw), Is.EqualTo(expected));
        }

        [Test]
        public void ComputeUniformGrid_TwoByTwo_NoGap_SplitsEvenly()
        {
            var cells = SheetLayoutMath.ComputeUniformGrid(100, 80, 2, 2, 0);
            Assert.That(cells, Has.Count.EqualTo(4));
            Assert.That(cells[0].Width, Is.EqualTo(50).Within(1e-9));
            Assert.That(cells[0].Height, Is.EqualTo(40).Within(1e-9));
            Assert.That(cells[3].X, Is.EqualTo(50).Within(1e-9)); // row-major 第 2 列第 2 欄
            Assert.That(cells[3].Y, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void ComputeComparisonGrid_ThreeSchemes_HasFourRowsAndScheduleStrip()
        {
            var layout = SheetLayoutMath.ComputeComparisonGrid(1000, 700, 3, 10, 60);
            Assert.That(layout.SchemeCells.Length, Is.EqualTo(4));
            Assert.That(layout.SchemeCells[0].Length, Is.EqualTo(3));
            Assert.That(layout.RowLabelCells, Has.Count.EqualTo(5));
            // 明細列橫跨方案欄：寬 = 3 欄 + 2 gap
            var expectedWidth = layout.SchemeCells[0].Sum(c => c.Width) + (2 * 10);
            Assert.That(layout.ScheduleCell.Width, Is.EqualTo(expectedWidth).Within(1e-6));
            // 列權重：前兩列高 = 1.1 / 1.0 倍於第三列
            Assert.That(layout.SchemeCells[0][0].Height / layout.SchemeCells[2][0].Height,
                Is.EqualTo(1.1).Within(1e-6));
            // 全部格子都在版面內
            Assert.That(layout.ScheduleCell.Y + layout.ScheduleCell.Height, Is.LessThanOrEqualTo(700 + 1e-6));
        }
    }
}
```

- [ ] **Step 2: 在 `MCP.Tests/RevitMCP.Tests.csproj` 的 ItemGroup 加入**

```xml
<Compile Include="..\MCP\Core\Grading\SheetLayoutMath.cs" Link="Core\Grading\SheetLayoutMath.cs" Condition="Exists('..\MCP\Core\Grading\SheetLayoutMath.cs')" />
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj --filter "FullyQualifiedName~SheetLayoutMath"`
Expected: FAIL（CS0103 `SheetLayoutMath` 不存在）

- [ ] **Step 4: 實作**

```csharp
using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>圖紙格位（左上原點、Y 向下；單位由呼叫端決定）。</summary>
    public readonly struct LayoutCell
    {
        public LayoutCell(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
    }

    /// <summary>比較圖紙格局：4 內容列 × N 方案欄＋左側列標籤欄＋底部明細表橫條。</summary>
    public sealed class ComparisonGridLayout
    {
        public IReadOnlyList<LayoutCell> RowLabelCells { get; set; }
        public LayoutCell[][] SchemeCells { get; set; }
        public LayoutCell ScheduleCell { get; set; }
    }

    /// <summary>圖紙排版純數學（零 Revit 相依，NUnit 覆蓋）。</summary>
    public static class SheetLayoutMath
    {
        /// <summary>Revit 常用視圖比例分母，由小到大。</summary>
        public static readonly int[] StandardScales =
            { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000 };

        /// <summary>C-00c 內容列權重：3D／坡度／正剖面／剖面立體。</summary>
        private static readonly double[] ContentRowWeights = { 1.1, 1.1, 1.0, 1.0 };
        private const double ScheduleRowWeight = 0.7;

        /// <summary>把原始比例分母向上取到最近的標準比例（放不下→圖形縮小）。</summary>
        public static int SnapToStandardScale(double rawScale)
        {
            if (double.IsNaN(rawScale) || double.IsInfinity(rawScale) || rawScale <= 0)
            {
                throw new ArgumentException("比例分母必須是正有限數。", nameof(rawScale));
            }

            foreach (var scale in StandardScales)
            {
                if (scale >= rawScale - 1e-9)
                {
                    return scale;
                }
            }

            return StandardScales[StandardScales.Length - 1];
        }

        /// <summary>均分格陣，row-major。</summary>
        public static IReadOnlyList<LayoutCell> ComputeUniformGrid(
            double width, double height, int columns, int rows, double gap)
        {
            if (columns <= 0 || rows <= 0)
            {
                throw new ArgumentException("columns 與 rows 必須大於 0。");
            }

            var cellWidth = (width - (gap * (columns - 1))) / columns;
            var cellHeight = (height - (gap * (rows - 1))) / rows;
            var cells = new List<LayoutCell>(columns * rows);
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    cells.Add(new LayoutCell(
                        column * (cellWidth + gap), row * (cellHeight + gap), cellWidth, cellHeight));
                }
            }

            return cells;
        }

        /// <summary>C-00c 比較圖紙格局：權重列高、左標籤欄、底部明細橫條。</summary>
        public static ComparisonGridLayout ComputeComparisonGrid(
            double width, double height, int schemeCount, double gap, double labelColumnWidth)
        {
            if (schemeCount <= 0)
            {
                throw new ArgumentException("schemeCount 必須大於 0。", nameof(schemeCount));
            }

            var totalRows = ContentRowWeights.Length + 1; // 4 內容列＋明細列
            var weightSum = ScheduleRowWeight;
            foreach (var weight in ContentRowWeights) { weightSum += weight; }
            var usableHeight = height - (gap * (totalRows - 1));
            var unitHeight = usableHeight / weightSum;

            var contentWidth = width - labelColumnWidth - gap;
            var schemeWidth = (contentWidth - (gap * (schemeCount - 1))) / schemeCount;
            var contentX = labelColumnWidth + gap;

            var labels = new List<LayoutCell>(totalRows);
            var schemeCells = new LayoutCell[ContentRowWeights.Length][];
            var y = 0.0;
            for (var row = 0; row < ContentRowWeights.Length; row++)
            {
                var rowHeight = unitHeight * ContentRowWeights[row];
                labels.Add(new LayoutCell(0, y, labelColumnWidth, rowHeight));
                schemeCells[row] = new LayoutCell[schemeCount];
                for (var column = 0; column < schemeCount; column++)
                {
                    schemeCells[row][column] = new LayoutCell(
                        contentX + (column * (schemeWidth + gap)), y, schemeWidth, rowHeight);
                }

                y += rowHeight + gap;
            }

            var scheduleHeight = unitHeight * ScheduleRowWeight;
            labels.Add(new LayoutCell(0, y, labelColumnWidth, scheduleHeight));
            var scheduleCell = new LayoutCell(
                contentX, y, (schemeWidth * schemeCount) + (gap * (schemeCount - 1)), scheduleHeight);

            return new ComparisonGridLayout
            {
                RowLabelCells = labels,
                SchemeCells = schemeCells,
                ScheduleCell = scheduleCell
            };
        }
    }
}
```

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj --filter "FullyQualifiedName~SheetLayoutMath"`
Expected: PASS（3 個 fixture、全綠）

- [ ] **Step 6: 全套測試＋建置**

Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj` → 全綠；`dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error

- [ ] **Step 7: Commit**

```bash
git add MCP/Core/Grading/SheetLayoutMath.cs MCP.Tests/Grading/SheetLayoutMathTests.cs MCP.Tests/RevitMCP.Tests.csproj
git commit -m "功能：SheetLayoutMath 排版純數學（標準比例取整＋均分格陣＋C-00c 比較格局）"
```

---

### Task 2: PlaceViewsOnSheet 自動比例（修疊圖根因）

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.SheetLayout.cs:79-121`（排圖迴圈）

**Interfaces:**
- Consumes: `SheetLayoutMath.SnapToStandardScale(double)`（Task 1）
- Produces: `private static void FitViewportToCell(Document doc, Viewport viewport, View view, double cellWidthFeet, double cellHeightFeet, XYZ center)`——Task 6 比較圖紙重用同一顆

- [ ] **Step 1: 在 `PlaceViewsOnSheet` 的 `Viewport.Create` 成功分支後加自動縮放**

把 `CommandExecutor.SheetLayout.cs:105-112`（`var viewport = Viewport.Create(...)` 到 `Success = true` 物件）改為：

```csharp
                        var viewport = Viewport.Create(doc, sheet.Id, viewElementId, center);
                        if (fitToCell && doc.GetElement(viewElementId) is View placedView)
                        {
                            FitViewportToCell(doc, viewport, placedView,
                                (maxU - minU) / columns - margin, (maxV - minV) / rows - margin, center);
                        }

                        successCount++;
                        placed.Add(new
                        {
                            ViewId = viewIds[index],
                            ViewportId = viewport.Id.GetIdValue(),
                            Scale = (doc.GetElement(viewElementId) as View)?.Scale,
                            Success = true
                        });
```

並在方法開頭參數區（`rows` 解析之後）加：

```csharp
            var fitToCell = parameters["fitToCell"]?.Value<bool?>() ?? true;
```

- [ ] **Step 2: 在同檔案新增共用縮放輔助（`ExportSheetsPdf` 方法之前）**

```csharp
        /// <summary>
        /// 依格框可用尺寸自動調整視圖比例並置中：量測 viewport 外框 → 需求縮放比 →
        /// 視圖比例向上取標準比例 → 重新置中。圖表（Schedule）與透視視圖不適用，呼叫端排除。
        /// 修正 2026-07-06 實機缺陷：排圖不設比例導致視圖互相疊壓。
        /// </summary>
        private static void FitViewportToCell(
            Document doc, Viewport viewport, View view,
            double cellWidthFeet, double cellHeightFeet, XYZ center)
        {
            if (view is View3D perspective && perspective.IsPerspective)
            {
                return; // 透視圖比例無意義，僅置中。
            }

            try
            {
                doc.Regenerate();
                var outline = viewport.GetBoxOutline();
                var outlineWidth = outline.MaximumPoint.X - outline.MinimumPoint.X;
                var outlineHeight = outline.MaximumPoint.Y - outline.MinimumPoint.Y;
                if (outlineWidth <= 1e-9 || outlineHeight <= 1e-9 || view.Scale <= 0)
                {
                    return;
                }

                var ratio = Math.Max(outlineWidth / cellWidthFeet, outlineHeight / cellHeightFeet);
                var targetScale = RevitMCP.Core.Grading.SheetLayoutMath.SnapToStandardScale(view.Scale * ratio);
                if (targetScale != view.Scale)
                {
                    view.Scale = targetScale;
                    doc.Regenerate();
                }

                viewport.SetBoxCenter(center);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別視圖不支援改比例（例如鎖定狀態限制）時保持原樣，不讓整批失敗。
                viewport.SetBoxCenter(center);
            }
        }
```

- [ ] **Step 3: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj`
Expected: 0 error

- [ ] **Step 4: 實機檢核點（需 Revit＋使用者）**

部署後以 MCP 重跑 `create_grading_presentation`（任一方案）：圖紙上三個視圖各佔一格、不重疊、回應含各視圖 `Scale`。未過→回 Step 1 檢討量測/縮放邏輯。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.SheetLayout.cs
git commit -m "修正：place_views_on_sheet 依格框自動設定視圖比例並置中（疊圖根因）"
```

---

### Task 3: 共用參數綁定與指標回寫

**Files:**
- Create: `MCP/Core/Grading/GradingSharedParameters.cs`（Revit 相依，**不加入** tests csproj）

**Interfaces:**
- Consumes: `RevitToposolidGradingAdapter.TryReadSchemeRecord(Document, long)`（既有，回 JSON 字串）
- Produces:
  - `static void GradingSharedParameters.EnsureBindings(Document doc)`（冪等；須在交易內呼叫）
  - `static void GradingSharedParameters.WriteSchemeMetrics(Document doc, Toposolid design, JObject record)`（須在交易內呼叫）
  - `static readonly string[] GradingSharedParameters.OrderedNames`（明細表欄位順序）

- [ ] **Step 1: 實作**

```csharp
#if REVIT2024_OR_GREATER
using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

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

            // 鬆實方三本帳：欄位鍵名以 MCP/Core/Grading/EarthworkLedger.cs 的序列化名為準
            //（實作本步驟時開檔核對；無 Ledger 的方案寫 0）。
            var ledger = record["Ledger"] as JObject;
            SetNumber(design, "RevitMCP運土鬆方m3", ledger?.Value<double?>("HaulLooseCubicMeters") ?? 0);
            SetNumber(design, "RevitMCP所需自然方m3", ledger?.Value<double?>("RequiredBankCubicMeters") ?? 0);
            SetNumber(design, "RevitMCP實質淨土方m3", ledger?.Value<double?>("EffectiveNetCubicMeters") ?? 0);
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
            if (parameter != null && !parameter.IsReadOnly) { parameter.Set(value); }
        }

        private static DefinitionFile OpenOrCreateDefinitionFile(Application app)
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
```

⚠️ 實作時開 `MCP/Core/Grading/EarthworkLedger.cs` 與 `GradingSchemeRecord.cs` 核對：(1) Ledger 三欄位的實際序列化鍵名；(2) `OffsetDistanceMeters`／`SlopeRatio` 的實際鍵名。不符就以檔案為準修正本檔。

- [ ] **Step 2: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error

- [ ] **Step 3: Commit**

```bash
git add MCP/Core/Grading/GradingSharedParameters.cs
git commit -m "功能：整地共用參數綁定與方案指標回寫（明細表資料來源）"
```

---

### Task 4: 比較明細表（ViewSchedule）建立與上圖

**Files:**
- Create: `MCP/Core/Commands/CommandExecutor.GradingComparison.cs`（本任務先放明細表兩個 helper，Task 5–6 續寫同檔）

**Interfaces:**
- Consumes: `GradingSharedParameters.OrderedNames`（Task 3）
- Produces:
  - `private static ViewSchedule EnsureComparisonSchedule(Document doc)`（名稱固定「整地方案比較表」，冪等：同名既有即回傳）
  - `private static ScheduleSheetInstance PlaceScheduleOnSheet(Document doc, ViewSheet sheet, ViewSchedule schedule, XYZ topLeft)`

- [ ] **Step 1: 建檔實作（partial class 骨架＋兩個 helper）**

```csharp
#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const string ComparisonScheduleName = "整地方案比較表";

        /// <summary>建立（或沿用）整地方案比較明細表：Toposolid 類別、欄位=共用參數、過濾方案名有值。</summary>
        private static ViewSchedule EnsureComparisonSchedule(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(candidate => candidate.Name == ComparisonScheduleName);
            if (existing != null)
            {
                return existing;
            }

            var schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_Toposolid));
            schedule.Name = ComparisonScheduleName;
            var definition = schedule.Definition;

            var schedulable = definition.GetSchedulableFields();
            ScheduleField nameField = null;
            foreach (var parameterName in GradingSharedParameters.OrderedNames)
            {
                var field = schedulable.FirstOrDefault(
                    candidate => candidate.GetName(doc) == parameterName);
                if (field == null)
                {
                    continue; // 未綁定的欄位跳過（EnsureBindings 應先跑）
                }

                var added = definition.AddField(field);
                if (parameterName == "RevitMCP方案名")
                {
                    nameField = added;
                }
            }

            if (nameField == null)
            {
                throw new InvalidOperationException(
                    "明細表缺「RevitMCP方案名」欄位；請先執行共用參數綁定。");
            }

            definition.AddFilter(new ScheduleFilter(
                nameField.FieldId, ScheduleFilterType.HasValue));
            definition.AddSortGroupField(new ScheduleSortGroupField(nameField.FieldId));
            return schedule;
        }

        /// <summary>把明細表放上圖紙（topLeft 為圖紙座標、feet）。</summary>
        private static ScheduleSheetInstance PlaceScheduleOnSheet(
            Document doc, ViewSheet sheet, ViewSchedule schedule, XYZ topLeft)
        {
            var existing = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .FirstOrDefault(instance => instance.ScheduleId == schedule.Id);
            if (existing != null)
            {
                doc.Delete(existing.Id); // 重排：舊實例移除重放
            }

            return ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, topLeft);
        }
    }
}
#endif
```

- [ ] **Step 2: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error

- [ ] **Step 3: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.GradingComparison.cs
git commit -m "功能：整地方案比較明細表（共用參數欄位＋方案名過濾＋圖紙放置）"
```

---

### Task 5: 共同剖面線＋剖面/剖面立體視圖

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.GradingComparison.cs`（續寫三個 helper）

**Interfaces:**
- Consumes: `RevitToposolidGradingAdapter.CollectSolids(Toposolid)`、`RevitToposolidGradingAdapter.IntersectTerrainTopZ(IList<Solid>, Point2D, double, double)`（既有 static）、`UniqueViewName(Document, string)`（既有，GradingView.cs）
- Produces:
  - `private static (XYZ Start, XYZ End) ComputeMaxDropSectionLine(Toposolid original)`（原地形頂面粗網格取樣，最高點連最低點、兩端外延 20%）
  - `private static ViewSection CreateSchemeSectionView(Document doc, string schemeName, XYZ start, XYZ end, IReadOnlyList<ElementId> otherDesignIds)`
  - `private static View3D CreateSchemeSectionIsoView(Document doc, string schemeName, XYZ start, XYZ end, IReadOnlyList<ElementId> isolateIds)`

- [ ] **Step 1: 實作三個 helper（附在 Task 4 同檔 partial class 內）**

```csharp
        /// <summary>沿原地形最大高差方向定共同剖面線：8×8 粗網格取樣頂面 Z，最高點連最低點、外延 20%。</summary>
        private static (XYZ Start, XYZ End) ComputeMaxDropSectionLine(Toposolid original)
        {
            var solids = RevitToposolidGradingAdapter.CollectSolids(original);
            var box = original.get_BoundingBox(null)
                ?? throw new InvalidOperationException("原地形沒有範圍盒。");
            var bottomZ = box.Min.Z - 10.0;
            var topZ = box.Max.Z + 10.0;

            XYZ highest = null, lowest = null;
            double highestZ = double.MinValue, lowestZ = double.MaxValue;
            const int steps = 8;
            for (var i = 0; i <= steps; i++)
            {
                for (var j = 0; j <= steps; j++)
                {
                    var x = box.Min.X + ((box.Max.X - box.Min.X) * i / steps);
                    var y = box.Min.Y + ((box.Max.Y - box.Min.Y) * j / steps);
                    var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        solids, new Point2D(x, y), bottomZ, topZ);
                    if (!z.HasValue) { continue; }
                    if (z.Value > highestZ) { highestZ = z.Value; highest = new XYZ(x, y, z.Value); }
                    if (z.Value < lowestZ) { lowestZ = z.Value; lowest = new XYZ(x, y, z.Value); }
                }
            }

            if (highest == null || lowest == null || highest.DistanceTo(lowest) < 1e-6)
            {
                throw new InvalidOperationException("取樣不到原地形高差方向，無法決定剖面線。");
            }

            var direction = (lowest - highest);
            var extend = 0.2;
            return (highest - (direction * extend), lowest + (direction * extend));
        }

        /// <summary>建方案正剖面：沿共同剖面線、階段過濾顯示拆除（原地形虛線疊比）、隱藏其他方案設計地形。</summary>
        private static ViewSection CreateSchemeSectionView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> otherDesignIds)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.Section)
                ?? throw new InvalidOperationException("找不到剖面視圖類型。");

            var mid = (start + end) / 2;
            var lineDirection = (end - start).Normalize();
            var viewDirection = lineDirection.CrossProduct(XYZ.BasisZ);
            var halfLength = start.DistanceTo(end) / 2;
            var depth = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Meters);
            var height = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);

            var transform = Transform.Identity;
            transform.Origin = new XYZ(mid.X, mid.Y, mid.Z);
            transform.BasisX = lineDirection;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = viewDirection;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, -height / 2, 0),
                Max = new XYZ(halfLength, height / 2, depth)
            };

            var section = ViewSection.CreateSection(doc, viewFamilyType.Id, sectionBox);
            section.Name = UniqueViewName(doc, $"整地-{schemeName}-剖面");
            // 顯示「拆除」的原地形（虛線疊比）：階段過濾用「全部顯示」
            var phaseFilter = new FilteredElementCollector(doc)
                .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .FirstOrDefault(filter => filter.Name == "全部顯示" || filter.Name == "Show All");
            if (phaseFilter != null)
            {
                section.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(phaseFilter.Id);
            }

            if (otherDesignIds.Count > 0)
            {
                section.HideElements(otherDesignIds.ToList());
            }

            return section;
        }

        /// <summary>建方案剖面立體圖：等角 3D＋剖面框沿剖切帶（規格風險節：不用透視，避免透視排圖限制）。</summary>
        private static View3D CreateSchemeSectionIsoView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> isolateIds)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("找不到 3D 視圖類型。");

            var view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            view.Name = UniqueViewName(doc, $"整地-{schemeName}-剖立體");
            view.IsolateElementsTemporary(isolateIds.ToList());
            view.ConvertTemporaryHideIsolateToPermanent();

            var mid = (start + end) / 2;
            var lineDirection = (end - start).Normalize();
            var viewDirection = lineDirection.CrossProduct(XYZ.BasisZ);
            var halfLength = start.DistanceTo(end) / 2;
            var halfBand = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Meters);
            var height = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);
            var transform = Transform.Identity;
            transform.Origin = mid;
            transform.BasisX = lineDirection;
            transform.BasisY = viewDirection;
            transform.BasisZ = XYZ.BasisZ;
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, 0, -height / 2),   // 剖面框自剖切線起、只留一側 → 剖開效果
                Max = new XYZ(halfLength, halfBand, height / 2)
            });
            view.SaveOrientationAndLock();
            return view;
        }
```

- [ ] **Step 2: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error（`Point2D` 型別在 `RevitMCP.Core.Grading`，using 已含）

- [ ] **Step 3: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.GradingComparison.cs
git commit -m "功能：共同剖面線計算與方案剖面/剖面立體視圖組合"
```

---

### Task 6: `create_grading_comparison_sheet` 命令組裝

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.GradingComparison.cs`（主命令）
- Modify: `MCP/Core/CommandExecutor.cs:488-490`（dispatcher 加 case，放在 `export_earthwork_gridsheet` case 之後、`#endif` 之前）

**Interfaces:**
- Consumes: Task 1 `SheetLayoutMath.ComputeComparisonGrid`；Task 2 `FitViewportToCell`；Task 3 `EnsureBindings`/`WriteSchemeMetrics`；Task 4 `EnsureComparisonSchedule`/`PlaceScheduleOnSheet`；Task 5 三個 helper；既有 `EnsureSchemeView`（GradingPresentation.cs）、`DuplicateSchemeView`、`CreateSlopeHeatmap`、`CreateGradingExtentLines`、`GradingNames.SanitizeElementName`
- Produces: dispatcher case `"create_grading_comparison_sheet"` → `private object CreateGradingComparisonSheet(JObject parameters)`；參數 `designToposolidIds`（選）、`titleBlockId`（選）、`export`（預設 true）、`outputPath`（選）

- [ ] **Step 1: dispatcher case（`CommandExecutor.cs`，`export_earthwork_gridsheet` case 後）**

```csharp
                    case "create_grading_comparison_sheet":
                        result = CreateGradingComparisonSheet(parameters);
                        break;

                    case "remove_grading_scheme":
                        result = RemoveGradingScheme(parameters);
                        break;
```

（`RemoveGradingScheme` 於 Task 7 實作；為避免兩次動 dispatcher，本步一次加兩個 case，Task 7 完成前先以 `throw new NotImplementedException("remove_grading_scheme 尚未實作")` 佔位方法讓建置通過，Task 7 替換之。）

- [ ] **Step 2: 主命令實作（附在同檔 partial class）**

```csharp
        /// <summary>
        /// C-00c 單張總比較圖紙：一欄一方案（3D／坡度熱區／正剖面／剖面立體），
        /// 底部整地方案比較明細表；A0 圖框；可選匯 PDF。逐方案容錯。
        /// </summary>
        private object CreateGradingComparisonSheet(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var records = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse).ToList();
            var filter = parameters["designToposolidIds"] as JArray;
            if (filter != null && filter.Count > 0)
            {
                var wanted = new HashSet<long>(filter.Values<long>());
                records = records.Where(r => wanted.Contains(r.Value<long>("DesignToposolidId"))).ToList();
            }

            if (records.Count == 0)
            {
                throw new InvalidOperationException("登記簿沒有可比較的方案。");
            }

            var titleBlockId = ResolveA0TitleBlock(doc, parameters);
            var export = parameters["export"]?.Value<bool?>() ?? true;

            // 1. 共用參數綁定＋指標回寫（單一交易）
            using (var transaction = new Transaction(doc, "回寫方案比較指標"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                GradingSharedParameters.EnsureBindings(doc);
                foreach (var record in records)
                {
                    var designId = record.Value<long>("DesignToposolidId");
                    if (doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design)
                    {
                        GradingSharedParameters.WriteSchemeMetrics(doc, design, record);
                    }
                }

                transaction.Commit();
            }

            // 2. 共同剖面線（取第一筆記錄的原地形）
            var originalId = records[0].Value<long>("OriginalToposolidId");
            var original = doc.GetElement(new ElementId(checked((int)originalId))) as Toposolid
                ?? throw new InvalidOperationException($"原地形 {originalId} 不存在。");
            var (sectionStart, sectionEnd) = ComputeMaxDropSectionLine(original);

            var allDesignIds = records
                .Select(r => new ElementId(checked((int)r.Value<long>("DesignToposolidId"))))
                .ToList();

            // 3. 每方案確保四個視圖（逐方案容錯，沿用 presentation 的 TrySubStep 模式）
            var schemeColumns = new List<(string Name, long[] ViewIds, List<string> Steps)>();
            foreach (var record in records)
            {
                var designId = record.Value<long>("DesignToposolidId");
                var designElementId = new ElementId(checked((int)designId));
                var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
                var steps = new List<string>();
                try
                {
                    var view3dId = EnsureSchemeView(doc, designId, record, steps);
                    var slopeViewId = DuplicateSchemeView(doc, view3dId, $"整地-{schemeName}-坡度比較");
                    TrySubStep(steps, "坡度熱區", () =>
                        CreateSlopeHeatmap(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));
                    TrySubStep(steps, "範圍線", () =>
                        CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));

                    var otherDesigns = allDesignIds.Where(id => id != designElementId).ToList();
                    long sectionId = 0, isoId = 0;
                    using (var transaction = new Transaction(doc, "建立比較剖面視圖"))
                    {
                        GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                        transaction.Start();
                        sectionId = CreateSchemeSectionView(doc, schemeName, sectionStart, sectionEnd, otherDesigns)
                            .Id.GetIdValue();
                        var isolate = new List<ElementId> { designElementId, original.Id };
                        foreach (var floorId in (record["FloorIds"] as JArray)?.Values<long>() ?? Enumerable.Empty<long>())
                        {
                            isolate.Add(new ElementId(checked((int)floorId)));
                        }

                        isoId = CreateSchemeSectionIsoView(doc, schemeName, sectionStart, sectionEnd, isolate)
                            .Id.GetIdValue();
                        transaction.Commit();
                    }

                    steps.Add("剖面×2");
                    schemeColumns.Add((schemeName, new[] { view3dId, slopeViewId, sectionId, isoId }, steps));
                }
                catch (Exception ex)
                {
                    steps.Add($"失敗：{ex.Message}");
                    schemeColumns.Add((schemeName, Array.Empty<long>(), steps));
                }
            }

            var usable = schemeColumns.Where(c => c.ViewIds.Length == 4).ToList();
            if (usable.Count == 0)
            {
                throw new InvalidOperationException(
                    "沒有任何方案成功備妥四個視圖：" + string.Join("；",
                        schemeColumns.Select(c => $"{c.Name}[{string.Join(",", c.Steps)}]")));
            }

            // 4. 建圖紙＋格局排版＋明細表（單一交易）
            long sheetIdValue;
            using (var transaction = new Transaction(doc, "建立方案總比較圖紙"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.Name = UniqueSheetName(doc, "整地方案總比較");
                sheet.SheetNumber = NextComparisonSheetNumber(doc);
                sheetIdValue = sheet.Id.GetIdValue();
                doc.Regenerate();

                var titleBlockBox = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement()?.get_BoundingBox(sheet)
                    ?? throw new InvalidOperationException("比較圖紙沒有圖框。");
                var margin = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Millimeters);
                var originX = titleBlockBox.Min.X + margin;
                var topY = titleBlockBox.Max.Y - margin;
                var width = (titleBlockBox.Max.X - titleBlockBox.Min.X) - (2 * margin);
                var height = (titleBlockBox.Max.Y - titleBlockBox.Min.Y) - (2 * margin);
                var gap = UnitUtils.ConvertToInternalUnits(8, UnitTypeId.Millimeters);
                var labelWidth = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
                var layout = SheetLayoutMath.ComputeComparisonGrid(width, height, usable.Count, gap, labelWidth);

                string[] rowLabels = { "(a) 3D", "(b) 坡度", "(c) 剖面", "(c2) 剖立體", "(d) 明細" };
                var textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .FirstElementId();
                for (var row = 0; row < layout.RowLabelCells.Count; row++)
                {
                    var cell = layout.RowLabelCells[row];
                    TextNote.Create(doc, sheet.Id,
                        new XYZ(originX + cell.X, topY - cell.Y - (cell.Height / 2), 0),
                        rowLabels[row], textType);
                }

                for (var column = 0; column < usable.Count; column++)
                {
                    for (var row = 0; row < 4; row++)
                    {
                        var cell = layout.SchemeCells[row][column];
                        var center = new XYZ(
                            originX + cell.X + (cell.Width / 2),
                            topY - cell.Y - (cell.Height / 2), 0);
                        var viewElementId = new ElementId(checked((int)usable[column].ViewIds[row]));
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewElementId))
                        {
                            usable[column].Steps.Add($"第{row + 1}列視圖已在其他圖紙，略過");
                            continue;
                        }

                        var viewport = Viewport.Create(doc, sheet.Id, viewElementId, center);
                        if (doc.GetElement(viewElementId) is View placedView)
                        {
                            FitViewportToCell(doc, viewport, placedView, cell.Width, cell.Height, center);
                        }
                    }
                }

                var schedule = EnsureComparisonSchedule(doc);
                PlaceScheduleOnSheet(doc, sheet, schedule,
                    new XYZ(originX + layout.ScheduleCell.X, topY - layout.ScheduleCell.Y, 0));
                transaction.Commit();
            }

            // 5. PDF
            object pdf = null;
            if (export)
            {
                try
                {
                    pdf = ExportSheetsPdf(new JObject
                    {
                        ["sheetIds"] = new JArray(new[] { sheetIdValue }),
                        ["outputPath"] = parameters["outputPath"]?.Value<string>(),
                        ["combine"] = true
                    });
                }
                catch (Exception ex) { pdf = new { Success = false, Error = ex.Message }; }
            }

            return new
            {
                SheetId = sheetIdValue,
                SchemeCount = usable.Count,
                Schemes = schemeColumns.Select(c => new { c.Name, c.ViewIds, c.Steps }),
                Pdf = pdf,
                Message = $"方案總比較圖紙完成（{usable.Count} 欄）。"
            };
        }

        /// <summary>找 A0 圖框：titleBlockId 參數優先；否則型別名或族名含「A0」者；找不到→引導。</summary>
        private static ElementId ResolveA0TitleBlock(Document doc, JObject parameters)
        {
            var explicitId = parameters["titleBlockId"]?.Value<long?>() ?? 0;
            if (explicitId > 0)
            {
                return new ElementId(checked((int)explicitId));
            }

            var a0 = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .FirstOrDefault(symbol =>
                    symbol.Name.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0
                    || symbol.FamilyName.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0);
            return a0?.Id ?? throw new InvalidOperationException(
                "模型沒有 A0 圖框；請載入 A0 公制圖框族群，或以 titleBlockId 指定圖框。");
        }

        /// <summary>比較圖紙編號 GRD-C01 起流水。</summary>
        private static string NextComparisonSheetNumber(Document doc)
        {
            var numbers = new HashSet<string>(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(sheet => sheet.SheetNumber));
            for (var index = 1; index < 100; index++)
            {
                var candidate = $"GRD-C{index:00}";
                if (!numbers.Contains(candidate)) { return candidate; }
            }

            return $"GRD-C{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }
```

- [ ] **Step 3: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error

- [ ] **Step 4: 實機檢核點（需 Revit＋使用者，A0 圖框先確認）**

先 `get_titleblocks` 確認 A0 存在（無則請使用者載入）→ MCP 呼叫 `create_grading_comparison_sheet` → 驗收：GRD-C01 圖紙、4 列×N 欄不重疊、明細表數字與 `list_grading_schemes` 一致、PDF 開得起來。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.GradingComparison.cs MCP/Core/CommandExecutor.cs
git commit -m "功能：create_grading_comparison_sheet 單張總比較圖紙（C-00c）"
```

---

### Task 7: `remove_grading_scheme` 刪除方案

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.GradingComparison.cs`（把 Task 6 Step 1 的佔位方法換成實作）

**Interfaces:**
- Consumes: `RequireSchemeRecord(Document, JObject)`（既有，GradingView.cs:18）、`RevitToposolidGradingAdapter.ReadSchemeRecords`
- Produces: `private object RemoveGradingScheme(JObject parameters)`；參數 `designToposolidId`（必填）、`confirm`（必填，須為 `true`——破壞性護欄）

- [ ] **Step 1: 實作**

```csharp
        /// <summary>
        /// 刪除整地方案（破壞性）：刪設計地形（記錄隨 ES 一併消失）＋方案相關視圖；
        /// 若刪後登記簿為空，解除原地形的「拆除」階段設定使其復原顯示。
        /// </summary>
        private object RemoveGradingScheme(JObject parameters)
        {
            if (parameters["confirm"]?.Value<bool?>() != true)
            {
                throw new InvalidOperationException(
                    "remove_grading_scheme 為破壞性操作，須帶 confirm=true。");
            }

            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var schemeName = record.Value<string>("SchemeName") ?? design.Id.GetIdValue().ToString();
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;

            // 蒐集要刪的視圖：記錄的方案視圖＋名稱前綴「整地-{方案名}」的衍生視圖
            var prefix = GradingNames.SanitizeElementName($"整地-{schemeName}");
            var viewIds = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(view => !view.IsTemplate && view.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(view => view.Id)
                .ToList();

            var deleted = new List<long>();
            using (var transaction = new Transaction(doc, $"刪除整地方案 {schemeName}"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                foreach (var viewId in viewIds)
                {
                    try { doc.Delete(viewId); deleted.Add(viewId.GetIdValue()); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* 已被圖紙相依刪除 */ }
                }

                deleted.Add(design.Id.GetIdValue());
                doc.Delete(design.Id); // 記錄在設計地形的 ES 上，隨元素一併刪除
                doc.Regenerate();

                var remaining = RevitToposolidGradingAdapter.ReadSchemeRecords(doc).Count;
                if (remaining == 0 && originalId > 0
                    && doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original)
                {
                    original.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(ElementId.InvalidElementId);
                }

                transaction.Commit();
                return new
                {
                    SchemeName = schemeName,
                    DeletedElementIds = deleted,
                    RemainingSchemes = remaining,
                    Message = $"方案「{schemeName}」已刪除（{deleted.Count} 個元素）；剩餘 {remaining} 筆方案。"
                };
            }
        }
```

- [ ] **Step 2: 建置＋全套測試**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error；`dotnet test MCP.Tests\RevitMCP.Tests.csproj` → 全綠

- [ ] **Step 3: 實機檢核點（需 Revit＋使用者）**

以 MCP 對 Offset 10m 的設計地形 ID 呼叫 `remove_grading_scheme confirm=true`（使用者已核准此刪除）→ 驗收：`list_grading_schemes` 少一筆、模型無孤兒「整地-Offset 10m-*」視圖、其餘方案不受影響。

- [ ] **Step 4: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.GradingComparison.cs
git commit -m "功能：remove_grading_scheme 刪除方案（confirm 護欄＋登記簿清空時復原原地形）"
```

---

### Task 8: 新整地方案對話框＋Ribbon 改四鈕

**Files:**
- Create: `MCP/Commands/NewGradingSchemeDialog.cs`
- Modify: `MCP/Commands/GradingRibbonCommands.cs`（加 `NewGradingSchemeCommand`、`GradingComparisonCommand`；保留 `GradingPresentationCommand`）
- Modify: `MCP/Application.cs:59-74`（整地圖說面板改四鈕）

**Interfaces:**
- Consumes: `GradingRibbonHelper.RunGrade(ExternalCommandData, string mode, JObject extra)`、`GradingRibbonHelper.Run(UIApplication, string commandName, JObject parameters, string title)`（既有）
- Produces: `class NewGradingSchemeDialog : System.Windows.Window`，屬性 `string Mode`（`footprint_only|offset_transition|slope_transition`）、`JObject ExtraParameters`（含 `schemeName` 與模式參數）；`ShowDialog()==true` 表示使用者按「執行並記錄」

- [ ] **Step 1: 對話框實作（LogViewerWindow 的 code-behind 模式；中英對照＋可編輯下拉）**

```csharp
#if REVIT2024_OR_GREATER
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 新整地方案對話框：模式中英對照、參數可編輯下拉（常用值＋自由輸入）、
    /// 方案名隨輸入即時自動組（使用者改過即停止自動覆蓋）。
    /// </summary>
    public class NewGradingSchemeDialog : Window
    {
        private readonly RadioButton _footprintRadio;
        private readonly RadioButton _offsetRadio;
        private readonly RadioButton _slopeRadio;
        private readonly ComboBox _offsetCombo;
        private readonly ComboBox _slopeCombo;
        private readonly TextBox _nameBox;
        private TextBox _looseFactorBox;
        private TextBox _compactionFactorBox;
        private bool _nameEdited;
        private bool _updatingName;

        public string Mode { get; private set; }
        public JObject ExtraParameters { get; private set; }

        public NewGradingSchemeDialog(string selectionSummary)
        {
            Title = "新整地方案";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "模式", FontWeight = FontWeights.Bold });
            _footprintRadio = new RadioButton { Content = "投影直貼 Footprint", GroupName = "mode", Margin = new Thickness(0, 2, 0, 0) };
            _offsetRadio = new RadioButton { Content = "外延漸變 Offset", GroupName = "mode", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
            _slopeRadio = new RadioButton { Content = "放坡銜接 Slope", GroupName = "mode", Margin = new Thickness(0, 2, 0, 0) };
            panel.Children.Add(_footprintRadio);
            panel.Children.Add(_offsetRadio);
            panel.Children.Add(_slopeRadio);

            panel.Children.Add(new TextBlock { Text = "外延距離（m）：常用 3／5／10／15／20，可自行輸入", Margin = new Thickness(0, 8, 0, 2) });
            _offsetCombo = new ComboBox { IsEditable = true, Text = "5" };
            foreach (var preset in new[] { "3", "5", "10", "15", "20" }) { _offsetCombo.Items.Add(preset); }
            panel.Children.Add(_offsetCombo);

            panel.Children.Add(new TextBlock { Text = "坡率 1:n：常用 1:1／1:1.5／1:2／1:3，可自行輸入", Margin = new Thickness(0, 8, 0, 2) });
            _slopeCombo = new ComboBox { IsEditable = true, Text = "1:2", IsEnabled = false };
            foreach (var preset in new[] { "1:1", "1:1.5", "1:2", "1:3" }) { _slopeCombo.Items.Add(preset); }
            panel.Children.Add(_slopeCombo);

            panel.Children.Add(new TextBlock { Text = "方案名稱（隨輸入值自動組，可改）", Margin = new Thickness(0, 8, 0, 2) });
            _nameBox = new TextBox();
            panel.Children.Add(_nameBox);

            panel.Children.Add(new TextBlock { Text = "鬆方係數／壓實係數（選填，成對；供鬆實方三本帳）", Margin = new Thickness(0, 8, 0, 2) });
            var factorRow = new StackPanel { Orientation = Orientation.Horizontal };
            _looseFactorBox = new TextBox { Width = 70 };
            _compactionFactorBox = new TextBox { Width = 70, Margin = new Thickness(8, 0, 0, 0) };
            factorRow.Children.Add(_looseFactorBox);
            factorRow.Children.Add(_compactionFactorBox);
            panel.Children.Add(factorRow);

            panel.Children.Add(new TextBlock { Text = selectionSummary, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "執行並記錄", Width = 96, IsDefault = true };
            ok.Click += OnOk;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;

            RoutedEventHandler refresh = (s, e) => RefreshEnabledAndName();
            _footprintRadio.Checked += refresh;
            _offsetRadio.Checked += refresh;
            _slopeRadio.Checked += refresh;
            _offsetCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabledAndName()));
            _slopeCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabledAndName()));
            _nameBox.TextChanged += (s, e) => { if (!_updatingName) { _nameEdited = true; } };
            RefreshEnabledAndName();
        }

        private void RefreshEnabledAndName()
        {
            if (_offsetCombo == null || _slopeCombo == null) { return; }
            _offsetCombo.IsEnabled = _offsetRadio.IsChecked == true;
            _slopeCombo.IsEnabled = _slopeRadio.IsChecked == true;
            if (_nameEdited) { return; }

            _updatingName = true;
            if (_footprintRadio.IsChecked == true) { _nameBox.Text = "投影直貼"; }
            else if (_offsetRadio.IsChecked == true) { _nameBox.Text = $"外延 {_offsetCombo.Text.Trim()}m"; }
            else { _nameBox.Text = $"放坡 {_slopeCombo.Text.Trim()}"; }
            _updatingName = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var extra = new JObject { ["schemeName"] = _nameBox.Text.Trim() };
            if (_offsetRadio.IsChecked == true)
            {
                if (!double.TryParse(_offsetCombo.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) || distance <= 0)
                {
                    MessageBox.Show("外延距離必須是正數。", "新整地方案"); return;
                }

                Mode = "offset_transition";
                extra["offsetDistance"] = distance;
            }
            else if (_slopeRadio.IsChecked == true)
            {
                var ratio = _slopeCombo.Text.Trim();
                var parts = ratio.Split(':');
                if (parts.Length != 2
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
                {
                    MessageBox.Show("坡率格式須為 1:n（n 為正數）。", "新整地方案"); return;
                }

                Mode = "slope_transition";
                extra["slopeRatio"] = ratio;
            }
            else
            {
                Mode = "footprint_only";
            }

            var looseText = _looseFactorBox.Text.Trim();
            var compactionText = _compactionFactorBox.Text.Trim();
            if (looseText.Length > 0 || compactionText.Length > 0)
            {
                if (!double.TryParse(looseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var loose) || loose <= 0
                    || !double.TryParse(compactionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var compaction) || compaction <= 0)
                {
                    MessageBox.Show("鬆方係數與壓實係數須成對填寫且為正數（例：1.25／0.9）。", "新整地方案"); return;
                }

                extra["looseFactor"] = loose;
                extra["compactionFactor"] = compaction;
            }

            ExtraParameters = extra;
            DialogResult = true;
        }
    }
}
#endif
```

- [ ] **Step 2: `GradingRibbonCommands.cs` 加兩個命令類（檔尾既有命令類旁）**

```csharp
    /// <summary>①新整地方案：對話框選模式/參數 → 執行並記錄一筆（重複按＝做下一個方案）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class NewGradingSchemeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var selectionSummary = GradingRibbonHelper.DescribeSelection(uiDoc);
            var dialog = new NewGradingSchemeDialog(selectionSummary);
            if (dialog.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            return GradingRibbonHelper.RunGrade(commandData, dialog.Mode, dialog.ExtraParameters);
        }
    }

    /// <summary>③比較出圖：登記簿全部方案 → C-00c 單張總比較圖紙＋PDF。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradingComparisonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.Run(
                commandData.Application, "create_grading_comparison_sheet", new JObject(), "比較出圖");
        }
    }
```

並在 `GradingRibbonHelper` 加：

```csharp
        /// <summary>目前選取的摘要字串（對話框顯示用），如「✔ 地形 ×1、樓板 ×2」。</summary>
        public static string DescribeSelection(UIDocument uiDoc)
        {
            var toposolids = 0;
            var floors = 0;
            if (uiDoc?.Document != null)
            {
                foreach (var id in uiDoc.Selection.GetElementIds())
                {
                    var element = uiDoc.Document.GetElement(id);
                    if (element is Toposolid) { toposolids++; }
                    else if (element is Floor) { floors++; }
                }
            }

            return (toposolids >= 1 && floors >= 1 ? "✔" : "⚠") + $" 選取狀態：地形 ×{toposolids}、樓板 ×{floors}";
        }
```

- [ ] **Step 3: `Application.cs:59-74` 面板改四鈕**

```csharp
#if REVIT2024_OR_GREATER
                // 整地圖說面板（Revit 2024+）：三鈕任務流＋成套圖（規格 2026-07-06 案乙）。
                RibbonPanel gradingPanel = application.CreateRibbonPanel("整地圖說");
                AddGradingButton(gradingPanel, assemblyPath, "NewGradingScheme", "新整地\n方案…",
                    "RevitMCP.Commands.NewGradingSchemeCommand",
                    "選取一片地形＋控制樓板後，選模式（投影直貼/外延漸變/放坡銜接）與參數，執行並記錄一筆方案");
                AddGradingButton(gradingPanel, assemblyPath, "SchemeOverview", "方案\n總覽",
                    "RevitMCP.Commands.SchemeOverviewCommand",
                    "檢視登記簿全部方案（數字/視圖/還原/刪除/匯出）");
                AddGradingButton(gradingPanel, assemblyPath, "GradingComparison", "比較\n出圖",
                    "RevitMCP.Commands.GradingComparisonCommand",
                    "把全部方案排成 A0 單張總比較圖紙（3D/坡度/剖面/剖面立體/明細表）並匯 PDF");
                AddGradingButton(gradingPanel, assemblyPath, "GradingPresentation", "方案\n成套圖",
                    "RevitMCP.Commands.GradingPresentationCommand",
                    "對登記簿方案出一案一張成套圖說（方案定案後用）");
#endif
```

（`SchemeOverviewCommand` 於 Task 9 實作；本任務先以最小殼——開 `TaskDialog.Show("方案總覽", "Task 9 實作中")`——讓建置通過，Task 9 替換。規格提到 SplitButton 收納成套圖，實測四鈕面板寬度可接受，取捨為四顆平鈕、於 spec 開放項允許範圍內。）

- [ ] **Step 4: 建置**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error（WPF 參照：csproj 已含 `UseWPF` 或既有 `LogViewerWindow` 可編譯即證明毋須改 csproj）

- [ ] **Step 5: 實機檢核點（需 Revit＋使用者）**

重佈署重啟 → ribbon 出現四鈕 → 選地形＋兩板 →「新整地方案」做一個「外延 7m」（驗自由輸入）→ 名稱自動組「外延 7m」→ 執行成功且 `list_grading_schemes` 有記錄。

- [ ] **Step 6: Commit**

```bash
git add MCP/Commands/NewGradingSchemeDialog.cs MCP/Commands/GradingRibbonCommands.cs MCP/Application.cs
git commit -m "功能：新整地方案對話框（中英對照＋可編輯下拉＋自動命名）與整地面板四鈕"
```

---

### Task 9: 方案總覽視窗（非模態）

**Files:**
- Create: `MCP/Commands/SchemeOverviewWindow.cs`
- Modify: `MCP/Commands/GradingRibbonCommands.cs`（把 Task 8 的 `SchemeOverviewCommand` 殼換成開窗）

**Interfaces:**
- Consumes: `ExternalEventManager.Instance.ExecuteCommand(Action<UIApplication>)`（MCP/Core/ExternalEventManager.cs:42）、`CommandExecutor.ExecuteCommand(RevitCommandRequest)`、命令 `list_grading_schemes`／`restore_grading_scheme`／`remove_grading_scheme`／`export_grading_comparison`／`create_grading_comparison_sheet`
- Produces: `class SchemeOverviewWindow : Window`（單例 `ShowOrActivate(UIApplication)`）

- [ ] **Step 1: 視窗實作**

```csharp
#if REVIT2024_OR_GREATER
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Core;
using RevitMCP.Models;

namespace RevitMCP.Commands
{
    /// <summary>方案總覽（非模態）：登記簿清單＋數字＋動作；Revit API 動作經 ExternalEventManager 排隊。</summary>
    public class SchemeOverviewWindow : Window
    {
        private static SchemeOverviewWindow _instance;
        private readonly ObservableCollection<SchemeRow> _rows = new ObservableCollection<SchemeRow>();
        private readonly ListView _list;

        public class SchemeRow
        {
            public string Name { get; set; }
            public string Mode { get; set; }
            public string Cut { get; set; }
            public string Fill { get; set; }
            public string Total { get; set; }
            public long DesignId { get; set; }
        }

        public static void ShowOrActivate(UIApplication uiApp)
        {
            if (_instance == null)
            {
                _instance = new SchemeOverviewWindow();
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }

            _instance.Reload();
        }

        private SchemeOverviewWindow()
        {
            Title = "整地方案總覽";
            Width = 620;
            Height = 380;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _list = new ListView { ItemsSource = _rows, Margin = new Thickness(8) };
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "方案", Width = 130, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            view.Columns.Add(new GridViewColumn { Header = "模式", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Mode") });
            view.Columns.Add(new GridViewColumn { Header = "挖方 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Cut") });
            view.Columns.Add(new GridViewColumn { Header = "填方 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Fill") });
            view.Columns.Add(new GridViewColumn { Header = "總量 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Total") });
            _list.View = view;
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(MakeButton("重新整理", (s, e) => Reload()));
            buttons.Children.Add(MakeButton("開啟方案視圖", (s, e) => OpenSchemeView()));
            buttons.Children.Add(MakeButton("還原方案", (s, e) => RunOnSelected("restore_grading_scheme", new JObject(), "還原方案")));
            buttons.Children.Add(MakeButton("刪除方案", (s, e) => DeleteSelected()));
            buttons.Children.Add(MakeButton("匯出 Excel", (s, e) => RunCommand("export_grading_comparison", new JObject(), "匯出 Excel")));
            buttons.Children.Add(MakeButton("比較出圖", (s, e) => RunCommand("create_grading_comparison_sheet", new JObject(), "比較出圖")));
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);
            Content = grid;
        }

        private static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += onClick;
            return button;
        }

        private void Reload()
        {
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var response = new CommandExecutor(app).ExecuteCommand(new RevitCommandRequest
                { CommandName = "list_grading_schemes", Parameters = new JObject(), RequestId = "ui-" + Guid.NewGuid().ToString("N") });
                var rows = new System.Collections.Generic.List<SchemeRow>();
                if (response.Success)
                {
                    var data = JObject.FromObject(response.Data);
                    foreach (var scheme in data["Schemes"] as JArray ?? new JArray())
                    {
                        var cut = scheme.Value<double?>("CutCubicMeters") ?? 0;
                        var fill = scheme.Value<double?>("FillCubicMeters") ?? 0;
                        rows.Add(new SchemeRow
                        {
                            Name = scheme.Value<string>("SchemeName"),
                            Mode = scheme.Value<string>("Mode"),
                            Cut = cut.ToString("0"),
                            Fill = fill.ToString("0"),
                            Total = (cut + fill).ToString("0"),
                            DesignId = scheme.Value<long?>("DesignToposolidId") ?? 0
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _rows.Clear();
                    foreach (var row in rows) { _rows.Add(row); }
                });
            });
        }

        private SchemeRow Selected => _list.SelectedItem as SchemeRow;

        private void OpenSchemeView()
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var json = RevitMCP.Core.Grading.RevitToposolidGradingAdapter.TryReadSchemeRecord(doc, row.DesignId);
                var viewId = json == null ? 0 : JObject.Parse(json).Value<long?>("SchemeViewId") ?? 0;
                if (viewId > 0 && doc.GetElement(new ElementId(checked((int)viewId))) is View view)
                {
                    app.ActiveUIDocument.RequestViewChange(view);
                }
            });
        }

        private void DeleteSelected()
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
            if (MessageBox.Show($"確定刪除方案「{row.Name}」？（設計地形與方案視圖都會刪除）",
                Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
            RunOnSelected("remove_grading_scheme", new JObject { ["confirm"] = true }, "刪除方案");
        }

        private void RunOnSelected(string commandName, JObject parameters, string title)
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
            parameters["designToposolidId"] = row.DesignId;
            RunCommand(commandName, parameters, title);
        }

        private void RunCommand(string commandName, JObject parameters, string title)
        {
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var response = new CommandExecutor(app).ExecuteCommand(new RevitCommandRequest
                { CommandName = commandName, Parameters = parameters, RequestId = "ui-" + Guid.NewGuid().ToString("N") });
                Dispatcher.Invoke(() =>
                {
                    if (!response.Success) { MessageBox.Show($"{title}失敗：{response.Error}", Title); }
                    Reload();
                });
            });
        }
    }
}
#endif
```

- [ ] **Step 2: 換掉 Task 8 的殼**

```csharp
    /// <summary>②方案總覽：非模態清單視窗（進度儀表板）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class SchemeOverviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SchemeOverviewWindow.ShowOrActivate(commandData.Application);
            return Result.Succeeded;
        }
    }
```

⚠️ `list_grading_schemes` 回傳欄位名（`Schemes[].SchemeName/Mode/CutCubicMeters/...`）以 `MCP/Core/Commands/CommandExecutor.GradingScheme.cs:12` 實作為準，動手時開檔核對。

- [ ] **Step 3: 建置＋全套測試**

Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj` → 0 error；`dotnet test MCP.Tests\RevitMCP.Tests.csproj` → 全綠

- [ ] **Step 4: 實機檢核點（需 Revit＋使用者）＝三鈕流全程驗收**

新整地方案 ×3（直貼／外延 5m／放坡 1:2）→ 總覽數字對 `list_grading_schemes` → 開視圖跳轉 → 比較出圖 GRD-C01 → PDF。全程不經 MCP 客戶端、純 ribbon 操作。

- [ ] **Step 5: Commit**

```bash
git add MCP/Commands/SchemeOverviewWindow.cs MCP/Commands/GradingRibbonCommands.cs
git commit -m "功能：方案總覽非模態視窗（清單/開視圖/還原/刪除/匯出/比較出圖）"
```

---

### Task 10: MCP 工具包裝＋文件/QA 收尾

**Files:**
- Modify: `MCP-Server/src/tools/grading-tools.ts`（`create_grading_presentation` 定義之後加兩個工具定義）
- Modify: `MCP-Server/src/tools/grading-tools.test.ts`（工具數與名稱斷言 +2）
- Modify: `log/2026-07.md`（append 完成紀錄）

**Interfaces:**
- Consumes: Task 6/7 的命令名與參數（`create_grading_comparison_sheet`：`designToposolidIds?/titleBlockId?/export?/outputPath?`；`remove_grading_scheme`：`designToposolidId`＋`confirm`）
- Produces: MCP 工具 `create_grading_comparison_sheet`、`remove_grading_scheme`

- [ ] **Step 1: 先改測試（期望工具數 +2 與兩個新名稱）再跑確認失敗**

打開 `grading-tools.test.ts`，找到工具數斷言（依現檔數字 +2）與名稱清單，加入兩個新工具名。
Run: `cd MCP-Server && npm test` → Expected: FAIL（工具數不符）

- [ ] **Step 2: 在 `grading-tools.ts` 的工具陣列（`create_grading_presentation` 條目後）加**

```typescript
    {
        name: "create_grading_comparison_sheet",
        description:
            "整地方案總比較圖紙（C-00c）：一欄一方案（3D/坡度熱區/正剖面/剖面立體），底部方案比較明細表（挖方/填方/淨土方/挖填總量/最大挖深填高/擾動面積/鬆實方三本帳），A0 圖框單張輸出可匯 PDF。逐方案容錯。",
        inputSchema: {
            type: "object",
            properties: {
                designToposolidIds: {
                    type: "array",
                    items: { type: "integer" },
                    description: "只比較這些方案的設計地形 ID；未提供時取登記簿全部方案",
                },
                titleBlockId: {
                    type: "integer",
                    description: "A0 圖框類型 ID；未提供時自動找名稱含 A0 的圖框",
                },
                export: {
                    type: "boolean",
                    default: true,
                    description: "是否於出圖後匯出 PDF",
                },
                outputPath: {
                    type: "string",
                    description: "PDF 輸出檔完整路徑；未提供時預設專案目錄+時間戳檔名",
                },
            },
        },
    },
    {
        name: "remove_grading_scheme",
        description:
            "刪除整地方案（破壞性，須 confirm=true）：刪設計地形與方案相關視圖、移除登記簿記錄；登記簿清空時解除原地形的拆除階段設定。",
        inputSchema: {
            type: "object",
            properties: {
                designToposolidId: {
                    type: "integer",
                    description: "要刪除方案的設計地形元素 ID",
                },
                confirm: {
                    type: "boolean",
                    description: "破壞性操作確認，必須為 true",
                },
            },
            required: ["designToposolidId", "confirm"],
        },
    },
```

（若該檔另有「工具轉發到 Revit 命令」的對映清單／型別，依同檔既有條目模式一併補上兩筆。）

- [ ] **Step 3: 建置與測試**

Run: `cd MCP-Server && npm run build && npm test` → 全綠
Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj` → 全綠
Run: `dotnet build -c Release.R24 MCP\RevitMCP.csproj && dotnet build -c Release.R23 MCP\RevitMCP.csproj` → 皆 0 error

- [ ] **Step 4: QA/QC 與文件**

Run: `.\scripts\verify-qaqc.ps1 -SkipBuild -SkipDeploy`
Expected: 通過（既有債除外——domain-diagram skill 計數、BIM_MCP 索引卡、worktree 複本 stale refs）。
工具計數：兩個新工具走 grading-tools.ts 外層包裝，`index.ts` 內層計數 96 不變 → CLAUDE.md/README 計數表**不需**變更；若 QA 報數字不符則以 QA 為準修文件。
`log/2026-07.md` append 一筆 feature 紀錄（格式照檔內既有條目）。

- [ ] **Step 5: Commit**

```bash
git add MCP-Server/src/tools/grading-tools.ts MCP-Server/src/tools/grading-tools.test.ts log/2026-07.md
git commit -m "功能：比較出圖與刪除方案 MCP 工具包裝＋QA/日誌收尾"
```

---

## 收尾（計畫外但必辦）

1. 佈署 R24（手動複製流程，`install-addon.ps1` 有既有 bug）＋使用者重啟 Revit。
2. 總驗三鈕流（Task 9 Step 4）由使用者實機操作驗收。
3. Ribbon 圖示（Codex 四風格提案）等使用者挑選後另一輪套用（`PushButtonData.LargeImage`/`Image`），不在本計畫內。
4. W3-1 挖填雙坡率：本計畫完成後的下一個獨立 TDD 循環。
