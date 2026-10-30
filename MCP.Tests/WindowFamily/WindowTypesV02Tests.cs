using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.WindowFamily;

namespace MCP.Tests.WindowFamily
{
    /// <summary>
    /// 窗型擴充 v0.2 測試（SPEC §3、ADR D-009／D-010）：
    /// 三軌橫拉含紗窗、垂直上下拉、下懸／內倒、整數軌序（相容舊 outer／inner）、無效規格。
    /// 期望值皆手算，算式寫在各測試註解。框料沿用預設：外框 4.5、中框 4.4、扇框 5、交疊＝扇框寬 5（cm）。
    /// </summary>
    [TestFixture]
    public class WindowTypesV02Tests
    {
        private const double LenTol = 0.1;      // cm
        private const double AreaTol = 0.001;   // m²

        // typeParams 不含 frameDepth：驗證預設框深 7（ADR D-010）
        private const string TypeParamsJson =
            "\"typeParams\":{\"frameOuter\":4.5,\"mullion\":4.4,\"sash\":5,\"glass_mm\":8}";

        private static WindowFamilySpec Spec(string generation, string instanceParams)
        {
            string json = "{\"familyName\":\"UW-test\",\"typeName\":\"T1\"," +
                          TypeParamsJson + "," +
                          "\"generation\":" + generation + "," +
                          "\"instanceParams\":" + instanceParams + "}";
            return WindowFamilySpec.Parse(JObject.Parse(json));
        }

        private static string Cell(params string[] sashes)
        {
            return "{\"sashes\":[" + string.Join(",", sashes) + "]}";
        }

        /// <summary>sashJson 例：S("sliding")、S("sliding", "\"track\":2")。</summary>
        private static string S(string type, string extra = null)
        {
            return extra == null
                ? "{\"type\":\"" + type + "\"}"
                : "{\"type\":\"" + type + "\"," + extra + "}";
        }

        private static WindowFamilySpec OneCell(string cell, double w, double h, double sill)
        {
            string gen = "{\"rows\":1,\"cols\":1,\"cells\":[[" + cell + "]]}";
            string ip = "{\"width\":" + w + ",\"height\":" + h + ",\"sillHeight\":" + sill +
                        ",\"rowRatios\":[100],\"colRatios\":[100]}";
            return Spec(gen, ip);
        }

        private static SashLayout Sash(WindowLayout lay, int index)
        {
            return lay.AllSashes.Single(s => s.Index == index);
        }

        // ────────────────────────────────────────────────
        // 1. 三軌橫拉含紗窗（二玻＋一紗）
        // ────────────────────────────────────────────────

