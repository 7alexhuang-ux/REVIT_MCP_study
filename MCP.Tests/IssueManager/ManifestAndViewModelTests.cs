using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    [TestFixture]
    public class ManifestBuilderTests
    {
        private static JObject LoadJson(params string[] relative)
        {
            var path = Path.Combine(new[] { TempDir.RepoRoot() }.Concat(relative).ToArray());
            return Jsons.Parse(File.ReadAllText(path));
        }

        private static MiniJsonSchema Schema() { return new MiniJsonSchema(LoadJson("docs", "schemas", "bundle-manifest.schema.json")); }

        /// <summary>與 docs/schemas/fixtures/bundle-manifest.sample.json 對應的固定輸入（golden 比對）。</summary>
        private static JObject BuildSample()
        {
            var request = new ExportRequest
            {
                SheetSetName = "送審圖", IncludeModel = true, IncludeDesignOptions = true,
                Sheets =
                {
                    Samples.Sheet("sheet-a2-uid", "A-2", "二層平面圖", Samples.Viewport("view-plan-2f-uid", 4, 3)),
                    Samples.Sheet("sheet-a10-uid", "A-10", "剖面與透視", Samples.Viewport("view-section-aa-uid", 1, 1), Samples.Viewport("view-3d-main-uid", 3, 2))
                },
                Views =
                {
                    Samples.View("view-3d-main-uid", "主透視", ViewKinds.ThreeD),
                    Samples.View("view-plan-2f-uid", "2F", ViewKinds.Plan),
                    Samples.View("view-section-aa-uid", "A-A 剖面", ViewKinds.Section)
                }
            };
            var files = new[]
            {
                new BundleFile { Kind = BundleFile.Pdf, Name = "sheets.pdf", Bytes = 1234, Sha256 = new string('a', 64) },
                new BundleFile { Kind = BundleFile.Glb, Name = "model.glb", Bytes = 5678, Sha256 = new string('b', 64) },
                new BundleFile { Kind = BundleFile.NamedViews, Name = "named_views.json", Bytes = 910, Sha256 = new string('c', 64) }
            };
            return ManifestBuilder.Build(request, new RevitDocInfo { Title = "AR_CPC", VersionNumber = "2024 (24.3.0.0)" },
                new DateTimeOffset(2026, 10, 25, 16, 30, 0, TimeSpan.FromHours(8)), new double[] { 0, 0, 0 }, files,
                new[] { "範例：沒有視圖位置檔（V5 才有）" });
        }

        [Test]
        public void X01_ManifestMatchesGoldenFixtureAndPassesSchema()
        {
            var manifest = BuildSample();
            var golden = LoadJson("docs", "schemas", "fixtures", "bundle-manifest.sample.json");
            Assert.IsTrue(JToken.DeepEquals(golden, manifest), "manifest 輸出與 fixture 不同：\n" + manifest);
            Assert.IsEmpty(Schema().Validate(manifest));
            Assert.IsEmpty(Schema().Validate(golden), "fixture 本身也要通過 schema（Node 端 ajv 也驗同一份）");
        }

        [Test]
        public void SheetNumbersComeFromRevitAndPagesFollowOrder()
        {
            var manifest = BuildSample();
            var sheets = (JArray)manifest["sheets"];
            Assert.AreEqual(new[] { 1, 2 }, sheets.Select(s => (int)s["pageNumber"]).ToArray());
            Assert.AreEqual(new[] { "A-2", "A-10" }, sheets.Select(s => (string)s["sheetNumber"]).ToArray());
        }

        [Test]
        public void PrimaryViewportIsTheLargestOnTheSheet()
        {
            var manifest = BuildSample();
            var viewports = (JArray)manifest["sheets"][1]["viewports"];
            Assert.IsFalse((bool)viewports[0]["isPrimary"]);
            Assert.IsTrue((bool)viewports[1]["isPrimary"]);
            Assert.AreEqual(1, viewports.Count(v => (bool)v["isPrimary"]));
        }

        [Test]
        public void ViewportsOfUncheckedViewsAreOmittedAndPrimaryIsChosenAmongTheRest()
        {
            var request = new ExportRequest
            {
                Sheets = { Samples.Sheet("s", "A-1", "x", Samples.Viewport("big-but-unchecked", 9, 9), Samples.Viewport("small", 1, 1)) },
                Views = { Samples.View("small", "小", ViewKinds.Plan) }
            };
            var manifest = ManifestBuilder.Build(request, new RevitDocInfo(), DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, null, null);
            var viewports = (JArray)manifest["sheets"][0]["viewports"];
            Assert.AreEqual(1, viewports.Count);
            Assert.AreEqual("small", (string)viewports[0]["viewUniqueId"]);
            Assert.IsTrue((bool)viewports[0]["isPrimary"]);
        }

        [Test]
        public void MissingPartsAreOmittedNotNull()
        {
            var request = new ExportRequest { IncludeModel = false };
            var manifest = ManifestBuilder.Build(request, new RevitDocInfo { Title = "T" }, DateTimeOffset.UtcNow, new double[] { 1, 2, 3 }, null, null);
            Assert.IsEmpty(((JObject)manifest["files"]).Properties());
            Assert.IsNull(manifest["sheetSet"], "自訂集合：省略 sheetSet");
            Assert.IsFalse((bool)manifest["options"]["designOptions"]);
            Assert.IsEmpty(Schema().Validate(manifest));
        }

        [Test]
        public void DuplicateSheetNumbersViewIdsAndBadOriginAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Build(
                new ExportRequest { Sheets = { Samples.Sheet("a", "A-1", "x"), Samples.Sheet("b", "a-1", "y") } },
                new RevitDocInfo(), DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, null, null));
            Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Build(
                new ExportRequest { Views = { Samples.View("v", "a", ViewKinds.Plan), Samples.View("v", "b", ViewKinds.Plan) } },
                new RevitDocInfo(), DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, null, null));
            Assert.Throws<ArgumentException>(() => ManifestBuilder.Build(new ExportRequest(), new RevitDocInfo(), DateTimeOffset.UtcNow, new[] { 0.0, double.NaN, 0.0 }, null, null));
        }

        [Test]
        public void SchemaValidatorActuallyRejectsBrokenManifests()
        {
            var good = BuildSample();
            var schema = Schema();
            Action<Action<JObject>> broken = mutate =>
            {
                var copy = (JObject)good.DeepClone();
                mutate(copy);
                Assert.IsNotEmpty(schema.Validate(copy));
            };
            broken(m => m["schemaVersion"] = 2);
            broken(m => m["files"]["pdf"]["sha256"] = "ABC");
            broken(m => m["sheets"][0]["pageNumber"] = 0);
            broken(m => m["sheets"][0]["viewports"][0]["viewKind"] = "legend");
            broken(m => m.Remove("options"));
            broken(m => m["extra"] = true);
            broken(m => m["originOffsetMeters"] = new JArray(0, 0));
        }
    }

    [TestFixture]
    public class NaturalComparerTests
    {
        [Test]
        public void SortsDigitsNumericallyAndIgnoresCase()
        {
            var sorted = new[] { "A-10", "a-2", "A-1", "B-1", "A-2a", "A-02" }.OrderBy(s => s, NaturalComparer.Instance).ToArray();
            CollectionAssert.AreEqual(new[] { "A-1", "A-02", "a-2", "A-2a", "A-10", "B-1" }, sorted);
        }
    }

    [TestFixture]
    public class ExportSelectionViewModelTests
    {
        private static ExportSelectionViewModel LoggedInVm(out ProjectSnapshot snapshot)
        {
            snapshot = Samples.Snapshot();
            var vm = new ExportSelectionViewModel(snapshot);
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "測試專案" } });
            vm.IsLoggedIn = true;
            return vm;
        }

        private static void Fill(ExportSelectionViewModel vm)
        {
            vm.SelectedProject = vm.Projects[0];
            vm.SelectedStage = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "第一版";
        }

        [Test]
        public void X06_StageStartsAsPlaceholderWithNoDefault()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            Assert.IsNull(vm.SelectedStage.Code);
            Assert.AreEqual("請選擇階段", vm.SelectedStage.Label);
            CollectionAssert.AreEqual(new string[] { null, "PD", "SD", "DD" }, vm.Stages.Select(s => s.Code).ToArray());
            vm.SelectedProject = vm.Projects[0];
            vm.Description = "說明";
            Assert.IsFalse(vm.CanUpload, "階段是「請選擇階段」時不可送出（D-587）");
            Assert.AreEqual("請選擇設計階段", vm.BlockReason);
        }

        [Test]
        public void X06_UploadNeedsLoginProjectStageDescriptionAndAtLeastOneItem()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            vm.IsLoggedIn = false;
            Fill(vm);
            Assert.AreEqual("請先登入", vm.BlockReason);
            vm.IsLoggedIn = true;
            Assert.IsTrue(vm.CanUpload, "預設勾選模型，所以只要其他條件齊全就能送出");

            vm.Description = "   ";
            Assert.AreEqual("請填寫版本說明", vm.BlockReason);
            vm.Description = "ok";
            vm.SelectedProject = null;
            Assert.AreEqual("請選擇專案", vm.BlockReason);
            vm.SelectedProject = vm.Projects[0];

            vm.IncludeModel = false;
            Assert.AreEqual(Strings.NothingSelected, vm.BlockReason);
            StringAssert.Contains("圖紙", vm.BlockReason);
            vm.Sheets.First().IsChecked = true;
            Assert.IsTrue(vm.CanUpload);

            vm.IsBusy = true;
            Assert.IsFalse(vm.CanUpload);
        }

        [Test]
        public void X06_SelectingSheetSetChecksItsMembersExactly()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "送審圖");

            Assert.AreEqual(new[] { "A-2" }, vm.Sheets.Where(s => s.IsChecked).Select(s => s.Item.Number).ToArray());
            var checkedViews = vm.Sheets.SelectMany(s => s.Viewports).Concat(vm.ViewGroups.SelectMany(g => g.Views)).Where(v => v.IsChecked).Select(v => v.Item.UniqueId).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { "v-elev-n", "v-plan-2f", "v-sec-aa" }, checkedViews, "圖紙 A-2 的兩個視口視圖，加上集內單獨的立面");
            Assert.AreEqual("送審圖", vm.SelectedSheetSet.Label);

            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "全部");
            Assert.AreEqual(3, vm.CheckedSheetCount);
            Assert.AreEqual(4, vm.CheckedViewCount, "全部圖紙的 4 個視口視圖；集內沒有的立面已被取消");
        }

        [Test]
        public void X06_ManualChangeSwitchesDropdownToCustom()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "送審圖");
            vm.Sheets.First(s => s.Item.Number == "A-10").IsChecked = true;
            Assert.AreEqual("（自訂）", vm.SelectedSheetSet.Label);
            Assert.IsNull(vm.BuildRequestForTest().SheetSetName);

            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "送審圖");
            vm.ViewGroups.First().Views.First().IsChecked = true;
            Assert.AreEqual("（自訂）", vm.SelectedSheetSet.Label);
        }

        [Test]
        public void CheckingSheetChecksViewportsAndViewportsCanBeToggledIndividually()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            var a10 = vm.Sheets.First(s => s.Item.Number == "A-10");
            a10.IsChecked = true;
            Assert.IsTrue(a10.Viewports.All(v => v.IsChecked));
            a10.Viewports.First().IsChecked = false;
            Assert.IsTrue(a10.IsChecked, "取消單一視口不影響圖紙本身");
            a10.IsChecked = false;
            Assert.IsTrue(a10.Viewports.All(v => !v.IsChecked));
        }

        [Test]
        public void SheetsAreListedInNaturalOrderAndOtherViewsGroupedByKindExcludingPlacedOnes()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            CollectionAssert.AreEqual(new[] { "A-1", "A-2", "A-10" }, vm.Sheets.Select(s => s.Item.Number).ToArray());
            CollectionAssert.AreEqual(new[] { ViewKinds.ThreeD, ViewKinds.Elevation }, vm.ViewGroups.Select(g => g.Kind).ToArray());
            Assert.AreEqual(new[] { "鳥瞰" }, vm.ViewGroups[0].Views.Select(v => v.Label).ToArray());
        }

        [Test]
        public void SummaryAndMissingWarningsFollowSelection()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            StringAssert.StartsWith("將上傳 0 張圖紙、0 個視圖", vm.SummaryText); StringAssert.Contains("1 個模型", vm.SummaryText);
            CollectionAssert.AreEqual(new[] { "這個版本將缺圖（沒有勾選任何圖紙）" }, vm.MissingWarnings.ToArray());
            vm.SelectAllSheets(true);
            vm.IncludeModel = false;
            StringAssert.StartsWith("將上傳 3 張圖紙、4 個視圖", vm.SummaryText); StringAssert.Contains("0 個模型", vm.SummaryText);
            CollectionAssert.AreEqual(new[] { "這個版本將缺模型（沒有勾選 3D 模型）" }, vm.MissingWarnings.ToArray());
            Assert.IsFalse(vm.DesignOptionsEnabled);
        }

        [Test]
        public void BuildRequestKeepsOnlyCheckedViewportsAndOrdersSheetsNaturally()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            Fill(vm);
            vm.Sheets.First(s => s.Item.Number == "A-10").IsChecked = true;
            vm.Sheets.First(s => s.Item.Number == "A-2").IsChecked = true;
            vm.Sheets.First(s => s.Item.Number == "A-10").Viewports.First(v => v.Item.UniqueId == "v-sec-bb").IsChecked = false;
            vm.ViewGroups.First(g => g.Kind == ViewKinds.Elevation).Views[0].IsChecked = true;
            vm.IncludeDesignOptions = false;

            var request = vm.BuildRequest();
            Assert.AreEqual("p1", request.ProjectId);
            Assert.AreEqual("SD", request.Stage);
            Assert.AreEqual("第一版", request.Description);
            CollectionAssert.AreEqual(new[] { "A-2", "A-10" }, request.Sheets.Select(s => s.Number).ToArray());
            CollectionAssert.AreEqual(new[] { "v-3d-main" }, request.Sheets[1].Viewports.Select(v => v.ViewUniqueId).ToArray());
            CollectionAssert.AreEqual(new[] { "v-3d-main", "v-plan-2f", "v-elev-n", "v-sec-aa" }, request.Views.Select(v => v.UniqueId).ToArray(), "依 3D→平面→立面→剖面排序");
            Assert.IsTrue(request.IncludeModel);
            Assert.IsFalse(request.IncludeDesignOptions);
        }

        [Test]
        public void BuildRequestThrowsWhenNotUploadable()
        {
            ProjectSnapshot snap;
            var vm = LoggedInVm(out snap);
            Assert.Throws<InvalidOperationException>(() => vm.BuildRequest());
        }

        [Test]
        public void NonModelViewKindsAreIgnored()
        {
            var snapshot = Samples.Snapshot();
            snapshot.Views.Add(Samples.View("v-legend", "圖例", "legend"));
            var vm = new ExportSelectionViewModel(snapshot);
            Assert.IsFalse(vm.ViewGroups.SelectMany(g => g.Views).Any(v => v.Item.UniqueId == "v-legend"));
        }
    }

    internal static class ViewModelTestExtensions
    {
        /// <summary>只為了檢視 SheetSetName：用最小條件組出請求，不要求 CanUpload。</summary>
        public static ExportRequest BuildRequestForTest(this ExportSelectionViewModel vm)
        {
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "C", Name = "N" } });
            vm.IsLoggedIn = true;
            vm.SelectedProject = vm.Projects[0];
            vm.SelectedStage = vm.Stages.First(s => s.Code == "PD");
            vm.Description = "x";
            return vm.BuildRequest();
        }
    }
}
