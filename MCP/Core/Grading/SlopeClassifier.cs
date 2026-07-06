using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Core.Grading
{
    /// <summary>台灣水土保持技術規範「六級坡」單一級別定義（純資料，供分級與圖例；不相依 Revit API）。</summary>
    public sealed class SlopeClass
    {
        public SlopeClass(
            int level,
            string name,
            string attribute,
            double? lowerExclusivePercent,
            double? upperInclusivePercent,
            int red,
            int green,
            int blue)
        {
            Level = level;
            Name = name;
            Attribute = attribute;
            LowerExclusivePercent = lowerExclusivePercent;
            UpperInclusivePercent = upperInclusivePercent;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public int Level { get; }
        public string Name { get; }

        /// <summary>屬性標註：五級坡＝宜林地、六級坡＝加強保育地；一般級別為空字串。</summary>
        public string Attribute { get; }

        /// <summary>坡度須「大於」此值（null＝無下界，一級坡）。</summary>
        public double? LowerExclusivePercent { get; }

        /// <summary>坡度須「小於等於」此值（null＝無上界，六級坡）。</summary>
        public double? UpperInclusivePercent { get; }

        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }

        /// <summary>圖例級距文字，如「≤5%」「>40 ~ 55%」「>55%」。</summary>
        public string RangeText
        {
            get
            {
                if (!LowerExclusivePercent.HasValue)
                {
                    return $"≤{UpperInclusivePercent.Value:0.#}%";
                }

                if (!UpperInclusivePercent.HasValue)
                {
                    return $">{LowerExclusivePercent.Value:0.#}%";
                }

                return $">{LowerExclusivePercent.Value:0.#} ~ {UpperInclusivePercent.Value:0.#}%";
            }
        }
    }

    /// <summary>
    /// 水保六級坡純分級器（可 NUnit 測試，零 Revit 相依）：
    /// 一級坡 ≤5%；二級坡 >5~15%；三級坡 >15~30%；四級坡 >30~40%；
    /// 五級坡 >40~55%（宜林地）；六級坡 >55%（加強保育地）。
    /// </summary>
    public static class SlopeClassifier
    {
        /// <summary>六級坡定義（Level 1..6），依級別遞增排序。</summary>
        public static readonly IReadOnlyList<SlopeClass> Classes = new List<SlopeClass>
        {
            new SlopeClass(1, "一級坡", string.Empty, null, 5.0, 43, 160, 43),
            new SlopeClass(2, "二級坡", string.Empty, 5.0, 15.0, 140, 200, 80),
            new SlopeClass(3, "三級坡", string.Empty, 15.0, 30.0, 255, 214, 0),
            new SlopeClass(4, "四級坡", string.Empty, 30.0, 40.0, 255, 127, 0),
            new SlopeClass(5, "五級坡", "宜林地", 40.0, 55.0, 227, 26, 28),
            new SlopeClass(6, "六級坡", "加強保育地", 55.0, null, 123, 50, 148),
        };

        /// <summary>
        /// 將表面坡度（%）分到六級坡的級別（回傳 1..6）。純函式。
        /// 邊界採右閉：≤5→1、(5,15]→2、(15,30]→3、(30,40]→4、(40,55]→5、>55→6。
        /// </summary>
        /// <param name="slopePercent">表面坡度百分比（非負有限數值）。</param>
        public static int Classify(double slopePercent)
        {
            if (double.IsNaN(slopePercent) || double.IsInfinity(slopePercent))
            {
                throw new ArgumentException("坡度百分比必須是有限數值。", nameof(slopePercent));
            }

            if (slopePercent < 0)
            {
                throw new ArgumentException("坡度百分比不可為負值。", nameof(slopePercent));
            }

            if (slopePercent <= 5.0)
            {
                return 1;
            }

            if (slopePercent <= 15.0)
            {
                return 2;
            }

            if (slopePercent <= 30.0)
            {
                return 3;
            }

            if (slopePercent <= 40.0)
            {
                return 4;
            }

            if (slopePercent <= 55.0)
            {
                return 5;
            }

            return 6;
        }

        /// <summary>取得指定級別（1..6）的級距定義；超界丟 ArgumentOutOfRangeException。</summary>
        public static SlopeClass GetClass(int level)
        {
            var found = Classes.FirstOrDefault(slopeClass => slopeClass.Level == level);
            if (found == null)
            {
                throw new ArgumentOutOfRangeException(nameof(level), "級別必須介於 1 與 6 之間。");
            }

            return found;
        }
    }
}
