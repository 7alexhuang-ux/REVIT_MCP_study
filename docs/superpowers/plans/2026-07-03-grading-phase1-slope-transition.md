# 整地 Phase 1：警告自動消化與放坡銜接框架 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 為 `grade_toposolid_to_floors` 加入 `IFailuresPreprocessor` 警告自動消化（消除方案三 127 秒模態阻塞），並以共用的邊界銜接框架實作模式 2（`offset_transition`）與模式 3（`slope_transition`），讓整地土方數字達到可決策精度。

**Architecture:** 純幾何邏輯（坡度解析、銜接目標高程、外圈方向）放在無 Revit 相依的 `TransitionGeometry.cs` 與 `Polygon2D.cs` 擴充（NUnit 可測）；Revit 相依部分（外圈取樣、摺線、包絡驗收、FailuresPreprocessor）放在 `RevitToposolidGradingAdapter.cs` 與新檔 `GradingFailuresPreprocessor.cs`（以建置驗證）。銜接帶採**距離場**設計：任一點的目標高程由「該點到樓板邊界的距離」決定，不做多邊形外偏移，天然處理凹角。

**Tech Stack:** C# (.NET Framework 4.8 / .NET 8, Nice3point Revit SDK)、NUnit 3.14、TypeScript (MCP SDK)、node:test。

## Global Constraints（自 CLAUDE.md 與規格複製）

- 錯誤訊息一律繁體中文；MCP 工具名稱 snake_case。
- Revit 模型變更必須在 `Transaction` 內且可回滾；本功能沿用既有 `TransactionGroup` 兩交易結構。
- 單一 `MCP/RevitMCP.csproj`；禁止新增版本專屬 csproj/addin；建置組態 `Release.R22`–`Release.R26`，整地程式碼包在 `#if REVIT2024_OR_GREATER`。
- footprint 內 2 mm 驗收閘門不變；「接受的案子必須給出可驗證的正確數字」。
- 單次執行效能目標數秒內；新增階段一律納入 `GradingTimeline` 分段計時。
- 測試先行（先寫失敗測試再實作）；NUnit 測試檔在 `MCP.Tests/Grading/`，純邏輯檔須加入 `RevitMCP.Tests.csproj` 的 `<Compile Include>`。
- 模式參數單位：`offsetDistance`、`maxExtension` 為**公尺**；`slopeRatio` 為字串 `1:n`（垂直:水平，如 `1:12`）。`maxExtension` 未提供時預設 20 公尺。
- `boundaryEdges`（只對指定邊界套用銜接）**不在 Phase 1 範圍**（YAGNI，規格列為可選草案）。
- 銜接帶驗收採 500 mm 粗閘門（抓「跨越樓板上空／漏摺線」級錯誤）；帶內為近似銜接面，非 2 mm 工程保證，但土方量以實際網格計算故仍精確——此定位須回寫規格。

---

### Task 1: GradingRequest 模式擴充與 TransitionSettings（純邏輯，TDD）

**Files:**
- Modify: `MCP/Core/Grading/GradingModels.cs`
- Test: `MCP.Tests/Grading/GradingRequestTests.cs`（改既有 + 新增）
- Test: `MCP.Tests/Grading/TransitionSettingsTests.cs`（新檔）

**Interfaces:**
- Consumes: 無（純模型層）。
- Produces:
  - `enum GradingMode { FootprintOnly, OffsetTransition, SlopeTransition }`
  - `GradingRequest` 新屬性：`double? OffsetDistanceMeters`、`string SlopeRatio`、`double? MaxExtensionMeters`
  - `TransitionSettings.FromRequest(GradingRequest) : TransitionSettings`（屬性：`GradingMode Mode`、`double OffsetDistanceMeters`、`double RunPerRise`、`double MaxExtensionMeters`；常數 `DefaultMaxExtensionMeters = 20.0`）
  - `TransitionSettings.ParseSlopeRatio(string) : double`（`"1:12"` → `12`）

- [ ] **Step 1: 寫失敗測試**

`MCP.Tests/Grading/TransitionSettingsTests.cs`（新檔全文）：

```csharp
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class TransitionSettingsTests
    {
        private static GradingRequest BaseRequest(string mode) => new GradingRequest
        {
            ToposolidId = 6278563,
            FloorIds = new[] { 7512796L },
            Mode = mode,
            TargetFace = "bottom"
        };

        [Test]
        public void FromRequest_footprint_only_不帶銜接參數()
        {
            var settings = TransitionSettings.FromRequest(BaseRequest("footprint_only"));
            Assert.AreEqual(GradingMode.FootprintOnly, settings.Mode);
        }

        [Test]
        public void FromRequest_footprint_only_夾帶銜接參數_拒絕()
        {
            var request = BaseRequest("footprint_only");
            request.OffsetDistanceMeters = 3.0;
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(request));
            StringAssert.Contains("offsetDistance", error.Message);
        }

        [Test]
        public void FromRequest_offset_transition_必須提供正的offset()
        {
            var request = BaseRequest("offset_transition");
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(request));
            StringAssert.Contains("offsetDistance", error.Message);

            request.OffsetDistanceMeters = 3.0;
            var settings = TransitionSettings.FromRequest(request);
            Assert.AreEqual(GradingMode.OffsetTransition, settings.Mode);
            Assert.AreEqual(3.0, settings.OffsetDistanceMeters, 1e-12);
        }

        [Test]
        public void FromRequest_slope_transition_解析坡度與預設上限()
        {
            var request = BaseRequest("slope_transition");
            request.SlopeRatio = "1:12";
            var settings = TransitionSettings.FromRequest(request);
            Assert.AreEqual(GradingMode.SlopeTransition, settings.Mode);
            Assert.AreEqual(12.0, settings.RunPerRise, 1e-12);
            Assert.AreEqual(TransitionSettings.DefaultMaxExtensionMeters, settings.MaxExtensionMeters, 1e-12);
        }

        [Test]
        public void FromRequest_slope_transition_缺坡度_拒絕()
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(BaseRequest("slope_transition")));
            StringAssert.Contains("slopeRatio", error.Message);
        }

        [Test]
        public void FromRequest_未知模式_拒絕()
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(BaseRequest("banana")));
            StringAssert.Contains("mode", error.Message);
        }

        [TestCase("1:12", 12.0)]
        [TestCase("1:1.5", 1.5)]
        [TestCase(" 1 : 8 ", 8.0)]
        public void ParseSlopeRatio_合法格式(string text, double expected)
        {
            Assert.AreEqual(expected, TransitionSettings.ParseSlopeRatio(text), 1e-12);
        }

        [TestCase("12:1")]
        [TestCase("1:0")]
        [TestCase("1:-3")]
        [TestCase("abc")]
        [TestCase("")]
        public void ParseSlopeRatio_非法格式_繁體中文錯誤(string text)
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.ParseSlopeRatio(text));
            StringAssert.Contains("1:n", error.Message);
        }
    }
}
```

