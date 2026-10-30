using System;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.WindowFamily;

namespace MCP.Tests.WindowFamily
{
    /// <summary>
    /// add-in 對話框的規格組裝層（WindowSpecBuilder）：UI 狀態 → 規格 JSON → 引擎解析／驗證。
    /// 純 C#，不碰 WPF／Revit API。
    /// </summary>
    [TestFixture]
    public class WindowSpecBuilderTests
    {
        [Test]
        public void DefaultState_ProducesJsonThatEngineParses()
        {
            var b = new WindowSpecBuilder();
            WindowFamilySpec spec = b.BuildSpec();

            Assert.That(spec.Rows, Is.EqualTo(2));
            Assert.That(spec.Cols, Is.EqualTo(2));
            Assert.That(spec.InstanceParams.Width, Is.EqualTo(240));
            Assert.That(spec.InstanceParams.Height, Is.EqualTo(180));
            Assert.That(spec.TypeParams.FrameDepth, Is.EqualTo(7.0), "框深預設 7 cm（ADR D-010）");
            Assert.That(spec.TypeParams.OverlapMode, Is.EqualTo(OverlapMode.EqualsSashFrameWidth));
            Assert.That(spec.DescribeOperations(), Is.EqualTo("R1: F | F / R2: CL,CR | CL,CR"));
        }

        [Test]
        public void AllPresets_ParseAndComputeLayout()
        {
            Assert.That(WindowSpecBuilder.Presets.Count, Is.GreaterThanOrEqualTo(10));
            foreach (var p in WindowSpecBuilder.Presets)
            {
                var b = new WindowSpecBuilder();
                b.ApplyPreset(p);
                WindowFamilySpec spec = b.BuildSpec();
                WindowLayout layout = WindowLayoutMath.Compute(spec);
                Assert.That(layout.Derived.SashCount, Is.GreaterThan(0), p.Name);
                Assert.That(layout.Derived.GlassAreaM2, Is.GreaterThan(0), p.Name);
            }
        }

        [Test]
        public void ThreeTrackPreset_HasScreenAndCountsGlassOnly()
        {
            var b = new WindowSpecBuilder();
            b.ApplyPreset(WindowSpecBuilder.Presets.First(p => p.Key == "tk"));
            var layout = WindowLayoutMath.Compute(b.BuildSpec());
            Assert.That(layout.Derived.SashCount, Is.EqualTo(2), "紗窗不計入扇數");
            Assert.That(layout.Derived.ScreenCount, Is.EqualTo(1));
        }

        [Test]
        public void Ratios_AutoCompleteTo100()
        {
            // 少給一個值 → 最後一個補足
            Assert.That(WindowSpecBuilder.ParseRatioText("35", 2), Is.EqualTo(new[] { 35.0, 65.0 }));
            // 個數正確但總和不是 100 → 依比例縮放，總和精確 100
            var scaled = WindowSpecBuilder.ParseRatioText("1 : 2", 2);
            Assert.That(scaled.Sum(), Is.EqualTo(100.0).Within(1e-9));
            Assert.That(scaled[0], Is.EqualTo(33.33).Within(1e-9));
            // 空字串 → 等分，三等分總和仍是 100
            var even = WindowSpecBuilder.ParseRatioText("", 3);
            Assert.That(even.Sum(), Is.EqualTo(100.0).Within(1e-9));

            // 經由 builder 進到引擎：引擎要求總和 100（容差 0.01）
            var b = new WindowSpecBuilder();
            b.SetRowRatios("30, 70");
            b.SetColRatios("1:1");
            var spec = b.BuildSpec();
            Assert.That(spec.InstanceParams.RowRatios, Is.EqualTo(new[] { 30.0, 70.0 }));
            Assert.That(spec.InstanceParams.ColRatios.Sum(), Is.EqualTo(100.0).Within(1e-9));
        }

        [Test]
        public void Ratios_BadInput_ThrowsReadableError()
        {
            Assert.Throws<WindowSpecException>(() => WindowSpecBuilder.ParseRatioText("a:b", 2));
            Assert.Throws<WindowSpecException>(() => WindowSpecBuilder.ParseRatioText("10:20:30", 2));
            Assert.Throws<WindowSpecException>(() => WindowSpecBuilder.ParseRatioText("0:100", 2));
        }

