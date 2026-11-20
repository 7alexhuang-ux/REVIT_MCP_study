using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class TransitionSettingsTests
    {
        private static GradingRequest BaseRequest(string mode) => new GradingRequest
        {
            ToposolidId = 6278563,
            FloorIds = new[] { 7512796L },
            Mode = mode,
            TargetFace = "bottom"
        };

        [Test]
        public void FromRequest_footprint_only_不帶銜接參數()
        {
            var settings = TransitionSettings.FromRequest(BaseRequest("footprint_only"));
            Assert.AreEqual(GradingMode.FootprintOnly, settings.Mode);
        }

        [Test]
        public void FromRequest_footprint_only_夾帶銜接參數_拒絕()
        {
            var request = BaseRequest("footprint_only");
            request.OffsetDistanceMeters = 3.0;
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(request));
            StringAssert.Contains("offsetDistance", error.Message);
        }

        [Test]
        public void FromRequest_offset_transition_必須提供正的offset()
        {
            var request = BaseRequest("offset_transition");
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(request));
            StringAssert.Contains("offsetDistance", error.Message);

            request.OffsetDistanceMeters = 3.0;
            var settings = TransitionSettings.FromRequest(request);
            Assert.AreEqual(GradingMode.OffsetTransition, settings.Mode);
            Assert.AreEqual(3.0, settings.OffsetDistanceMeters, 1e-12);
        }

        [Test]
        public void FromRequest_slope_transition_解析坡度與預設上限()
        {
            var request = BaseRequest("slope_transition");
            request.SlopeRatio = "1:12";
            var settings = TransitionSettings.FromRequest(request);
            Assert.AreEqual(GradingMode.SlopeTransition, settings.Mode);
            Assert.AreEqual(12.0, settings.RunPerRise, 1e-12);
            Assert.AreEqual(TransitionSettings.DefaultMaxExtensionMeters, settings.MaxExtensionMeters, 1e-12);
        }

        [Test]
        public void FromRequest_slope_transition_缺坡度_拒絕()
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(BaseRequest("slope_transition")));
            StringAssert.Contains("slopeRatio", error.Message);
        }

        [Test]
        public void FromRequest_未知模式_拒絕()
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.FromRequest(BaseRequest("banana")));
            StringAssert.Contains("mode", error.Message);
        }

        [TestCase("1:12", 12.0)]
        [TestCase("1:1.5", 1.5)]
        [TestCase(" 1 : 8 ", 8.0)]
        public void ParseSlopeRatio_合法格式(string text, double expected)
        {
            Assert.AreEqual(expected, TransitionSettings.ParseSlopeRatio(text), 1e-12);
        }

        [TestCase("12:1")]
        [TestCase("1:0")]
        [TestCase("1:-3")]
        [TestCase("abc")]
        [TestCase("")]
        public void ParseSlopeRatio_非法格式_繁體中文錯誤(string text)
        {
            var error = Assert.Throws<System.ArgumentException>(
                () => TransitionSettings.ParseSlopeRatio(text));
            StringAssert.Contains("1:n", error.Message);
        }
    }
}