`MCP.Tests/Grading/GradingRequestTests.cs`：把既有 `Validate_非本次模式_拒絕執行` 改名重寫（`slope_transition` 已是合法模式，改測「缺參數被擋」與「未知模式被擋」）：

```csharp
        [Test]
        public void Validate_slope_transition_缺坡度_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "slope_transition",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("slopeRatio", error.Message);
        }

        [Test]
        public void Validate_未知模式_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "banana",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("mode", error.Message);
        }

        [Test]
        public void Validate_offset_transition_合法參數_不拋出例外()
        {
            Assert.DoesNotThrow(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "offset_transition",
                TargetFace = "bottom",
                OffsetDistanceMeters = 3.0
            }.Validate());
        }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 編譯錯誤（`GradingMode`、`TransitionSettings`、`OffsetDistanceMeters` 不存在）。

- [ ] **Step 3: 實作**

`MCP/Core/Grading/GradingModels.cs`：`GradingRequest` 加三個屬性並改寫 `Validate()`；檔尾加 `GradingMode` 與 `TransitionSettings`。

```csharp
    public enum GradingMode
    {
        FootprintOnly,
        OffsetTransition,
        SlopeTransition
    }

    /// <summary>整地邊界銜接設定；由 GradingRequest 驗證並轉出。</summary>
    public sealed class TransitionSettings
    {
        public const double DefaultMaxExtensionMeters = 20.0;

        private TransitionSettings() { }

        public GradingMode Mode { get; private set; }
        public double OffsetDistanceMeters { get; private set; }
        public double RunPerRise { get; private set; }
        public double MaxExtensionMeters { get; private set; }

        public static TransitionSettings FromRequest(GradingRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var mode = ParseMode(request.Mode);
            switch (mode)
            {
                case GradingMode.FootprintOnly:
                    RejectParameter(request.OffsetDistanceMeters.HasValue, "offsetDistance", "footprint_only");
                    RejectParameter(!string.IsNullOrWhiteSpace(request.SlopeRatio), "slopeRatio", "footprint_only");
                    RejectParameter(request.MaxExtensionMeters.HasValue, "maxExtension", "footprint_only");
                    return new TransitionSettings { Mode = mode };
                case GradingMode.OffsetTransition:
                    if (!(request.OffsetDistanceMeters > 0))
                        throw new ArgumentException("offset_transition 模式必須提供大於 0 的 offsetDistance（公尺）。");
                    RejectParameter(!string.IsNullOrWhiteSpace(request.SlopeRatio), "slopeRatio", "offset_transition");
                    RejectParameter(request.MaxExtensionMeters.HasValue, "maxExtension", "offset_transition");
                    return new TransitionSettings
                    {
                        Mode = mode,
                        OffsetDistanceMeters = request.OffsetDistanceMeters.Value
                    };
                case GradingMode.SlopeTransition:
                    RejectParameter(request.OffsetDistanceMeters.HasValue, "offsetDistance", "slope_transition");
                    var runPerRise = ParseSlopeRatio(request.SlopeRatio);
                    var maxExtension = request.MaxExtensionMeters ?? DefaultMaxExtensionMeters;
                    if (!(maxExtension > 0))
                        throw new ArgumentException("maxExtension 必須大於 0（公尺）。");
                    return new TransitionSettings
                    {
                        Mode = mode,
                        RunPerRise = runPerRise,
                        MaxExtensionMeters = maxExtension
                    };
                default:
                    throw new ArgumentException($"不支援的整地 mode：{request.Mode}。");
            }
        }

        public static double ParseSlopeRatio(string slopeRatio)
        {
            if (string.IsNullOrWhiteSpace(slopeRatio))
                throw new ArgumentException("slope_transition 模式必須提供 slopeRatio，格式 1:n（例如 1:12）。");
            var parts = slopeRatio.Split(':');
            if (parts.Length == 2
                && parts[0].Trim() == "1"
                && double.TryParse(
                    parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var run)
                && run > 0)
            {
                return run;
            }

            throw new ArgumentException("slopeRatio 格式必須為 1:n 且 n 大於 0（例如 1:12）。");
        }

        private static GradingMode ParseMode(string mode)
        {
            if (string.Equals(mode, "footprint_only", StringComparison.OrdinalIgnoreCase))
                return GradingMode.FootprintOnly;
            if (string.Equals(mode, "offset_transition", StringComparison.OrdinalIgnoreCase))
                return GradingMode.OffsetTransition;
            if (string.Equals(mode, "slope_transition", StringComparison.OrdinalIgnoreCase))
                return GradingMode.SlopeTransition;
            throw new ArgumentException(
                $"不支援的整地 mode：{mode}；可用值為 footprint_only、offset_transition、slope_transition。");
        }

        private static void RejectParameter(bool provided, string parameterName, string mode)
        {
            if (provided)
                throw new ArgumentException($"{mode} 模式不接受 {parameterName} 參數。");
        }
    }
