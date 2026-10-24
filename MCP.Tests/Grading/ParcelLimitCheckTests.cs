using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// ParcelLimitCheck：整地方案樓板 × 坵塊法限建坵塊疊圖重疊檢查。
    /// 設計見 docs/superpowers/specs/2026-10-05-grading-parcel-limit-check-design.md §7。
    /// </summary>
    [TestFixture]
    public class ParcelLimitCheckTests
    {
        private static ParcelCellSummary SquareCell(
            string label, double x0, double y0, double size, double slopePercent)
        {
            return new ParcelCellSummary
            {
                Label = label,
                SlopePercent = slopePercent,
                Restriction = ParcelSlopeMath.Classify(slopePercent),
                CornersMeters = new List<Point2D>
                {
                    new Point2D(x0, y0),
                    new Point2D(x0 + size, y0),
                    new Point2D(x0 + size, y0 + size),
                    new Point2D(x0, y0 + size),
                },
            };
        }

        private static List<Point2D> Rectangle(double x0, double y0, double x1, double y1)
        {
            return new List<Point2D>
            {
                new Point2D(x0, y0),
                new Point2D(x1, y0),
                new Point2D(x1, y1),
                new Point2D(x0, y1),
            };
        }

        [Test]
        public void CheckFloor_完全不重疊_無任何MatchedCells()
        {
            var floor = Rectangle(100, 100, 110, 110); // 遠離坵塊網格
            var cells = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 40) }; // OpenSpaceOnly

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Is.Empty);
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(0));
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(0));
        }

        [Test]
        public void CheckFloor_樓板完全落在單一None坵塊內_無MatchedCells且限建面積皆0()
        {
            var cells = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 10) }; // None：10% ≤30
            var floor = Rectangle(2, 2, 8, 8); // 完全落在坵塊內

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Is.Empty);
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(0));
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(0));
        }

        [Test]
        public void CheckFloor_樓板完全落在單一OpenSpaceOnly坵塊內_重疊面積等於樓板全部面積()
        {
            var cells = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 40) }; // (30,55] → OpenSpaceOnly
            var floor = Rectangle(2, 2, 6, 6); // 4x4 = 16 m²，完全落在坵塊內

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(16.0).Within(1e-9));
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(0));
            Assert.That(result.MatchedCells[0].Restriction, Is.EqualTo(ParcelRestriction.OpenSpaceOnly));
            Assert.That(result.MatchedCells[0].OverlapFractionOfFloor, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void CheckFloor_樓板跨兩格_一格None一格Excluded_重疊面積正確分配且面積守恆()
        {
            var cells = new List<ParcelCellSummary>
            {
                SquareCell("A", 0, 0, 10, 10), // None
                SquareCell("B", 10, 0, 10, 60), // >55 → Excluded
            };
            var floor = Rectangle(5, 0, 15, 10); // 跨兩格，各一半：50 m² + 50 m²

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1)); // None 格不收錄
            Assert.That(result.MatchedCells[0].CellLabel, Is.EqualTo("B"));
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(50.0).Within(1e-6));
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(0));

            var floorArea = Polygon2D.Area(floor);
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.LessThanOrEqualTo(floorArea + 1e-6));
        }

        [Test]
        public void CheckFloor_相切零面積重疊_排除不收錄()
        {
            var cells = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 40) };
            var floor = Rectangle(10, 0, 20, 10); // 與坵塊共用 x=10 這條邊，理論重疊面積為 0

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Is.Empty);
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(0));
        }

        [Test]
        public void CheckFloor_L形凹多邊形樓板跨三格_重疊面積正確()
        {
            // L 形：10x10 大正方形扣掉右上角 5x5 的缺角，面積 = 100 - 25 = 75 m²。
            var floor = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(10, 0),
                new Point2D(10, 5),
                new Point2D(5, 5),
                new Point2D(5, 10),
                new Point2D(0, 10),
            };
            Assert.That(Polygon2D.Area(floor), Is.EqualTo(75.0).Within(1e-9));

            var cells = new List<ParcelCellSummary>
            {
                SquareCell("A", 0, 0, 5, 40),  // 左下 5x5，OpenSpaceOnly，全落在 L 形內 → 25
                SquareCell("B", 5, 0, 5, 60),  // 右下 5x5，Excluded，全落在 L 形內 → 25
                SquareCell("C", 0, 5, 5, 60),  // 左上 5x5，Excluded，全落在 L 形內 → 25
                // 右上 5x5（缺角）沒有對應坵塊，刻意不加，驗證缺角不計入。
            };

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            Assert.That(result.MatchedCells, Has.Count.EqualTo(3));
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(25.0).Within(1e-9));
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(50.0).Within(1e-9));
            Assert.That(
                result.OpenSpaceOnlyOverlapAreaSquareMeters + result.ExcludedOverlapAreaSquareMeters,
                Is.EqualTo(75.0).Within(1e-9));
        }

        [Test]
        public void CheckFloor_坵塊網格旋轉45度_重疊面積仍正確()
        {
            // 以 (0,0) 為中心、半邊長 5 的正方形，繞原點旋轉 45 度 → 角點變成菱形排列。
            var half = 5.0;
            var cos = Math.Cos(Math.PI / 4);
            var sin = Math.Sin(Math.PI / 4);
            Point2D Rotate(double x, double y) => new Point2D((x * cos) - (y * sin), (x * sin) + (y * cos));

            var rotatedCorners = new List<Point2D>
            {
                Rotate(-half, -half),
                Rotate(half, -half),
                Rotate(half, half),
                Rotate(-half, half),
            };
            var cell = new ParcelCellSummary
            {
                Label = "1",
                SlopePercent = 40,
                Restriction = ParcelSlopeMath.Classify(40),
                CornersMeters = rotatedCorners,
            };

            // 樓板是一個包住旋轉菱形的大正方形，重疊面積應等於旋轉後坵塊的全部面積（10x10=100）。
            var floor = Rectangle(-10, -10, 10, 10);

            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(100.0).Within(1e-6));
        }

        [Test]
        public void FeetToMeters_已知數值_等式斷言()
        {
            // 2026-10-05 審查修正：原本的英呎→公尺換算（CommandExecutor.ParcelSlope.cs 的
            // ParcelFeetToMeters）呼叫 Revit API（UnitUtils），測試專案拿不到，之前只能用
            // Is.Not.EqualTo 這種弱斷言繞過。抽成零 Revit 相依的 ParcelUnitConversion 後，
            // 用已知數值做等式斷言：1 英呎＝0.3048 公尺是國際標準定義的精確值。
            Assert.That(ParcelUnitConversion.FeetToMeters(1.0), Is.EqualTo(0.3048).Within(1e-12));
            Assert.That(ParcelUnitConversion.FeetToMeters(10.0), Is.EqualTo(3.048).Within(1e-12));
            Assert.That(ParcelUnitConversion.FeetToMeters(0.0), Is.EqualTo(0.0).Within(1e-12));

            var feetPoint = new Point2D(10.0, 20.0);
            var metersPoint = ParcelUnitConversion.FeetPointToMeters(feetPoint);
            Assert.That(metersPoint.X, Is.EqualTo(3.048).Within(1e-12));
            Assert.That(metersPoint.Y, Is.EqualTo(6.096).Within(1e-12));
        }

        [Test]
        public void CheckFloor_單位換算鎖定_樓板輪廓以公尺輸入才能得到正確面積()
        {
            // 鎖住呼叫端必須先把樓板輪廓（原英呎）轉成公尺再傳入——
            // 本測試直接構造「已轉換為公尺」的輪廓，驗證 1 英呎≠1 公尺造成的面積差異不會被忽略。
            var cells = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 40) };

            var floorMeters = Rectangle(0, 0, 3.048, 3.048); // 10 英呎 = 3.048 公尺（用 ParcelUnitConversion 換算的值）
            Assert.That(floorMeters[1].X, Is.EqualTo(ParcelUnitConversion.FeetToMeters(10.0)).Within(1e-12));
            var resultMeters = ParcelLimitCheck.CheckFloor(1, floorMeters, cells);
            Assert.That(resultMeters.FloorAreaSquareMeters, Is.EqualTo(3.048 * 3.048).Within(1e-9));
            Assert.That(resultMeters.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(3.048 * 3.048).Within(1e-6));

            // 若誤用英呎數值當公尺（未換算），面積會被錯誤放大為 100（10x10），
            // 與正確值 3.048x3.048=9.290304 比較要用等式／不等式雙重鎖死，不能只靠「不等於」。
            var floorUnconverted = Rectangle(0, 0, 10, 10);
            var resultUnconverted = ParcelLimitCheck.CheckFloor(1, floorUnconverted, cells);
            Assert.That(resultUnconverted.FloorAreaSquareMeters, Is.EqualTo(100.0).Within(1e-9));
            Assert.That(resultMeters.FloorAreaSquareMeters, Is.EqualTo(9.290304).Within(1e-9));
        }

        [Test]
        public void CheckFloor_外接矩形快篩與全算結果一致_不漏判()
        {
            var random = new Random(20261005);
            var cells = new List<ParcelCellSummary>();
            for (var col = 0; col < 6; col++)
            {
                for (var row = 0; row < 6; row++)
                {
                    var slope = 10 + ((col + row) * 8); // 製造 None/OpenSpaceOnly/Excluded 混合
                    cells.Add(SquareCell($"{col}-{row}", col * 10, row * 10, 10, slope));
                }
            }

            for (var trial = 0; trial < 20; trial++)
            {
                var x0 = random.NextDouble() * 60;
                var y0 = random.NextDouble() * 60;
                var floor = Rectangle(x0, y0, x0 + 1 + (random.NextDouble() * 15), y0 + 1 + (random.NextDouble() * 15));

                var withBoundingBoxSkip = ParcelLimitCheck.CheckFloor(1, floor, cells);

                // 「全算」對照組：直接對每一格呼叫 Clip，不靠 CheckFloor 內部的 bounding box 快篩，
                // 驗證快篩沒有漏判任何真實重疊的格子。
                var bruteForceMatched = cells
                    .Where(cell => cell.Restriction != ParcelRestriction.None)
                    .Select(cell => (cell, clipped: ParcelLimitCheck.Clip(floor, cell.CornersMeters)))
                    .Where(pair => pair.clipped.Count >= 3
                        && Polygon2D.Area(pair.clipped) > ParcelLimitCheck.DefaultAreaToleranceSquareMeters)
                    .Select(pair => pair.cell.Label)
                    .OrderBy(label => label)
                    .ToList();

                var fastMatched = withBoundingBoxSkip.MatchedCells
                    .Select(m => m.CellLabel)
                    .OrderBy(label => label)
                    .ToList();

                Assert.That(fastMatched, Is.EqualTo(bruteForceMatched), $"trial={trial}");
            }
        }

        [Test]
        public void CheckFloor_門檻邊界恰為30與55_分級結果等於ParcelSlopeMath常數()
        {
            var atSteepThreshold = ParcelSlopeMath.Classify(ParcelSlopeMath.SteepThresholdPercent);
            var atNoOpenSpaceThreshold = ParcelSlopeMath.Classify(ParcelSlopeMath.NoOpenSpaceThresholdPercent);

            // 右閉邊界：恰為 30 仍是 None；恰為 55 仍是 OpenSpaceOnly（§262，見 ParcelSlopeMath.Classify）。
            Assert.That(atSteepThreshold, Is.EqualTo(ParcelRestriction.None));
            Assert.That(atNoOpenSpaceThreshold, Is.EqualTo(ParcelRestriction.OpenSpaceOnly));

            var cell = SquareCell("1", 0, 0, 10, ParcelSlopeMath.NoOpenSpaceThresholdPercent + 0.01);
            Assert.That(cell.Restriction, Is.EqualTo(ParcelRestriction.ExcludedFromOpenSpace));

            var floor = Rectangle(0, 0, 10, 10);
            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });
            Assert.That(result.ExcludedOverlapAreaSquareMeters, Is.EqualTo(100.0).Within(1e-9));
        }

        [Test]
        public void CheckFloor_面積防重複計算_樓板跨四格共用邊_總重疊不超過樓板總面積()
        {
            var cells = new List<ParcelCellSummary>
            {
                SquareCell("A", 0, 0, 10, 40),
                SquareCell("B", 10, 0, 10, 60),
                SquareCell("C", 0, 10, 10, 60),
                SquareCell("D", 10, 10, 10, 40),
            };
            var floor = Rectangle(5, 5, 15, 15); // 跨四格，交會點恰在共用角 (10,10)

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            var floorArea = Polygon2D.Area(floor);
            var totalOverlap = result.OpenSpaceOnlyOverlapAreaSquareMeters + result.ExcludedOverlapAreaSquareMeters;
            Assert.That(totalOverlap, Is.LessThanOrEqualTo(floorArea + 1e-6));
            Assert.That(totalOverlap, Is.EqualTo(floorArea).Within(1e-6)); // 四格剛好鋪滿整片樓板，不留縫也不重複。
        }

        [Test]
        public void CheckFloor_空坵塊清單或全部None_回傳空MatchedCells不丟例外()
        {
            var floor = Rectangle(0, 0, 10, 10);

            Assert.DoesNotThrow(() =>
            {
                var resultEmpty = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary>());
                Assert.That(resultEmpty.MatchedCells, Is.Empty);
            });

            var allNone = new List<ParcelCellSummary> { SquareCell("1", 0, 0, 10, 5) };
            var resultAllNone = ParcelLimitCheck.CheckFloor(1, floor, allNone);
            Assert.That(resultAllNone.MatchedCells, Is.Empty);
            Assert.That(resultAllNone.OpenSpaceOnlyOverlapAreaSquareMeters, Is.EqualTo(0));
            Assert.That(resultAllNone.ExcludedOverlapAreaSquareMeters, Is.EqualTo(0));
        }

        [Test]
        public void CheckFloor_大樓板跨數百格_不逾時不堆疊溢位()
        {
            var cells = new List<ParcelCellSummary>();
            for (var col = 0; col < 30; col++)
            {
                for (var row = 0; row < 30; row++)
                {
                    cells.Add(SquareCell($"{col}-{row}", col * 5, row * 5, 5, 35));
                }
            }

            var floor = Rectangle(0, 0, 150, 150); // 跨滿 900 格

            ParcelFloorLimitResult result = null;
            Assert.DoesNotThrow(() => result = ParcelLimitCheck.CheckFloor(1, floor, cells));
            Assert.That(result.MatchedCells, Has.Count.EqualTo(900));
        }

        [Test]
        public void CheckFloor_輸入不足三點或坵塊資料不完整_丟例外或安全跳過()
        {
            Assert.Throws<ArgumentException>(() =>
                ParcelLimitCheck.CheckFloor(1, new List<Point2D> { new Point2D(0, 0), new Point2D(1, 1) },
                    new List<ParcelCellSummary>()));

            var floor = Rectangle(0, 0, 10, 10);
            var incompleteCell = new ParcelCellSummary
            {
                Label = "bad",
                Restriction = ParcelRestriction.OpenSpaceOnly,
                CornersMeters = new List<Point2D> { new Point2D(0, 0), new Point2D(1, 0) }, // 只有 2 角
            };

            ParcelFloorLimitResult result = null;
            Assert.DoesNotThrow(() =>
                result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { incompleteCell }));
            Assert.That(result.MatchedCells, Is.Empty);
        }

        // ──────────────────────────────────────────────────────────────
        // 2026-10-05 審查修正：以下為補強測試（獨立審查者認為原 14 項太鬆）。
        // ──────────────────────────────────────────────────────────────

        private static ParcelCellSummary RectCell(string label, double x0, double y0, double x1, double y1, double slopePercent)
        {
            return new ParcelCellSummary
            {
                Label = label,
                SlopePercent = slopePercent,
                Restriction = ParcelSlopeMath.Classify(slopePercent),
                CornersMeters = Rectangle(x0, y0, x1, y1),
            };
        }

        [Test]
        public void CheckFloor_L形樓板跨凹角坵塊_手算期望值18點75()
        {
            // L 形樓板：外側 10x10 正方形扣掉右上角 (5,5)-(10,10) 的 5x5 缺角，面積 = 100-25 = 75。
            var floor = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(10, 0),
                new Point2D(10, 5),
                new Point2D(5, 5),
                new Point2D(5, 10),
                new Point2D(0, 10),
            };
            Assert.That(Polygon2D.Area(floor), Is.EqualTo(75.0).Within(1e-9));

            // 坵塊剛好跨在凹角上：(2.5,2.5)-(7.5,7.5)，邊長 5、面積 25。
            // 手算：坵塊落在樓板「缺角」(x>5 且 y>5) 裡的部分是 (5,5)-(7.5,7.5)，面積 2.5*2.5=6.25；
            // 其餘 25-6.25=18.75 落在 L 形樓板內——此為獨立審查者用 shapely 驗算過的期望值。
            var cell = RectCell("corner", 2.5, 2.5, 7.5, 7.5, 40);

            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(18.75).Within(1e-9));
        }

        [Test]
        public void CheckFloor_U形樓板被一格裁成兩塊_面積仍正確加總不漏算()
        {
            // U 形（開口朝上）：外框 10x10，中央扣掉 (3,3)-(7,10) 的直通缺口，面積 = 100 - 4*7 = 72。
            var floor = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(10, 0),
                new Point2D(10, 10),
                new Point2D(7, 10),
                new Point2D(7, 3),
                new Point2D(3, 3),
                new Point2D(3, 10),
                new Point2D(0, 10),
            };
            Assert.That(Polygon2D.Area(floor), Is.EqualTo(72.0).Within(1e-9));

            // 坵塊橫跨 U 形兩支腳的頂端：x:[0,10] y:[8,10]，同時碰到左腳(x:0-3)與右腳(x:7-10)，
            // 中間缺口(x:3-7)不屬於樓板——真正的交集是「兩塊分開」的矩形，各 3x2=6 m²，合計 12 m²。
            // 這個案例專門驗證 Sutherland-Hodgman 對「單一凸裁切把凹樓板切成兩塊不連通區域」的
            // 處理是否仍正確——退化橋接點不應讓面積算錯（shoelace 對零寬度橋接的淨貢獻為 0）。
            var cell = RectCell("top-strip", 0, 8, 10, 10, 40);

            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(12.0).Within(1e-6));
        }

        [Test]
        public void CheckFloor_順時針繞向的樓板與坵塊_結果與逆時針繞向一致()
        {
            var ccwFloor = Rectangle(0, 0, 10, 10); // Rectangle() 回傳逆時針順序
            var cwFloor = new List<Point2D>
            {
                new Point2D(0, 0), new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 0),
            }; // 同一個正方形，改成順時針

            var ccwCell = RectCell("1", 2, 2, 6, 6, 40);
            var cwCell = new ParcelCellSummary
            {
                Label = "1",
                SlopePercent = 40,
                Restriction = ParcelRestriction.OpenSpaceOnly,
                CornersMeters = new List<Point2D>
                {
                    new Point2D(2, 2), new Point2D(2, 6), new Point2D(6, 6), new Point2D(6, 2),
                }, // 順時針版本
            };

            var resultCcw = ParcelLimitCheck.CheckFloor(1, ccwFloor, new List<ParcelCellSummary> { ccwCell });
            var resultCw = ParcelLimitCheck.CheckFloor(1, cwFloor, new List<ParcelCellSummary> { cwCell });

            Assert.That(resultCcw.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(16.0).Within(1e-9)); // 4x4
            Assert.That(resultCw.MatchedCells[0].OverlapAreaSquareMeters,
                Is.EqualTo(resultCcw.MatchedCells[0].OverlapAreaSquareMeters).Within(1e-9));
        }

        [Test]
        public void CheckFloor_旋轉格網部分重疊_手算期望值1點0且分率不等於1()
        {
            var half = 1.0;
            var cos = Math.Cos(Math.PI / 4);
            var sin = Math.Sin(Math.PI / 4);
            Point2D Rotate(double x, double y) => new Point2D((x * cos) - (y * sin), (x * sin) + (y * cos));

            // 邊長 2 的正方形（半邊 1）繞原點旋轉 45 度，變成對角線 2√2 的菱形。
            var rotatedCorners = new List<Point2D>
            {
                Rotate(-half, -half), Rotate(half, -half), Rotate(half, half), Rotate(-half, half),
            };
            var cell = new ParcelCellSummary
            {
                Label = "1",
                SlopePercent = 40,
                Restriction = ParcelRestriction.OpenSpaceOnly,
                CornersMeters = rotatedCorners,
            };

            // 樓板只蓋第一象限（x≥0,y≥0），只會跟菱形的 1/4 重疊：
            // 頂點 (0,0),(√2,0),(0,√2) 的直角三角形，面積 = 0.5×√2×√2 = 1.0（手算，非整個包住）。
            var floor = Rectangle(0, 0, 10, 10);

            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(result.MatchedCells[0].OverlapFractionOfFloor, Is.Not.EqualTo(1.0));
        }

        [Test]
        public void CheckFloor_部分重疊時OverlapFractionOfFloor不等於1()
        {
            var cell = RectCell("1", 0, 0, 10, 10, 40);
            var floor = Rectangle(5, 5, 15, 15); // 只有 1/4（5-10,5-10=25 m²）落在坵塊內，樓板總面積 100。

            var result = ParcelLimitCheck.CheckFloor(1, floor, new List<ParcelCellSummary> { cell });

            Assert.That(result.MatchedCells, Has.Count.EqualTo(1));
            Assert.That(result.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(25.0).Within(1e-9));
            Assert.That(result.MatchedCells[0].OverlapFractionOfFloor, Is.EqualTo(0.25).Within(1e-9));
            Assert.That(result.MatchedCells[0].OverlapFractionOfFloor, Is.Not.EqualTo(1.0));
        }

        [Test]
        public void CheckFloor_真實BuildGrid鋪滿樓板_重疊加總等於樓板面積_等式而非小於等於()
        {
            // 用生產路徑真正的 ParcelSlopeMath.BuildGrid（非手寫方格），驗證角點順序、旋轉中心、
            // 格數計算與 ParcelLimitCheck 的裁切邏輯接得起來。
            var grid = ParcelSlopeMath.BuildGrid(minX: 0, minY: 0, maxX: 20, maxY: 20, cellSizeMeters: 5, rotationDegrees: 0);
            var cells = grid.Select(c => new ParcelCellSummary
            {
                Label = $"{c.Col}-{c.Row}",
                SlopePercent = 40,
                Restriction = ParcelRestriction.OpenSpaceOnly, // 全部標成限建，整片樓板理應被完整覆蓋。
                CornersMeters = c.Corners.Select(p => new Point2D(p.X, p.Y)).ToList(),
            }).ToList();

            var floor = Rectangle(0, 0, 20, 20);
            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);

            var totalOverlap = result.MatchedCells.Sum(m => m.OverlapAreaSquareMeters);
            Assert.That(totalOverlap, Is.EqualTo(400.0).Within(1e-6)); // 20x20，等式而非 ≤。
            Assert.That(totalOverlap, Is.EqualTo(result.FloorAreaSquareMeters).Within(1e-6));
            Assert.That(result.CoveredAreaSquareMeters, Is.EqualTo(result.FloorAreaSquareMeters).Within(1e-6));
        }

        [Test]
        public void CheckFloor_容差邊界_0點01平方公尺兩側各一例()
        {
            var cell = RectCell("1", 0, 0, 10, 10, 40);

            // 重疊帶寬 0.0009 m、高 10 m → 面積 0.009 m² < 0.01 容差 → 應排除。
            var floorBelowTolerance = Rectangle(10 - 0.0009, 0, 10 + 1, 10);
            var resultBelow = ParcelLimitCheck.CheckFloor(1, floorBelowTolerance, new List<ParcelCellSummary> { cell });
            Assert.That(resultBelow.MatchedCells, Is.Empty, "重疊面積 0.009 m² 應低於容差被排除");

            // 重疊帶寬 0.0011 m、高 10 m → 面積 0.011 m² > 0.01 容差 → 應收錄。
            var floorAboveTolerance = Rectangle(10 - 0.0011, 0, 10 + 1, 10);
            var resultAbove = ParcelLimitCheck.CheckFloor(1, floorAboveTolerance, new List<ParcelCellSummary> { cell });
            Assert.That(resultAbove.MatchedCells, Has.Count.EqualTo(1), "重疊面積 0.011 m² 應高於容差被收錄");
            Assert.That(resultAbove.MatchedCells[0].OverlapAreaSquareMeters, Is.EqualTo(0.011).Within(1e-9));
        }

        [Test]
        public void CheckFloor_涵蓋面積_樓板部分落在格網外_CoveredArea小於樓板面積()
        {
            // 審查修正 1 的純數學驗證：格網只蓋 [0,10]x[0,10]，樓板跨出到 [5,20]x[5,20]，
            // 右上大半落在格網外，CoveredAreaSquareMeters 必須小於樓板總面積才能讓呼叫端算出
            // 正確的「未涵蓋面積」警示，不能靜默當作 0。
            var cells = new List<ParcelCellSummary> { RectCell("1", 0, 0, 10, 10, 40) };
            var floor = Rectangle(5, 5, 20, 20);

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);
            var floorArea = Polygon2D.Area(floor);

            Assert.That(floorArea, Is.EqualTo(225.0).Within(1e-9));
            Assert.That(result.CoveredAreaSquareMeters, Is.EqualTo(25.0).Within(1e-9)); // 只有 (5,5)-(10,10) 落在格網內
            Assert.That(result.CoveredAreaSquareMeters, Is.LessThan(floorArea));

            var uncovered = floorArea - result.CoveredAreaSquareMeters;
            Assert.That(uncovered, Is.EqualTo(200.0).Within(1e-9));
        }

        [Test]
        public void CheckFloor_涵蓋面積_樓板完全落在格網內_CoveredArea等於樓板面積()
        {
            var cells = new List<ParcelCellSummary>
            {
                RectCell("A", 0, 0, 10, 10, 10), // None
                RectCell("B", 10, 0, 20, 10, 40), // OpenSpaceOnly
            };
            var floor = Rectangle(2, 2, 18, 8); // 完全落在 A+B 範圍內，跨 None 與 OpenSpaceOnly 兩格。

            var result = ParcelLimitCheck.CheckFloor(1, floor, cells);
            var floorArea = Polygon2D.Area(floor);

            Assert.That(result.CoveredAreaSquareMeters, Is.EqualTo(floorArea).Within(1e-6));
        }

        [Test]
        public void ComputeLabelAnchor_一般矩形_回傳面積質心且落在板內()
        {
            var floor = Rectangle(0, 0, 10, 4); // 質心 (5,2)

            var anchor = ParcelLimitCheck.ComputeLabelAnchor(floor);

            Assert.That(anchor.X, Is.EqualTo(5.0).Within(1e-9));
            Assert.That(anchor.Y, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(Polygon2D.Contains(floor, anchor, 1e-9), Is.True);
        }

        [Test]
        public void ComputeLabelAnchor_極端U形質心落在板外_退回邊內側點仍落在板內()
        {
            // 窄底、窄腳、高缺口的 U 形（沿用已驗證過的 U 形頂點繞法：底部滿寬、缺口從上方切入）：
            // 底 10x1（面積10）+ 左右腳各寬 1、高 9（面積各 9），總面積 28；
            // 質心 y = (10*0.5 + 9*5.5 + 9*5.5)/28 = 104/28 ≈ 3.714，x=5（對稱），
            // 落在缺口 x∈[1,9]、y∈[1,10] 範圍內──在板外，強制觸發退回分支。
            var extremeU = new List<Point2D>
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10),
                new Point2D(9, 10), new Point2D(9, 1), new Point2D(1, 1),
                new Point2D(1, 10), new Point2D(0, 10),
            };
            Assert.That(Polygon2D.Area(extremeU), Is.EqualTo(28.0).Within(1e-9));

            var centroid = ParcelLimitCheck.PolygonCentroid(extremeU);
            Assert.That(Polygon2D.Contains(extremeU, centroid, 1e-9), Is.False,
                "本測試前提：質心必須落在板外才算真的觸發退回分支");

            var anchor = ParcelLimitCheck.ComputeLabelAnchor(extremeU);
            Assert.That(Polygon2D.Contains(extremeU, anchor, 1e-6), Is.True,
                $"anchor ({anchor.X:0.###},{anchor.Y:0.###}) 必須落在 U 形板內，不可沿用落在板外的質心");
        }
    }
}