        [Test]
        public void ThreeTrackWithScreen_Layout_ScreenCoversFirstGlassAndTakesInnermostTrack()
        {
            // W180 H150：availW = 180 − 9 = 171，玻璃扇寬 = (171 + 1·5)/2 = 88，availH = 150 − 9 = 141
            var spec = OneCell(Cell(S("sliding"), S("sliding"), S("screen")), 180, 150, 90);
            var lay = WindowLayoutMath.Compute(spec);

            var g0 = Sash(lay, 0);
            var g1 = Sash(lay, 1);
            var sc = Sash(lay, 2);

            Assert.That(g0.Width, Is.EqualTo(88.0).Within(LenTol));
            Assert.That(g1.X, Is.EqualTo(87.5).Within(LenTol));            // 4.5 + (88 − 5)
            // 紗窗寬＝玻璃扇寬，位置罩第 0 片玻璃
            Assert.That(sc.Width, Is.EqualTo(88.0).Within(LenTol));
            Assert.That(sc.X, Is.EqualTo(g0.X).Within(LenTol));
            Assert.That(sc.Height, Is.EqualTo(141.0).Within(LenTol));

            // 未指定 track：玻璃 0／1 交錯，紗窗放最內軌＝玻璃最大軌(1) + 1 = 2；軌數 = 2 + 1 = 3
            Assert.That(new[] { g0.Track, g1.Track, sc.Track }, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(sc.TrackCount, Is.EqualTo(3));
            Assert.That(g0.IsOuterTrack, Is.True);
            Assert.That(g1.IsOuterTrack, Is.False);
        }

        [Test]
        public void ThreeTrackWithScreen_Areas_ScreenExcludedFromGlassDaylightAndOpenable()
        {
            var lay = WindowLayoutMath.Compute(OneCell(Cell(S("sliding"), S("sliding"), S("screen")), 180, 150, 90));
            var d = lay.Derived;

            // 每片玻璃 (88 − 2·5)·(141 − 2·5) = 78·131 = 10218 cm² = 1.0218 m²，兩片 2.0436；紗窗 0
            Assert.That(d.GlassAreaM2, Is.EqualTo(2.0436).Within(AreaTol));
            // sill 90 > 75：全部玻璃在採光下限之上
            Assert.That(d.DaylightAreaM2, Is.EqualTo(2.0436).Within(AreaTol));
            // 橫拉半扇：2.0436 / 2
            Assert.That(d.OpenableAreaM2, Is.EqualTo(1.0218).Within(AreaTol));
            // 紗窗不計入扇數與可開扇數，另計
            Assert.That(d.SashCount, Is.EqualTo(2));
            Assert.That(d.ScreenCount, Is.EqualTo(1));
            Assert.That(d.OpenableCount, Is.EqualTo(2));
            Assert.That(Sash(lay, 2).GlassAreaM2, Is.EqualTo(0.0));
            Assert.That(Sash(lay, 2).IsOpenable, Is.False);
        }

        [Test]
        public void ThreeTrack_DepthPositions_SplitFrameDepthEvenly()
        {
            // 框深 7、3 軌：單軌寬 7/3 = 2.3333；中心偏移（+ 朝室外）= 3.5 − 2.3333·(t + 0.5)
            //   t0 = 2.3333、t1 = 0、t2 = −2.3333
            Assert.That(WindowLayoutMath.TrackCentreOffsetCm(0, 3, 7.0), Is.EqualTo(2.3333).Within(0.001));
            Assert.That(WindowLayoutMath.TrackCentreOffsetCm(1, 3, 7.0), Is.EqualTo(0.0).Within(0.001));
            Assert.That(WindowLayoutMath.TrackCentreOffsetCm(2, 3, 7.0), Is.EqualTo(-2.3333).Within(0.001));
            // 扇框深度 = min(0.35·7 = 2.45, 0.8·7/3 = 1.8667) = 1.8667，三軌不重疊（< 單軌寬 2.3333）
            Assert.That(WindowLayoutMath.SlidingSashDepthCm(3, 7.0), Is.EqualTo(1.8667).Within(0.001));
            // 二軌沿用 v0.1：±0.22·D = ±1.54、扇框深 0.35·D = 2.45
            Assert.That(WindowLayoutMath.TrackCentreOffsetCm(0, 2, 7.0), Is.EqualTo(1.54).Within(0.001));
            Assert.That(WindowLayoutMath.TrackCentreOffsetCm(1, 2, 7.0), Is.EqualTo(-1.54).Within(0.001));
            Assert.That(WindowLayoutMath.SlidingSashDepthCm(2, 7.0), Is.EqualTo(2.45).Within(0.001));
        }

        [Test]
        public void ThreeGlassSliders_DefaultTracksAlternateZeroOne()
        {
            // 三扇橫拉未指定 track：依玻璃扇序交錯 0、1、0（第 1、3 扇相隔一片，不互卡）
            var cell = OneCell(Cell(S("sliding"), S("sliding"), S("sliding")), 270, 150, 90).Cells[0][0];
            var placements = cell.ResolveSlidePlacements();

            Assert.That(placements.Select(p => p.Track).ToArray(), Is.EqualTo(new[] { 0, 1, 0 }));
            Assert.That(cell.TrackCount, Is.EqualTo(2));
        }

        // ────────────────────────────────────────────────
        // 2. 垂直上下拉
        // ────────────────────────────────────────────────

        [Test]
        public void SlidingV_TwoSashes_OverlapAlongVerticalAxis()
        {
            // W90 H150：availW = 81、availH = 141、交疊 5；扇高 = (141 + 5)/2 = 73
            var lay = WindowLayoutMath.Compute(OneCell(Cell(S("sliding_v"), S("sliding_v")), 90, 150, 90));
            var top = Sash(lay, 0);
            var bottom = Sash(lay, 1);

            Assert.That(top.IsVerticalSlide, Is.True);
            Assert.That(top.Width, Is.EqualTo(81.0).Within(LenTol));
            Assert.That(top.Height, Is.EqualTo(73.0).Within(LenTol));
            Assert.That(top.Y, Is.EqualTo(4.5).Within(LenTol));
            Assert.That(bottom.Y, Is.EqualTo(72.5).Within(LenTol));            // 4.5 + (73 − 5)
            // 兩扇含交疊剛好填滿格高：72.5 + 73 = 145.5 = 4.5 + 141
            Assert.That(bottom.Y + bottom.Height, Is.EqualTo(145.5).Within(LenTol));
            Assert.That(top.X, Is.EqualTo(bottom.X).Within(LenTol));
            Assert.That(new[] { top.Track, bottom.Track }, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void SlidingV_Areas_HalfOpenableAndIfcOperation()
        {
            var spec = OneCell(Cell(S("sliding_v"), S("sliding_v")), 90, 150, 90);
            var d = WindowLayoutMath.Compute(spec).Derived;

            // 每片玻璃 (81 − 10)·(73 − 10) = 71·63 = 4473 cm² = 0.4473 m²，兩片 0.8946
            Assert.That(d.GlassAreaM2, Is.EqualTo(0.8946).Within(AreaTol));
            // 上扇玻璃底 167.5 與下扇玻璃底 99.5（FL 以上）都 ≥ 75，無扣減
            Assert.That(d.DaylightAreaM2, Is.EqualTo(0.8946).Within(AreaTol));
            // 垂直上下拉同橫拉以半扇計：0.8946 / 2
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.4473).Within(AreaTol));
            Assert.That(spec.Cells[0][0].Sashes[0].IfcOperationType, Is.EqualTo("SLIDINGVERTICAL"));
            Assert.That(spec.Cells[0][0].Sashes[0].Abbreviation, Is.EqualTo("V"));
            // 兩片上下疊 → 分割線水平
            Assert.That(spec.IfcPartitioningType(), Is.EqualTo("DOUBLE_PANEL_HORIZONTAL"));
        }

        // ────────────────────────────────────────────────
        // 3. 下懸／內倒
        // ────────────────────────────────────────────────

        [Test]
        public void Hopper_Area_FullGlassOpenable_DefaultSwingIn()
        {
            // W60 H60 sill 150：availW = availH = 60 − 9 = 51；扇框 5 → 玻璃 41·41 = 1681 cm² = 0.1681 m²
            var spec = OneCell(Cell(S("hopper")), 60, 60, 150);
            var d = WindowLayoutMath.Compute(spec).Derived;

            Assert.That(d.GlassAreaM2, Is.EqualTo(0.1681).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.EqualTo(0.1681).Within(AreaTol));     // sill 150 > 75
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.1681).Within(AreaTol));     // 下懸整扇計入（同 awning 規則）
            Assert.That(d.OpenableCount, Is.EqualTo(1));