```

`GradingRequest` 修改（保留既有 ID/TargetFace/UpdateExisting 檢查，把模式檢查改為委派）：

```csharp
        public double? OffsetDistanceMeters { get; set; }
        public string SlopeRatio { get; set; }
        public double? MaxExtensionMeters { get; set; }

        public void Validate()
        {
            if (ToposolidId <= 0) throw new ArgumentException("地形 ID 必須大於 0。");
            if (FloorIds == null || FloorIds.Count == 0) throw new ArgumentException("至少一片樓板才能執行整地。");
            if (FloorIds.Any(id => id <= 0)) throw new ArgumentException("樓板 ID 必須大於 0。");
            if (!string.Equals(TargetFace, "bottom", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("目前僅支援樓板底面 bottom。");
            if (UpdateExisting)
                throw new ArgumentException("目前尚未支援 updateExisting=true。");
            TransitionSettings.FromRequest(this);
        }
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 全數 PASS（含既有 Polygon2D/Timeline 測試）。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Grading/GradingModels.cs MCP.Tests/Grading/GradingRequestTests.cs MCP.Tests/Grading/TransitionSettingsTests.cs
git commit -m "功能：整地模式擴充與 TransitionSettings 驗證（offset/slope 參數）"
```

---

### Task 2: TransitionGeometry 純幾何函式（TDD）

**Files:**
- Create: `MCP/Core/Grading/TransitionGeometry.cs`
- Modify: `MCP.Tests/RevitMCP.Tests.csproj`（加 Compile Include）
- Test: `MCP.Tests/Grading/TransitionGeometryTests.cs`（新檔）

**Interfaces:**
- Consumes: 無。
- Produces（全部 `public static`，單位皆為 Revit 內部單位英呎，呼叫端自行轉換）：
  - `double OffsetTargetZ(double boundaryBottomZ, double localTerrainZ, double distanceFromBoundary, double offsetDistance)`
  - `double SlopeTargetZ(double boundaryBottomZ, double localTerrainZ, double distanceFromBoundary, double runPerRise)`
  - `double SlopeExtensionNeeded(double boundaryBottomZ, double terrainZ, double runPerRise)`

- [ ] **Step 1: 寫失敗測試**

`MCP.Tests/Grading/TransitionGeometryTests.cs`（新檔全文）：

```csharp
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class TransitionGeometryTests
    {
        [Test]
        public void OffsetTargetZ_帶內線性漸變()
        {
            // 板底 10、地形 16、offset 帶寬 3：帶中點目標 = 13。
            Assert.AreEqual(13.0, TransitionGeometry.OffsetTargetZ(10, 16, 1.5, 3.0), 1e-12);
            Assert.AreEqual(10.0, TransitionGeometry.OffsetTargetZ(10, 16, 0.0, 3.0), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.OffsetTargetZ(10, 16, 3.0, 3.0), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.OffsetTargetZ(10, 16, 99.0, 3.0), 1e-12);
        }

        [Test]
        public void OffsetTargetZ_地形低於板底_向下漸變()
        {
            Assert.AreEqual(8.0, TransitionGeometry.OffsetTargetZ(10, 6, 1.5, 3.0), 1e-12);
        }

        [Test]
        public void SlopeTargetZ_固定坡度爬升並貼合地形()
        {
            // 板底 10、地形 16、坡 1:12：距離 24 處 = 10 + 24/12 = 12；距離 100 處已達地形 16。
            Assert.AreEqual(12.0, TransitionGeometry.SlopeTargetZ(10, 16, 24, 12), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.SlopeTargetZ(10, 16, 100, 12), 1e-12);
        }

        [Test]
        public void SlopeTargetZ_地形低於板底_下坡並貼合()
        {
            Assert.AreEqual(8.0, TransitionGeometry.SlopeTargetZ(10, 6, 24, 12), 1e-12);
            Assert.AreEqual(6.0, TransitionGeometry.SlopeTargetZ(10, 6, 100, 12), 1e-12);
        }

        [Test]
        public void SlopeExtensionNeeded_由高差與坡度求水平距離()
        {
            Assert.AreEqual(72.0, TransitionGeometry.SlopeExtensionNeeded(10, 16, 12), 1e-12);
            Assert.AreEqual(48.0, TransitionGeometry.SlopeExtensionNeeded(10, 6, 12), 1e-12);
            Assert.AreEqual(0.0, TransitionGeometry.SlopeExtensionNeeded(10, 10, 12), 1e-12);
        }

        [Test]
        public void 非法參數_繁體中文錯誤()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.OffsetTargetZ(10, 16, 1, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.SlopeTargetZ(10, 16, 1, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.SlopeExtensionNeeded(10, 16, -1));
        }
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 編譯錯誤（`TransitionGeometry` 不存在）。

- [ ] **Step 3: 實作**

`MCP/Core/Grading/TransitionGeometry.cs`（新檔全文）：

```csharp
using System;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 邊界銜接帶的純幾何：距離場目標高程計算。
    /// 所有長度與高程使用同一單位（Revit 內部英呎），由呼叫端負責轉換。
    /// </summary>
    public static class TransitionGeometry
    {
        /// <summary>模式 2：offset 帶內由板底高程線性漸變回該點地形高程。</summary>
        public static double OffsetTargetZ(
            double boundaryBottomZ,
            double localTerrainZ,
            double distanceFromBoundary,
            double offsetDistance)
        {
            if (offsetDistance <= 0)
                throw new ArgumentOutOfRangeException(nameof(offsetDistance), "offset 帶寬必須大於 0。");
            var t = distanceFromBoundary / offsetDistance;
            if (t <= 0) return boundaryBottomZ;
            if (t >= 1) return localTerrainZ;
            return boundaryBottomZ + (t * (localTerrainZ - boundaryBottomZ));
        }

        /// <summary>模式 3：以 1:n 固定坡度由板底向地形靠攏，到達該點地形高程即貼合不再變化。</summary>
        public static double SlopeTargetZ(
            double boundaryBottomZ,
            double localTerrainZ,
            double distanceFromBoundary,
            double runPerRise)
        {
            if (runPerRise <= 0)
                throw new ArgumentOutOfRangeException(nameof(runPerRise), "坡度分母 n 必須大於 0。");
            if (distanceFromBoundary <= 0) return boundaryBottomZ;
            var delta = distanceFromBoundary / runPerRise;
            return localTerrainZ >= boundaryBottomZ
                ? Math.Min(boundaryBottomZ + delta, localTerrainZ)
                : Math.Max(boundaryBottomZ - delta, localTerrainZ);
        }

        /// <summary>模式 3：從板底放坡到指定地形高程所需的水平距離。</summary>
        public static double SlopeExtensionNeeded(
            double boundaryBottomZ,
            double terrainZ,
            double runPerRise)
        {
            if (runPerRise <= 0)
                throw new ArgumentOutOfRangeException(nameof(runPerRise), "坡度分母 n 必須大於 0。");
            return Math.Abs(terrainZ - boundaryBottomZ) * runPerRise;
        }
    }
}
```

`MCP.Tests/RevitMCP.Tests.csproj` 的 `<ItemGroup>` 加：

```xml
    <Compile Include="..\MCP\Core\Grading\TransitionGeometry.cs" Link="Core\Grading\TransitionGeometry.cs" />
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 全數 PASS。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Grading/TransitionGeometry.cs MCP.Tests/Grading/TransitionGeometryTests.cs MCP.Tests/RevitMCP.Tests.csproj
git commit -m "功能：銜接帶距離場純幾何（offset 漸變／固定坡度目標高程）"
```

---

### Task 3: Polygon2D 擴充——DistanceToBoundary 公開化、NearestBoundaryPoint、OutwardDirections（TDD）

**Files:**
- Modify: `MCP/Core/Grading/Polygon2D.cs`
- Modify: `MCP/Core/Grading/RevitToposolidGradingAdapter.cs`（刪私有 `DistanceToBoundary`，改呼叫 `Polygon2D.DistanceToBoundary`）
- Test: `MCP.Tests/Grading/Polygon2DTests.cs`（新增測試）

**Interfaces:**
- Consumes: 既有 `Point2D`。
- Produces（`Polygon2D` 新增 `public static`）：
  - `double DistanceToBoundary(IReadOnlyList<Point2D> polygon, Point2D point)`
  - `(Point2D Point, double Distance) NearestBoundaryPoint(IReadOnlyList<Point2D> polygon, Point2D point)`
  - `IReadOnlyList<Point2D> OutwardDirections(IReadOnlyList<Point2D> polygon)`（每頂點單位外向量：相鄰兩邊外法線的角平分向量；退化時退回其中一邊外法線）

- [ ] **Step 1: 寫失敗測試**

`MCP.Tests/Grading/Polygon2DTests.cs` 新增：

```csharp
        private static readonly Point2D[] UnitSquare =
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
        };

        [Test]
        public void DistanceToBoundary_內外點皆回傳到邊界最短距離()
        {
            Assert.AreEqual(3.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(5, 3)), 1e-9);
            Assert.AreEqual(2.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(12, 5)), 1e-9);
            Assert.AreEqual(5.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(13, 14)), 1e-9);
        }

        [Test]
        public void NearestBoundaryPoint_回傳邊界上最近點()
        {
            var (point, distance) = Polygon2D.NearestBoundaryPoint(UnitSquare, new Point2D(12, 5));
            Assert.AreEqual(10.0, point.X, 1e-9);
            Assert.AreEqual(5.0, point.Y, 1e-9);
            Assert.AreEqual(2.0, distance, 1e-9);
        }

        [Test]
        public void OutwardDirections_正方形四角指向對角外側()
        {
            var directions = Polygon2D.OutwardDirections(UnitSquare);
            Assert.AreEqual(4, directions.Count);
            // 頂點 (0,0)：相鄰邊外法線 (0,-1) 與 (-1,0)，角平分 = (-√2/2, -√2/2)。
            Assert.AreEqual(-System.Math.Sqrt(2) / 2, directions[0].X, 1e-9);
            Assert.AreEqual(-System.Math.Sqrt(2) / 2, directions[0].Y, 1e-9);
            // 頂點 (10,10)：角平分 = (+√2/2, +√2/2)。
            Assert.AreEqual(System.Math.Sqrt(2) / 2, directions[2].X, 1e-9);
            Assert.AreEqual(System.Math.Sqrt(2) / 2, directions[2].Y, 1e-9);
        }

        [Test]
        public void OutwardDirections_順時針多邊形結果一致()
        {
            var clockwise = new[]
            {
                new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 0), new Point2D(0, 0)
            };
            var directions = Polygon2D.OutwardDirections(clockwise);
            // clockwise[3] = (0,0)，外向仍應指向 (-,-)。
            Assert.Less(directions[3].X, 0);
            Assert.Less(directions[3].Y, 0);
        }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 編譯錯誤（新方法不存在）。

- [ ] **Step 3: 實作**

`Polygon2D.cs` 新增（放在 `Contains` 之後；重用既有私有 `Cross`/`DistanceSquared`）：

```csharp
        public static double DistanceToBoundary(IReadOnlyList<Point2D> polygon, Point2D point)
        {
            return NearestBoundaryPoint(polygon, point).Distance;
        }

        public static (Point2D Point, double Distance) NearestBoundaryPoint(
            IReadOnlyList<Point2D> polygon,
            Point2D point)
        {
            if (polygon == null || polygon.Count < 2)
            {
                throw new ArgumentException("多邊形至少需要兩個頂點。", nameof(polygon));
            }

            var best = polygon[0];
            var bestDistanceSquared = double.MaxValue;
            for (var index = 0; index < polygon.Count; index++)
            {
                var start = polygon[index];
                var end = polygon[(index + 1) % polygon.Count];
                var edgeX = end.X - start.X;
                var edgeY = end.Y - start.Y;
                var lengthSquared = (edgeX * edgeX) + (edgeY * edgeY);
                double t = 0;
                if (lengthSquared > 0)
                {
                    t = (((point.X - start.X) * edgeX) + ((point.Y - start.Y) * edgeY)) / lengthSquared;
                    t = Math.Max(0, Math.Min(1, t));
                }

                var candidate = new Point2D(start.X + (t * edgeX), start.Y + (t * edgeY));
                var distanceSquared = DistanceSquared(candidate, point);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = candidate;
                }
            }

            return (best, Math.Sqrt(bestDistanceSquared));
        }

        public static IReadOnlyList<Point2D> OutwardDirections(IReadOnlyList<Point2D> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            // CCW（SignedArea>0）時邊 (dx,dy) 的外法線為 (dy,-dx)；CW 則相反。
            var orientation = SignedArea(polygon) >= 0 ? 1.0 : -1.0;
            var directions = new List<Point2D>(polygon.Count);
            for (var index = 0; index < polygon.Count; index++)
            {
                var previous = polygon[(index - 1 + polygon.Count) % polygon.Count];
                var current = polygon[index];
                var next = polygon[(index + 1) % polygon.Count];
                var incoming = EdgeOutwardNormal(previous, current, orientation);
                var outgoing = EdgeOutwardNormal(current, next, orientation);
                var sumX = incoming.X + outgoing.X;
                var sumY = incoming.Y + outgoing.Y;
                var length = Math.Sqrt((sumX * sumX) + (sumY * sumY));
                directions.Add(length > 1e-12
                    ? new Point2D(sumX / length, sumY / length)
                    : outgoing);
            }

            return directions;
        }

        private static Point2D EdgeOutwardNormal(Point2D start, Point2D end, double orientation)
        {
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var length = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
            if (length <= 1e-12)
            {
                return new Point2D(0, 0);
            }

            return new Point2D(orientation * edgeY / length, orientation * -edgeX / length);
        }
```

`RevitToposolidGradingAdapter.cs`：刪除私有 `DistanceToBoundary`（第 781–808 行），`VerifySurfaceAgainstFootprints` 內呼叫處改為 `Polygon2D.DistanceToBoundary(loop, sample)`。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal`
Expected: 全數 PASS。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Grading/Polygon2D.cs MCP/Core/Grading/RevitToposolidGradingAdapter.cs MCP.Tests/Grading/Polygon2DTests.cs
git commit -m "功能：Polygon2D 邊界距離場與外向方向（銜接帶基礎）"
```

---

### Task 4: GradingFailuresPreprocessor 警告自動消化（Revit 相依，建置驗證）

**Files:**
- Create: `MCP/Core/Grading/GradingFailuresPreprocessor.cs`
- Modify: `MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs`（兩個交易掛載）

**Interfaces:**
- Consumes: Revit API `IFailuresPreprocessor`。
- Produces: `internal sealed class GradingFailuresPreprocessor : IFailuresPreprocessor`，屬性 `IReadOnlyList<string> DismissedWarnings`；靜態方法 `void Attach(Transaction transaction, GradingFailuresPreprocessor preprocessor)`。

- [ ] **Step 1: 實作**（Revit API 無法在 net48 測試專案單元測試；以 R24 建置＋實機驗證把關）

`MCP/Core/Grading/GradingFailuresPreprocessor.cs`（新檔全文）：

```csharp
#if REVIT2024_OR_GREATER
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 整地交易的失敗預處理：自動刪除可忽略的警告（如元素重疊），
    /// 避免模態對話框阻塞無人值守的 MCP 呼叫（方案三曾因此阻塞 126.9 秒）。
    /// 錯誤（Error 以上）不處理，交由既有回滾機制。
    /// </summary>
    internal sealed class GradingFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _dismissedWarnings = new List<string>();

        public IReadOnlyList<string> DismissedWarnings => _dismissedWarnings;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    _dismissedWarnings.Add(failure.GetDescriptionText());
                    failuresAccessor.DeleteWarning(failure);
                }
            }

            return FailureProcessingResult.Continue;
        }

        public static void Attach(Transaction transaction, GradingFailuresPreprocessor preprocessor)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }
    }
}
#endif
```

`CommandExecutor.ToposolidGrading.cs`：`GradeToposolidToFloors` 開頭（`request.Validate();` 之後）加：

```csharp
            var failuresPreprocessor = new GradingFailuresPreprocessor();
