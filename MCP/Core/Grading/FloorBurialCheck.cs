using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 非控制樓板覆蓋判定（純函式，無 Revit API 相依）。
    /// 語意：只認「整地造成的新增覆蓋」——設計地形高過板頂、且該處原地形本來沒有蓋住板頂。
    /// 原本就埋在原地形下方的樓板（如地下室板）屬刻意設計，不告警。
    /// 所有高程使用同一單位（Revit 內部英呎），由呼叫端負責轉換。
    /// </summary>
    public static class FloorBurialCheck
    {
        /// <summary>
        /// 逐取樣點判定新增覆蓋；回傳是否有新增覆蓋與最大新增埋深（無則 0）。
        /// tolerance 為覆蓋判定門檻（告警用途，非工程閘門）。
        /// </summary>
        public static (bool NewlyBuried, double MaxNewDepth) Evaluate(
            IEnumerable<(double FloorTopZ, double OriginalZ, double DesignZ)> samples,
            double tolerance)
        {
            if (tolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance), "覆蓋判定門檻不可為負。");
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));

            var newlyBuried = false;
            var maxNewDepth = 0.0;
            foreach (var (floorTopZ, originalZ, designZ) in samples)
            {
                var coveredNow = designZ > floorTopZ + tolerance;
                var coveredBefore = originalZ > floorTopZ + tolerance;
                if (!coveredNow || coveredBefore)
                {
                    continue;
                }

                newlyBuried = true;
                maxNewDepth = Math.Max(maxNewDepth, designZ - floorTopZ);
            }

            return (newlyBuried, maxNewDepth);
        }

        /// <summary>
        /// 不覆蓋政策（使用者裁決 2026-07-08）：帶頂點落在非控制樓板投影內時的目標高程壓蓋。
        /// 原本外露的樓板（原地形未高於板底）→ 目標壓至板底以下；
        /// 原本就埋在原地形下的樓板（如地下室板）→ 不動，避免把既有覆土挖開。
        /// </summary>
        public static double CapTargetZ(
            double fieldTargetZ,
            double originalTerrainZ,
            double floorBottomZ,
            double tolerance)
        {
            if (tolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance), "覆蓋判定門檻不可為負。");

            var coveredBefore = originalTerrainZ > floorBottomZ + tolerance;
            return coveredBefore ? fieldTargetZ : Math.Min(fieldTargetZ, floorBottomZ);
        }

        /// <summary>
        /// 連續放坡（使用者裁決 2026-07-08：壓蓋板緣不要高差）：
        /// 任一取樣點「原本外露（原地形未高於板底）且距離場目標會蓋過板底」→ 該板應自動納入控制樓板，
        /// 讓地形沿板底通過、於板緣接續放坡。距離場目標低於板底（高架板）或原本就埋者不納入。
        /// </summary>
        public static bool ShouldAutoInclude(
            IEnumerable<(double FloorBottomZ, double OriginalZ, double FieldTargetZ)> samples,
            double tolerance)
        {
            if (tolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance), "覆蓋判定門檻不可為負。");
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));

            foreach (var (floorBottomZ, originalZ, fieldTargetZ) in samples)
            {
                var wouldCover = fieldTargetZ > floorBottomZ + tolerance;
                var coveredBefore = originalZ > floorBottomZ + tolerance;
                if (wouldCover && !coveredBefore)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