        [Test]
        public void AutoNames_FollowTopologyAndSize()
        {
            var b = new WindowSpecBuilder();
            Assert.That(b.AutoTypeName(), Is.EqualTo("240x180"));
            Assert.That(b.AutoFamilyName(), Is.EqualTo("UW-R2C2-F-CL-CR"));

            b.Width = 180.5;
            b.Height = 150;
            Assert.That(b.EffectiveTypeName, Is.EqualTo("180.5x150"));

            var json = b.ToJson();
            Assert.That((string)json["familyName"], Does.StartWith("UW-R2C2-"));
            Assert.That((string)json["typeName"], Is.EqualTo("180.5x150"));

            // 關閉自動命名 → 用手動輸入
            b.AutoNames = false;
            b.FamilyName = "MyWindow";
            b.TypeName = "T-01";
            Assert.That((string)b.ToJson()["familyName"], Is.EqualTo("MyWindow"));
            Assert.That((string)b.ToJson()["typeName"], Is.EqualTo("T-01"));
        }

        [Test]
        public void SetGrid_PreservesExistingCells()
        {
            var b = new WindowSpecBuilder();   // 預設：上固定下外推 2x2
            b.SetGrid(2, 3);

            Assert.That(b.Rows, Is.EqualTo(2));
            Assert.That(b.Cols, Is.EqualTo(3));
            Assert.That(b.CellSummary(0, 0), Is.EqualTo("F"));
            Assert.That(b.CellSummary(1, 0), Is.EqualTo("CL+CR"), "既有格保留");
            Assert.That(b.CellSummary(1, 1), Is.EqualTo("CL+CR"), "既有格保留");
            Assert.That(b.CellSummary(1, 2), Is.EqualTo("F"), "新增的格預設單扇固定");
            Assert.That(b.RowRatios, Is.EqualTo(new[] { 35.0, 65.0 }), "排數未變 → 排高比保留");
            Assert.That(b.ColRatios.Sum(), Is.EqualTo(100.0).Within(1e-9));
            Assert.That(b.ColRatios.Length, Is.EqualTo(3), "樘數改變 → 樘寬比重設為等分");

            // 縮回 1 排：第一排保留、第二排消失
            b.SetGrid(1, 2);
            Assert.That(b.Rows, Is.EqualTo(1));
            Assert.That(b.CellSummary(0, 0), Is.EqualTo("F"));
            Assert.That(b.BuildSpec().Rows, Is.EqualTo(1));
        }

        [Test]
        public void SetGrid_ClampsToEngineLimits()
        {
            var b = new WindowSpecBuilder();
            b.SetGrid(9, 9);
            Assert.That(b.Rows, Is.EqualTo(WindowFamilySpec.MaxRows));
            Assert.That(b.Cols, Is.EqualTo(WindowFamilySpec.MaxCols));
            b.SetGrid(0, 0);
            Assert.That(b.Rows, Is.EqualTo(1));
            Assert.That(b.Cols, Is.EqualTo(1));
        }

        [Test]
        public void SashFields_OnlyEmittedWhereTheyApply()
        {
            var b = new WindowSpecBuilder();
            b.ApplyPreset(WindowSpecBuilder.Presets.First(p => p.Key == "fx"));
            var sash = b.Cells[0][0][0];

            // 外推＋右鉸鏈＋向內
            b.SetSashType(sash, SashType.Casement);
            sash.Hinge = HingeSide.Right;
            sash.SwingRaw = SwingDirection.In;
            var o = (Newtonsoft.Json.Linq.JObject)b.ToJson()["generation"]["cells"][0][0]["sashes"][0];
            Assert.That((string)o["type"], Is.EqualTo("casement"));
            Assert.That((string)o["hinge"], Is.EqualTo("R"));
            Assert.That((string)o["swing"], Is.EqualTo("in"));
            Assert.That(o["track"], Is.Null);
            Assert.That(b.BuildSpec().Cells[0][0].Sashes[0].Abbreviation, Is.EqualTo("CRi"));

            // 改成橫拉＋軌序 1：鉸鏈與開向不輸出
            b.SetSashType(sash, SashType.Sliding);
            sash.Track = 1;
            o = (Newtonsoft.Json.Linq.JObject)b.ToJson()["generation"]["cells"][0][0]["sashes"][0];
            Assert.That((string)o["type"], Is.EqualTo("sliding"));
            Assert.That(o["hinge"], Is.Null);
            Assert.That(o["swing"], Is.Null);
            Assert.That((int)o["track"], Is.EqualTo(1));

            // 再改回固定：軌序被清掉
            b.SetSashType(sash, SashType.Fixed);
            Assert.That(sash.Track, Is.Null);
            Assert.That(b.ToJson()["generation"]["cells"][0][0]["sashes"][0]["track"], Is.Null);
        }