```

兩個交易在 `Start()` 之前掛載（`setupTransaction` 與 `gradingTransaction` 各一行）：

```csharp
                    using (var setupTransaction = new Transaction(doc, "建立整地設計副本"))
                    {
                        GradingFailuresPreprocessor.Attach(setupTransaction, failuresPreprocessor);
```

```csharp
                    using (var gradingTransaction = new Transaction(doc, "套用樓板投影並計算挖填方"))
                    {
                        GradingFailuresPreprocessor.Attach(gradingTransaction, failuresPreprocessor);
```

結果 `Warnings` 併入被消化的警告（在 `var result = new GradingResult` 前組字串）：

```csharp
            var warnings = new List<string>();
            foreach (var dismissed in failuresPreprocessor.DismissedWarnings.Distinct())
            {
                warnings.Add($"已自動略過 Revit 警告：{dismissed}");
            }
```

`GradingResult` 的 `Warnings = warnings`（取代 `new string[0]`；Task 6 會再併入放坡截止警告）。

- [ ] **Step 2: 建置驗證**

Run: `dotnet build -c Release.R24 MCP/RevitMCP.csproj --nologo -v minimal`
Expected: 0 錯誤。

- [ ] **Step 3: Commit**

```bash
git add MCP/Core/Grading/GradingFailuresPreprocessor.cs MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs
git commit -m "修正：整地交易掛 IFailuresPreprocessor 自動消化警告，杜絕模態阻塞"
```

---

### Task 5: Adapter 銜接帶框架（外圈取樣、摺線、頂點分類、包絡驗收）

**Files:**
- Modify: `MCP/Core/Grading/RevitToposolidGradingAdapter.cs`

**Interfaces:**
- Consumes: Task 1 `TransitionSettings`/`GradingMode`、Task 2 `TransitionGeometry`、Task 3 `Polygon2D.NearestBoundaryPoint`/`OutwardDirections`/`DistanceToBoundary`。
- Produces: 介面方法改名並擴充簽章——
  `int ApplyGrading(Document doc, Toposolid original, Toposolid design, IReadOnlyList<FloorFootprint> footprints, TransitionSettings settings, ICollection<string> warnings)`
  （取代 `ApplyFootprintOnly`；`original` 供包絡驗收讀取未修改地形；`warnings` 收集放坡截止警告）。

**設計要點（距離場銜接帶）：**

1. 頂點分類擴充：樓板投影內 → 板底 Z（既有）；投影外且距最近樓板邊界 d ≤ 帶寬 → 銜接 Z（新）。「最近樓板」＝`DistanceToBoundary` 最小者；`boundaryBottomZ` 用 `NearestBoundaryPoint` 求出的邊界點代入 `BottomElevationAt`；`localTerrainZ` 用該頂點修改前的 `Position.Z`（頂點收集在任何校準寫入之前，Z 即原地形）。目標與地形差 ≤ 2 mm 時不動（帶外或已貼合）。
2. 外圈播點：每個邊界頂點沿 `OutwardDirections` 外推——模式 2 推 `offsetDistance`；模式 3 以定點迭代（8 輪）求放坡與地形相交距離，上限 `maxExtension`，未收斂不報錯（最終目標一律由距離場函式算，圈點只是網格密度控制）。圈點落在任何樓板投影內、或該處無地形（規則 7）→ 跳過。加入點高程 = 該處現況地形 Z（與既有邊界播點一致，最終高程交給兩段式校準）。
3. 外圈摺線：連接相鄰圈點 `DrawSplitLine`（沿用既有 try-catch 跳過模式），讓 daylight 線成為網格硬邊。
4. 放坡截止：模式 3 圈點若在 `maxExtension` 處與地形殘差 > 2 mm，計數並記錄最大殘差，彙整為每樓板一則繁體中文警告（規格：「超過上限則回報警告」）。
5. 包絡驗收（新 timeline 階段「銜接帶包絡驗收」）：2 m 網格取樣銜接帶（樓板 bbox 各向外擴帶寬；排除投影內、距邊界 < 300 mm、距帶外緣 < 300 mm），原地形 Z 從 **original** Toposolid 實體射線取樣，設計面 Z 從修改後 design 實體取樣；設計面須落在 `[min(板底Z, 原地形Z) − 500 mm, max(板底Z, 原地形Z) + 500 mm]` 包絡內，違者回滾。500 mm 為粗閘門常數 `TransitionBandToleranceMillimeters`，抓「跨越樓板上空／漏摺線」級錯誤；帶內為近似銜接面，土方量以實際網格計算故仍精確。
6. `footprint_only` 走原路徑（跳過 2、3、5），行為不變。

- [ ] **Step 1: 介面與方法改名**

`IToposolidGradingAdapter` 介面第 18 行改為：

```csharp
        int ApplyGrading(
            Document doc,
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            ICollection<string> warnings);
```

- [ ] **Step 2: 實作 ApplyGrading**

`ApplyFootprintOnly` 更名為 `ApplyGrading`，簽章同上；方法開頭參數檢查加：

```csharp
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (warnings == null) throw new ArgumentNullException(nameof(warnings));
            if (original == null || !doc.Equals(original.Document))
                throw new ArgumentException("原始 Toposolid 必須屬於指定文件。", nameof(original));
```

既有「邊界摺線」區塊之後、頂點分類之前插入外圈播點與摺線：

```csharp
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                IReadOnlyList<IReadOnlyList<XYZ>> transitionRings;
                using (_timeline.Measure("銜接帶外圈取樣"))
                {
                    transitionRings = BuildTransitionRings(
                        solids, footprints, settings, xyTolerance, elevationTolerance,
                        rayBottomZ, rayTopZ, warnings);
                }

                var ringPoints = transitionRings.SelectMany(ring => ring)
                    .Where(candidate => !HasNearbyXY(existingPositions, candidate, xyTolerance)
                        && !HasNearbyXY(pointsToAdd, candidate, xyTolerance))
                    .ToList();
                if (ringPoints.Count > 0)
                {
                    using (_timeline.Measure("銜接帶外圈加入與重生"))
                    {
                        EnsurePointLimit(existingPositions.Count + pointsToAdd.Count + ringPoints.Count);
                        editor.AddPoints(ringPoints);
                        doc.Regenerate();
                    }
                }

                using (_timeline.Measure("銜接帶摺線"))
                {
                    DrawRingSplitLines(editor, transitionRings, xyTolerance);
                    doc.Regenerate();
                }
            }
```

（注意：既有程式把 `pointsToAdd` 的 `AddPoints` 放在邊界摺線之前，該順序不變；上述區塊需在 `pointsToAdd` 已加入、邊界摺線已畫之後執行，`existingPositions` 為修改前收集的清單。）

頂點分類迴圈之後（`targets.Count == 0` 檢查之前）加入銜接帶分類：

```csharp
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                var bandCandidateWidth = ToInternalMeters(
                    settings.Mode == GradingMode.OffsetTransition
                        ? settings.OffsetDistanceMeters
                        : settings.MaxExtensionMeters);
                var assigned = new HashSet<int>();
                for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    assigned.Add(FindNearestPositionIndex(allPositions, targets[targetIndex].Position));
                }

                var bandScope = _timeline.Measure("銜接帶頂點分類");
                for (var positionIndex = 0; positionIndex < allPositions.Count; positionIndex++)
                {
                    if (assigned.Contains(positionIndex))
                    {
                        continue;
                    }

                    var position = allPositions[positionIndex];
                    var sample = ToPoint2D(position);
                    FloorFootprint nearestFootprint = null;
                    var nearestDistance = double.MaxValue;
                    var nearestBoundary = default(Point2D);
                    foreach (var footprint in footprints)
                    {
                        var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(footprint.OuterLoop, sample);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestFootprint = footprint;
                            nearestBoundary = boundaryPoint;
                        }
                    }

                    if (nearestFootprint == null || nearestDistance > bandCandidateWidth + xyTolerance)
                    {
                        continue;
                    }

                    var boundaryBottomZ = nearestFootprint.BottomElevationAt(nearestBoundary.X, nearestBoundary.Y);
                    var localTerrainZ = position.Z;
                    var targetZ = settings.Mode == GradingMode.OffsetTransition
                        ? TransitionGeometry.OffsetTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance,
                            ToInternalMeters(settings.OffsetDistanceMeters))
                        : TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, localTerrainZ, nearestDistance, settings.RunPerRise);
                    if (Math.Abs(targetZ - localTerrainZ) <= elevationTolerance)
                    {
                        continue;
                    }

                    targets.Add(new VertexTarget(position, targetZ));
                }

                bandScope.Dispose();
            }
