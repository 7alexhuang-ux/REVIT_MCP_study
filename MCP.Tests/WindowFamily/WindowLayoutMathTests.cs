using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.WindowFamily;

namespace MCP.Tests.WindowFamily
{
    /// <summary>
    /// 佈局數學驗收測試。
    /// 期望值來源：docs/universal-window/configurator-demo.html 的 layout()／summary()，
    /// 以 node 直接執行該段邏輯算出（六個 preset），見報告。誤差容許 0.1 cm／0.001 m²。
    /// </summary>
    [TestFixture]
    public class WindowLayoutMathTests
    {
        private const double LenTol = 0.1;      // cm
        private const double AreaTol = 0.001;   // m²

        // ────────────────────────────────────────────────
        // 六個 demo preset 的規格 JSON
        // ────────────────────────────────────────────────

        private const string TypeParamsJson =
            "\"typeParams\":{\"frameOuter\":4.5,\"mullion\":4.4,\"sash\":5,\"frameDepth\":8,\"glass_mm\":8}";

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

        private static string S(string type, string hinge = null)
        {
            return hinge == null
                ? "{\"type\":\"" + type + "\"}"
                : "{\"type\":\"" + type + "\",\"hinge\":\"" + hinge + "\"}";
        }

        /// <summary>preset ufl：上固定下外推 240×180 sill 90 rows 35/65 cols 50/50。</summary>
        private static WindowFamilySpec Ufl()
        {
            string gen = "{\"rows\":2,\"cols\":2,\"cells\":[" +
                         "[" + Cell(S("fixed")) + "," + Cell(S("fixed")) + "]," +
                         "[" + Cell(S("casement", "L"), S("casement", "R")) + "," + Cell(S("casement", "L"), S("casement", "R")) + "]" +
                         "]}";
            string ip = "{\"width\":240,\"height\":180,\"sillHeight\":90,\"rowRatios\":[35,65],\"colRatios\":[50,50]}";
            return Spec(gen, ip);
        }

        /// <summary>preset sl：橫拉窗 180×150 sill 90，單格兩扇橫拉。</summary>
        private static WindowFamilySpec Sliding()
        {
            string gen = "{\"rows\":1,\"cols\":1,\"cells\":[[" + Cell(S("sliding"), S("sliding")) + "]]}";
            string ip = "{\"width\":180,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[100]}";
            return Spec(gen, ip);
        }

        /// <summary>preset fx：固定窗 120×150 sill 90。</summary>
        private static WindowFamilySpec FixedWindow()
        {
            string gen = "{\"rows\":1,\"cols\":1,\"cells\":[[" + Cell(S("fixed")) + "]]}";
            string ip = "{\"width\":120,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[100]}";
            return Spec(gen, ip);
        }

        /// <summary>preset up：上窗（上懸）180×210 sill 60 rows 30/70。</summary>
        private static WindowFamilySpec AwningTop()
        {
            string gen = "{\"rows\":2,\"cols\":1,\"cells\":[[" + Cell(S("awning")) + "],[" + Cell(S("fixed")) + "]]}";
            string ip = "{\"width\":180,\"height\":210,\"sillHeight\":60,\"rowRatios\":[30,70],\"colRatios\":[100]}";
            return Spec(gen, ip);
        }

        /// <summary>preset dn：下窗（橫拉）180×210 sill 60 rows 40/60。</summary>
        private static WindowFamilySpec SlidingBottom()
        {
            string gen = "{\"rows\":2,\"cols\":1,\"cells\":[[" + Cell(S("fixed")) + "],[" + Cell(S("sliding"), S("sliding")) + "]]}";
            string ip = "{\"width\":180,\"height\":210,\"sillHeight\":60,\"rowRatios\":[40,60],\"colRatios\":[100]}";
            return Spec(gen, ip);
        }

        /// <summary>preset tri：三樘組合 360×180 sill 90 cols 30/40/30。</summary>
        private static WindowFamilySpec ThreeBay()
        {
            string gen = "{\"rows\":1,\"cols\":3,\"cells\":[[" +
                         Cell(S("fixed")) + "," + Cell(S("sliding"), S("sliding")) + "," + Cell(S("fixed")) +
                         "]]}";
            string ip = "{\"width\":360,\"height\":180,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[30,40,30]}";
            return Spec(gen, ip);
        }

