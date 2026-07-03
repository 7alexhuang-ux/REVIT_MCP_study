using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class GradingSchemeRecordTests
    {
        [Test]
        public void 記錄模型_預設SchemaVersion為2()
        {
            // v2：新增鬆實方三本帳 Ledger（可為 null）。
            var record = new GradingSchemeRecord();
            Assert.AreEqual(2, record.SchemaVersion);
            Assert.IsNull(record.Ledger);
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