```

（`allPositions` 為分類階段開頭 `CollectVertexPositions(editor)` 的結果，原本以 `foreach` 直接列舉，改為先存進 `var allPositions = CollectVertexPositions(editor);` 再以索引迴圈跑既有樓板分類與上述帶分類；`FindNearestPositionIndex` 為新私有小函式，以 `XYDistanceSquared` 找最近索引。）

新私有方法（完整程式碼）：

```csharp
        private static double ToInternalMeters(double meters)
        {
            return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        }

        private static int FindNearestPositionIndex(IReadOnlyList<XYZ> positions, XYZ position)
        {
            var nearestIndex = -1;
            var nearestDistanceSquared = double.MaxValue;
            for (var index = 0; index < positions.Count; index++)
            {
                var distanceSquared = XYDistanceSquared(positions[index], position);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestIndex = index;
                }
            }

            return nearestIndex;
        }

        private IReadOnlyList<IReadOnlyList<XYZ>> BuildTransitionRings(
            IReadOnlyList<Solid> solids,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double xyTolerance,
            double elevationTolerance,
            double rayBottomZ,
            double rayTopZ,
            ICollection<string> warnings)
        {
            var offsetFeet = settings.Mode == GradingMode.OffsetTransition
                ? ToInternalMeters(settings.OffsetDistanceMeters)
                : 0.0;
            var maxExtensionFeet = settings.Mode == GradingMode.SlopeTransition
                ? ToInternalMeters(settings.MaxExtensionMeters)
                : 0.0;
            var rings = new List<IReadOnlyList<XYZ>>(footprints.Count);
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                var directions = Polygon2D.OutwardDirections(loop);
                var ring = new List<XYZ>(loop.Count);
                var cappedCount = 0;
                var maxResidualFeet = 0.0;
                for (var index = 0; index < loop.Count; index++)
                {
                    var boundaryPoint = loop[index];
                    var direction = directions[index];
                    var boundaryBottomZ = footprint.BottomElevationAt(boundaryPoint.X, boundaryPoint.Y);
                    double extension;
                    if (settings.Mode == GradingMode.OffsetTransition)
                    {
                        extension = offsetFeet;
                    }
                    else
                    {
                        // 定點迭代：放坡與地形相交距離依地形起伏而變，迭代 8 輪足夠收斂；
                        // 未收斂不報錯——最終高程由距離場函式決定，圈點只是網格密度控制。
                        extension = maxExtensionFeet;
                        for (var iteration = 0; iteration < 8; iteration++)
                        {
                            var probe = new Point2D(
                                boundaryPoint.X + (direction.X * extension),
                                boundaryPoint.Y + (direction.Y * extension));
                            var probeTerrainZ = IntersectTerrainTopZ(solids, probe, rayBottomZ, rayTopZ);
                            if (!probeTerrainZ.HasValue)
                            {
                                break;
                            }

                            var needed = TransitionGeometry.SlopeExtensionNeeded(
                                boundaryBottomZ, probeTerrainZ.Value, settings.RunPerRise);
                            extension = Math.Min(needed, maxExtensionFeet);
                        }

                        if (extension < xyTolerance)
                        {
                            continue; // 地形已在板底高程，無帶可放。
                        }
                    }

                    var ringXY = new Point2D(
                        boundaryPoint.X + (direction.X * extension),
                        boundaryPoint.Y + (direction.Y * extension));
                    if (footprints.Any(other => Polygon2D.Contains(other.OuterLoop, ringXY, xyTolerance)))
                    {
                        continue; // 凹角或鄰板：圈點落回投影內時跳過，缺段由摺線 try-catch 與包絡驗收把關。
                    }

                    var terrainZ = IntersectTerrainTopZ(solids, ringXY, rayBottomZ, rayTopZ);
                    if (!terrainZ.HasValue)
                    {
                        continue; // 規則 7：超出地形不處理。
                    }

                    if (settings.Mode == GradingMode.SlopeTransition)
                    {
                        var targetAtRing = TransitionGeometry.SlopeTargetZ(
                            boundaryBottomZ, terrainZ.Value, extension, settings.RunPerRise);
                        var residual = Math.Abs(targetAtRing - terrainZ.Value);
                        if (extension >= maxExtensionFeet - xyTolerance && residual > elevationTolerance)
                        {
                            cappedCount++;
                            maxResidualFeet = Math.Max(maxResidualFeet, residual);
                        }
                    }

                    ring.Add(new XYZ(ringXY.X, ringXY.Y, terrainZ.Value));
                }

                if (cappedCount > 0)
                {
                    var residualMeters = UnitUtils.ConvertFromInternalUnits(maxResidualFeet, UnitTypeId.Meters);
                    warnings.Add(
                        $"樓板 ID {footprint.FloorId} 放坡有 {cappedCount} 個邊界點在 maxExtension 上限截止，"
                        + $"殘留高差最大 {residualMeters:F2} m。");
                }

                rings.Add(ring);
            }

            return rings;
        }

        private static void DrawRingSplitLines(
            SlabShapeEditor editor,
            IReadOnlyList<IReadOnlyList<XYZ>> rings,
            double xyTolerance)
        {
            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                if (vertex != null && vertex.IsValidObject)
                {
                    vertices.Add(vertex);
                }
            }

            var matchTolerance = VertexMatchTolerance;
            foreach (var ring in rings)
            {
                for (var index = 0; index < ring.Count; index++)
                {
                    var startVertex = FindNearestVertex(vertices, ToPoint2D(ring[index]), matchTolerance);
                    var endVertex = FindNearestVertex(
                        vertices, ToPoint2D(ring[(index + 1) % ring.Count]), matchTolerance);
                    if (startVertex == null || endVertex == null)
                    {
                        continue;
                    }

                    if (XYDistanceSquared(startVertex.Position, endVertex.Position) <= xyTolerance * xyTolerance)
                    {
                        continue;
                    }

                    try
                    {
                        editor.DrawSplitLine(startVertex, endVertex);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue; // 既有摺線或退化線段；缺段風險由包絡驗收把關。
                    }
                }
            }
        }
