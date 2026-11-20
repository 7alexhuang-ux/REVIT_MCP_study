using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class GradingRequestTests
    {
        [Test]
        public void Validate_合法試跑參數_不拋出例外()
        {
            Assert.DoesNotThrow(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L, 7512816L },
                Mode = "footprint_only",
                TargetFace = "bottom"
            }.Validate());
        }

        [Test]
        public void Validate_樓板清單為空_回報繁體中文錯誤()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new long[0],
                Mode = "footprint_only",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("至少一片樓板", error.Message);
        }

        [Test]
        public void Validate_slope_transition_缺坡度_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "slope_transition",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("slopeRatio", error.Message);
        }

        [Test]
        public void Validate_未知模式_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "banana",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("mode", error.Message);
        }

        [Test]
        public void Validate_鬆實方係數成對且為正_不拋出例外()
        {
            Assert.DoesNotThrow(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "footprint_only",
                TargetFace = "bottom",
                LooseFactor = 1.25,
                CompactionFactor = 0.9
            }.Validate());
        }

        [Test]
        public void Validate_鬆實方係數只給一個_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "footprint_only",
                TargetFace = "bottom",
                LooseFactor = 1.25
            }.Validate());
            StringAssert.Contains("成對", error.Message);
        }

        [Test]
        public void Validate_offset_transition_合法參數_不拋出例外()
        {
            Assert.DoesNotThrow(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "offset_transition",
                TargetFace = "bottom",
                OffsetDistanceMeters = 3.0
            }.Validate());
        }

        [Test]
        public void Validate_要求更新既有結果_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "footprint_only",
                TargetFace = "bottom",
                UpdateExisting = true
            }.Validate());
            StringAssert.Contains("updateExisting=true", error.Message);
        }
    }
}
