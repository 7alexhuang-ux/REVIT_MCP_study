using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    // imrevitaddin（Issue Manager D-801～D-805、spec/revit-addin-export.md §8）：視圖位置與參數兩個匯出步驟、params.json 的組裝規則、
    // 舊網站（沒有 viewPositions／params 上傳網址）的相容、完成畫面的版本號。全部離線（假步驟、假伺服器）。
    [TestFixture]
    public class ParamsFileBuilderTests
    {
        private static ParamsFileData Sample()
        {
            var data = new ParamsFileData { GeneratedAt = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.FromHours(8)), Note = "Revit 2024：AR_TEST" };
            data.Levels.Add(new ParamsLevelData { Name = "1F", RevitName = "1F", ElevationMeters = -2.848123456 });
            data.Elements.Add(new ParamsElementData { UniqueId = "wall-1", ElementId = 10, Category = "Walls", Family = "Basic Wall", Type = "RC-15", Level = "1F", LengthMm = 6800, HeightMm = 4000, ThicknessMm = 150, AreaSqm = 27.2, VolumeCum = 4.08, Materials = { "混凝土", "混凝土" } });
            data.Elements.Add(new ParamsElementData { UniqueId = "door-1", Category = "Doors", Level = "1F", WidthMm = 900 }); // 沒有高度：不寫 height，也不補 0
            data.Elements.Add(new ParamsElementData { UniqueId = "room-1", Category = "Rooms", Name = "大廳", AreaSqm = 186.42 });
            var schedule = new ParamsScheduleData { Id = "area-by-room", Name = "房間面積表" };
            schedule.Columns.Add(new ParamsColumnData { Key = "number", Label = "房號" });
            schedule.Columns.Add(new ParamsColumnData { Key = "area", Label = "面積", Type = "area", Unit = "m2" });
            schedule.Rows.Add(new ParamsRowData { UniqueId = "room-1", Cells = { { "number", "101" }, { "area", 186.42 } } });
            schedule.Rows.Add(new ParamsRowData { UniqueId = "room-2", Cells = { { "number", "102" }, { "area", null } } });
            data.Schedules.Add(schedule);
            return data;
        }

        [Test]
        public void BuildsTheImRevitParamsContract()
        {
            var json = ParamsFileBuilder.Build(Sample());
            Assert.AreEqual("im-revit-params/1", (string)json["schema"]);
            Assert.AreEqual(false, (bool)json["isTestData"]);
            Assert.AreEqual("revit-addin", (string)json["source"]["kind"]);
            Assert.AreEqual(-2.8481, (double)json["levels"][0]["elevation"], 1e-9, "高程四捨五入到 0.1 mm");
            var wall = json["elements"]["wall-1"];
            Assert.AreEqual(6800, (double)wall["values"]["length"]);
            Assert.AreEqual(27.2, (double)wall["values"]["area"]);
            Assert.AreEqual(1, wall["materials"].Count(), "重複的材料名稱只留一個");
            Assert.IsNull(json["elements"]["door-1"]["values"]["height"], "讀不到的欄位不寫（不補 0）");
            Assert.AreEqual(900, (double)json["elements"]["door-1"]["values"]["width"]);
        }

        [Test]
        public void ScheduleKeepsNullCellsAndColumnOrder()
        {
            var schedule = ParamsFileBuilder.Build(Sample())["schedules"][0];
            Assert.AreEqual("area-by-room", (string)schedule["id"]);
            CollectionAssert.AreEqual(new[] { "number", "area" }, schedule["columns"].Select(c => (string)c["key"]).ToArray());
            Assert.AreEqual("m2", (string)schedule["columns"][1]["unit"]);
            Assert.AreEqual(JTokenType.Null, schedule["rows"][1]["cells"]["area"].Type, "缺的格子是 null，網站顯示「—」");
            Assert.AreEqual("room-2", (string)schedule["rows"][1]["uniqueId"]);
        }

        [Test]
        public void NoSchedulesMeansNoSchedulesKey()
        {
            var data = Sample();
            data.Schedules.Clear();
            Assert.IsNull(ParamsFileBuilder.Build(data)["schedules"]);
        }

        [Test]
        public void DuplicateUniqueIdsAreCollapsedCaseInsensitively()
        {
            var data = new ParamsFileData();
            data.Elements.Add(new ParamsElementData { UniqueId = "ABC", Category = "Walls" });
            data.Elements.Add(new ParamsElementData { UniqueId = "abc", Category = "Walls" });
            Assert.AreEqual(1, ((JObject)ParamsFileBuilder.Build(data)["elements"]).Count);
        }

        [Test]
        public void WriteStaysUnderTheEightMiBLimitKeepingRoomsAndOpeningsFirst()
        {
            var data = new ParamsFileData();
            data.Elements.Add(new ParamsElementData { UniqueId = "room-keep", Category = "Rooms", AreaSqm = 10 });
            data.Elements.Add(new ParamsElementData { UniqueId = "door-keep", Category = "Doors", WidthMm = 900 });
            var big = new string('x', 150);
            for (var i = 0; i < 60000; i++)
                data.Elements.Add(new ParamsElementData { UniqueId = "wall-" + i.ToString("D6"), Category = "Walls", Family = big, Type = big, Name = big, LengthMm = i });
            var dir = TempDir.Create("params");
            try
            {
                var path = Path.Combine(dir, "params.json");
                var result = ParamsFileBuilder.Write(data, path);
                Assert.LessOrEqual(new FileInfo(path).Length, ParamsFileBuilder.MaxBytes);
                var parsed = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                Assert.IsNotNull(parsed["elements"]["room-keep"], "房間最後才丟");
                Assert.IsNotNull(parsed["elements"]["door-keep"], "門窗其次");
                Assert.Less(result.Elements, 60002);
                Assert.IsTrue(result.Warnings.Any(w => w.Contains("8 MB")), "被截斷要用白話告訴使用者");
                Assert.AreEqual(result.Elements, ((JObject)parsed["elements"]).Count);
            }
            finally { TempDir.TryDelete(dir); }
        }

        [Test]
        public void SmallFilesAreWrittenWholeWithoutTruncationWarning()
        {
            var dir = TempDir.Create("params-small");
            try
            {
                var path = Path.Combine(dir, "params.json");
                var result = ParamsFileBuilder.Write(Sample(), path);
                Assert.AreEqual(3, result.Elements);
                Assert.AreEqual(1, result.Levels);
                Assert.AreEqual(1, result.Schedules);
                Assert.IsFalse(result.Warnings.Any(w => w.Contains("8 MB")));
                Assert.AreEqual((byte)'{', File.ReadAllBytes(path)[0], "UTF-8 不帶 BOM");
            }
            finally { TempDir.TryDelete(dir); }
        }
    }

    [TestFixture]
    public class BundleExporterPositionsAndParamsTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("positions"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        [Test]
        public void X03_PositionsAndParamsUseTheSameOriginAsTheGlbAndAppearInTheManifest()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.IncludeParams = true;
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb", "namedViews", "viewPositions", "params" }, steps.Calls);
            CollectionAssert.AreEqual(steps.Origin, steps.LastPositionOrigin, "位置檔與 GLB 同一組原點（D-597）");
            CollectionAssert.AreEqual(steps.Origin, steps.LastParamsOrigin, "參數檔與 GLB 同一組原點");
            CollectionAssert.AreEqual(new[] { "v-plan" }, steps.LastPositionIds.ToArray(), "只有平面／天花／剖面／立面送去匯出位置，3D 視圖不送");
            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews", "viewPositions", "params" }, bundle.Files.Select(f => f.Kind).ToArray());
            var files = (JObject)bundle.Manifest["files"];
            Assert.AreEqual("view_positions.json", (string)files["viewPositions"]["name"]);
            Assert.AreEqual("params.json", (string)files["params"]["name"]);
            Assert.AreEqual(64, ((string)files["params"]["sha256"]).Length);
            Assert.IsTrue(bundle.Manifest["warnings"].Select(w => (string)w).Any(w => w.Contains("連結模型")), "參數匯出的提醒要進 manifest warnings");
        }

        [Test]
        public void ParamsCanBeExportedWithoutAModel()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.IncludeModel = false;
            request.IncludeParams = true;
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc", "pdf", "params" }, steps.Calls, "參數是 Revit 讀的，不依賴 GLB；位置資料需要模型，沒模型就略過");
            var warnings = bundle.Manifest["warnings"].Select(w => (string)w).ToList();
            Assert.IsTrue(warnings.Any(w => w.Contains("略過平面／立面／剖面的位置資料")), "沒模型卻勾了平面視圖要提醒");
        }

        [Test]
        public void OnlyThreeDViewsMeansNoPositionsFile()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.Views.RemoveAll(v => v.Kind != ViewKinds.ThreeD);
            new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb", "namedViews" }, steps.Calls);
        }

        [Test]
        public void DetailViewsHaveNoPosition()
        {
            Assert.IsFalse(ViewKinds.HasPosition(ViewKinds.Detail));
            Assert.IsFalse(ViewKinds.HasPosition(ViewKinds.ThreeD));
            foreach (var kind in new[] { ViewKinds.Plan, ViewKinds.Ceiling, ViewKinds.Section, ViewKinds.Elevation }) Assert.IsTrue(ViewKinds.HasPosition(kind), kind);
        }

        [Test]
        public void PositionsThatCouldNotBeReadAreExplainedInPlainLanguage()
        {
            var steps = new FakeSteps { PositionViewCountOverride = 0 };
            var bundle = new BundleExporter().Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            var warnings = bundle.Manifest["warnings"].Select(w => (string)w).ToList();
            Assert.IsTrue(warnings.Any(w => w.Contains("只有 0 個讀得到位置") && w.Contains("尚無 3D 對應")));
        }

        [Test]
        public void PreWarningsFromTheFlowAreKeptAndSkipViewPositionsIsHonoured()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.SkipViewPositions = true;
            request.PreWarnings.Add("測試提醒");
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.DoesNotContain(steps.Calls, "viewPositions");
            Assert.IsTrue(bundle.Manifest["warnings"].Select(w => (string)w).Contains("測試提醒"));
        }

        [Test]
        public void SummaryCountsPositionedViewsAndShowsParamsState()
        {
            Assert.AreEqual("將上傳 2 張圖紙、5 個視圖（其中 3 個有位置資料）、1 個模型、參數與面積表 ○", Strings.Summary(2, 5, 3, 1, true));
            StringAssert.EndsWith("✕", Strings.Summary(0, 0, 0, 1, false));
            StringAssert.Contains("v14", Strings.Uploaded("v14"));
            StringAssert.Contains("同一個版本號", Strings.Uploaded("v14"));
            Assert.AreEqual("完成：Issue Manager 已建立一個工作版本。", Strings.Uploaded(null), "舊網站不回版本號時維持原文");
        }
    }

    [TestFixture]
    public class UploadFlowImRevitAddinTests
    {
        private string _dir;
        private FakeTokenServer _server;
        [SetUp] public void SetUp() { _dir = TempDir.Create("flow-addin"); _server = new FakeTokenServer { PartBytes = 16 }; }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private async Task<UploadFlow> NewFlow(FakeSteps steps)
        {
            var api = await Harness.LoggedInApiAsync(_server);
            return new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"),
                uploader: new BundleUploader(api, 2, (span, token) => Task.FromResult(0)));
        }

        [Test]
        public async Task X06_NewServerReceivesPositionsAndParamsAndReturnsTheVersionNumber()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var request = RequestFactory.Full();
            request.IncludeParams = true;
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);
            Assert.AreEqual("v1", result.Version);
            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews", "viewPositions", "params" },
                ((JObject)_server.FinalizeManifests[0]["files"]).Properties().Select(p => p.Name).ToArray());
            Assert.IsNotNull(result.SetId);
        }

        [Test]
        public async Task X05_OldServerWithoutParamsTicketSkipsParamsAndSaysSo()
        {
            _server.Kinds = new[] { "pdf", "glb", "namedViews", "viewPositions" };
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var request = RequestFactory.Full();
            request.IncludeParams = true;
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);
            CollectionAssert.DoesNotContain(steps.Calls, "params", "舊網站不核發 params 網址 → 不匯出也不上傳");
            var manifest = _server.FinalizeManifests[0];
            Assert.IsNull(manifest["files"]["params"]);
            Assert.IsTrue(manifest["warnings"].Select(w => (string)w).Contains(Strings.ServerWithoutParams));
            Assert.IsNotNull(result.SetId, "圖紙與模型照常建立");
        }

        [Test]
        public async Task X05_OlderServerWithoutViewPositionsTicketSkipsThePositionsFile()
        {
            _server.Kinds = new[] { "pdf", "glb", "namedViews" };
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var request = RequestFactory.Full();
            request.IncludeParams = true;
            await flow.RunAsync(request, null, null, CancellationToken.None);
            CollectionAssert.DoesNotContain(steps.Calls, "viewPositions");
            CollectionAssert.DoesNotContain(steps.Calls, "params");
            var warnings = _server.FinalizeManifests[0]["warnings"].Select(w => (string)w).ToList();
            CollectionAssert.IsSupersetOf(warnings, new[] { Strings.ServerWithoutViewPositions, Strings.ServerWithoutParams });
        }
    }
}