```

- [ ] **Step 3: 包絡驗收**

表面抽樣驗收之後（`VerifySurfaceAgainstFootprints` 呼叫後）加：

```csharp
            if (settings.Mode != GradingMode.FootprintOnly)
            {
                using (_timeline.Measure("銜接帶包絡驗收"))
                {
                    VerifyTransitionBand(
                        original, design, footprints, settings,
                        elevationTolerance, xyTolerance, rayBottomZ, rayTopZ);
                }
            }
```

新私有方法（完整程式碼）：

```csharp
        // 銜接帶粗閘門：帶內為近似銜接面（非 2 mm 工程保證），此閘門抓「跨越樓板上空／
        // 漏摺線」級錯誤；土方量以實際網格計算，數字仍精確。
        private const double TransitionBandToleranceMillimeters = 500.0;

        private static void VerifyTransitionBand(
            Toposolid original,
            Toposolid design,
            IReadOnlyList<FloorFootprint> footprints,
            TransitionSettings settings,
            double elevationTolerance,
            double xyTolerance,
            double rayBottomZ,
            double rayTopZ)
        {
            var originalSolids = CollectSolids(original);
            var designSolids = CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                throw new InvalidOperationException("包絡驗收無法取得原地形或設計地形的實體幾何。");
            }

            var bandTolerance = UnitUtils.ConvertToInternalUnits(
                TransitionBandToleranceMillimeters, UnitTypeId.Millimeters);
            var boundaryMargin = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
            var sampleStep = UnitUtils.ConvertToInternalUnits(2000, UnitTypeId.Millimeters);
            var bandWidth = ToInternalMeters(settings.Mode == GradingMode.OffsetTransition
                ? settings.OffsetDistanceMeters
                : settings.MaxExtensionMeters);
            foreach (var footprint in footprints)
            {
                var loop = footprint.OuterLoop;
                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;
                foreach (var point in loop)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }

                for (var x = minX - bandWidth + (sampleStep / 2); x <= maxX + bandWidth; x += sampleStep)
                {
                    for (var y = minY - bandWidth + (sampleStep / 2); y <= maxY + bandWidth; y += sampleStep)
                    {
                        var sample = new Point2D(x, y);
                        if (footprints.Any(any => Polygon2D.Contains(any.OuterLoop, sample, xyTolerance)))
                        {
                            continue; // 投影內由既有 2 mm 抽樣把關。
                        }

                        var (boundaryPoint, distance) = Polygon2D.NearestBoundaryPoint(loop, sample);
                        if (distance < boundaryMargin || distance > bandWidth - boundaryMargin)
                        {
                            continue;
                        }

                        var terrainZ = IntersectTerrainTopZ(originalSolids, sample, rayBottomZ, rayTopZ);
                        var designZ = IntersectTerrainTopZ(designSolids, sample, rayBottomZ, rayTopZ);
                        if (!terrainZ.HasValue || !designZ.HasValue)
                        {
                            continue; // 規則 7：超出地形不檢查。
                        }

                        var boundaryBottomZ = footprint.BottomElevationAt(boundaryPoint.X, boundaryPoint.Y);
                        if (settings.Mode == GradingMode.SlopeTransition)
                        {
                            var targetZ = TransitionGeometry.SlopeTargetZ(
                                boundaryBottomZ, terrainZ.Value, distance, settings.RunPerRise);
                            if (Math.Abs(targetZ - terrainZ.Value) <= elevationTolerance)
                            {
                                continue; // 帶外：放坡已在此距離前貼合地形。
                            }
                        }

                        var lower = Math.Min(boundaryBottomZ, terrainZ.Value) - bandTolerance;
                        var upper = Math.Max(boundaryBottomZ, terrainZ.Value) + bandTolerance;
                        if (designZ.Value < lower || designZ.Value > upper)
                        {
                            var deviationMeters = UnitUtils.ConvertFromInternalUnits(
                                designZ.Value > upper ? designZ.Value - upper : lower - designZ.Value,
                                UnitTypeId.Meters);
                            throw new InvalidOperationException(
                                $"樓板 ID {footprint.FloorId} 銜接帶抽樣超出包絡 {deviationMeters:F2} m"
                                + "（容許 0.5 m），疑似網格跨越或摺線缺漏，已回滾整地。");
                        }
                    }
                }
            }
        }
