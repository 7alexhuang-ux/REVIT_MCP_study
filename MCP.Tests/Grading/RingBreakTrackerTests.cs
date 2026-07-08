using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// RingBreakTracker：放坡外圈摺線的斷段追蹤。
    /// 實機教訓（2026-07-08）：兩板相鄰時，大板南邊外圈錨點全落入坡道投影被跳過，
    /// 封圈摺線以一條弦跨過 37m 缺口、從山坡高點斜切板面，SlabShape 沿線抬升成垂直細片鰭。
    /// 規則：任何跳過都要讓摺線斷段，永不跨缺口連弦；缺口段的地形由鄰板邊界摺線把關。
    /// 邊索引 i 代表點 i → 點 i+1 的摺線段；封圈邊索引 = 點數-1（最後點 → 第 0 點）。
    /// </summary>
    [TestFixture]
    public class RingBreakTrackerTests
    {
        [Test]
        public void NoSkips_FullRing_NoBreaks()
        {
            var tracker = new RingBreakTracker();
            for (var i = 0; i < 4; i++) tracker.MarkAdded();
            Assert.That(tracker.Finalize(), Is.Empty);
        }

        [Test]
        public void SkipInMiddle_BreaksEdgeBetweenSurroundingPoints()
        {
            // 點0、點1、（跳過）、點2 → 點1→點2 的邊（索引 1）斷。
            var tracker = new RingBreakTracker();
            tracker.MarkAdded();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            tracker.MarkAdded();
            Assert.That(tracker.Finalize(), Is.EquivalentTo(new[] { 1 }));
        }

        [Test]
        public void SkipAtStart_BreaksClosingEdge()
        {
            // （跳過）、點0、點1、點2 → 封圈邊（索引 2：點2→點0）斷。
            var tracker = new RingBreakTracker();
            tracker.MarkSkipped();
            tracker.MarkAdded();
            tracker.MarkAdded();
            tracker.MarkAdded();
            Assert.That(tracker.Finalize(), Is.EquivalentTo(new[] { 2 }));
        }

        [Test]
        public void SkipAtEnd_BreaksClosingEdge()
        {
            // 點0、點1、點2、（跳過）→ 封圈邊（索引 2）斷。
            var tracker = new RingBreakTracker();
            tracker.MarkAdded();
            tracker.MarkAdded();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            Assert.That(tracker.Finalize(), Is.EquivalentTo(new[] { 2 }));
        }

        [Test]
        public void LongSkippedStretch_SingleBreakOnThatEdge()
        {
            // 鄰板情境：整條南邊連續跳過只斷一條邊，不重複記。
            var tracker = new RingBreakTracker();
            tracker.MarkAdded();
            for (var i = 0; i < 37; i++) tracker.MarkSkipped();
            tracker.MarkAdded();
            tracker.MarkAdded();
            Assert.That(tracker.Finalize(), Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void MultipleGaps_EachBreaksOwnEdge_PlusClosingIfEndsSkipped()
        {
            // 點0、跳、點1、點2、跳、點3、跳 → 邊0（0→1）斷、邊2（2→3）斷、封圈邊3（3→0）斷。
            var tracker = new RingBreakTracker();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            tracker.MarkAdded();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            Assert.That(tracker.Finalize(), Is.EquivalentTo(new[] { 0, 2, 3 }));
        }

        [Test]
        public void NoPoints_NoBreaks()
        {
            var tracker = new RingBreakTracker();
            tracker.MarkSkipped();
            tracker.MarkSkipped();
            Assert.That(tracker.Finalize(), Is.Empty);
        }

        [Test]
        public void SinglePoint_NoEdges_NoBreaks()
        {
            var tracker = new RingBreakTracker();
            tracker.MarkSkipped();
            tracker.MarkAdded();
            tracker.MarkSkipped();
            Assert.That(tracker.Finalize(), Is.Empty);
        }
    }
}