            var sash = spec.Cells[0][0].Sashes[0];
            Assert.That(sash.Swing, Is.EqualTo(SwingDirection.In), "hopper 預設內倒");
            Assert.That(sash.IfcOperationType, Is.EqualTo("BOTTOMHUNG"));
            Assert.That(sash.Abbreviation, Is.EqualTo("H"));
        }

        [Test]
        public void Swing_ExplicitOverridesDefault_AndAppearsInAbbreviation()
        {
            var cell = OneCell(Cell(
                S("hopper", "\"swing\":\"out\""),
                S("casement", "\"hinge\":\"L\",\"swing\":\"in\""),
                S("awning")), 180, 150, 90).Cells[0][0];

            Assert.That(cell.Sashes[0].Swing, Is.EqualTo(SwingDirection.Out));
            Assert.That(cell.Sashes[0].Abbreviation, Is.EqualTo("Ho"));
            Assert.That(cell.Sashes[1].Swing, Is.EqualTo(SwingDirection.In));
            Assert.That(cell.Sashes[1].Abbreviation, Is.EqualTo("CLi"));
            Assert.That(cell.Sashes[2].Swing, Is.EqualTo(SwingDirection.Out), "awning 預設向外");
            Assert.That(cell.Sashes[2].Abbreviation, Is.EqualTo("A"));
        }

        // ────────────────────────────────────────────────
        // 4. 規格相容與預設
        // ────────────────────────────────────────────────

        [Test]
        public void LegacyTrackStrings_OuterInner_MapToZeroOne()
        {
            // v0.1 規格 JSON：track = "outer"／"inner"；舊值反序（inner 在前）→ 第 1 片玻璃在第 1 軌
            var spec = OneCell(Cell(
                S("sliding", "\"track\":\"inner\""),
                S("sliding", "\"track\":\"outer\"")), 180, 150, 90);
            var sashes = spec.Cells[0][0].Sashes;

            Assert.That(sashes[0].Track, Is.EqualTo(1));
            Assert.That(sashes[1].Track, Is.EqualTo(0));

            var lay = WindowLayoutMath.Compute(spec);
            Assert.That(Sash(lay, 0).IsOuterTrack, Is.False);
            Assert.That(Sash(lay, 1).IsOuterTrack, Is.True);

            // 整數與數字字串同義
            var spec2 = OneCell(Cell(
                S("sliding", "\"track\":0"),
                S("sliding", "\"track\":\"1\"")), 180, 150, 90);
            Assert.That(spec2.Cells[0][0].Sashes.Select(s => s.Track).ToArray(), Is.EqualTo(new int?[] { 0, 1 }));
        }