        private static SashLayout Sash(WindowLayout lay, int row, int col, int index)
        {
            return lay.AllSashes.Single(s => s.Row == row && s.Col == col && s.Index == index);
        }

        // ────────────────────────────────────────────────
        // 1. ufl 上固定下外推
        // ────────────────────────────────────────────────

        [Test]
        public void Ufl_GridSizes_MatchDemo()
        {
            var lay = WindowLayoutMath.Compute(Ufl());

            Assert.That(lay.ColWidths, Is.EqualTo(new[] { 113.30, 113.30 }).Within(LenTol));
            Assert.That(lay.RowHeights, Is.EqualTo(new[] { 58.31, 108.29 }).Within(LenTol));
            Assert.That(lay.AvailWidth, Is.EqualTo(226.6).Within(LenTol));
            Assert.That(lay.AvailHeight, Is.EqualTo(166.6).Within(LenTol));
        }

        [Test]
        public void Ufl_SashRectangles_MatchDemo()
        {
            var lay = WindowLayoutMath.Compute(Ufl());

            var topLeft = Sash(lay, 0, 0, 0);
            Assert.That(topLeft.X, Is.EqualTo(4.5).Within(LenTol));
            Assert.That(topLeft.Y, Is.EqualTo(4.5).Within(LenTol));
            Assert.That(topLeft.Width, Is.EqualTo(113.30).Within(LenTol));
            Assert.That(topLeft.Height, Is.EqualTo(58.31).Within(LenTol));
            // 固定扇壓條＝扇框寬 × 0.5
            Assert.That(topLeft.SashFrameWidth, Is.EqualTo(2.5).Within(0.001));

            var bottomLeftRight = Sash(lay, 1, 0, 1);
            Assert.That(bottomLeftRight.X, Is.EqualTo(61.15).Within(LenTol));
            Assert.That(bottomLeftRight.Y, Is.EqualTo(67.21).Within(LenTol));
            Assert.That(bottomLeftRight.Width, Is.EqualTo(56.65).Within(LenTol));
            Assert.That(bottomLeftRight.Height, Is.EqualTo(108.29).Within(LenTol));
            Assert.That(bottomLeftRight.Hinge, Is.EqualTo(HingeSide.Right));

            var secondBayTop = Sash(lay, 0, 1, 0);
            Assert.That(secondBayTop.X, Is.EqualTo(122.20).Within(LenTol));
        }

        [Test]
        public void Ufl_Derived_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(Ufl());
            var d = lay.Derived;

