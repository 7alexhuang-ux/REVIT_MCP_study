using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// 鎖定「開視窗不卡死」的結構：開視窗只讀一次便宜欄位（沒有任何量測呼叫），
    /// 視口外框這種昂貴資料延後到匯出、只量需要的視口、分批、可取消、量過不重量。
    /// 背景：實機在 55 張圖紙的範例專案開窗凍結 12～15 分鐘（疑為逐視口 Viewport.GetBoxOutline）。
    /// </summary>
    [TestFixture]
    public class OpenWindowCostTests
    {
        /// <summary>計數用的假來源：大型專案（N 張圖紙、每張 M 個視口）。</summary>
        private sealed class CountingSource : IProjectSource
        {
            public int ReadViewsCalls, ReadSheetsCalls, ReadSheetSetsCalls;
            public int SheetCount = 55, ViewportsPerSheet = 3;

            public string Title { get { return "SnowdonLike"; } }
            public string Path { get { return @"C:\models\big.rvt"; } }

            public IList<SourceView> ReadViews()
            {
                ReadViewsCalls++;
                var list = new List<SourceView>();
                for (var s = 0; s < SheetCount; s++)
                    for (var v = 0; v < ViewportsPerSheet; v++)
                        list.Add(new SourceView { UniqueId = "view-" + s + "-" + v, ElementId = 1000 + s * 10 + v, Name = "視圖" + s + "-" + v, Kind = ViewKinds.Plan });
                list.Add(new SourceView { UniqueId = "view-bad-kind", ElementId = 9, Name = "x", Kind = "schedule" });
                return list;
            }

            public IList<SourceSheet> ReadSheets()
            {
                ReadSheetsCalls++;
                var list = new List<SourceSheet>();
                for (var s = 0; s < SheetCount; s++)
                {
                    var sheet = new SourceSheet { UniqueId = "sheet-" + s, ElementId = 5000 + s, Number = "A-" + s, Title = "機密圖名" + s };
                    for (var v = 0; v < ViewportsPerSheet; v++)
                        sheet.Viewports.Add(new SourceViewport { ViewportElementId = 70000 + s * 10 + v, ViewElementId = 1000 + s * 10 + v });
                    // 圖例／明細表視口：對應的視圖不在可匯出清單，要被略過。
                    sheet.Viewports.Add(new SourceViewport { ViewportElementId = 80000 + s, ViewElementId = 9 });
                    list.Add(sheet);
                }
                return list;
            }

            public IList<SourceSheetSet> ReadSheetSets()
            {
                ReadSheetSetsCalls++;
                var good = new SourceSheetSet { Name = "送審圖" };
                good.Members.Add(new SourceSheetSetMember { UniqueId = "sheet-1", IsSheet = true });
                good.Members.Add(new SourceSheetSetMember { UniqueId = "view-2-0", IsSheet = false });
                good.Members.Add(new SourceSheetSetMember { UniqueId = "not-exportable", IsSheet = false });
                return new List<SourceSheetSet> { good, new SourceSheetSet { Name = "<In-Session>" }, new SourceSheetSet { Name = "  " } };
            }
        }

        [Test]
        public void OpeningTheWindowReadsEachCheapListExactlyOnce()
        {
            var source = new CountingSource();
            SnapshotBuilder.Build(source);
            Assert.AreEqual(1, source.ReadViewsCalls);
            Assert.AreEqual(1, source.ReadSheetsCalls);
            Assert.AreEqual(1, source.ReadSheetSetsCalls);
        }

        [Test]
        public void TheSourceContractHasNoWayToMeasureAnything()
        {
            // 型別層級的保證：開視窗用的來源介面只有 Title／Path 與三個清單讀取，沒有任何量測、外框、裁剪框、版面位置的成員。
            var names = typeof(IProjectSource).GetMembers().Select(m => m.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "ReadSheetSets", "ReadSheets", "ReadViews", "get_Path", "get_Title", "Path", "Title" }.OrderBy(n => n).ToArray(), names);
            foreach (var name in names)
                Assert.That(name, Does.Not.Match("(?i)measure|outline|size|crop|bounding|location"), "昂貴資料不可出現在開視窗用的來源介面");
        }

        [Test]
        public void SnapshotViewportsCarryIdsButNoMeasurements()
        {
            var snapshot = SnapshotBuilder.Build(new CountingSource());
            Assert.AreEqual(55, snapshot.Sheets.Count);
            Assert.AreEqual(55 * 3, snapshot.Views.Count, "不可匯出的種類（明細表）不列入");
            foreach (var sheet in snapshot.Sheets)
            {
                Assert.AreEqual(3, sheet.Viewports.Count, "圖例／明細表視口要略過");
                foreach (var viewport in sheet.Viewports)
                {
                    Assert.That(viewport.ViewportElementId, Is.GreaterThan(0));
                    Assert.AreEqual(0, viewport.Width);
                    Assert.AreEqual(0, viewport.Height);
                    Assert.IsFalse(viewport.SizeMeasured);
                }
            }
        }

        [Test]
        public void SheetSetsKeepOnlyExportableMembersAndSkipTemporarySets()
        {
            var snapshot = SnapshotBuilder.Build(new CountingSource());
            Assert.AreEqual(1, snapshot.SheetSets.Count);
            CollectionAssert.AreEqual(new[] { "sheet-1" }, snapshot.SheetSets[0].SheetUniqueIds);
            CollectionAssert.AreEqual(new[] { "view-2-0" }, snapshot.SheetSets[0].ViewUniqueIds);
        }

        [Test]
        public void ViewModelCanBeBuiltFromAnUnmeasuredSnapshot()
        {
            var snapshot = SnapshotBuilder.Build(new CountingSource());
            var vm = new ExportSelectionViewModel(snapshot);
            Assert.AreEqual(55, vm.Sheets.Count);
            vm.SelectAllSheets(true);
            Assert.AreEqual(55, vm.CheckedSheetCount);
            Assert.AreEqual(55 * 3, vm.CheckedViewCount);
        }

        [Test]
        public void TimingLogHasCountsAndMillisecondsButNoNames()
        {
            var lines = new List<string>();
            SnapshotBuilder.Build(new CountingSource(), lines.Add);
            Assert.AreEqual(3, lines.Count, "視圖、圖紙、圖紙集三個階段各一行");
            foreach (var line in lines)
            {
                Assert.That(line, Does.Contain("ms"));
                Assert.That(line, Does.Not.Contain("機密圖名"));
                Assert.That(line, Does.Not.Contain("SnowdonLike"));
                Assert.That(line, Does.Not.Contain("big.rvt"));
            }
            StringAssert.Contains("55 張", lines[1]);
            StringAssert.Contains("165 個", lines[1], "視口數也要記，實機計時才能算每個視口的平均成本");
        }
    }

    [TestFixture]
    public class DeferredViewportMeasureTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("measure"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static ViewportItem Port(string view, long id)
        {
            return new ViewportItem { ViewUniqueId = view, ViewportElementId = id };
        }

        private static ExportRequest Request(params SheetItem[] sheets)
        {
            var request = new ExportRequest { ProjectId = "p", Stage = "SD", Description = "d", IncludeModel = false };
            request.Sheets.AddRange(sheets);
            foreach (var view in sheets.SelectMany(s => s.Viewports).Select(v => v.ViewUniqueId).Distinct())
                request.Views.Add(Samples.View(view, "名" + view, ViewKinds.Plan));
            return request;
        }

        private ExportedBundle Run(ExportRequest request, FakeSteps steps, IExportProgress progress = null, CancellationToken ct = default(CancellationToken))
        {
            return new BundleExporter().Export(request, steps, Path.Combine(_dir, Guid.NewGuid().ToString("N").Substring(0, 6)), progress, ct);
        }

        [Test]
        public void SheetsWithASingleViewportNeverMeasureAnything()
        {
            var steps = new FakeSteps();
            Run(Request(Samples.Sheet("s1", "A-1", "x", Port("v1", 11)), Samples.Sheet("s2", "A-2", "y", Port("v2", 12))), steps);
            CollectionAssert.DoesNotContain(steps.Calls, "measure", "只有一個視口時它必然是主要視口，不必量外框");
        }

        [Test]
        public void OnlyViewportsOfSheetsWithSeveralCheckedViewportsAreMeasuredAndPrimaryIsTheLargest()
        {
            var steps = new FakeSteps();
            steps.ViewportSizes[21] = new[] { 1.0, 1.0 };
            steps.ViewportSizes[22] = new[] { 4.0, 3.0 };
            steps.ViewportSizes[23] = new[] { 2.0, 2.0 };
            var bundle = Run(Request(
                Samples.Sheet("s1", "A-1", "單一視口", Port("v1", 11)),
                Samples.Sheet("s2", "A-2", "多視口", Port("v21", 21), Port("v22", 22), Port("v23", 23))), steps);

            Assert.AreEqual(1, steps.MeasureBatches.Count);
            CollectionAssert.AreEquivalent(new long[] { 21, 22, 23 }, steps.MeasureBatches[0], "單一視口的圖紙(11)不量");
            var viewports = (JArray)bundle.Manifest["sheets"][1]["viewports"];
            Assert.AreEqual("v22", (string)viewports.First(v => (bool)v["isPrimary"])["viewUniqueId"]);
        }

        [Test]
        public void UncheckedViewportsAreNotMeasured()
        {
            var steps = new FakeSteps();
            steps.ViewportSizes[31] = new[] { 1.0, 1.0 };
            steps.ViewportSizes[32] = new[] { 2.0, 2.0 };
            // 勾選視窗在 BuildRequest 時已把未勾的視口從圖紙拿掉：這裡模擬同一張圖紙只剩一個被勾選的視口。
            Run(Request(Samples.Sheet("s1", "A-1", "x", Port("v31", 31))), steps);
            CollectionAssert.DoesNotContain(steps.Calls, "measure");
            Assert.AreEqual(0, steps.MeasureBatches.Count);
        }

        [Test]
        public void MeasuringIsBatchedSoTheUiCanBreathe()
        {
            var steps = new FakeSteps();
            var sheets = new List<SheetItem>();
            long id = 100;
            for (var s = 0; s < 5; s++)
                sheets.Add(Samples.Sheet("s" + s, "A-" + s, "t", Port("v" + id, id++), Port("v" + id, id++), Port("v" + id, id++)));
            var progress = new RecordingExportProgress();
            Run(Request(sheets.ToArray()), steps, progress);

            Assert.AreEqual(15, steps.MeasureBatches.Sum(b => b.Count));
            Assert.That(steps.MeasureBatches.Count, Is.EqualTo(3), "15 個視口、每批 " + BundleExporter.MeasureChunkSize + " 個 → 3 批");
            Assert.That(steps.MeasureBatches.All(b => b.Count <= BundleExporter.MeasureChunkSize));
            Assert.That(progress.Steps.Any(t => t.Contains("讀取視口大小 0/15")), string.Join("|", progress.Steps));
            Assert.That(progress.Steps.Any(t => t.Contains("讀取視口大小 12/15")), string.Join("|", progress.Steps));
            Assert.That(progress.Steps.Any(t => t.Contains("讀取視口大小 15/15")), string.Join("|", progress.Steps));
        }

        [Test]
        public async Task EveryBatchIsItsOwnQueuedCallSoRevitReturnsToItsMessageLoopBetweenThem()
        {
            var steps = new FakeSteps();
            var sheets = new List<SheetItem>();
            long id = 100;
            for (var s = 0; s < 4; s++)
                sheets.Add(Samples.Sheet("s" + s, "A-" + s, "t", Port("v" + id, id++), Port("v" + id, id++), Port("v" + id, id++)));
            var runner = new InlineRunner();
            await new BundleExporter().ExportAsync(Request(sheets.ToArray()), steps, runner, Path.Combine(_dir, "b"), null, CancellationToken.None);
            var measureCalls = steps.Calls.Count(c => c == "measure");
            Assert.AreEqual(2, measureCalls, "12 個視口 → 2 批");
            Assert.AreEqual(1 + measureCalls + 1, runner.Calls, "doc ＋ 每批各一次 ＋ pdf；每次都是一個獨立的 ExternalEvent");
        }

        [Test]
        public void MeasuredViewportsAreNotMeasuredAgainOnTheNextExport()
        {
            var steps = new FakeSteps();
            steps.ViewportSizes[41] = new[] { 1.0, 1.0 };
            steps.ViewportSizes[42] = new[] { 3.0, 3.0 };
            var request = Request(Samples.Sheet("s", "A-1", "x", Port("v41", 41), Port("v42", 42)));
            Run(request, steps);
            Run(request, steps);
            Assert.AreEqual(1, steps.MeasureBatches.Count, "量過的視口（SizeMeasured）重試上傳時不重量");
        }

        [Test]
        public void ViewportsThatCannotBeMeasuredFallBackToTheFirstOne()
        {
            var steps = new FakeSteps(); // 沒有任何尺寸 → 全部 Ok=false
            var bundle = Run(Request(Samples.Sheet("s", "A-1", "x", Port("va", 51), Port("vb", 52))), steps);
            var viewports = (JArray)bundle.Manifest["sheets"][0]["viewports"];
            Assert.IsTrue((bool)viewports[0]["isPrimary"]);
            Assert.IsFalse((bool)viewports[1]["isPrimary"]);
        }

        [Test]
        public void CancelBetweenBatchesStopsBeforeTheNextBatchAndBeforePdf()
        {
            var steps = new FakeSteps();
            var sheets = new List<SheetItem>();
            long id = 100;
            for (var s = 0; s < 5; s++)
                sheets.Add(Samples.Sheet("s" + s, "A-" + s, "t", Port("v" + id, id++), Port("v" + id, id++), Port("v" + id, id++)));
            using (var cts = new CancellationTokenSource())
            {
                steps.OnCall = name => { if (name == "measure") cts.Cancel(); };
                Assert.Throws<OperationCanceledException>(() => Run(Request(sheets.ToArray()), steps, null, cts.Token));
            }
            Assert.AreEqual(1, steps.MeasureBatches.Count, "第一批後取消 → 不再量第二批");
            CollectionAssert.DoesNotContain(steps.Calls, "pdf");
        }

        [Test]
        public void MeasurementsFromTheTestHelpersAreLeftAlone()
        {
            // 既有呼叫端（ViewportElementId = 0）自己帶寬高：不量測、行為不變。
            var steps = new FakeSteps();
            var bundle = Run(Request(Samples.Sheet("s", "A-1", "x", Samples.Viewport("va", 1, 1), Samples.Viewport("vb", 5, 5))), steps);
            CollectionAssert.DoesNotContain(steps.Calls, "measure");
            var viewports = (JArray)bundle.Manifest["sheets"][0]["viewports"];
            Assert.IsTrue((bool)viewports[1]["isPrimary"]);
        }
    }
}