        [Test]
        public void FrameDepthDefault_IsSevenCm_AndVoidDepthStillFifty()
        {
            // ADR D-010：未給 frameDepth → 預設 7（YKK AP YRB-A70）
            var spec = OneCell(Cell(S("fixed")), 120, 150, 90);
            Assert.That(spec.TypeParams.FrameDepth, Is.EqualTo(7.0).Within(0.001));
            // 空心預設深度 = max(50, 7 + 5 + 5 + 20 = 37) = 50
            Assert.That(spec.ResolveVoidDepth(), Is.EqualTo(50.0).Within(0.001));
        }

        [Test]
        public void ScreenSash_IfcNotDefined_AndPartitioningIgnoresScreen()
        {
            var spec = OneCell(Cell(S("sliding"), S("sliding"), S("screen")), 180, 150, 90);

            Assert.That(spec.Cells[0][0].Sashes[2].IfcOperationType, Is.EqualTo("NOTDEFINED"), "紗窗不輸出玻璃扇操作屬性");
            Assert.That(spec.Cells[0][0].Sashes[2].Abbreviation, Is.EqualTo("SC"));
            Assert.That(spec.DescribeOperations(), Is.EqualTo("R1: S,S,SC"));
            // 紗窗不計面板數：仍是二面板橫拉 → DOUBLE_PANEL_VERTICAL（非 TRIPLE）
            Assert.That(spec.IfcPartitioningType(), Is.EqualTo("DOUBLE_PANEL_VERTICAL"));
        }

        // ────────────────────────────────────────────────
        // 5. 無效規格
        // ────────────────────────────────────────────────

        [Test]
        public void Invalid_SlidingClassMixedWithOpeningTypes_Throws()
        {
            foreach (var cell in new[]
            {
                Cell(S("sliding"), S("hopper")),
                Cell(S("sliding_v"), S("casement", "\"hinge\":\"L\"")),
                Cell(S("sliding"), S("sliding"), S("fixed", null)),
                Cell(S("sliding"), S("screen"), S("awning"))
            })
            {
                var ex = Assert.Throws<WindowSpecException>(() => OneCell(cell, 180, 150, 90), cell);
                Assert.That(ex.Message, Does.Contain("混用"), cell);
            }
        }

        [Test]
        public void Invalid_SlidingAndSlidingVInSameCell_Throws()
        {
            var ex = Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding"), S("sliding_v")), 180, 150, 90));
            Assert.That(ex.Message, Does.Contain("垂直上下拉"));
            Assert.That(ex.Message, Does.Contain("不可同格"));
        }

        [Test]
        public void Invalid_ScreenAlone_Throws()
        {
            var ex = Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("screen")), 180, 150, 90));
            Assert.That(ex.Message, Does.Contain("紗窗不能單獨成格"));

            // 兩片紗窗也沒有玻璃扇 → 同樣無效
            var ex2 = Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("screen"), S("screen")), 180, 150, 90));
            Assert.That(ex2.Message, Does.Contain("紗窗不能單獨成格"));
        }

        [Test]
        public void Invalid_AdjacentSashesOnSameTrack_Throws()
        {
            // 兩片玻璃都在第 0 軌且位置相鄰（交疊處）→ 軌道互卡
            var ex = Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding", "\"track\":0"), S("sliding", "\"track\":0")), 180, 150, 90));
            Assert.That(ex.Message, Does.Contain("軌道會互卡"));

            // 紗窗指定到所罩玻璃扇的同一軌 → 同樣衝突
            var ex2 = Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding"), S("sliding"), S("screen", "\"track\":0")), 180, 150, 90));
            Assert.That(ex2.Message, Does.Contain("軌道會互卡"));
        }

        [Test]
        public void Invalid_TrackAndSwingValues_Throw()
        {
            Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding", "\"track\":\"middle\""), S("sliding")), 180, 150, 90));
            Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding", "\"track\":-1"), S("sliding")), 180, 150, 90));
            Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding", "\"track\":1.5"), S("sliding")), 180, 150, 90));
            Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("sliding", "\"track\":9"), S("sliding")), 180, 150, 90));
            Assert.Throws<WindowSpecException>(
                () => OneCell(Cell(S("hopper", "\"swing\":\"up\"")), 60, 60, 150));
        }
    }
}
