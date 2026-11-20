using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class EarthworkLedgerTests
    {
        [Test]
        public void Compute_三本帳數值正確()
        {
            var ledger = EarthworkLedger.Compute(
                cutCubicMeters: 100.0,
                fillCubicMeters: 80.0,
                looseFactor: 1.25,
                compactionFactor: 0.9);

            Assert.AreEqual(1.25, ledger.LooseFactor, 1e-12);
            Assert.AreEqual(0.9, ledger.CompactionFactor, 1e-12);
            Assert.AreEqual(125.0, ledger.HaulVolumeLooseCubicMeters, 1e-9);
            Assert.AreEqual(80.0 / 0.9, ledger.RequiredBankForFillCubicMeters, 1e-9);
            // 實質淨土方（同既有號誌：負值＝餘土）：80/0.9 − 100 ≈ −11.11
            Assert.AreEqual((80.0 / 0.9) - 100.0, ledger.EffectiveNetCubicMeters, 1e-9);
        }

        [TestCase(0.0, 0.9)]
        [TestCase(-1.0, 0.9)]
        [TestCase(1.25, 0.0)]
        [TestCase(1.25, -0.5)]
        public void Compute_係數非正_繁體中文錯誤(double loose, double compaction)
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => EarthworkLedger.Compute(100, 80, loose, compaction));
            StringAssert.Contains("係數", error.Message);
        }
    }
}
