using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// FloorBurialCheck.Evaluate：非控制樓板「新增覆蓋」判定。
    /// 實機教訓（2026-07-07）：放坡只納入大板時，1:12 緩填坡合法蓋過鄰接坡道板
    /// （非控制樓板），Revit 的 overlap 警告被自動消化不夠醒目——需要明確告警。
    /// 語意：只抓「整地造成的新增覆蓋」；原本就埋在原地形下的樓板（如地下室板）不告警。
    /// </summary>
    [TestFixture]
    public class FloorBurialCheckTests
    {
        private const double Tolerance = 0.05;

        [Test]
        public void Evaluate_DesignCoversPreviouslyExposedFloor_NewlyBuried()
        {
            // 坡道情境：板頂 2.65、原地形 0.5（外露）、設計地形 2.81（蓋過）。
            var result = FloorBurialCheck.Evaluate(
                new[] { (FloorTopZ: 2.65, OriginalZ: 0.5, DesignZ: 2.81) }, Tolerance);
            Assert.That(result.NewlyBuried, Is.True);
            Assert.That(result.MaxNewDepth, Is.EqualTo(0.16).Within(1e-9));
        }

        [Test]
        public void Evaluate_FloorAlreadyUnderOriginalTerrain_Silent()
        {
            // 地下室情境：整地前就被原地形蓋住，屬刻意設計，不告警。
            var result = FloorBurialCheck.Evaluate(
                new[] { (FloorTopZ: 1.0, OriginalZ: 3.0, DesignZ: 2.5) }, Tolerance);
            Assert.That(result.NewlyBuried, Is.False);
            Assert.That(result.MaxNewDepth, Is.EqualTo(0.0));
        }

        [Test]
        public void Evaluate_DesignBelowFloorTop_Silent()
        {
            var result = FloorBurialCheck.Evaluate(
                new[] { (FloorTopZ: 3.22, OriginalZ: 0.5, DesignZ: 3.0) }, Tolerance);
            Assert.That(result.NewlyBuried, Is.False);
        }

        [Test]
        public void Evaluate_CoverWithinTolerance_Silent()
        {
            var result = FloorBurialCheck.Evaluate(
                new[] { (FloorTopZ: 3.0, OriginalZ: 0.5, DesignZ: 3.04) }, Tolerance);
            Assert.That(result.NewlyBuried, Is.False);
        }

        [Test]
        public void Evaluate_MixedSamples_ReturnsMaxNewDepthAcrossNewlyBuriedOnly()
        {
            var samples = new[]
            {
                (FloorTopZ: 2.0, OriginalZ: 0.0, DesignZ: 2.3),  // 新增覆蓋 0.3
                (FloorTopZ: 2.0, OriginalZ: 0.0, DesignZ: 2.7),  // 新增覆蓋 0.7
                (FloorTopZ: 1.0, OriginalZ: 6.5, DesignZ: 6.0),  // 原本就埋 5.0：不計
                (FloorTopZ: 3.0, OriginalZ: 0.0, DesignZ: 1.0),  // 未覆蓋
            };
            var result = FloorBurialCheck.Evaluate(samples, Tolerance);
            Assert.That(result.NewlyBuried, Is.True);
            Assert.That(result.MaxNewDepth, Is.EqualTo(0.7).Within(1e-9));
        }

        [Test]
        public void Evaluate_EmptySamples_Silent()
        {
            var result = FloorBurialCheck.Evaluate(
                System.Array.Empty<(double, double, double)>(), Tolerance);
            Assert.That(result.NewlyBuried, Is.False);
            Assert.That(result.MaxNewDepth, Is.EqualTo(0.0));
        }

        [Test]
        public void Evaluate_NegativeTolerance_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                FloorBurialCheck.Evaluate(
                    new[] { (FloorTopZ: 1.0, OriginalZ: 0.0, DesignZ: 2.0) }, -0.01));
        }
    }
}
