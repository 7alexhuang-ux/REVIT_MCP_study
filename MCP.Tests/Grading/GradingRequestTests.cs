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
        public void Validate_非本次模式_拒絕執行()
        {
            var error = Assert.Throws<System.ArgumentException>(() => new GradingRequest
            {
                ToposolidId = 6278563,
                FloorIds = new[] { 7512796L },
                Mode = "slope_transition",
                TargetFace = "bottom"
            }.Validate());
            StringAssert.Contains("footprint_only", error.Message);
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
