using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// 只傳圖面、接到已上傳的模型，以及記下來源模型路徑（IM D-870～D-872、D-871，spec/revit-addin-export.md §9）。
    /// X01～X06：不匯出 GLB、沿用被接模型的原點、manifest 帶 attachToModelId 與路徑欄位、勾選視窗的下拉規則、API 端點與錯誤白話。
    /// </summary>
    [TestFixture]
    public class SheetsOnlyAttachTests
    {
        private static readonly double[] ModelOrigin = { -433.698, -1414.604, 0 };
        private string _dir;

        [SetUp] public void SetUp() { _dir = TempDir.Create("sheetsonly"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static AttachableModel Model(string id, string name, double[] origin, string version = "v3")
        {
            return new AttachableModel { Id = id, FileName = name, VersionLabel = version, OriginOffsetMeters = origin, CreatedAt = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero) };
        }

        private static MiniJsonSchema Schema()
        {
            return new MiniJsonSchema(Jsons.Parse(File.ReadAllText(Path.Combine(TempDir.RepoRoot(), "docs", "schemas", "bundle-manifest.schema.json"))));
        }

        private static ExportRequest SheetsOnly(AttachableModel attach)
        {
            var request = RequestFactory.Full();
            request.IncludeModel = false;
            request.AttachedModel = attach;
            return request;
        }

        // ── X01 匯出：不匯出 GLB、原點沿用被接的模型 ─────────────────────────

        [Test]
        public void X01_AttachExportsSheetsAndViewsWithoutGlbAndUsesTheModelOrigin()
        {
            var steps = new FakeSteps { Origin = new double[] { 1, 2, 3 } }; // 若誤用匯出模型的原點，這組會出現在結果裡
            var bundle = new BundleExporter().Export(SheetsOnly(Model("model-1", "model.glb", ModelOrigin)), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "doc", "pdf", "namedViews", "viewPositions" }, steps.Calls, "不匯出 GLB，但 3D 視角與平立剖位置照常匯出");
            CollectionAssert.AreEqual(ModelOrigin, steps.LastNamedViewOrigin, "3D 視角必須用被接模型的原點");
            CollectionAssert.AreEqual(ModelOrigin, steps.LastPositionOrigin, "位置檔必須用被接模型的原點");
            CollectionAssert.AreEqual(new[] { "pdf", "namedViews", "viewPositions" }, bundle.Files.Select(f => f.Kind).ToArray());

            var manifest = bundle.Manifest;
            Assert.AreEqual("model-1", (string)manifest["attachToModelId"]);
            CollectionAssert.AreEqual(ModelOrigin, manifest["originOffsetMeters"].Select(v => (double)v).ToArray(), "manifest 的原點＝被接模型的原點");
            Assert.IsNull(manifest["files"]["glb"]);
            Assert.IsEmpty(Schema().Validate(manifest), "manifest 要通過網站端 schema（含 attachToModelId）");
            var warnings = manifest["warnings"].Select(w => (string)w).ToList();
            Assert.IsFalse(warnings.Any(w => w.Contains("略過 3D 視角") || w.Contains("略過平面")), "有接模型就不該說「沒有模型所以略過」");
        }

        [Test]
        public void X01_WithoutAttachBehaviourIsUnchanged()
        {
            var steps = new FakeSteps();
            var bundle = new BundleExporter().Export(SheetsOnly(null), steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc", "pdf" }, steps.Calls);
            Assert.IsNull(bundle.Manifest["attachToModelId"]);
            var warnings = bundle.Manifest["warnings"].Select(w => (string)w).ToList();
            Assert.IsTrue(warnings.Any(w => w.Contains("略過 3D 視角")));
        }

        [Test]
        public void X01_ExportingTheModelWinsOverAnAttachedModel()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.AttachedModel = Model("model-1", "model.glb", ModelOrigin);
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.Contains(steps.Calls, "glb");
            CollectionAssert.AreEqual(steps.Origin, steps.LastNamedViewOrigin, "有匯出模型就用它自己的原點");
            Assert.IsNull(bundle.Manifest["attachToModelId"]);
        }

        [Test]
        public void X01_AttachedModelWithoutOriginIsRefusedWithAPlainMessage()
        {
            var steps = new FakeSteps();
            var ex = Assert.Throws<InvalidOperationException>(() => new BundleExporter().Export(SheetsOnly(Model("model-1", "model.glb", null)), steps, Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("沒有記錄座標原點", ex.Message);
            CollectionAssert.IsEmpty(steps.Calls, "原點不明就不該開始匯出");
        }

        [Test]
        public void X01_AttachWithoutSheetsIsRefusedByTheManifestBuilder()
        {
            var request = SheetsOnly(Model("model-1", "model.glb", ModelOrigin));
            request.Sheets.Clear();
            var ex = Assert.Throws<InvalidOperationException>(() => new BundleExporter().Export(request, new FakeSteps(), Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("至少要勾選一張圖紙", ex.Message);
        }

        // ── X02 來源路徑（D-871）──────────────────────────────────────────

        [Test]
        public void X02_ManifestCarriesSourcePathsAndSavedAt_AndOmitsEmptyOnes()
        {
            var request = SheetsOnly(null);
            var files = new[] { new BundleFile { Kind = BundleFile.Pdf, Name = "sheets.pdf", Bytes = 10, Sha256 = new string('a', 64) } };
            var doc = new RevitDocInfo
            {
                Title = "AR-CPC", VersionNumber = "2024 (test)", Path = @"H:\我的雲端硬碟\01.Projects\BIM\AR-CPC.rvt", CentralPath = @"H:\central\AR-CPC.rvt",
                SavedAtUtc = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc)
            };
            var manifest = ManifestBuilder.Build(request, doc, DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, files, null);
            Assert.AreEqual(@"H:\我的雲端硬碟\01.Projects\BIM\AR-CPC.rvt", (string)manifest["revitDoc"]["path"]);
            Assert.AreEqual(@"H:\central\AR-CPC.rvt", (string)manifest["revitDoc"]["centralPath"]);
            Assert.AreEqual("2026-10-30T08:00:00Z", (string)manifest["revitDoc"]["savedAt"]);
            Assert.IsEmpty(Schema().Validate(manifest));

            var bare = ManifestBuilder.Build(request, new RevitDocInfo { Title = "新專案", VersionNumber = "2024", Path = "", CentralPath = null, SavedAtUtc = null },
                DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, files, null);
            CollectionAssert.AreEquivalent(new[] { "title", "versionNumber" }, ((JObject)bare["revitDoc"]).Properties().Select(p => p.Name).ToArray(), "沒有路徑資訊就不送這三個欄位（舊行為）");
            Assert.IsEmpty(Schema().Validate(bare));
        }

        [Test]
        public void X02_VeryLongPathIsClippedInsteadOfFailingTheWholeUpload()
        {
            var files = new[] { new BundleFile { Kind = BundleFile.Pdf, Name = "sheets.pdf", Bytes = 10, Sha256 = new string('a', 64) } };
            var doc = new RevitDocInfo { Title = "AR", VersionNumber = "2024", Path = @"C:\" + new string('長', 700) + ".rvt" };
            var manifest = ManifestBuilder.Build(SheetsOnly(null), doc, DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, files, null);
            Assert.AreEqual(ManifestBuilder.MaxSourcePathChars, ((string)manifest["revitDoc"]["path"]).Length);
            Assert.IsEmpty(Schema().Validate(manifest));
        }

        // ── X03 勾選視窗的下拉規則 ─────────────────────────────────────────

        private static ExportSelectionViewModel Vm()
        {
            var vm = new ExportSelectionViewModel(Samples.Snapshot());
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "測試專案" } });
            vm.IsLoggedIn = true;
            vm.SelectedProject = vm.Projects[0];
            vm.SelectedStage = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "只更新圖面";
            return vm;
        }

        [Test]
        public void X03_DropdownIsHiddenWhileTheModelIsExported_AndOnlyHasTheNoneOptionBeforeModelsLoad()
        {
            var vm = Vm();
            Assert.IsTrue(vm.IncludeModel);
            Assert.IsFalse(vm.AttachVisible);
            vm.IncludeModel = false;
            Assert.IsTrue(vm.AttachVisible);
            Assert.AreEqual(1, vm.AttachOptions.Count);
            Assert.IsNull(vm.SelectedAttach.Model);
            Assert.AreEqual(Strings.AttachNone, vm.SelectedAttach.Label);
            Assert.AreEqual(Strings.AttachLoading, vm.AttachNote, "選了專案、清單還沒回來");
        }

        [Test]
        public void X03_DefaultsToTheNewestUsableModel_AndTheNoneOptionIsAlwaysLast()
        {
            var vm = Vm();
            vm.IncludeModel = false;
            vm.SetAttachableModels(new[] { Model("m-new", "new.glb", null), Model("m-mid", "mid.glb", ModelOrigin), Model("m-old", "old.glb", ModelOrigin) });
            Assert.AreEqual(4, vm.AttachOptions.Count);
            Assert.AreEqual("m-mid", vm.SelectedAttach.Model.Id, "最新的沒有原點，預設選第一個能用的");
            Assert.IsNull(vm.AttachOptions.Last().Model);
            StringAssert.Contains(Strings.AttachNoOriginSuffix, vm.AttachOptions[0].Label);
            StringAssert.Contains("v3", vm.AttachOptions[1].Label);
            Assert.AreEqual(string.Empty, vm.AttachNote);
        }

        [Test]
        public void X03_NoModelsInTheProjectGivesAnExplanation()
        {
            var vm = Vm();
            vm.IncludeModel = false;
            vm.SetAttachableModels(new AttachableModel[0]);
            Assert.AreEqual(Strings.AttachEmpty, vm.AttachNote);
            Assert.IsNull(vm.EffectiveAttach);
        }

        [Test]
        public void X03_SummaryRequestAndBlockReasonsFollowTheChoice()
        {
            var vm = Vm();
            vm.SetAttachableModels(new[] { Model("m1", "model.glb", ModelOrigin) });
            vm.IncludeModel = false;
            vm.SelectAllSheets(true);
            Assert.IsTrue(vm.CanUpload, vm.BlockReason);
            StringAssert.Contains("接到已上傳的模型「model.glb」", vm.SummaryText);
            CollectionAssert.AreEqual(new[] { Strings.AttachedNote("model.glb") }, vm.MissingWarnings.ToArray());
            var request = vm.BuildRequest();
            Assert.IsFalse(request.IncludeModel);
            Assert.AreEqual("m1", request.AttachedModel.Id);

            vm.SelectAllSheets(false);
            Assert.IsFalse(vm.CanUpload);
            Assert.AreEqual(Strings.NothingSelected, vm.BlockReason, "不匯出模型又沒勾圖紙：視窗先擋（接模型不改變這條，伺服器也會拒絕）");

            vm.SelectAllSheets(true);
            vm.IncludeModel = true;
            Assert.IsNull(vm.EffectiveAttach, "勾了匯出模型就不接");
            Assert.IsNull(vm.BuildRequest().AttachedModel);
        }

        [Test]
        public void X03_ChoosingAModelWithoutOriginBlocksUploadWithAPlainReason()
        {
            var vm = Vm();
            vm.SetAttachableModels(new[] { Model("m1", "model.glb", null) });
            vm.IncludeModel = false;
            vm.SelectAllSheets(true);
            vm.SelectedAttach = vm.AttachOptions[0];
            Assert.IsFalse(vm.CanUpload);
            Assert.AreEqual(Strings.AttachNoOrigin, vm.BlockReason);
            vm.SelectedAttach = vm.AttachOptions.Last(); // 不接
            Assert.IsTrue(vm.CanUpload);
            Assert.IsNull(vm.BuildRequest().AttachedModel);
        }

        [Test]
        public void X03_SwitchingProjectDropsThePreviousProjectsModels()
        {
            var vm = Vm();
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "A" }, new AddinProject { Id = "p2", Code = "XYZ", Name = "B" } });
            vm.SelectedProject = vm.Projects[0];
            vm.IncludeModel = false;
            vm.SetAttachableModels(new[] { Model("m1", "model.glb", ModelOrigin) });
            Assert.IsNotNull(vm.EffectiveAttach);
            vm.SelectedProject = vm.Projects[1];
            Assert.IsNull(vm.EffectiveAttach, "換專案後，舊專案的模型不能還被選著");
            Assert.AreEqual(1, vm.AttachOptions.Count);
        }

        [Test]
        public void X03_AttachOptionListIsAFreshObjectEachTime_SoWpfRefreshesTheItemsSource()
        {
            var vm = Vm();
            var first = vm.AttachOptions;
            vm.SetAttachableModels(new[] { Model("m1", "model.glb", ModelOrigin) });
            Assert.AreNotSame(first, vm.AttachOptions);
            var second = vm.AttachOptions;
            vm.ClearAttachableModels("x");
            Assert.AreNotSame(second, vm.AttachOptions);
        }

        // ── X04 API 端點與錯誤白話 ──────────────────────────────────────────

        [Test]
        public async Task X04_AttachableModelsAreParsed_IncludingOneWithoutOrigin()
        {
            var server = new FakeTokenServer
            {
                AttachableModels = new List<JObject>
                {
                    new JObject { ["id"] = "m1", ["fileName"] = "model.glb", ["description"] = "基本設計", ["createdAt"] = "2026-10-30T08:00:00.000Z", ["versionLabel"] = "v3", ["originOffsetMeters"] = new JArray(-433.698, -1414.604, 0) },
                    new JObject { ["id"] = "m2", ["fileName"] = "old.glb", ["description"] = "", ["createdAt"] = "2026-10-01T08:00:00.000Z", ["versionLabel"] = null, ["originOffsetMeters"] = null },
                    new JObject { ["fileName"] = "沒有 id 的壞資料" }
                }
            };
            var api = await Harness.LoggedInApiAsync(server);
            var models = await api.GetAttachableModelsAsync("p1", CancellationToken.None);
            Assert.AreEqual(2, models.Count, "沒有 id 的項目略過");
            Assert.AreEqual("m1", models[0].Id);
            Assert.IsTrue(models[0].HasOrigin);
            CollectionAssert.AreEqual(new[] { -433.698, -1414.604, 0 }, models[0].OriginOffsetMeters);
            Assert.AreEqual("v3", models[0].VersionLabel);
            Assert.AreEqual(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), models[0].CreatedAt);
            Assert.IsFalse(models[1].HasOrigin);
            Assert.IsNull(models[1].VersionLabel);
        }

        [Test]
        public async Task X04_OldServerWithoutTheEndpointReportsNotFound()
        {
            var api = await Harness.LoggedInApiAsync(new FakeTokenServer());
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.GetAttachableModelsAsync("p1", CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NotFound, ex.Kind);
        }

        [Test]
        public async Task X04_AttachErrorsHavePlainMessagesAndNeverShowTheCode()
        {
            var notFound = await AddinHttp.ToExceptionAsync(FakeHandler.Error(HttpStatusCode.NotFound, "attach_model_not_found"));
            Assert.AreEqual(AddinErrorKind.Rejected, notFound.Kind, "要接的模型不見了不能套「找不到這個項目或沒有權限」");
            StringAssert.Contains("找不到要接的模型", notFound.Message);
            var mismatch = await AddinHttp.ToExceptionAsync(FakeHandler.Error(HttpStatusCode.BadRequest, "attach_origin_mismatch"));
            Assert.AreEqual(AddinErrorKind.Rejected, mismatch.Kind);
            StringAssert.Contains("跑位", mismatch.Message);
            StringAssert.Contains("整包沒有上傳", mismatch.Message);
            var unknown = await AddinHttp.ToExceptionAsync(FakeHandler.Error(HttpStatusCode.BadRequest, "attach_origin_unknown"));
            StringAssert.Contains("沒有記錄座標原點", unknown.Message);
            foreach (var ex in new[] { notFound, mismatch, unknown }) StringAssert.DoesNotContain("attach_", ex.Message);
        }

        [Test]
        public async Task X04_PlainNotFoundStillUsesTheGenericSentence()
        {
            var ex = await AddinHttp.ToExceptionAsync(FakeHandler.Error(HttpStatusCode.NotFound, "not_found"));
            Assert.AreEqual(AddinErrorKind.NotFound, ex.Kind);
        }

        // ── X05 假伺服器整合：manifest 到 finalize ─────────────────────────────

        [Test]
        public async Task X05_FullFlowSendsTheAttachManifestAndNoGlbToTheServer()
        {
            var server = new FakeTokenServer { PartBytes = 1 << 20 };
            var api = await Harness.LoggedInApiAsync(server);
            var request = SheetsOnly(Model("model-1", "model.glb", ModelOrigin));
            request.ProjectId = "p1";
            var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles"));
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);
            Assert.IsNotNull(result.SetId);
            var sent = server.FinalizeManifests.Last();
            Assert.AreEqual("model-1", (string)sent["attachToModelId"]);
            Assert.IsNull(sent["files"]["glb"], "只傳圖面：沒有上傳 GLB");
            Assert.IsNotNull(sent["files"]["pdf"]);
            CollectionAssert.AreEqual(ModelOrigin, sent["originOffsetMeters"].Select(v => (double)v).ToArray());
            Assert.IsEmpty(Schema().Validate(sent));
        }
    }
}
