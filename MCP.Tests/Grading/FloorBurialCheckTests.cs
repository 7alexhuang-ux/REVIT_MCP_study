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

        // ---- CapTargetZ：不覆蓋政策（使用者裁決 2026-07-08：不要只警告，一律不覆蓋樓板）----

        [Test]
        public void CapTargetZ_ExposedFloor_FieldTargetAboveBottom_CappedToBottom()
        {
            // 坡道情境：原地形 0.5（外露）、板底 2.43、1:12 場目標 2.81 → 壓到板底。
            Assert.That(
                FloorBurialCheck.CapTargetZ(
                    fieldTargetZ: 2.81, originalTerrainZ: 0.5, floorBottomZ: 2.43, tolerance: Tolerance),
                Is.EqualTo(2.43));
        }

        [Test]
        public void CapTargetZ_ExposedFloor_FieldTargetBelowBottom_Unchanged()
        {
            Assert.That(
                FloorBurialCheck.CapTargetZ(
                    fieldTargetZ: 1.2, originalTerrainZ: 0.5, floorBottomZ: 2.43, tolerance: Tolerance),
                Is.EqualTo(1.2));
        }

        [Test]
        public void CapTargetZ_FloorAlreadyEmbeddedInOriginalTerrain_NotExcavated()
        {
            // 地下室情境：原地形 3.0 已高於板底 0.8 → 不壓蓋（否則會把既有覆土挖開）。
            Assert.That(
                FloorBurialCheck.CapTargetZ(
                    fieldTargetZ: 2.5, originalTerrainZ: 3.0, floorBottomZ: 0.8, tolerance: Tolerance),
                Is.EqualTo(2.5));
        }

        [Test]
        public void CapTargetZ_SlabOnGrade_BottomTouchingTerrain_StillCapped()
        {
            // 貼地板：原地形≈板底（50mm 門檻內視為外露）→ 壓蓋生效，填坡不得蓋過。
            Assert.That(
                FloorBurialCheck.CapTargetZ(
                    fieldTargetZ: 1.5, originalTerrainZ: 1.02, floorBottomZ: 1.0, tolerance: Tolerance),
                Is.EqualTo(1.0));
        }

        [Test]
        public void CapTargetZ_NegativeTolerance_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                FloorBurialCheck.CapTargetZ(1.0, 0.0, 2.0, -0.01));
        }

        // ---- ShouldAutoInclude：連續放坡（使用者裁決 2026-07-08：壓蓋板緣不要高差）----
        // 會被放坡覆蓋的「外露」樓板 → 自動納入控制樓板，地形沿板底通過、板緣接續放坡。

        [Test]
        public void ShouldAutoInclude_ExposedFloorCoveredByField_True()
        {
            // 坡道情境：板底 2.43、原地形 0.5（外露）、距離場目標 2.81（會覆蓋）。
            Assert.That(
                FloorBurialCheck.ShouldAutoInclude(
                    new[] { (FloorBottomZ: 2.43, OriginalZ: 0.5, FieldTargetZ: 2.81) }, Tolerance),
                Is.True);
        }

        [Test]
        public void ShouldAutoInclude_FieldStaysBelowFloorBottom_False()
        {
            // 高架板情境：距離場目標低於板底 → 不會覆蓋，也絕不可為了貼板底把地形抬高。
            Assert.That(
                FloorBurialCheck.ShouldAutoInclude(
                    new[] { (FloorBottomZ: 5.0, OriginalZ: 0.5, FieldTargetZ: 2.0) }, Tolerance),
                Is.False);
        }

        [Test]
        public void ShouldAutoInclude_FloorAlreadyEmbedded_False()
        {
            // 地下室情境：原地形已高於板底 → 屬既有覆土，不納入（避免整地把它挖開）。
            Assert.That(
                FloorBurialCheck.ShouldAutoInclude(
                    new[] { (FloorBottomZ: 0.8, OriginalZ: 3.0, FieldTargetZ: 2.5) }, Tolerance),
                Is.False);
        }

        [Test]
        public void ShouldAutoInclude_AnyExposedCoveredSample_True()
        {
            var samples = new[]
            {
                (FloorBottomZ: 5.0, OriginalZ: 0.5, FieldTargetZ: 2.0),   // 高架段：不覆蓋
                (FloorBottomZ: 2.43, OriginalZ: 0.5, FieldTargetZ: 2.81), // 覆蓋段：觸發
            };
            Assert.That(FloorBurialCheck.ShouldAutoInclude(samples, Tolerance), Is.True);
        }

        [Test]
        public void ShouldAutoInclude_EmptySamples_False()
        {
            Assert.That(
                FloorBurialCheck.ShouldAutoInclude(
                    System.Array.Empty<(double, double, double)>(), Tolerance),
                Is.False);
        }
    }
}
