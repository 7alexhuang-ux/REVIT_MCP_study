using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// imsheetmarks：圖紙版面（視口外框、紙張↔模型轉換、剖面標記）。全是合成資料，沒有 Revit；
    /// 數字與網站端 src/lib/sheet-layout.test.ts 逐一對應（兩邊用同一個範例）。
    /// </summary>
    [TestFixture]
    public class SheetMarkMathTests
    {
        // 平面圖：1:100，glTF 公尺；東＝+X、北＝−Z、看向模型＝−Y（俯視）。視口中心在紙上 (200,150)，外框 300×200mm。
        private static ViewportPlacement Plan(double rotationDeg = 0)
        {
            return new ViewportPlacement
            {
                ViewUniqueId = "v-plan", ViewKind = ViewKinds.Plan,
                BoxMinX = 50, BoxMinY = 50, BoxMaxX = 350, BoxMaxY = 250,
                CenterX = 200, CenterY = 150, Scale = 100, RotationDeg = rotationDeg,
                Origin = new Vec3(0, 0, 0), Right = new Vec3(1, 0, 0), Up = new Vec3(0, 0, -1), Direction = new Vec3(0, -1, 0),
                OutlineCenterU = 0, OutlineCenterV = 0, CutPlaneMeters = 1.2,
            };
        }

        [Test]
        public void ModelToPaperAppliesScaleAndCenter()
        {
            double x, y;
            // 往東 10 公尺、往北 5 公尺 → 1:100 → 紙上右 100mm、上 50mm
            SheetMarkMath.ModelToPaper(Plan(), new Vec3(10, 0, -5), out x, out y);
            Assert.AreEqual(300, x, 1e-9);
            Assert.AreEqual(200, y, 1e-9);
        }

        [Test]
        public void OutlineCenterOffsetShiftsThePaperPosition()
        {
            var vp = Plan();
            vp.OutlineCenterU = 10; vp.OutlineCenterV = -20; // 視圖輪廓中心不在視圖原點
            double x, y;
            SheetMarkMath.ModelToPaper(vp, new Vec3(10, 0, -5), out x, out y);
            Assert.AreEqual(290, x, 1e-9);
            Assert.AreEqual(220, y, 1e-9);
        }

        [TestCase(90.0, 150.0, 250.0)]
        [TestCase(-90.0, 250.0, 50.0)]
        [TestCase(180.0, 100.0, 100.0)]
        public void RotationTurnsAboutTheViewportCenter(double deg, double ex, double ey)
        {
            double x, y;
            SheetMarkMath.ModelToPaper(Plan(deg), new Vec3(10, 0, -5), out x, out y);
            Assert.AreEqual(ex, x, 1e-9);
            Assert.AreEqual(ey, y, 1e-9);
        }

        [TestCase(0.0)]
        [TestCase(90.0)]
        [TestCase(-90.0)]
        [TestCase(37.5)]
        public void PaperToModelIsTheInverseOfModelToPaper(double deg)
        {
            var vp = Plan(deg);
            vp.OutlineCenterU = 12.5; vp.OutlineCenterV = -3.25;
            var original = new Vec3(7.3, 4.0, -11.9);
            double x, y;
            SheetMarkMath.ModelToPaper(vp, original, out x, out y);
            var back = SheetMarkMath.PaperToModel(vp, x, y, heightMeters: 4.0);
            Assert.AreEqual(original.X, back.X, 1e-9);
            Assert.AreEqual(original.Y, back.Y, 1e-9);
            Assert.AreEqual(original.Z, back.Z, 1e-9);
        }

        [Test]
        public void PaperToModelWithoutHeightStaysOnTheViewPlane()
        {
            var back = SheetMarkMath.PaperToModel(Plan(), 300, 200);
            Assert.AreEqual(10, back.X, 1e-9);
            Assert.AreEqual(0, back.Y, 1e-9);
            Assert.AreEqual(-5, back.Z, 1e-9);
        }

        [Test]
        public void PaperToModelSolvesTheHeightAlongTheViewDirection()
        {
            var back = SheetMarkMath.PaperToModel(Plan(), 300, 200, 3.0);
            Assert.AreEqual(10, back.X, 1e-9);
            Assert.AreEqual(3, back.Y, 1e-9);
            Assert.AreEqual(-5, back.Z, 1e-9);
        }

        [Test]
        public void SectionViewportMapsHeightToThePaperVerticalAxis()
        {
            // 北向剖面：看向 −Z（北）… 這裡用 right＝+X、up＝+Y、direction＝−Z
            var vp = new ViewportPlacement
            {
                ViewUniqueId = "v-sec", ViewKind = ViewKinds.Section, BoxMinX = 0, BoxMinY = 0, BoxMaxX = 100, BoxMaxY = 100,
                CenterX = 50, CenterY = 50, Scale = 50, Origin = new Vec3(0, 0, 0),
                Right = new Vec3(1, 0, 0), Up = new Vec3(0, 1, 0), Direction = new Vec3(0, 0, -1),
            };
            double x, y;
            SheetMarkMath.ModelToPaper(vp, new Vec3(5, 3, 40), out x, out y); // 5m → 100mm、3m → 60mm
            Assert.AreEqual(150, x, 1e-9);
            Assert.AreEqual(110, y, 1e-9);
        }

        [Test]
        public void ZeroScaleIsRejected()
        {
            var vp = Plan(); vp.Scale = 0;
            double x, y;
            Assert.Throws<ArgumentException>(() => SheetMarkMath.ModelToPaper(vp, new Vec3(1, 0, 1), out x, out y));
            Assert.Throws<ArgumentException>(() => SheetMarkMath.PaperToModel(vp, 1, 1));
        }

        [Test]
        public void SegmentIntersectsBoxCases()
        {
            Assert.IsTrue(SheetMarkMath.SegmentIntersectsBox(60, 60, 100, 100, 50, 50, 350, 250), "完全在內");
            Assert.IsTrue(SheetMarkMath.SegmentIntersectsBox(0, 150, 400, 150, 50, 50, 350, 250), "穿過兩側");
            Assert.IsTrue(SheetMarkMath.SegmentIntersectsBox(0, 0, 100, 100, 50, 50, 350, 250), "一端在外、斜穿角落");
            Assert.IsFalse(SheetMarkMath.SegmentIntersectsBox(0, 0, 40, 40, 50, 50, 350, 250), "完全在外");
            Assert.IsFalse(SheetMarkMath.SegmentIntersectsBox(0, 300, 400, 300, 50, 50, 350, 250), "平行於框外");
            Assert.IsTrue(SheetMarkMath.SegmentIntersectsBox(100, 100, 100, 100, 50, 50, 350, 250), "零長度的點在框內");
        }

        private static SectionLine Line(string id = "v-sec-aa")
        {
            // 一條東西向、在北邊 5 公尺處、長 20 公尺的剖面線；垂直範圍 −2～12 公尺
            return new SectionLine { ViewUniqueId = id, Kind = ViewKinds.Section, P0 = new Vec3(0, 0, -5), P1 = new Vec3(20, 0, -5), MinY = -2, MaxY = 12 };
        }

        [Test]
        public void MarkerOnPlanViewportGivesPaperEndpoints()
        {
            var marker = SheetMarkMath.MarkerOnViewport(Plan(), Line());
            Assert.NotNull(marker);
            Assert.AreEqual("v-sec-aa", (string)marker["viewUniqueId"]);
            Assert.AreEqual("section", (string)marker["kind"]);
            Assert.AreEqual("v-plan", (string)marker["hostViewUniqueId"]);
            CollectionAssert.AreEqual(new[] { 200.0, 200.0 }, marker["p0Mm"].Select(t => (double)t).ToArray());
            CollectionAssert.AreEqual(new[] { 400.0, 200.0 }, marker["p1Mm"].Select(t => (double)t).ToArray());
            CollectionAssert.AreEqual(new[] { 20.0, 0.0, -5.0 }, marker["p1"].Select(t => (double)t).ToArray());
        }

        [Test]
        public void MarkerIsHiddenWhenItDoesNotApply()
        {
            Assert.IsNull(SheetMarkMath.MarkerOnViewport(Plan(), new SectionLine { ViewUniqueId = "v", Kind = "section", P0 = new Vec3(100, 0, -5), P1 = new Vec3(120, 0, -5), MinY = -2, MaxY = 12 }), "整條在視口外");
            Assert.IsNull(SheetMarkMath.MarkerOnViewport(Plan(), new SectionLine { ViewUniqueId = "v", Kind = "section", P0 = new Vec3(0, 0, -5), P1 = new Vec3(20, 0, -5), MinY = 5, MaxY = 12 }), "切高 1.2m 不在剖面的垂直範圍");
            Assert.IsNull(SheetMarkMath.MarkerOnViewport(Plan(), Line("v-plan")), "不標記自己");
            var sectionHost = Plan(); sectionHost.ViewKind = ViewKinds.Section;
            Assert.IsNull(SheetMarkMath.MarkerOnViewport(sectionHost, Line()), "只算在平面／天花平面上");
            var noCut = Plan(); noCut.CutPlaneMeters = null;
            Assert.NotNull(SheetMarkMath.MarkerOnViewport(noCut, new SectionLine { ViewUniqueId = "v", Kind = "elevation", P0 = new Vec3(0, 0, -5), P1 = new Vec3(20, 0, -5), MinY = 5, MaxY = 12 }), "沒有切高資料就不以高度過濾");
        }

        [Test]
        public void BuildLayoutIsNullWithoutSheetsAndCarriesVersionAndMarkers()
        {
            Assert.IsNull(SheetMarkMath.BuildLayout(null, null));
            Assert.IsNull(SheetMarkMath.BuildLayout(new[] { new SheetPlacement { SheetUniqueId = "s" } }, null), "沒有視口的圖紙不輸出");

            var sheet = new SheetPlacement { SheetUniqueId = "sheet-1", SheetNumber = "A-101", WidthMm = 841, HeightMm = 594, OutlineMinXMm = 10, OutlineMinYMm = 20 };
            sheet.Viewports.Add(Plan());
            var layout = SheetMarkMath.BuildLayout(new[] { sheet }, new[] { Line() });
            Assert.AreEqual(1, (int)layout["formatVersion"]);
            Assert.AreEqual("mm", (string)layout["units"]["paper"]);
            var first = layout["sheets"][0];
            Assert.AreEqual("sheet-1", (string)first["sheetUniqueId"]);
            CollectionAssert.AreEqual(new[] { 841.0, 594.0 }, first["sizeMm"].Select(t => (double)t).ToArray());
            var vp = first["viewports"][0];
            Assert.AreEqual("v-plan", (string)vp["viewUniqueId"]);
            Assert.AreEqual(100.0, (double)vp["scale"]);
            Assert.AreEqual(1.2, (double)vp["cutPlaneMeters"], 1e-9);
            CollectionAssert.AreEqual(new[] { 50.0, 50.0 }, vp["boxMm"]["min"].Select(t => (double)t).ToArray());
            Assert.AreEqual(1, first["markers"].Count());
        }

        [Test]
        public void MergeKeepsOtherFieldsAndDoesNotRewriteDateLikeStrings()
        {
            var original = new JObject { ["schemaVersion"] = 1, ["revitDoc"] = "2026-11-02T01:02:03Z", ["views"] = new JArray() }.ToString();
            var merged = Jsons.Parse(SheetMarkMath.MergeIntoViewPositions(original, new JObject { ["formatVersion"] = 1 }));
            Assert.AreEqual("2026-11-02T01:02:03Z", (string)merged["revitDoc"]);
            Assert.AreEqual(1, (int)merged["schemaVersion"]);
            Assert.AreEqual(1, (int)merged["sheetLayout"]["formatVersion"]);
        }
    }

    [TestFixture]
    public class SheetLayoutExportTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("sheetlayout"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static ExportRequest Request(int sheetCount = 2)
        {
            var request = new ExportRequest { ProjectId = "p1", Stage = "SD", Description = "測試", IncludeModel = true };
            request.Views.Add(Samples.View("v-plan", "2F", ViewKinds.Plan));
            request.Views.Add(Samples.View("v-sec-aa", "A-A", ViewKinds.Section));
            for (var i = 0; i < sheetCount; i++)
                request.Sheets.Add(Samples.Sheet("sheet-" + i, "A-" + (i + 1), "圖 " + i, Samples.Viewport("v-plan", 4, 3)));
            return request;
        }

        private static SheetPlacement PlacementFor(string sheetId)
        {
            var sheet = new SheetPlacement { SheetUniqueId = sheetId, SheetNumber = sheetId, WidthMm = 841, HeightMm = 594 };
            sheet.Viewports.Add(new ViewportPlacement
            {
                ViewUniqueId = "v-plan", ViewKind = ViewKinds.Plan, BoxMinX = 50, BoxMinY = 50, BoxMaxX = 350, BoxMaxY = 250, CenterX = 200, CenterY = 150, Scale = 100,
                Origin = new Vec3(0, 0, 0), Right = new Vec3(1, 0, 0), Up = new Vec3(0, 0, -1), Direction = new Vec3(0, -1, 0),
            });
            return sheet;
        }

        private static FakeSteps StepsWithLayout(int sheetCount = 2)
        {
            var steps = new FakeSteps();
            for (var i = 0; i < sheetCount; i++) steps.PlacementsToReturn.Add(PlacementFor("sheet-" + i));
            steps.SectionLinesToReturn.Add(new SectionLine { ViewUniqueId = "v-sec-aa", Kind = ViewKinds.Section, P0 = new Vec3(0, 0, -5), P1 = new Vec3(20, 0, -5), MinY = -2, MaxY = 12 });
            return steps;
        }

        [Test]
        public void LayoutIsMergedIntoViewPositionsAndTheHashMatchesTheMergedFile()
        {
            var steps = StepsWithLayout();
            var bundle = new BundleExporter().Export(Request(), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            var file = bundle.Files.Single(f => f.Kind == BundleFile.ViewPositions);
            var json = Jsons.Parse(File.ReadAllText(file.Path));
            Assert.AreEqual(1, (int)json["schemaVersion"], "位置檔原有欄位不動");
            Assert.AreEqual(1, (int)json["sheetLayout"]["formatVersion"]);
            Assert.AreEqual(2, json["sheetLayout"]["sheets"].Count());
            Assert.AreEqual(1, json["sheetLayout"]["sheets"][0]["markers"].Count());
            Assert.AreEqual(new FileInfo(file.Path).Length, file.Bytes);
            Assert.AreEqual(BundleExporter.Sha256Hex(file.Path), file.Sha256, "雜湊必須是併入版面之後的檔案");
            var manifest = Jsons.Parse(File.ReadAllText(bundle.ManifestPath));
            Assert.AreEqual(file.Sha256, (string)manifest["files"]["viewPositions"]["sha256"]);
        }

        [Test]
        public void SheetsAreReadInChunksSoTheMessageLoopCanBreathe()
        {
            var steps = StepsWithLayout(9);
            new BundleExporter().Export(Request(9), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 4, 4, 1 }, steps.PlacementBatchSizes);
        }

        [Test]
        public void OptingOutSkipsTheLayoutStepEntirely()
        {
            var steps = StepsWithLayout();
            var request = Request();
            request.IncludeSheetLayout = false;
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.IsEmpty(steps.PlacementBatchSizes);
            var json = Jsons.Parse(File.ReadAllText(bundle.Files.Single(f => f.Kind == BundleFile.ViewPositions).Path));
            Assert.IsNull(json["sheetLayout"]);
        }

        [Test]
        public void NoSheetLayoutWithoutViewPositions()
        {
            var steps = StepsWithLayout();
            var request = Request();
            request.SkipViewPositions = true;
            new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.IsEmpty(steps.PlacementBatchSizes);
        }

        [Test]
        public void ALayoutFailureDoesNotBreakTheUploadAndLeavesThePositionsFileUntouched()
        {
            var steps = StepsWithLayout();
            steps.ThrowInPlacements = new InvalidOperationException("boom");
            var bundle = new BundleExporter().Export(Request(), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            var json = Jsons.Parse(File.ReadAllText(bundle.Files.Single(f => f.Kind == BundleFile.ViewPositions).Path));
            Assert.IsNull(json["sheetLayout"]);
            var warnings = Jsons.Parse(File.ReadAllText(bundle.ManifestPath))["warnings"].Select(w => (string)w).ToList();
            Assert.That(warnings.Any(w => w.Contains("圖紙版面匯出失敗")), "要有白話警告：" + string.Join("|", warnings));
        }

        [Test]
        public void EmptyPlacementsLeaveTheFileByteForByteTheSame()
        {
            var plain = new BundleExporter().Export(Request(), new FakeSteps(), Path.Combine(_dir, "a"), null, CancellationToken.None);
            var json = File.ReadAllText(plain.Files.Single(f => f.Kind == BundleFile.ViewPositions).Path);
            StringAssert.DoesNotContain("sheetLayout", json);
        }

        [Test]
        public void SingleViewportSheetsAreMeasuredOnlyWhenTheLayoutNeedsTheBox()
        {
            var request = Request();
            foreach (var sheet in request.Sheets) foreach (var viewport in sheet.Viewports) viewport.ViewportElementId = 1000 + request.Sheets.IndexOf(sheet);
            Assert.AreEqual(0, BundleExporter.ViewportsNeedingMeasure(request).Count, "舊行為：單一視口不量");
            Assert.AreEqual(2, BundleExporter.ViewportsNeedingMeasure(request, true).Count, "要版面時單一視口也要外框");
            foreach (var sheet in request.Sheets) foreach (var viewport in sheet.Viewports) viewport.HasBox = true;
            Assert.AreEqual(0, BundleExporter.ViewportsNeedingMeasure(request, true).Count, "量過就不重量");
        }
    }
}
