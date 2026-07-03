using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class TransitionGeometryTests
    {
        [Test]
        public void OffsetTargetZ_帶內線性漸變()
        {
            // 板底 10、地形 16、offset 帶寬 3：帶中點目標 = 13。
            Assert.AreEqual(13.0, TransitionGeometry.OffsetTargetZ(10, 16, 1.5, 3.0), 1e-12);
            Assert.AreEqual(10.0, TransitionGeometry.OffsetTargetZ(10, 16, 0.0, 3.0), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.OffsetTargetZ(10, 16, 3.0, 3.0), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.OffsetTargetZ(10, 16, 99.0, 3.0), 1e-12);
        }

        [Test]
        public void OffsetTargetZ_地形低於板底_向下漸變()
        {
            Assert.AreEqual(8.0, TransitionGeometry.OffsetTargetZ(10, 6, 1.5, 3.0), 1e-12);
        }

        [Test]
        public void SlopeTargetZ_固定坡度爬升並貼合地形()
        {
            // 板底 10、地形 16、坡 1:12：距離 24 處 = 10 + 24/12 = 12；距離 100 處已達地形 16。
            Assert.AreEqual(12.0, TransitionGeometry.SlopeTargetZ(10, 16, 24, 12), 1e-12);
            Assert.AreEqual(16.0, TransitionGeometry.SlopeTargetZ(10, 16, 100, 12), 1e-12);
        }

        [Test]
        public void SlopeTargetZ_地形低於板底_下坡並貼合()
        {
            Assert.AreEqual(8.0, TransitionGeometry.SlopeTargetZ(10, 6, 24, 12), 1e-12);
            Assert.AreEqual(6.0, TransitionGeometry.SlopeTargetZ(10, 6, 100, 12), 1e-12);
        }

        [Test]
        public void SlopeExtensionNeeded_由高差與坡度求水平距離()
        {
            Assert.AreEqual(72.0, TransitionGeometry.SlopeExtensionNeeded(10, 16, 12), 1e-12);
            Assert.AreEqual(48.0, TransitionGeometry.SlopeExtensionNeeded(10, 6, 12), 1e-12);
            Assert.AreEqual(0.0, TransitionGeometry.SlopeExtensionNeeded(10, 10, 12), 1e-12);
        }

        [Test]
        public void 非法參數_繁體中文錯誤()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.OffsetTargetZ(10, 16, 1, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.SlopeTargetZ(10, 16, 1, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TransitionGeometry.SlopeExtensionNeeded(10, 16, -1));
        }
    }
}