```

- [ ] **Step 4: 建置與測試**

Run: `dotnet build -c Release.R24 MCP/RevitMCP.csproj --nologo -v minimal` → 0 錯誤。
Run: `dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal` → 全數 PASS（純邏輯不受影響）。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Grading/RevitToposolidGradingAdapter.cs
git commit -m "功能：銜接帶框架——外圈取樣、摺線、距離場頂點分類與包絡驗收"
```

---

### Task 6: CommandExecutor 參數解析、settings 傳遞與回應擴充

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs`

**Interfaces:**
- Consumes: Task 1 `TransitionSettings`、Task 4 preprocessor、Task 5 `ApplyGrading` 新簽章。
- Produces: 工具回應新增 `Mode` 欄位；`Warnings` 含放坡截止與已消化警告；效能記錄含 `Mode`。

- [ ] **Step 1: 參數解析**（`request` 初始化加三行）

```csharp
                OffsetDistanceMeters = parameters["offsetDistance"]?.Value<double?>(),
                SlopeRatio = parameters["slopeRatio"]?.Value<string>(),
                MaxExtensionMeters = parameters["maxExtension"]?.Value<double?>(),
```

`request.Validate();` 之後：

```csharp
            var settings = TransitionSettings.FromRequest(request);
            var warnings = new List<string>();
```

- [ ] **Step 2: 呼叫端改用 ApplyGrading**

```csharp
                        modifiedPointCount = adapter.ApplyGrading(
                            doc, original, design, footprints, settings, warnings);
```

- [ ] **Step 3: 回應與記錄**

Task 4 的已消化警告改為附加到同一個 `warnings` 清單（放在交易群組完成後）：

```csharp
            foreach (var dismissed in failuresPreprocessor.DismissedWarnings.Distinct())
            {
                warnings.Add($"已自動略過 Revit 警告：{dismissed}");
            }
