using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// 縣市預設檔、統計彙總、計算書 CSV 測試。方法定義見 domain/parcel-slope-analysis.md。
    /// </summary>
    [TestFixture]
    public class ParcelAnalysisTests
    {
        // ── 預設檔解析 ──

        [Test]
        public void Resolve_未指定或central_回中央且已驗證且非落回()
        {
            foreach (var input in new[] { null, "", "  ", "central", "CENTRAL", "中央" })
            {
                var r = ParcelPresets.Resolve(input);
                Assert.AreEqual(ParcelPresets.Central, r.Preset.Key, $"輸入 [{input}]");
                Assert.IsFalse(r.IsFallback);
                Assert.IsNull(r.FallbackNote);
                Assert.IsTrue(r.Preset.Verified, "central 已核原文");
            }
        }

        [Test]
        public void Resolve_已知預設檔與別名_且未驗證者verified為false()
        {
            var soil = ParcelPresets.Resolve("soil_conservation");
            Assert.AreEqual(ParcelPresets.SoilConservation, soil.Preset.Key);
            Assert.IsFalse(soil.Preset.Verified);

            foreach (var input in new[] { "taipei", "Taipei", "台北", "臺北市" })
            {
                var r = ParcelPresets.Resolve(input);
                Assert.AreEqual(ParcelPresets.Taipei, r.Preset.Key, input);
                Assert.IsFalse(r.Preset.Verified);
                Assert.IsFalse(r.IsFallback);
            }

            foreach (var input in new[] { "new_taipei", "new-taipei", "新北", "新北市" })
            {
                var r = ParcelPresets.Resolve(input);
                Assert.AreEqual(ParcelPresets.NewTaipei, r.Preset.Key, input);
                Assert.IsTrue(r.Preset.RequiresOriginalAndCurrent);
                Assert.IsFalse(r.Preset.Verified);
            }
        }

        [Test]
        public void Resolve_未查證縣市_落回中央並附說明()
        {
            var r = ParcelPresets.Resolve("kaohsiung");
            Assert.AreEqual(ParcelPresets.Central, r.Preset.Key);
            Assert.IsTrue(r.IsFallback);
            Assert.AreEqual("本縣市無查證到的特別規定，沿用中央", r.FallbackNote);
            Assert.AreEqual("kaohsiung", r.Requested);
            Assert.IsTrue(ParcelPresets.Warnings(r).Any(w => w.Contains("沿用中央")));
        }

        [Test]
        public void 預設檔數值與交辦包一致()
        {
            var c = ParcelPresets.Resolve("central").Preset;
            Assert.AreEqual(25.0, c.MaxCellSizeMeters);
            Assert.AreEqual(1200, c.MinMapScaleDenominator);
            CollectionAssert.AreEqual(new[] { 30.0, 55.0 }, c.GradeThresholdsPercent.ToArray());

            var s = ParcelPresets.Resolve("soil_conservation").Preset;
            CollectionAssert.AreEqual(new[] { 10.0, 25.0 }, s.AllowedCellSizes.ToArray());
            CollectionAssert.AreEqual(new[] { 5.0, 15.0, 30.0, 40.0, 55.0, 100.0 }, s.GradeThresholdsPercent.ToArray());

            var t = ParcelPresets.Resolve("taipei").Preset;
            Assert.AreEqual(10.0, t.MaxCellSizeMeters);
            Assert.IsTrue(t.MaxCellSizeExclusive);
            Assert.AreEqual(1000, t.MinMapScaleDenominator);

            var n = ParcelPresets.Resolve("new_taipei").Preset;
            Assert.AreEqual(25.0, n.MaxCellSizeMeters);
        }

        // ── 格邊長驗證 ──

        [Test]
        public void ValidateCellSize_中央與新北_上限25含()
        {
            foreach (var key in new[] { "central", "new_taipei" })
            {
                var p = ParcelPresets.Resolve(key).Preset;
                Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 25.0));
                Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 3.0));
                Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 25.0001));
            }
        }

        [Test]
        public void ValidateCellSize_水保_只允許10或25()
        {
            var p = ParcelPresets.Resolve("soil_conservation").Preset;
            Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 10.0));
            Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 25.0));
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 20.0));
            StringAssert.Contains("10m 或 25m", ex.Message);
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 12.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 30.0));
        }

        [Test]
        public void ValidateCellSize_臺北_須小於10_不含10()
        {
            var p = ParcelPresets.Resolve("taipei").Preset;
            Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 9.99));
            Assert.DoesNotThrow(() => ParcelPresets.ValidateCellSize(p, 5.0));
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 10.0));
            StringAssert.Contains("須小於", ex.Message);
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 25.0));
        }

        [Test]
        public void ValidateCellSize_非正數或非有限值丟例外()
        {
            var p = ParcelPresets.Resolve("central").Preset;
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, -5.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelPresets.ValidateCellSize(p, double.NaN));
        }

        [Test]
        public void Warnings_未驗證預設檔必附verified警告_central無此警告()
        {
            var central = ParcelPresets.Warnings(ParcelPresets.Resolve("central"));
            Assert.IsFalse(central.Any(w => w.Contains("verified=false")));

            foreach (var key in new[] { "soil_conservation", "taipei", "new_taipei" })
            {
                var w = ParcelPresets.Warnings(ParcelPresets.Resolve(key));
                Assert.IsTrue(w.Any(x => x.Contains("verified=false")), key);
            }

            Assert.IsTrue(ParcelPresets.Warnings(ParcelPresets.Resolve("new_taipei"))
                .Any(x => x.Contains("原始地形與現況")));
        }

        // ── 分級與判定 ──

        [Test]
        public void GradeLabel_中央三級_邊界右閉()
        {
            var th = new[] { 30.0, 55.0 };
            Assert.AreEqual("≤30%", ParcelReport.GradeLabel(30.0, th));
            Assert.AreEqual("30–55%", ParcelReport.GradeLabel(30.001, th));
            Assert.AreEqual("30–55%", ParcelReport.GradeLabel(55.0, th));
            Assert.AreEqual(">55%", ParcelReport.GradeLabel(55.001, th));
        }

        [Test]
        public void GradeLabel_水保七級()
        {
            var th = ParcelPresets.Resolve("soil_conservation").Preset.GradeThresholdsPercent;
            Assert.AreEqual(7, ParcelReport.GradeLabels(th).Count);
            Assert.AreEqual("≤5%", ParcelReport.GradeLabel(5.0, th));
            Assert.AreEqual("5–15%", ParcelReport.GradeLabel(5.01, th));
            Assert.AreEqual("30–40%", ParcelReport.GradeLabel(40.0, th));
            Assert.AreEqual("55–100%", ParcelReport.GradeLabel(100.0, th));
            Assert.AreEqual(">100%", ParcelReport.GradeLabel(100.01, th));
        }

        [Test]
        public void VerdictLabel_對應三段()
        {
            Assert.AreEqual("≤30%：無§262坡度限制", ParcelReport.VerdictLabel(ParcelRestriction.None));
            Assert.AreEqual("30–55%：得作法定空地，不得配置建築物（§262Ⅲ）",
                ParcelReport.VerdictLabel(ParcelRestriction.OpenSpaceOnly));
            Assert.AreEqual(">55%：不得計入法定空地（§262Ⅲ）",
                ParcelReport.VerdictLabel(ParcelRestriction.ExcludedFromOpenSpace));
        }

        [Test]
        public void VerdictLabel_只陳述法律效果_不得寫可否建築的結論()
        {
            foreach (ParcelRestriction r in Enum.GetValues(typeof(ParcelRestriction)))
            {
                var label = ParcelReport.VerdictLabel(r);
                Assert.IsFalse(label.Contains("可建築"), $"{r} 不得寫「可建築」");
                Assert.IsFalse(label.Contains("不得開發"), $"{r} 不得輸出開發結論");
                Assert.IsFalse(label.Contains(","), $"{r} 判定文字不得含半形逗號（CSV 欄位）");
            }
        }

        // ── 別名與路徑 ──

        [Test]
        public void Resolve_縣市別名_不分大小寫空白與台臺()
        {
            foreach (var input in new[]
            {
                "Taipei City", "taipei city", "TAIPEI", "臺北市政府", "台北市政府", "台北", "臺北", " 台北市 ",
            })
            {
                var r = ParcelPresets.Resolve(input);
                Assert.AreEqual(ParcelPresets.Taipei, r.Preset.Key, input);
                Assert.IsFalse(r.IsFallback, input);
            }

            foreach (var input in new[]
            {
                "New Taipei City", "new taipei", "NEW_TAIPEI", "新北市政府", "新北", "新北市",
            })
            {
                var r = ParcelPresets.Resolve(input);
                Assert.AreEqual(ParcelPresets.NewTaipei, r.Preset.Key, input);
                Assert.IsFalse(r.IsFallback, input);
            }

            // 「新北」不可被誤認為臺北
            Assert.AreNotEqual(ParcelPresets.Taipei, ParcelPresets.Resolve("新北市政府").Preset.Key);
        }

        [Test]
        public void Resolve_無法辨識_落回中央且警告明列未辨識縣市()
        {
            var r = ParcelPresets.Resolve("台中市");
            Assert.IsTrue(r.IsFallback);
            Assert.AreEqual(ParcelPresets.Central, r.Preset.Key);
            Assert.IsTrue(ParcelPresets.Warnings(r).Any(w => w.Contains("未辨識縣市，沿用中央")));
        }

        [Test]
        public void IsAbsolutePath_只認絕對路徑()
        {
            Assert.IsTrue(ParcelReport.IsAbsolutePath(@"C:\Temp\parcel.csv"));
            Assert.IsTrue(ParcelReport.IsAbsolutePath("D:/work/parcel.csv"));
            Assert.IsTrue(ParcelReport.IsAbsolutePath(@"\\server\share\parcel.csv"));

            Assert.IsFalse(ParcelReport.IsAbsolutePath("parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(@"out\parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(@".\parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(@"..\parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(@"C:parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(@"\parcel.csv"));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(""));
            Assert.IsFalse(ParcelReport.IsAbsolutePath(null));
        }

        // ── 統計 ──

        private static ParcelCellRecord Cell(string label, double slope, double area, int n = 0)
        {
            return new ParcelCellRecord
            {
                Label = label,
                IntersectionCount = n,
                EdgeCounts = new[] { n, 0, 0, 0 },
                SlopePercent = slope,
                AreaSquareMeters = area,
                Restriction = ParcelSlopeMath.Classify(slope),
                GradeLabel = ParcelReport.GradeLabel(slope, new[] { 30.0, 55.0 }),
            };
        }

        [Test]
        public void Summarize_面積加權平均與各級面積()
        {
            var central = ParcelPresets.Resolve("central").Preset;
            var cells = new List<ParcelCellRecord>
            {
                Cell("1", 10.0, 100.0),
                Cell("2", 40.0, 200.0),
                Cell("3", 60.0, 100.0),
            };

            var s = ParcelReport.Summarize(cells, central);

            Assert.AreEqual(3, s.CellCount);
            Assert.AreEqual(400.0, s.TotalAreaSquareMeters, 1e-9);
            Assert.AreEqual(10 * 100 + 40 * 200 + 60 * 100, s.SlopeAreaSum, 1e-9);
            Assert.AreEqual(15000.0 / 400.0, s.WeightedAverageSlopePercent, 1e-9);
            Assert.AreEqual(60.0, s.MaxSlopePercent);
            Assert.AreEqual(10.0, s.MinSlopePercent);
            Assert.AreEqual(100.0, s.AreaNoneSquareMeters, 1e-9);
            Assert.AreEqual(200.0, s.AreaOpenSpaceOnlySquareMeters, 1e-9);
            Assert.AreEqual(100.0, s.AreaExcludedSquareMeters, 1e-9);
            Assert.AreEqual(300.0, s.AreaOver30SquareMeters, 1e-9);
            Assert.AreEqual(1, s.CountNone);
            Assert.AreEqual(1, s.CountOpenSpaceOnly);
            Assert.AreEqual(1, s.CountExcluded);

            var byLabel = s.Grades.ToDictionary(g => g.Label, g => g);
            Assert.AreEqual(100.0, byLabel["≤30%"].AreaSquareMeters, 1e-9);
            Assert.AreEqual(200.0, byLabel["30–55%"].AreaSquareMeters, 1e-9);
            Assert.AreEqual(100.0, byLabel[">55%"].AreaSquareMeters, 1e-9);
        }

        [Test]
        public void Summarize_空集合不丟例外()
        {
            var s = ParcelReport.Summarize(new List<ParcelCellRecord>(), ParcelPresets.Resolve("central").Preset);
            Assert.AreEqual(0, s.CellCount);
            Assert.AreEqual(0.0, s.WeightedAverageSlopePercent);
            Assert.AreEqual(3, s.Grades.Count);
        }

        [Test]
        public void Delta_為shifted減main()
        {
            var central = ParcelPresets.Resolve("central").Preset;
            var main = ParcelReport.Summarize(new List<ParcelCellRecord>
            {
                Cell("1", 20.0, 100.0), Cell("2", 40.0, 100.0),
            }, central);
            var shifted = ParcelReport.Summarize(new List<ParcelCellRecord>
            {
                Cell("1", 35.0, 100.0), Cell("2", 40.0, 100.0), Cell("3", 10.0, 50.0),
            }, central);

            var d = ParcelReport.Delta(main, shifted);

            Assert.AreEqual(1, d.CellCount);
            Assert.AreEqual(50.0, d.TotalAreaSquareMeters, 1e-9);
            Assert.AreEqual(shifted.WeightedAverageSlopePercent - main.WeightedAverageSlopePercent,
                d.AverageSlopePercent, 1e-12);
            Assert.AreEqual(100.0, d.AreaOver30SquareMeters, 1e-9); // 200 − 100
        }

        // ── CSV ──

        [Test]
        public void BuildCsv_欄位順序_逐格資料與尾端摘要列()
        {
            var central = ParcelPresets.Resolve("central");
            var cells = new List<ParcelCellRecord>
            {
                Cell("1", 15.7, 625.0, n: 10),
                Cell("2", 40.0, 312.5, n: 25),
            };
            cells[1].EdgeCounts = new[] { 7, 6, 5, 7 };
            var stats = ParcelReport.Summarize(cells, central.Preset);

            var csv = ParcelReport.BuildCsv(cells, stats, new ParcelCsvContext
            {
                Resolution = central,
                CellSizeMeters = 25.0,
                ContourIntervalMeters = 1.0,
                RotationDegrees = 12.5,
                OriginOffsetXMeters = 3.0,
                OriginOffsetYMeters = -2.0,
                BoundaryDescription = "DWG 圖層「SITE」，面積 937.50 ㎡",
            });

            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.AreEqual(
                "編號,交點數(n),坡度(%),基地內面積(㎡),坡度面積(S×A),坡度等級,判定,"
                + "下邊交點數,右邊交點數,上邊交點數,左邊交點數", lines[0]);
            Assert.AreEqual("1,10,15.70,625.00,9812.50,≤30%,≤30%：無§262坡度限制,10,0,0,0", lines[1]);
            Assert.AreEqual(
                "2,25,40.00,312.50,12500.00,30–55%,30–55%：得作法定空地，不得配置建築物（§262Ⅲ）,7,6,5,7",
                lines[2]);
            Assert.AreEqual(25, cells[1].EdgeCounts.Sum(), "四邊之和 = n");
            Assert.AreEqual(string.Empty, lines[3], "資料列與摘要列之間空一列");

            StringAssert.Contains("central", csv);
            StringAssert.Contains("建築技術規則建築設計施工編第261、262條", csv);
            StringAssert.Contains("方格邊長L(m),25.00", csv);
            StringAssert.Contains("等高線首曲線間距h(m),1.000", csv);
            StringAssert.Contains("網格旋轉角(度),12.50", csv);
            StringAssert.Contains("原點平移X/Y(m),3.000 / -2.000", csv);
            StringAssert.Contains("DWG 圖層「SITE」，面積 937.50 ㎡", csv.Replace("\"", string.Empty));

            // 平均坡度 = Σ坡度面積 / Σ面積 = (9812.5 + 12500) / 937.5 = 23.8
            StringAssert.Contains("22312.50 / 937.50 = 23.80", csv);
        }

        [Test]
        public void BuildCsv_含逗號的欄位會加引號_落回縣市有註記()
        {
            var fallback = ParcelPresets.Resolve("kaohsiung");
            var cells = new List<ParcelCellRecord> { Cell("1", 5.0, 625.0, n: 3) };
            var csv = ParcelReport.BuildCsv(cells, ParcelReport.Summarize(cells, fallback.Preset), new ParcelCsvContext
            {
                Resolution = fallback,
                CellSizeMeters = 25.0,
                ContourIntervalMeters = 2.0,
                BoundaryDescription = "a,b",
            });

            StringAssert.Contains("\"a,b\"", csv);
            StringAssert.Contains("本縣市無查證到的特別規定，沿用中央", csv);
            StringAssert.Contains("未指定", ParcelReport.BuildCsv(cells, ParcelReport.Summarize(cells, fallback.Preset),
                new ParcelCsvContext { Resolution = fallback, CellSizeMeters = 25.0, ContourIntervalMeters = 2.0 }));
        }
    }
}