        [Test]
        public void SetSashCount_GrowsAndShrinksWithinLimits()
        {
            var b = new WindowSpecBuilder();
            b.SetSashCount(1, 0, 4);
            Assert.That(b.Cells[1][0].Count, Is.EqualTo(4));
            // 外推新增的扇鉸鏈左右交替
            Assert.That(b.Cells[1][0].Select(s => s.Hinge).ToArray(),
                Is.EqualTo(new[] { HingeSide.Left, HingeSide.Right, HingeSide.Left, HingeSide.Right }));
            b.SetSashCount(1, 0, 99);
            Assert.That(b.Cells[1][0].Count, Is.EqualTo(WindowFamilySpec.MaxSashesPerCell));
            b.SetSashCount(1, 0, 1);
            Assert.That(b.Cells[1][0].Count, Is.EqualTo(1));
        }

        [Test]
        public void ManualOverlap_EmittedAndParsed()
        {
            var b = new WindowSpecBuilder();
            b.ApplyPreset(WindowSpecBuilder.Presets.First(p => p.Key == "sl"));
            b.OverlapEqualsSash = false;
            b.OverlapMm = 60;
            var spec = b.BuildSpec();
            Assert.That(spec.TypeParams.OverlapMode, Is.EqualTo(OverlapMode.Manual));
            Assert.That(spec.TypeParams.OverlapCm, Is.EqualTo(6.0).Within(1e-9));

            b.OverlapEqualsSash = true;
            Assert.That(b.ToJson()["typeParams"]["meetingRailOverlapMode"], Is.Null, "預設交疊不輸出 manual 欄位");
            Assert.That(b.BuildSpec().TypeParams.OverlapCm, Is.EqualTo(b.Sash).Within(1e-9));
        }

        [Test]
        public void EngineValidationErrors_SurfaceAsReadableExceptions()
        {
            var b = new WindowSpecBuilder();
            b.ApplyPreset(WindowSpecBuilder.Presets.First(p => p.Key == "sl"));
            // 同格橫拉＋外推 → 引擎規則：滑動類不可與開啟類混用
            b.SetSashType(b.Cells[0][0][1], SashType.Casement);
            var ex = Assert.Throws<WindowSpecException>(() => b.BuildSpec());
            Assert.That(ex.Message, Does.Contain("R1C1"));

            // 尺寸扣掉框料後非正值 → 佈局數學的錯誤
            var b2 = new WindowSpecBuilder();
            b2.Width = 10;
            var spec = b2.BuildSpec();
            Assert.Throws<WindowSpecException>(() => WindowLayoutMath.Compute(spec));
        }

        [Test]
        public void OutputOptions_ReachJson()
        {
            var b = new WindowSpecBuilder();
            b.OutputDir = @"C:\Temp\uw";
            b.LoadIntoProject = false;
            b.Overwrite = true;
            var spec = b.BuildSpec();
            Assert.That(spec.OutputDir, Is.EqualTo(@"C:\Temp\uw"));
            Assert.That(spec.LoadIntoProject, Is.False);
            Assert.That(spec.Overwrite, Is.True);

            b.OutputDir = "  ";
            Assert.That(b.ToJson()["outputDir"], Is.Null, "空白輸出資料夾不輸出，改用預設 %APPDATA%\\RevitMCP\\families");
        }
    }
}