```

`GradingResult` 設 `Warnings = warnings`；回應匿名物件與兩處 `WriteGradingPerformanceRecord` 各加 `Mode = request.Mode`；回應 `Message` 改為 `$"樓板投影整地完成（{request.Mode}）。"`。

- [ ] **Step 4: 建置驗證**

Run: `dotnet build -c Release.R24 MCP/RevitMCP.csproj --nologo -v minimal` → 0 錯誤。
Run: `dotnet build -c Release.R23 MCP/RevitMCP.csproj --nologo -v minimal` → 0 錯誤（`#if REVIT2024_OR_GREATER` 之外不受影響）。

- [ ] **Step 5: Commit**

```bash
git add MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs
git commit -m "功能：整地命令支援 offset/slope 模式參數與警告回報"
```

---

### Task 7: MCP 工具 schema 與測試（TDD）

**Files:**
- Modify: `MCP-Server/src/tools/grading-tools.ts`
- Test: `MCP-Server/src/tools/grading-tools.test.ts`

- [ ] **Step 1: 改測試（先失敗）**

`grading-tools.test.ts` 第一個測試改為：

```typescript
test("整地工具暴露三種模式與銜接參數", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["toposolidId", "floorIds"]);
    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.deepEqual((properties.mode as { enum: string[] }).enum,
        ["footprint_only", "offset_transition", "slope_transition"]);
    assert.deepEqual((properties.targetFace as { enum: string[] }).enum, ["bottom"]);
    assert.equal(properties.offsetDistance.type, "number");
    assert.equal(properties.offsetDistance.exclusiveMinimum, 0);
    assert.match(String(properties.offsetDistance.description), /公尺/);
    assert.equal(properties.slopeRatio.type, "string");
    assert.match(String(properties.slopeRatio.description), /1:n/);
    assert.equal(properties.maxExtension.type, "number");
    assert.match(String(properties.maxExtension.description), /20/);
});
```

Run: `cd MCP-Server && npm test`
Expected: FAIL（enum 只有 footprint_only、新屬性不存在）。

- [ ] **Step 2: 改 schema**

`grading-tools.ts`：`description` 改為「依指定樓板底面的水平投影範圍整平 Toposolid；支援 footprint_only 直貼、offset_transition 外延漸變、slope_transition 指定坡度放坡三種模式。」；`mode` 改為：

```typescript
                mode: {
                    type: "string",
                    enum: ["footprint_only", "offset_transition", "slope_transition"],
                    default: "footprint_only",
                    description:
                        "整地模式：footprint_only 投影內直貼板底（邊界垂直落差）；offset_transition 投影外再外延 offsetDistance 漸變回原地形；slope_transition 由邊界以 slopeRatio 固定坡度放坡回原地形",
                },
```

`updateExisting` 之前插入：

```typescript
                offsetDistance: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "offset_transition 模式的外延距離（公尺）；僅該模式接受此參數",
                },
                slopeRatio: {
                    type: "string",
                    description:
                        "slope_transition 模式的目標坡度，格式 1:n（垂直:水平，例如 1:12）；僅該模式接受此參數",
                },
                maxExtension: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description:
                        "slope_transition 模式的放坡延伸距離上限（公尺），未提供時預設 20；放坡在上限截止時回報警告；僅該模式接受此參數",
                },
```

- [ ] **Step 3: 跑測試確認通過**

Run: `cd MCP-Server && npm run build && npm test`
Expected: 全數 PASS（含既有 Profile 測試）。

- [ ] **Step 4: Commit**

```bash
git add MCP-Server/src/tools/grading-tools.ts MCP-Server/src/tools/grading-tools.test.ts
git commit -m "功能：整地工具 schema 開放 offset/slope 模式與銜接參數"
```

---

### Task 8: 全量驗證、規格同步、日誌與部署準備

**Files:**
- Modify: `docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md`（模式 Roadmap 狀態、計時階段清單、銜接帶驗收定位）
- Modify: `log/2026-07.md`（追加條目）

- [ ] **Step 1: 全量測試與建置**

```powershell
dotnet test MCP.Tests/RevitMCP.Tests.csproj --nologo -v minimal   # 全數 PASS
dotnet build -c Release.R24 MCP/RevitMCP.csproj --nologo -v minimal  # 0 錯誤
dotnet build -c Release.R23 MCP/RevitMCP.csproj --nologo -v minimal  # 0 錯誤
cd MCP-Server; npm run build; npm test                             # 全數 PASS
```

- [ ] **Step 2: 規格同步**

- 模式 Roadmap 表：模式 2、3 狀態改「已實作（2026-07-03）」，補 `maxExtension` 預設 20 m 與 `boundaryEdges` 未實作註記。
- 計時階段清單加：銜接帶外圈取樣、銜接帶外圈加入與重生、銜接帶摺線、銜接帶頂點分類、銜接帶包絡驗收。
- 「測試與驗證」節補：銜接帶包絡驗收 500 mm 粗閘門定位（footprint 內 2 mm 剛性閘門不變）。

- [ ] **Step 3: QA/QC 與日誌**

Run: `.\scripts\verify-qaqc.ps1 -SkipBuild -SkipDeploy`（與基線比對，變更檔不得有新 FAIL）。
`log/2026-07.md` 追加 feature 條目（actor、files、summary 完整）。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md log/2026-07.md
git commit -m "文件：Phase 1 放坡框架規格同步與日誌"
```

- [ ] **Step 5: 部署與實機驗證檢查點（需要 Revit 環境）**

- 檢查 Revit 是否執行中：`Get-Process Revit -ErrorAction SilentlyContinue`。
- 未執行 → 執行 `scripts/install-addon.ps1`（或手動 SHA256 比對複製）部署 R24 DLL。
- 實機驗證項目（需使用者在 Revit 開啟 TOPO_example）：
  1. `offset_transition`（offsetDistance 3 m）：邊界外 3 m 帶內漸變、無垂直斷面、CUT/FILL 大於 footprint_only 的挖方。
  2. `slope_transition`（slopeRatio 1:2，maxExtension 20 m）：放坡貼合地形、殘差截止有警告。
  3. 連跑兩個方案不再出現 127 秒模態阻塞；回應 Warnings 列出被消化的警告。

---

## Self-Review 紀錄

- **Spec coverage**：IFailuresPreprocessor（Task 4）、模式 2（Task 1/2/5/6/7）、模式 3 含 1:n 與 maxExtension 警告（同上）、共用框架（距離場，Task 2/3/5）、效能計時新階段（Task 5/8）、規格同步（Task 8）。`boundaryEdges` 明確列為不在範圍（Global Constraints）。
- **Placeholder scan**：無 TBD/TODO；所有程式碼步驟含完整程式碼。
- **Type consistency**：`TransitionSettings.FromRequest`/`ParseSlopeRatio`（Task 1）與 Task 5/6 呼叫一致；`ApplyGrading` 六參數簽章在 Task 5 定義、Task 6 呼叫一致；`Polygon2D.NearestBoundaryPoint` 回傳 tuple `(Point, Distance)` 兩處用法一致。
