using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class GradingSchemeRecordTests
    {
        [Test]
        public void 記錄模型_預設SchemaVersion為6()
        {
            // v2 加鬆實方 Ledger；v3 加方案視圖/截圖與樓板高程偏移（還原依據）；
            // v4 加邊坡率政策快照 EdgePolicies（V3-S2 逐邊坡率）。
            // v5 加樓板幾何快照 FloorSnapshots，避免後續修改同一片樓板污染舊方案比較圖。
            // v6 加「產生分析圖」狀態時間戳與其建立的視圖 ID（AnalysisGeneratedAt 等）。
            var record = new GradingSchemeRecord();
            Assert.AreEqual(6, record.SchemaVersion);
            Assert.IsNull(record.Ledger);
            Assert.IsNull(record.SchemeViewId);
            Assert.IsNull(record.ScreenshotPath);
            Assert.IsNull(record.EdgePolicies);
            Assert.IsNull(record.FloorSnapshots);
            Assert.IsNull(record.AnalysisGeneratedAt);
            Assert.IsNull(record.AnalysisCutfillViewId);
            Assert.IsNull(record.AnalysisSlopeViewId);
            Assert.IsNull(record.AnalysisSectionViewId);
            Assert.IsNull(record.AnalysisSectionIsoViewId);
            Assert.IsNull(new FloorMetric().HeightOffsetMeters);
        }

        [Test]
        public void 比較圖聚焦範圍_使用樓板快照而非目前樓板狀態()
        {
            var schemeSnapshot = new FloorGeometrySnapshot
            {
                FloorId = 101,
                BottomZMinMeters = 12.0,
                BottomZMaxMeters = 13.0,
                OuterLoopMeters = new[]
                {
                    new FloorSnapshotPoint { XMeters = 10.0, YMeters = 20.0 },
                    new FloorSnapshotPoint { XMeters = 30.0, YMeters = 20.0 },
                    new FloorSnapshotPoint { XMeters = 30.0, YMeters = 35.0 },
                    new FloorSnapshotPoint { XMeters = 10.0, YMeters = 35.0 },
                }
            };

            var focus = GradingComparisonFocusBounds.FromFloorSnapshots(
                new[] { schemeSnapshot },
                horizontalPaddingMeters: 5.0,
                minimumPlanSpanMeters: 18.0,
                verticalPaddingMeters: 2.0,
                minimumHeightMeters: 6.0);

            Assert.That(focus.MinXMeters, Is.EqualTo(5.0).Within(1e-9));
            Assert.That(focus.MaxXMeters, Is.EqualTo(35.0).Within(1e-9));
            Assert.That(focus.MinYMeters, Is.EqualTo(15.0).Within(1e-9));
            Assert.That(focus.MaxYMeters, Is.EqualTo(40.0).Within(1e-9));
            Assert.That(focus.MinZMeters, Is.EqualTo(9.5).Within(1e-9));
            Assert.That(focus.MaxZMeters, Is.EqualTo(15.5).Within(1e-9));
            Assert.That(focus.CenterXMeters, Is.EqualTo(20.0).Within(1e-9));
            Assert.That(focus.CenterYMeters, Is.EqualTo(27.5).Within(1e-9));
            Assert.That(focus.CenterZMeters, Is.EqualTo(12.5).Within(1e-9));
        }

        [Test]
        public void MaxCutDepth_混合挖填_取挖方最大值()
        {
            var pairs = new[]
            {
                (OriginalZ: 10.0, TargetZ: 7.0),   // 挖 3
                (OriginalZ: 10.0, TargetZ: 12.0),  // 填 2
                (OriginalZ: 10.0, TargetZ: 4.5)    // 挖 5.5
            };
            Assert.AreEqual(5.5, SchemeMetrics.MaxCutDepth(pairs), 1e-12);
        }

        [Test]
        public void MaxFillHeight_混合挖填_取填方最大值()
        {
            var pairs = new[]
            {
                (OriginalZ: 10.0, TargetZ: 7.0),   // 挖 3
                (OriginalZ: 10.0, TargetZ: 12.0),  // 填 2
                (OriginalZ: 10.0, TargetZ: 13.75)  // 填 3.75
            };
            Assert.AreEqual(3.75, SchemeMetrics.MaxFillHeight(pairs), 1e-12);
        }

        [Test]
        public void 指標_空清單_回傳零()
        {
            var empty = new (double OriginalZ, double TargetZ)[0];
            Assert.AreEqual(0.0, SchemeMetrics.MaxCutDepth(empty), 1e-12);
            Assert.AreEqual(0.0, SchemeMetrics.MaxFillHeight(empty), 1e-12);
        }
    }
}