            Assert.That(d.SashCount, Is.EqualTo(6));
            Assert.That(d.OpenableCount, Is.EqualTo(4));
            Assert.That(d.GlassAreaM2, Is.EqualTo(2.988786).Within(AreaTol));
            // sill 90 > 75，全部玻璃都在採光下限之上
            Assert.That(d.DaylightAreaM2, Is.EqualTo(2.988786).Within(AreaTol));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(1.834091).Within(AreaTol));
            Assert.That(d.MoWidth, Is.EqualTo(245.0).Within(LenTol));
            Assert.That(d.MoHeight, Is.EqualTo(185.0).Within(LenTol));
        }

        // ────────────────────────────────────────────────
        // 2. sl 橫拉窗
        // ────────────────────────────────────────────────

        [Test]
        public void Sliding_OverlapAndTracks_MatchDemo()
        {
            var spec = Sliding();
            var lay = WindowLayoutMath.Compute(spec);

            // ADR D-005：交疊預設＝扇框寬 5 cm
            Assert.That(lay.Overlap, Is.EqualTo(5.0).Within(0.001));
            Assert.That(lay.ColWidths[0], Is.EqualTo(171.0).Within(LenTol));

            // 扇寬 = (格寬 + (n-1)*交疊)/n = (171 + 5)/2 = 88
            var outerSash = Sash(lay, 0, 0, 0);
            var innerSash = Sash(lay, 0, 0, 1);
            Assert.That(outerSash.Width, Is.EqualTo(88.0).Within(LenTol));
            Assert.That(innerSash.Width, Is.EqualTo(88.0).Within(LenTol));
            Assert.That(outerSash.X, Is.EqualTo(4.5).Within(LenTol));
            Assert.That(innerSash.X, Is.EqualTo(87.5).Within(LenTol));   // 4.5 + (88-5)
            Assert.That(outerSash.IsOuterTrack, Is.True, "偶數索引為外軌");
            Assert.That(innerSash.IsOuterTrack, Is.False);
            Assert.That(outerSash.X + outerSash.Width + innerSash.Width - lay.Overlap,
                        Is.EqualTo(4.5 + 171.0).Within(LenTol), "兩扇含交疊應剛好填滿格寬");
        }

        [Test]
        public void Sliding_Derived_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(Sliding());
            var d = lay.Derived;

            Assert.That(d.SashCount, Is.EqualTo(2));
            Assert.That(d.OpenableCount, Is.EqualTo(2));
            Assert.That(d.GlassAreaM2, Is.EqualTo(2.043600).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.EqualTo(2.043600).Within(AreaTol));
            // 橫拉只算半扇
            Assert.That(d.OpenableAreaM2, Is.EqualTo(1.021800).Within(AreaTol));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(d.GlassAreaM2 / 2).Within(AreaTol));
        }

        // ────────────────────────────────────────────────
        // 3. fx 固定窗
        // ────────────────────────────────────────────────

        [Test]
        public void Fixed_Derived_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(FixedWindow());
            var d = lay.Derived;

            Assert.That(lay.ColWidths[0], Is.EqualTo(111.0).Within(LenTol));
            Assert.That(lay.RowHeights[0], Is.EqualTo(141.0).Within(LenTol));
            Assert.That(d.SashCount, Is.EqualTo(1));
            Assert.That(d.OpenableCount, Is.EqualTo(0));
            Assert.That(d.GlassAreaM2, Is.EqualTo(1.441600).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.EqualTo(1.441600).Within(AreaTol));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.0).Within(AreaTol));
            Assert.That(d.MoWidth, Is.EqualTo(125.0).Within(LenTol));
            Assert.That(d.MoHeight, Is.EqualTo(155.0).Within(LenTol));
        }

        // ────────────────────────────────────────────────
        // 4. up 上窗（上懸）
        // ────────────────────────────────────────────────

        [Test]
        public void AwningTop_RowHeights_MatchDemo()
        {
            var lay = WindowLayoutMath.Compute(AwningTop());

            Assert.That(lay.RowHeights, Is.EqualTo(new[] { 58.98, 137.62 }).Within(LenTol));
            Assert.That(Sash(lay, 1, 0, 0).Y, Is.EqualTo(67.88).Within(LenTol));
        }

        [Test]
        public void AwningTop_Derived_DeductsBelowDaylightDatum()
        {
            var lay = WindowLayoutMath.Compute(AwningTop());
            var d = lay.Derived;

            Assert.That(d.SashCount, Is.EqualTo(2));
            Assert.That(d.OpenableCount, Is.EqualTo(1));
            Assert.That(d.GlassAreaM2, Is.EqualTo(2.990070).Within(AreaTol));
            // sill 60 → 下排玻璃有一段落在 FL+75 以下，採光面積小於玻璃面積
            Assert.That(d.DaylightAreaM2, Is.EqualTo(2.857270).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.LessThan(d.GlassAreaM2));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.788578).Within(AreaTol));
        }

        // ────────────────────────────────────────────────
        // 5. dn 下窗（橫拉）
        // ────────────────────────────────────────────────

        [Test]
        public void SlidingBottom_Layout_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(SlidingBottom());

            Assert.That(lay.RowHeights, Is.EqualTo(new[] { 78.64, 117.96 }).Within(LenTol));
            var outerSash = Sash(lay, 1, 0, 0);
            Assert.That(outerSash.Y, Is.EqualTo(87.54).Within(LenTol));
            Assert.That(outerSash.Width, Is.EqualTo(88.0).Within(LenTol));
            Assert.That(Sash(lay, 1, 0, 1).X, Is.EqualTo(87.5).Within(LenTol));
        }

        [Test]
        public void SlidingBottom_Derived_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(SlidingBottom());
            var d = lay.Derived;

            Assert.That(d.SashCount, Is.EqualTo(3));
            Assert.That(d.OpenableCount, Is.EqualTo(2));
            Assert.That(d.GlassAreaM2, Is.EqualTo(2.906600).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.EqualTo(2.820800).Within(AreaTol));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.842088).Within(AreaTol));
            Assert.That(d.MoHeight, Is.EqualTo(215.0).Within(LenTol));
        }

        // ────────────────────────────────────────────────
        // 6. tri 三樘組合
        // ────────────────────────────────────────────────

        [Test]
        public void ThreeBay_ColWidths_MatchDemo()
        {
            var lay = WindowLayoutMath.Compute(ThreeBay());

            Assert.That(lay.ColWidths, Is.EqualTo(new[] { 102.66, 136.88, 102.66 }).Within(LenTol));
            Assert.That(lay.Bays[1].X, Is.EqualTo(111.56).Within(LenTol));
            Assert.That(Sash(lay, 0, 1, 0).Width, Is.EqualTo(70.94).Within(LenTol));
            Assert.That(Sash(lay, 0, 1, 1).X, Is.EqualTo(177.50).Within(LenTol));
            Assert.That(Sash(lay, 0, 2, 0).X, Is.EqualTo(252.84).Within(LenTol));
        }

        [Test]
        public void ThreeBay_Derived_MatchesDemo()
        {
            var lay = WindowLayoutMath.Compute(ThreeBay());
            var d = lay.Derived;

            Assert.That(d.SashCount, Is.EqualTo(4));
            Assert.That(d.OpenableCount, Is.EqualTo(2));
            Assert.That(d.GlassAreaM2, Is.EqualTo(5.204580).Within(AreaTol));
            Assert.That(d.DaylightAreaM2, Is.EqualTo(5.204580).Within(AreaTol));
            Assert.That(d.OpenableAreaM2, Is.EqualTo(0.981134).Within(AreaTol));
            Assert.That(d.MoWidth, Is.EqualTo(365.0).Within(LenTol));
        }

        // ────────────────────────────────────────────────
        // 無效規格
        // ────────────────────────────────────────────────

        [Test]
        public void Invalid_MixedSlidingInOneCell_Throws()
        {
            string gen = "{\"rows\":1,\"cols\":1,\"cells\":[[" + Cell(S("sliding"), S("fixed")) + "]]}";
            string ip = "{\"width\":180,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[100]}";

            var ex = Assert.Throws<WindowSpecException>(() => Spec(gen, ip));
            Assert.That(ex.Message, Does.Contain("混用"));
        }

        [Test]
        public void Invalid_RatioSumNotHundred_Throws()
        {
            string gen = "{\"rows\":2,\"cols\":1,\"cells\":[[" + Cell(S("fixed")) + "],[" + Cell(S("fixed")) + "]]}";
            string ip = "{\"width\":180,\"height\":210,\"sillHeight\":60,\"rowRatios\":[30,60],\"colRatios\":[100]}";

            var ex = Assert.Throws<WindowSpecException>(() => Spec(gen, ip));
            Assert.That(ex.Message, Does.Contain("100"));
        }

        [Test]
        public void Invalid_NegativeAvailableWidth_Throws()
        {
            // 4 樘、寬只有 20 cm → 扣掉外框與中框後淨寬為負
            string gen = "{\"rows\":1,\"cols\":4,\"cells\":[[" +
                         Cell(S("fixed")) + "," + Cell(S("fixed")) + "," + Cell(S("fixed")) + "," + Cell(S("fixed")) +
                         "]]}";
            string ip = "{\"width\":20,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[25,25,25,25]}";
            var spec = Spec(gen, ip);   // 規格本身合法，錯在算出來的尺寸

            var ex = Assert.Throws<WindowSpecException>(() => WindowLayoutMath.Compute(spec));
            Assert.That(ex.Message, Does.Contain("淨寬"));
        }

        [Test]
        public void Invalid_GlassSizeNonPositive_Throws()
        {
            // 扇框 5 cm、單格切 4 扇、格寬僅 ~9 cm → 玻璃寬算出負值
            string gen = "{\"rows\":1,\"cols\":1,\"cells\":[[" +
                         Cell(S("casement", "L"), S("casement", "R"), S("casement", "L"), S("casement", "R")) + "]]}";
            string ip = "{\"width\":18,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[100]}";
            var spec = Spec(gen, ip);

            var ex = Assert.Throws<WindowSpecException>(() => WindowLayoutMath.Compute(spec));
            Assert.That(ex.Message, Does.Contain("玻璃"));
        }

        // ────────────────────────────────────────────────
        // 其他規則
        // ────────────────────────────────────────────────

        [Test]
        public void Overlap_ManualModeOverridesSashFrameWidth()
        {
            string json = "{\"familyName\":\"UW-test\",\"typeName\":\"T1\"," +
                          "\"typeParams\":{\"frameOuter\":4.5,\"mullion\":4.4,\"sash\":5,\"frameDepth\":8,\"glass_mm\":8," +
                          "\"meetingRailOverlap_mm\":30,\"meetingRailOverlapMode\":\"manual\"}," +
                          "\"generation\":{\"rows\":1,\"cols\":1,\"cells\":[[" + Cell(S("sliding"), S("sliding")) + "]]}," +
                          "\"instanceParams\":{\"width\":180,\"height\":150,\"sillHeight\":90,\"rowRatios\":[100],\"colRatios\":[100]}}";
            var lay = WindowLayoutMath.Compute(WindowFamilySpec.Parse(JObject.Parse(json)));

            Assert.That(lay.Overlap, Is.EqualTo(3.0).Within(0.001));
            // (171 + 3)/2 = 87
            Assert.That(Sash(lay, 0, 0, 0).Width, Is.EqualTo(87.0).Within(LenTol));
            Assert.That(Sash(lay, 0, 0, 1).X, Is.EqualTo(4.5 + 87.0 - 3.0).Within(LenTol));
        }

        [Test]
        public void FamilyCoordinateConversion_CentresWidthAndMeasuresZFromSill()
        {
            var lay = WindowLayoutMath.Compute(Ufl());
            var topLeft = Sash(lay, 0, 0, 0);

            Assert.That(WindowLayoutMath.ToFamilyX(topLeft.X, lay.Width), Is.EqualTo(-115.5).Within(LenTol));
            // demo Y 自窗頂往下；族 Z 自窗台往上
            Assert.That(WindowLayoutMath.ToFamilyZ(topLeft.Y, lay.Height), Is.EqualTo(175.5).Within(LenTol));
            Assert.That(WindowLayoutMath.ToFamilyZ(topLeft.Y + topLeft.Height, lay.Height),
                        Is.EqualTo(180.0 - 62.81).Within(LenTol));
        }

        [Test]
        public void DescribeOperations_AndIfcPartitioning_MatchTopology()
        {
            var ufl = Ufl();
            Assert.That(ufl.DescribeOperations(), Is.EqualTo("R1: F | F / R2: CL,CR | CL,CR"));
            Assert.That(ufl.IfcPartitioningType(), Is.EqualTo("USERDEFINED"));

            Assert.That(FixedWindow().IfcPartitioningType(), Is.EqualTo("SINGLE_PANEL"));
            Assert.That(Sliding().IfcPartitioningType(), Is.EqualTo("DOUBLE_PANEL_VERTICAL"));
            Assert.That(AwningTop().IfcPartitioningType(), Is.EqualTo("DOUBLE_PANEL_HORIZONTAL"));
            // 上排 1 扇、下排 2 扇 → demo ifcPartition() 判為 TRIPLE_PANEL_TOP
            Assert.That(SlidingBottom().IfcPartitioningType(), Is.EqualTo("TRIPLE_PANEL_TOP"));
        }

        [Test]
        public void VoidDepth_DefaultsCoverFrameAndCutAllowances()
        {
            var spec = Ufl();
            // 未指定 → max(50, 8 + 5 + 5 + 20) = 50
            Assert.That(spec.ResolveVoidDepth(), Is.EqualTo(50.0).Within(0.001));

            spec.InstanceParams.Mo.VoidDepth = 72.5;
            Assert.That(spec.ResolveVoidDepth(), Is.EqualTo(72.5).Within(0.001));
        }
    }
}
