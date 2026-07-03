using System;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 鬆實方三本帳：自然方（Revit CUT/FILL 原值）、鬆方（運輸）、壓實方（填築需求）。
    /// 實質淨土方沿用既有號誌（Fill − Cut，負值＝餘土），但填方以壓實需求換算回自然方。
    /// </summary>
    public sealed class EarthworkLedger
    {
        private EarthworkLedger() { }

        public double LooseFactor { get; private set; }
        public double CompactionFactor { get; private set; }

        /// <summary>運土鬆方 = CUT × 鬆方係數（挖出土的運輸體積）。</summary>
        public double HaulVolumeLooseCubicMeters { get; private set; }

        /// <summary>填方所需自然方 = FILL ÷ 壓實係數（要壓實成 FILL 需要的自然土量）。</summary>
        public double RequiredBankForFillCubicMeters { get; private set; }

        /// <summary>實質淨土方 = FILL/壓實係數 − CUT（負值＝餘土，正值＝需進土）。</summary>
        public double EffectiveNetCubicMeters { get; private set; }

        public static EarthworkLedger Compute(
            double cutCubicMeters,
            double fillCubicMeters,
            double looseFactor,
            double compactionFactor)
        {
            if (looseFactor <= 0 || compactionFactor <= 0)
            {
                throw new ArgumentException("鬆方係數與壓實係數都必須大於 0。");
            }

            var requiredBank = fillCubicMeters / compactionFactor;
            return new EarthworkLedger
            {
                LooseFactor = looseFactor,
                CompactionFactor = compactionFactor,
                HaulVolumeLooseCubicMeters = cutCubicMeters * looseFactor,
                RequiredBankForFillCubicMeters = requiredBank,
                EffectiveNetCubicMeters = requiredBank - cutCubicMeters
            };
        }
    }
}
