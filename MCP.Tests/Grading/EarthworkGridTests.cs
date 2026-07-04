using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class EarthworkGridTests
    {
        [Test]
        public void Compute_混合格_挖填分帳且略過無值格()
        {
            var cells = new[]
            {
                new GridCell(0, 0, 2.0),   // 挖 2m
                new GridCell(0, 1, -1.0),  // 填 1m
                new GridCell(1, 0, null),  // 格中心無地形
                new GridCell(1, 1, 0.0)    // 不挖不填
            };

            var result = EarthworkGrid.Compute(cells, cellAreaSquareMeters: 100.0);

            Assert.AreEqual(200.0, result.CutCubicMeters, 1e-9);
            Assert.AreEqual(100.0, result.FillCubicMeters, 1e-9);
            Assert.AreEqual(3, result.SampledCellCount);
        }

        [Test]
        public void Compute_格面積非正_繁體中文錯誤()
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => EarthworkGrid.Compute(new[] { new GridCell(0, 0, 1.0) }, 0));
            StringAssert.Contains("面積", error.Message);
        }
    }
}
